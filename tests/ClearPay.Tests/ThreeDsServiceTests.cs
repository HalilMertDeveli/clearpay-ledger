using ClearPay.Application.Payments;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Payments;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Tests;

/// <summary>Gateway + fake ACS on an in-memory SQLite ledger with a controllable clock.</summary>
public sealed class ThreeDsServiceTests : IDisposable
{
    private const string Secret = "test-only-3ds-acs-key-not-a-secret-0123456789";
    private const string TestCard = "4000000000000010";

    private readonly SqliteConnection _connection;
    private readonly ClearPayDbContext _db;
    private readonly TestClock _clock = new();
    private readonly ThreeDsOptions _options = new(Secret, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));
    private readonly SqlThreeDsService _service;
    private readonly FakeAcs _acs;

    public ThreeDsServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new ClearPayDbContext(new DbContextOptionsBuilder<ClearPayDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _service = new SqlThreeDsService(
            _db, new SqlIdempotencyStore(_db, _clock), _clock, _options, NullLogger<SqlThreeDsService>.Instance);
        _acs = new FakeAcs(_options, _clock);
    }

    [Fact]
    public async Task Correct_otp_moves_challenge_to_authenticated_with_outbox_and_audit()
    {
        var started = await StartAsync();
        started.Status.Should().Be(ThreeDsStatus.Challenge);
        started.AcsUrl.Should().StartWith("/fake-acs?creq=");

        var challenge = _acs.ReadChallenge(CreqOf(started))!;
        challenge.Amount.Should().Be(125.50m);
        challenge.CardLast4.Should().Be("0010");

        var cres = _acs.Complete(challenge, ThreeDsTestCards.DemoOtp, cancelled: false);
        var outcome = await _service.HandleCallbackAsync(cres);

        outcome.Kind.Should().Be(ThreeDsCallbackKind.Applied);
        var row = await _db.ThreeDsSessions.AsNoTracking().SingleAsync();
        row.Status.Should().Be(ThreeDsStatus.Authenticated);
        row.Eci.Should().Be("05");
        row.AuthenticationValue.Should().NotBeNullOrEmpty();
        (await _db.OutboxMessages.Select(m => m.Type).ToListAsync())
            .Should().ContainSingle(t => t == "clearpay.3ds.authenticated");
        (await _db.AuditLogs.Select(a => a.Action).ToListAsync())
            .Should().BeEquivalentTo(new[] { "3ds.initiated", "3ds.authenticated" });
        (await _db.LedgerEntries.CountAsync()).Should().Be(0, "authentication alone never moves money");
    }

    [Fact]
    public async Task Wrong_otp_or_cancel_ends_failed()
    {
        var wrong = await StartAsync("k-wrong");
        var cancel = await StartAsync("k-cancel");

        (await _service.HandleCallbackAsync(_acs.Complete(_acs.ReadChallenge(CreqOf(wrong))!, "000000", false)))
            .Status.Should().Be(ThreeDsStatus.Failed);
        (await _service.HandleCallbackAsync(_acs.Complete(_acs.ReadChallenge(CreqOf(cancel))!, null, true)))
            .Status.Should().Be(ThreeDsStatus.Failed);

        var rows = await _db.ThreeDsSessions.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.Status == ThreeDsStatus.Failed && r.AuthenticationValue == null);
    }

    [Fact]
    public async Task Declined_test_card_fails_without_challenge()
    {
        var started = await StartAsync(card: "4000000000000002");

        started.Status.Should().Be(ThreeDsStatus.Failed);
        started.AcsUrl.Should().BeNull();
        (await _db.OutboxMessages.SingleAsync()).Type.Should().Be("clearpay.3ds.failed");
    }

    [Fact]
    public async Task Tampered_callback_is_rejected_and_changes_nothing()
    {
        var started = await StartAsync();
        var cres = _acs.Complete(_acs.ReadChallenge(CreqOf(started))!, "000000", false);

        // Attacker flips a failed result to success without the ACS key.
        var forged = cres with { TransStatus = "Y", AuthenticationValue = "forged" };
        (await _service.HandleCallbackAsync(forged)).Kind.Should().Be(ThreeDsCallbackKind.InvalidSignature);
        (await _service.HandleCallbackAsync(cres with { Signature = null })).Kind.Should().Be(ThreeDsCallbackKind.InvalidSignature);

        var row = await _db.ThreeDsSessions.AsNoTracking().SingleAsync();
        row.Status.Should().Be(ThreeDsStatus.Challenge);
        (await _db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Same_result_delivered_twice_is_a_no_op_and_a_second_result_conflicts()
    {
        var started = await StartAsync();
        var challenge = _acs.ReadChallenge(CreqOf(started))!;
        var first = _acs.Complete(challenge, ThreeDsTestCards.DemoOtp, false);

        (await _service.HandleCallbackAsync(first)).Kind.Should().Be(ThreeDsCallbackKind.Applied);
        (await _service.HandleCallbackAsync(first)).Kind.Should().Be(ThreeDsCallbackKind.AlreadyApplied);

        var other = _acs.Complete(challenge, "999999", false);
        var conflict = await _service.HandleCallbackAsync(other);
        conflict.Kind.Should().Be(ThreeDsCallbackKind.Conflict);
        conflict.Status.Should().Be(ThreeDsStatus.Authenticated);
        (await _db.OutboxMessages.CountAsync(m => m.Type == "clearpay.3ds.authenticated")).Should().Be(1);
    }

    [Fact]
    public async Task Result_outside_the_time_window_is_stale()
    {
        var started = await StartAsync();
        var cres = _acs.Complete(_acs.ReadChallenge(CreqOf(started))!, ThreeDsTestCards.DemoOtp, false);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(6);
        (await _service.HandleCallbackAsync(cres)).Kind.Should().Be(ThreeDsCallbackKind.Stale);
    }

    [Fact]
    public async Task Expiry_job_times_out_open_challenges_once()
    {
        await StartAsync("k-a");
        await StartAsync("k-b");
        await StartAsync("k-declined", card: "4000000000000002");

        (await _service.ExpireStaleAsync()).Should().Be(0);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(11);
        (await _service.ExpireStaleAsync()).Should().Be(2);
        (await _service.ExpireStaleAsync()).Should().Be(0);

        (await _db.ThreeDsSessions.CountAsync(s => s.Status == ThreeDsStatus.TimedOut)).Should().Be(2);
        (await _db.OutboxMessages.CountAsync(m => m.Type == "clearpay.3ds.timedout")).Should().Be(2);
    }

    [Fact]
    public async Task Callback_after_deadline_times_out_instead_of_authenticating()
    {
        var started = await StartAsync();
        var challenge = _acs.ReadChallenge(CreqOf(started))!;
        _clock.UtcNow = _clock.UtcNow.AddMinutes(10).AddSeconds(1);
        var lateResult = _acs.Complete(challenge, ThreeDsTestCards.DemoOtp, false);

        var outcome = await _service.HandleCallbackAsync(lateResult);

        outcome.Kind.Should().Be(ThreeDsCallbackKind.Expired);
        (await _db.ThreeDsSessions.AsNoTracking().SingleAsync()).Status.Should().Be(ThreeDsStatus.TimedOut);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_existing_session()
    {
        var first = await StartAsync("same-key");
        var again = await _service.InitiateAsync(new ThreeDsInitiateCommand("user-1", 999m, TestCard, "same-key"));

        again.Kind.Should().Be(ThreeDsInitiateKind.Replay);
        again.SessionId.Should().Be(first.SessionId);
        (await _db.ThreeDsSessions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(0, TestCard, ThreeDsInitiateKind.InvalidAmount)]
    [InlineData(10.005, TestCard, ThreeDsInitiateKind.InvalidAmount)]
    [InlineData(10, "4000000000000011", ThreeDsInitiateKind.InvalidCard)]
    [InlineData(10, "12ab", ThreeDsInitiateKind.InvalidCard)]
    public async Task Invalid_input_creates_nothing(double amount, string card, ThreeDsInitiateKind expected)
    {
        var outcome = await _service.InitiateAsync(
            new ThreeDsInitiateCommand("user-1", (decimal)amount, card, "k-" + Guid.NewGuid()));

        outcome.Kind.Should().Be(expected);
        (await _db.ThreeDsSessions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Owner_only_can_read_the_session()
    {
        var started = await StartAsync();
        (await _service.GetAsync(started.SessionId, "user-1")).Should().NotBeNull();
        (await _service.GetAsync(started.SessionId, "someone-else")).Should().BeNull();
    }

    private async Task<ThreeDsInitiateOutcome> StartAsync(string key = "k-1", string card = TestCard)
    {
        var outcome = await _service.InitiateAsync(new ThreeDsInitiateCommand("user-1", 125.50m, card, key));
        outcome.Kind.Should().Be(ThreeDsInitiateKind.Created);
        return outcome;
    }

    private static string CreqOf(ThreeDsInitiateOutcome outcome) =>
        Uri.UnescapeDataString(outcome.AcsUrl!["/fake-acs?creq=".Length..]);

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

public sealed class ThreeDsDomainAndSignerTests
{
    private const string Secret = "test-only-3ds-acs-key-not-a-secret-0123456789";

    [Theory]
    [InlineData(ThreeDsStatus.Initiated, ThreeDsStatus.Challenge, true)]
    [InlineData(ThreeDsStatus.Initiated, ThreeDsStatus.Failed, true)]
    [InlineData(ThreeDsStatus.Initiated, ThreeDsStatus.TimedOut, true)]
    [InlineData(ThreeDsStatus.Initiated, ThreeDsStatus.Authenticated, false)]
    [InlineData(ThreeDsStatus.Challenge, ThreeDsStatus.Authenticated, true)]
    [InlineData(ThreeDsStatus.Challenge, ThreeDsStatus.Failed, true)]
    [InlineData(ThreeDsStatus.Challenge, ThreeDsStatus.TimedOut, true)]
    [InlineData(ThreeDsStatus.Authenticated, ThreeDsStatus.Failed, false)]
    [InlineData(ThreeDsStatus.Failed, ThreeDsStatus.Authenticated, false)]
    [InlineData(ThreeDsStatus.TimedOut, ThreeDsStatus.Authenticated, false)]
    [InlineData(ThreeDsStatus.Authenticated, ThreeDsStatus.TimedOut, false)]
    public void State_machine_allows_only_forward_moves(ThreeDsStatus from, ThreeDsStatus to, bool allowed)
    {
        var session = new ThreeDsSession { Status = from };
        session.TryMoveTo(to, DateTimeOffset.UnixEpoch).Should().Be(allowed);
        session.Status.Should().Be(allowed ? to : from);
    }

    [Fact]
    public void Signature_covers_every_field()
    {
        var result = new AcsChallengeResult(Guid.NewGuid(), "Y", "05", "abc", 1_700_000_000, "n1", null);
        var sig = ThreeDsSigner.Sign(ThreeDsSigner.CanonicalChallengeResult(result), Secret);

        ThreeDsSigner.Verify(ThreeDsSigner.CanonicalChallengeResult(result), sig, Secret).Should().BeTrue();
        foreach (var tampered in new[]
                 {
                     result with { SessionId = Guid.NewGuid() },
                     result with { TransStatus = "N" },
                     result with { Eci = "07" },
                     result with { AuthenticationValue = "abd" },
                     result with { IssuedAtUnix = 1_700_000_001 },
                     result with { Nonce = "n2" }
                 })
        {
            ThreeDsSigner.Verify(ThreeDsSigner.CanonicalChallengeResult(tampered), sig, Secret).Should().BeFalse();
        }

        ThreeDsSigner.Verify(ThreeDsSigner.CanonicalChallengeResult(result), sig, Secret + "x").Should().BeFalse();
    }

    [Fact]
    public void Challenge_token_rejects_tampering_and_wrong_key()
    {
        var id = Guid.NewGuid();
        var token = ChallengeToken.Create(id, 10m, "0010", DateTimeOffset.UnixEpoch.AddDays(1), Secret);

        ChallengeToken.Read(token, Secret)!.SessionId.Should().Be(id);
        ChallengeToken.Read(token, Secret + "x").Should().BeNull();
        ChallengeToken.Read(token.Replace('.', ','), Secret).Should().BeNull();
        var other = ChallengeToken.Create(id, 99999m, "0010", DateTimeOffset.UnixEpoch.AddDays(1), Secret);
        ChallengeToken.Read(other.Split('.')[0] + "." + token.Split('.')[1], Secret)
            .Should().BeNull("an amount change invalidates the signature");
        ChallengeToken.Read(null, Secret).Should().BeNull();
    }

    [Theory]
    [InlineData("4000000000000010", true)]
    [InlineData("4000 0000 0000 0002", true)]
    [InlineData("4242424242424242", true)]
    [InlineData("4242424242424241", false)]
    [InlineData("", false)]
    public void Luhn_check(string number, bool valid) =>
        ThreeDsTestCards.IsLuhnValid(number).Should().Be(valid);
}
