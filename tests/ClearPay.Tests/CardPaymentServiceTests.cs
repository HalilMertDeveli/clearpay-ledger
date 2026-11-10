using ClearPay.Application.Banking;
using ClearPay.Application.Payments;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Payments.Psp;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Tests;

public sealed class CardPaymentServiceTests : IDisposable
{
    private const string User = "card-user";
    private readonly PaymentTestHost _host = new();

    [Theory]
    [InlineData(SimBankRestPsp.ProviderName)]
    [InlineData(SimBankSoapPsp.ProviderName)]
    [InlineData(SimPayPsp.ProviderName)]
    public async Task Every_adapter_authorizes_and_captures_into_the_wallet(string provider)
    {
        var session = await _host.AuthenticatedSessionAsync(User, 120m);

        var result = await CreateAsync(session, provider);

        result.Kind.Should().Be(PaymentResultKind.Ok);
        result.Payment!.Status.Should().Be(CardPaymentStatus.Captured);
        result.Payment.Provider.Should().Be(provider);
        result.Payment.ProviderReference.Should().StartWith(provider.ToUpperInvariant());
        (await _host.NetAsync(User)).Should().Be(120m);
        (await _host.NetAsync(Treasury.UserId)).Should().Be(-120m);

        await using var db = _host.NewContext();
        (await db.LedgerEntries.Select(e => e.Kind).Distinct().ToListAsync())
            .Should().Equal(LedgerEntryKind.CardPayment);
        (await db.OutboxMessages.Select(m => m.Type).ToListAsync())
            .Should().BeEquivalentTo(new[] { "clearpay.payment.authorized", "clearpay.payment.captured" });
        (await db.SimPspTransactions.Where(t => t.Provider == provider).Select(t => t.Type).ToListAsync())
            .Should().BeEquivalentTo(new[] { PaymentOperationType.Authorize, PaymentOperationType.Capture });
    }

    [Fact]
    public async Task Replayed_create_executes_nothing_twice()
    {
        var session = await _host.AuthenticatedSessionAsync(User, 50m);
        (await CreateAsync(session, key: "same")).Kind.Should().Be(PaymentResultKind.Ok);

        var again = await CreateAsync(session, key: "same");

        again.Kind.Should().Be(PaymentResultKind.Replay);
        again.Payment!.Status.Should().Be(CardPaymentStatus.Captured);
        (await _host.NetAsync(User)).Should().Be(50m);
        await using var db = _host.NewContext();
        (await db.SimPspTransactions.CountAsync()).Should().Be(2, "one authorization and one capture at the PSP");
    }

    [Fact]
    public async Task Three_ds_must_be_authenticated_and_is_single_use()
    {
        var used = await _host.AuthenticatedSessionAsync(User, 20m);
        (await CreateAsync(used, key: "a")).Kind.Should().Be(PaymentResultKind.Ok);
        (await CreateAsync(used, key: "b")).Kind.Should().Be(PaymentResultKind.ThreeDsAlreadyUsed);

        await using (var db = _host.NewContext())
        {
            var open = new ThreeDsSession
            {
                Id = Guid.NewGuid(), UserId = User, Amount = 5m, CardLast4 = "0010", Status = ThreeDsStatus.Challenge,
                CorrelationId = Guid.NewGuid(), CreatedAt = _host.Clock.UtcNow, UpdatedAt = _host.Clock.UtcNow,
                ExpiresAt = _host.Clock.UtcNow.AddMinutes(5)
            };
            db.ThreeDsSessions.Add(open);
            await db.SaveChangesAsync();
            (await CreateAsync(open.Id, key: "c")).Kind.Should().Be(PaymentResultKind.ThreeDsNotAuthenticated);
        }

        (await CreateAsync(Guid.NewGuid(), key: "d")).Kind.Should().Be(PaymentResultKind.NotFound);
        var other = await _host.AuthenticatedSessionAsync("someone-else", 5m);
        (await CreateAsync(other, key: "e")).Kind.Should().Be(PaymentResultKind.NotFound);
    }

    [Theory]
    [InlineData(SimBankRestPsp.ProviderName, 10.51, "51")]
    [InlineData(SimBankSoapPsp.ProviderName, 10.05, "05")]
    public async Task Declined_authorization_posts_nothing(string provider, double amount, string code)
    {
        var session = await _host.AuthenticatedSessionAsync(User, (decimal)amount);

        var result = await CreateAsync(session, provider);

        result.Kind.Should().Be(PaymentResultKind.Declined);
        result.DeclineCode.Should().Be(code);
        result.Payment!.Status.Should().Be(CardPaymentStatus.Declined);
        (await _host.NetAsync(User)).Should().Be(0m);
    }

