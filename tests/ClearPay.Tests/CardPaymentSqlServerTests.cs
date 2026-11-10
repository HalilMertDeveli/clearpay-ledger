using ClearPay.Application.Payments;
using ClearPay.Domain.Payments;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Tests;

/// <summary>Refund races on the real SQL Server schema (Serializable + CHECK constraints from the migration).</summary>
public sealed class CardPaymentSqlServerTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public CardPaymentSqlServerTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [SqlServerFact]
    public async Task Parallel_partial_refunds_never_exceed_the_captured_amount()
    {
        using var host = new PaymentTestHost(_fixture.ConnectionString);
        var user = "race-" + Guid.NewGuid().ToString("N")[..8];
        var session = await host.AuthenticatedSessionAsync(user, 100m);
        Guid paymentId;
        await using (var db = host.NewContext())
        {
            var created = await host.NewService(db).CreateAsync(
                new CreateCardPaymentCommand(user, session, null, true, "create-" + Guid.NewGuid()));
            created.Payment!.Status.Should().Be(CardPaymentStatus.Captured);
            paymentId = created.Payment.PaymentId;
        }

        // Ten different keys race for 30.00 each; at most three can fit into 100.00.
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var db = host.NewContext();
                    return await host.NewService(db).RefundAsync(
                        new PaymentActionCommand(user, paymentId, 30m, $"refund-{i}"));
                }
                catch (Exception ex) when (attempt < 5 && IsDeadlock(ex))
                {
                    // Serializable turns contention into deadlocks; the caller retries with the same key.
                }
            }
        }));

        results.Count(r => r.Kind == PaymentResultKind.Ok).Should().Be(3);
        results.Where(r => r.Kind != PaymentResultKind.Ok)
            .Should().OnlyContain(r => r.Kind == PaymentResultKind.ExceedsRefundable);
        (await host.NetAsync(user)).Should().Be(10m);

        await using var check = host.NewContext();
        var payment = await check.CardPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
        payment.RefundedAmount.Should().Be(90m);
        payment.Status.Should().Be(CardPaymentStatus.PartiallyRefunded);
        (await check.PaymentOperations.CountAsync(o => o.PaymentId == paymentId && o.Type == PaymentOperationType.Refund))
            .Should().Be(3);
    }

    [SqlServerFact]
    public async Task Database_rejects_a_refunded_amount_above_the_captured_amount()
    {
        using var host = new PaymentTestHost(_fixture.ConnectionString);
        var user = "ck-" + Guid.NewGuid().ToString("N")[..8];
        var session = await host.AuthenticatedSessionAsync(user, 10m);
        Guid paymentId;
        await using (var db = host.NewContext())
        {
            paymentId = (await host.NewService(db).CreateAsync(
                new CreateCardPaymentCommand(user, session, null, true, "ck-" + Guid.NewGuid()))).Payment!.PaymentId;
        }

        await using var raw = host.NewContext();
        var act = () => raw.CardPayments.Where(p => p.Id == paymentId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RefundedAmount, 10.01m));
        await act.Should().ThrowAsync<Exception>().Where(e => e.ToString().Contains("CK_CardPayment_Refund_LE_Captured"));
    }

    private static bool IsDeadlock(Exception ex) =>
        ex.ToString().Contains("deadlock", StringComparison.OrdinalIgnoreCase)
        || ex is InvalidOperationException { InnerException: not null } && ex.InnerException.ToString().Contains("1205");
}
