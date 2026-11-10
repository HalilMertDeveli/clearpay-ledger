using ClearPay.Application.Payments;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Payments;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Tests;

/// <summary>Compare-and-set state changes on the real SQL Server schema (migration applied).</summary>
public sealed class ThreeDsSqlServerTests : IClassFixture<SqlServerFixture>
{
    private const string Secret = "test-only-3ds-acs-key-not-a-secret-0123456789";
    private readonly SqlServerFixture _fixture;
    private readonly ThreeDsOptions _options = new(Secret, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

    public ThreeDsSqlServerTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [SqlServerFact]
    public async Task Racing_callbacks_and_expiry_leave_exactly_one_terminal_transition()
    {
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        Guid sessionId;
        await using (var db = _fixture.CreateContext())
        {
            var started = await Service(db, clock).InitiateAsync(
                new ThreeDsInitiateCommand("race-user", 77m, "4000000000000010", "race-" + Guid.NewGuid()));
            started.Status.Should().Be(ThreeDsStatus.Challenge);
            sessionId = started.SessionId;
        }

        var acs = new FakeAcs(_options, clock);
        var challenge = new AcsChallenge(sessionId, 77m, "0010", clock.UtcNow.AddMinutes(10));
        var results = Enumerable.Range(0, 8)
            .Select(i => acs.Complete(challenge, i % 2 == 0 ? ThreeDsTestCards.DemoOtp : "000000", false))
            .ToList();

        var outcomes = await Task.WhenAll(results.Select(async r =>
        {
            await using var db = _fixture.CreateContext();
            return await Service(db, clock).HandleCallbackAsync(r);
        }));

        outcomes.Count(o => o.Kind == ThreeDsCallbackKind.Applied).Should().Be(1);
        outcomes.Where(o => o.Kind != ThreeDsCallbackKind.Applied)
            .Should().OnlyContain(o => o.Kind == ThreeDsCallbackKind.Conflict);

        await using var check = _fixture.CreateContext();
        var row = await check.ThreeDsSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        row.IsTerminal.Should().BeTrue();
        var events = await check.OutboxMessages
            .Where(m => m.Type.StartsWith("clearpay.3ds.") && m.Payload.Contains(sessionId.ToString()))
            .CountAsync();
        events.Should().Be(1, "only the winning transition writes its outbox event");

        // The expiry job arriving later must not overwrite the terminal state.
        clock.UtcNow = clock.UtcNow.AddHours(1);
        (await Service(check, clock).ExpireStaleAsync()).Should().Be(0);
    }

    private SqlThreeDsService Service(ClearPayDbContext db, TestClock clock) =>
        new(db, new SqlIdempotencyStore(db, clock), clock, _options, NullLogger<SqlThreeDsService>.Instance);
}