    [Fact]
    public async Task Authorize_only_then_void_moves_no_money()
    {
        var session = await _host.AuthenticatedSessionAsync(User, 70m);
        var created = await CreateAsync(session, capture: false);
        created.Payment!.Status.Should().Be(CardPaymentStatus.Authorized);
        var id = created.Payment.PaymentId;

        var voided = await ActAsync(s => s.VoidAsync, id, null, "void-1");
        voided.Payment!.Status.Should().Be(CardPaymentStatus.Voided);
        (await ActAsync(s => s.VoidAsync, id, null, "void-1")).Kind.Should().Be(PaymentResultKind.Replay);
        (await ActAsync(s => s.VoidAsync, id, null, "void-2")).Kind.Should().Be(PaymentResultKind.InvalidState);
        (await ActAsync(s => s.CaptureAsync, id, null, "cap-1")).Kind.Should().Be(PaymentResultKind.InvalidState);
        (await ActAsync(s => s.RefundAsync, id, 10m, "ref-1")).Kind.Should().Be(PaymentResultKind.InvalidState);

        (await _host.NetAsync(User)).Should().Be(0m);
        await using var db = _host.NewContext();
        (await db.LedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Separate_capture_after_authorization()
    {
        var session = await _host.AuthenticatedSessionAsync(User, 33m);
        var id = (await CreateAsync(session, capture: false)).Payment!.PaymentId;

        var captured = await ActAsync(s => s.CaptureAsync, id, null, "cap");

        captured.Payment!.Status.Should().Be(CardPaymentStatus.Captured);
        (await ActAsync(s => s.CaptureAsync, id, null, "cap")).Kind.Should().Be(PaymentResultKind.Replay);
        (await ActAsync(s => s.VoidAsync, id, null, "void")).Kind.Should().Be(PaymentResultKind.InvalidState);
        (await _host.NetAsync(User)).Should().Be(33m);
    }

    [Fact]
    public async Task Partial_refunds_never_exceed_the_captured_amount()
    {
        var id = await CapturedAsync(100m);

        (await ActAsync(s => s.RefundAsync, id, 30m, "r1")).Payment!.Status.Should().Be(CardPaymentStatus.PartiallyRefunded);
        (await ActAsync(s => s.RefundAsync, id, 30m, "r2")).Payment!.RefundedAmount.Should().Be(60m);
        (await ActAsync(s => s.RefundAsync, id, 50m, "r3")).Kind.Should().Be(PaymentResultKind.ExceedsRefundable);
        var last = await ActAsync(s => s.RefundAsync, id, 40m, "r4");

        last.Payment!.Status.Should().Be(CardPaymentStatus.Refunded);
        last.Payment.RefundedAmount.Should().Be(100m);
        (await _host.NetAsync(User)).Should().Be(0m);
        (await ActAsync(s => s.RefundAsync, id, 0.01m, "r5")).Kind.Should().Be(PaymentResultKind.InvalidState);

        await using var db = _host.NewContext();
        (await db.LedgerEntries.SumAsync(e => (double)e.Amount)).Should().Be(0d);
        (await db.SimPspTransactions.CountAsync(t => t.Type == PaymentOperationType.Refund)).Should().Be(3);
    }

    [Fact]
    public async Task Refund_retry_with_same_key_debits_once_and_a_changed_body_is_rejected()
    {
        var id = await CapturedAsync(80m);

        (await ActAsync(s => s.RefundAsync, id, 25m, "rk")).Kind.Should().Be(PaymentResultKind.Ok);
        (await ActAsync(s => s.RefundAsync, id, 25m, "rk")).Kind.Should().Be(PaymentResultKind.Replay);
        (await ActAsync(s => s.RefundAsync, id, 26m, "rk")).Kind.Should().Be(PaymentResultKind.KeyPayloadMismatch);

        (await _host.NetAsync(User)).Should().Be(55m);
    }

    [Fact]
    public async Task Refund_declined_by_psp_is_reversed_in_the_ledger()
    {
        var id = await CapturedAsync(90m);

        var declined = await ActAsync(s => s.RefundAsync, id, 10.52m, "rd");

        declined.Kind.Should().Be(PaymentResultKind.Declined);
        declined.DeclineCode.Should().Be("R52");
        declined.Payment!.Status.Should().Be(CardPaymentStatus.Captured);
        declined.Payment.RefundedAmount.Should().Be(0m);
        (await _host.NetAsync(User)).Should().Be(90m);
        await using var db = _host.NewContext();
        (await db.LedgerEntries.CountAsync(e => e.Kind == LedgerEntryKind.Refund)).Should().Be(4, "reserve pair + reversal pair");
        (await db.OutboxMessages.CountAsync(m => m.Type == "clearpay.payment.refund_failed")).Should().Be(1);
    }

    [Fact]
    public async Task Refund_cannot_overdraw_the_wallet()
    {
        var id = await CapturedAsync(100m);
        await _host.SpendAsync(User, 80m);

        (await ActAsync(s => s.RefundAsync, id, 50m, "rx")).Kind.Should().Be(PaymentResultKind.InsufficientFunds);
        (await ActAsync(s => s.RefundAsync, id, 20m, "ry")).Kind.Should().Be(PaymentResultKind.Ok);
        (await _host.NetAsync(User)).Should().Be(0m);
    }

    [Fact]
    public async Task Lost_psp_response_is_resumed_with_the_same_key_without_double_posting()
    {
        // simpay executes amounts ending in .99 but "loses" the response.
        var session = await _host.AuthenticatedSessionAsync(User, 49.99m);

        var first = await CreateAsync(session, SimPayPsp.ProviderName, key: "lost");
        first.Kind.Should().Be(PaymentResultKind.TimedOut);
        first.Payment!.Status.Should().Be(CardPaymentStatus.Pending);

        var second = await CreateAsync(session, SimPayPsp.ProviderName, key: "lost");
        second.Kind.Should().Be(PaymentResultKind.TimedOut, "authorization is now known, but the capture response is lost too");
        second.Payment!.Status.Should().Be(CardPaymentStatus.Authorized);

        var third = await CreateAsync(session, SimPayPsp.ProviderName, key: "lost");
        third.Kind.Should().Be(PaymentResultKind.Ok);
        third.Payment!.Status.Should().Be(CardPaymentStatus.Captured);

        (await _host.NetAsync(User)).Should().Be(49.99m);
        await using var db = _host.NewContext();
        (await db.SimPspTransactions.CountAsync()).Should().Be(2, "the PSP saw each provider key once");
        (await db.LedgerEntries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Owner_only_reads_the_payment()
    {
        var id = await CapturedAsync(10m);
        await using var db = _host.NewContext();
        (await _host.NewService(db).GetAsync(id, User)).Should().NotBeNull();
        (await _host.NewService(db).GetAsync(id, "intruder")).Should().BeNull();
        (await ActAsyncAs("intruder", s => s.RefundAsync, id, 1m, "steal")).Kind.Should().Be(PaymentResultKind.NotFound);
    }

    private async Task<Guid> CapturedAsync(decimal amount)
    {
        var session = await _host.AuthenticatedSessionAsync(User, amount);
        var result = await CreateAsync(session, key: "cap-" + Guid.NewGuid());
        result.Payment!.Status.Should().Be(CardPaymentStatus.Captured);
        return result.Payment.PaymentId;
    }

    private async Task<PaymentResult> CreateAsync(
        Guid session, string provider = SimBankRestPsp.ProviderName, bool capture = true, string? key = null)
    {
        await using var db = _host.NewContext();
        return await _host.NewService(db).CreateAsync(
            new CreateCardPaymentCommand(User, session, provider, capture, key ?? Guid.NewGuid().ToString("N")));
    }

    private Task<PaymentResult> ActAsync(
        Func<Infrastructure.Payments.SqlCardPaymentService, Func<PaymentActionCommand, CancellationToken, Task<PaymentResult>>> pick,
        Guid id, decimal? amount, string key) => ActAsyncAs(User, pick, id, amount, key);

    private async Task<PaymentResult> ActAsyncAs(
        string user,
        Func<Infrastructure.Payments.SqlCardPaymentService, Func<PaymentActionCommand, CancellationToken, Task<PaymentResult>>> pick,
        Guid id, decimal? amount, string key)
    {
        await using var db = _host.NewContext();
        var service = _host.NewService(db);
        return await pick(service)(new PaymentActionCommand(user, id, amount, key), CancellationToken.None);
    }

    public void Dispose() => _host.Dispose();
}
