using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace ClearPay.Tests;

/// <summary>
/// End-to-end checks of the money API through HTTP + JWT against the real EF model
/// (SQLite file database, same migrations-free schema the other integration tests use).
/// Covers authorization, validation, error contract, audit/outbox and ledger invariants.
/// </summary>
public sealed class LedgerSafetyApiTests : IClassFixture<ClearPayWebFactory>
{
    private readonly ClearPayWebFactory _factory;

    public LedgerSafetyApiTests(ClearPayWebFactory factory)
    {
        _factory = factory;
    }

    // ---------- Authorization: a caller can only ever debit their own wallet ----------

    [Fact]
    public async Task Body_fields_naming_another_wallet_are_ignored_and_only_the_caller_is_debited()
    {
        var client = _factory.CreateClient();
        var victim = await ApiSession.RegisterAsync(client, "victim");
        var attacker = await ApiSession.RegisterAsync(client, "attacker");
        var receiver = await ApiSession.RegisterAsync(client, "receiver");
        await ApiSession.TopUpAsync(client, victim, 100m);
        await ApiSession.TopUpAsync(client, attacker, 50m);
        var victimWalletId = await WalletIdAsync(victim.Id);

        var json = JsonSerializer.Serialize(new
        {
            recipient = receiver.Email,
            amount = 20m,
            description = "spoof",
            actorUserId = victim.Id,
            fromUserId = victim.Id,
            fromWalletId = victimWalletId,
            sender = victim.Email
        });
        var response = await ApiSession.PostJsonAsync(
            client, attacker.Token, "/api/transfers", json, ApiSession.NewKey("spoof"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ApiSession.BalanceAsync(client, victim)).Should().Be(100m);
        (await ApiSession.BalanceAsync(client, attacker)).Should().Be(30m);
        (await ApiSession.BalanceAsync(client, receiver)).Should().Be(20m);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        {
            (await db.Transfers.CountAsync(t => t.FromWalletId == victimWalletId)).Should().Be(0);
        }
    }

    [Fact]
    public async Task Caller_without_funds_cannot_spend_another_users_balance()
    {
        var client = _factory.CreateClient();
        var victim = await ApiSession.RegisterAsync(client, "rich");
        var attacker = await ApiSession.RegisterAsync(client, "empty");
        var receiver = await ApiSession.RegisterAsync(client, "emptyto");
        await ApiSession.TopUpAsync(client, victim, 100m);

        var json = JsonSerializer.Serialize(new
        {
            recipient = receiver.Email,
            amount = 60m,
            actorUserId = victim.Id,
            fromUserId = victim.Id
        });
        var response = await ApiSession.PostJsonAsync(
            client, attacker.Token, "/api/transfers", json, ApiSession.NewKey("steal"));

        // The server debits the authenticated caller (balance 0), never the wallet named in the body.
        response.StatusCode.Should().Be((HttpStatusCode)422);
        (await ApiSession.BalanceAsync(client, receiver)).Should().Be(0m);
        (await ApiSession.BalanceAsync(client, victim)).Should().Be(100m);
        (await ApiSession.BalanceAsync(client, attacker)).Should().Be(0m);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_401_and_moves_no_money()
    {
        var client = _factory.CreateClient();
        var victim = await ApiSession.RegisterAsync(client, "forged");
        var receiver = await ApiSession.RegisterAsync(client, "forgedto");
        await ApiSession.TopUpAsync(client, victim, 100m);

        var forged = CreateToken(
            victim.Id,
            victim.Email,
            "attacker-controlled-signing-key-0123456789abcdef",
            DateTime.UtcNow.AddHours(1));

        (await ApiSession.TransferAsync(client, forged, ApiSession.NewKey("forged"), receiver.Email, 10m))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiSession.GetAsync(client, forged, "/api/wallet"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiSession.BalanceAsync(client, victim)).Should().Be(100m);
    }

    [Fact]
    public async Task Expired_token_is_401_and_moves_no_money()
    {
        var client = _factory.CreateClient();
        var user = await ApiSession.RegisterAsync(client, "expired");
        var receiver = await ApiSession.RegisterAsync(client, "expiredto");
        await ApiSession.TopUpAsync(client, user, 100m);

        var expired = CreateToken(
            user.Id,
            user.Email,
            ClearPayWebFactory.TestSigningKey,
            DateTime.UtcNow.AddMinutes(-10));

        (await ApiSession.TransferAsync(client, expired, ApiSession.NewKey("expired"), receiver.Email, 10m))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiSession.BalanceAsync(client, user)).Should().Be(100m);
    }

    [Fact]
    public async Task Receipt_of_someone_elses_transfer_is_404_for_a_stranger()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "rcpt");
        var receiver = await ApiSession.RegisterAsync(client, "rcptto");
        var stranger = await ApiSession.RegisterAsync(client, "rcptx");
        await ApiSession.TopUpAsync(client, sender, 20m);
        var created = await ApiSession.TransferAsync(client, sender.Token, ApiSession.NewKey("rcpt"), receiver.Email, 5m);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var correlationId = body.RootElement.GetProperty("correlationId").GetGuid();

        (await ApiSession.GetAsync(client, sender.Token, $"/api/receipts/{correlationId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiSession.GetAsync(client, receiver.Token, $"/api/receipts/{correlationId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApiSession.GetAsync(client, stranger.Token, $"/api/receipts/{correlationId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ApiSession.GetAsync(client, stranger.Token, $"/api/receipts/{correlationId}/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- Validation ----------

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.005")]
    public async Task Invalid_amount_is_400_problem_and_writes_nothing(string amount)
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "amount");
        var receiver = await ApiSession.RegisterAsync(client, "amountto");
        await ApiSession.TopUpAsync(client, sender, 50m);

        var json = $$"""{"recipient":"{{receiver.Email}}","amount":{{amount}},"description":"x"}""";
        var response = await ApiSession.PostJsonAsync(
            client, sender.Token, "/api/transfers", json, ApiSession.NewKey("amount"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        await AssertNoTransferFromAsync(sender.Id);
        (await ApiSession.BalanceAsync(client, sender)).Should().Be(50m);
    }

    [Fact]
    public async Task Malformed_json_is_400_and_writes_nothing()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "malformed");
        await ApiSession.TopUpAsync(client, sender, 50m);

        var response = await ApiSession.PostJsonAsync(
            client, sender.Token, "/api/transfers", "{\"recipient\": \"x@clearpay.test\", \"amount\": ", ApiSession.NewKey("bad"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoTransferFromAsync(sender.Id);
    }

    [Fact]
    public async Task Idempotency_key_longer_than_the_column_is_400_and_writes_nothing()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "longkey");
        var receiver = await ApiSession.RegisterAsync(client, "longkeyto");
        await ApiSession.TopUpAsync(client, sender, 50m);

        var key = new string('k', LedgerSchema.IdempotencyKeyMaxLength + 1);
        var response = await ApiSession.TransferAsync(client, sender.Token, key, receiver.Email, 5m);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        await AssertNoTransferFromAsync(sender.Id);
        (await ApiSession.BalanceAsync(client, sender)).Should().Be(50m);
    }

    [Fact]
    public async Task Description_longer_than_140_is_400_and_writes_nothing()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "longdesc");
        var receiver = await ApiSession.RegisterAsync(client, "longdescto");
        await ApiSession.TopUpAsync(client, sender, 50m);

        var response = await ApiSession.TransferAsync(
            client, sender.Token, ApiSession.NewKey("desc"), receiver.Email, 5m, new string('d', 141));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoTransferFromAsync(sender.Id);
        (await ApiSession.BalanceAsync(client, sender)).Should().Be(50m);
    }

    [Fact]
    public async Task Unknown_recipient_is_404_and_writes_nothing()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "nobody");
        await ApiSession.TopUpAsync(client, sender, 50m);

        var response = await ApiSession.TransferAsync(
            client, sender.Token, ApiSession.NewKey("nobody"), $"ghost.{Guid.NewGuid():N}@clearpay.test", 5m);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertNoTransferFromAsync(sender.Id);
    }

    // ---------- Error contract ----------

    [Fact]
    public async Task Errors_are_problem_details_and_replay_returns_the_original_ids()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "problem");
        var receiver = await ApiSession.RegisterAsync(client, "problemto");
        await ApiSession.TopUpAsync(client, sender, 30m);

        var poor = await ApiSession.TransferAsync(client, sender.Token, ApiSession.NewKey("poor"), receiver.Email, 31m);
        poor.StatusCode.Should().Be((HttpStatusCode)422);
        poor.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using (var problem = JsonDocument.Parse(await poor.Content.ReadAsStringAsync()))
        {
            problem.RootElement.GetProperty("title").GetString().Should().Be("Insufficient funds");
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(422);
        }

        var key = ApiSession.NewKey("replay");
        var first = await ApiSession.TransferAsync(client, sender.Token, key, receiver.Email, 10m);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        var replay = await ApiSession.TransferAsync(client, sender.Token, key, receiver.Email, 10m);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        replay.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var conflict = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        conflict.RootElement.GetProperty("transferId").GetGuid()
            .Should().Be(created.RootElement.GetProperty("transferId").GetGuid());
        conflict.RootElement.GetProperty("correlationId").GetGuid()
            .Should().Be(created.RootElement.GetProperty("correlationId").GetGuid());
        (await ApiSession.BalanceAsync(client, sender)).Should().Be(20m);
    }

    // ---------- Audit + outbox ----------

    [Fact]
    public async Task Transfer_commits_audit_and_pending_outbox_with_the_ledger_correlation_id()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "audit");
        var receiver = await ApiSession.RegisterAsync(client, "auditto");
        await ApiSession.TopUpAsync(client, sender, 40m);

        var response = await ApiSession.TransferAsync(client, sender.Token, ApiSession.NewKey("audit"), receiver.Email, 15m);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var transferId = body.RootElement.GetProperty("transferId").GetGuid();
        var correlationId = body.RootElement.GetProperty("correlationId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        {
            var transfer = await db.Transfers.AsNoTracking().SingleAsync(t => t.Id == transferId);
            transfer.CorrelationId.Should().Be(correlationId);
            transfer.Status.Should().Be(TransferStatus.Completed);

            var pair = await db.LedgerEntries.AsNoTracking().Where(e => e.TransferId == transferId).ToListAsync();
            pair.Should().HaveCount(2);
            pair.Should().OnlyContain(e => e.CorrelationId == correlationId);
            pair.Sum(e => e.Amount).Should().Be(0m);
            pair.Single(e => e.IsCredit).Amount.Should().Be(15m);

            var audit = await db.AuditLogs.AsNoTracking().Where(a => a.CorrelationId == correlationId).ToListAsync();
            audit.Should().ContainSingle();
            audit[0].Action.Should().Be("transfer");
            audit[0].ActorUserId.Should().Be(sender.Id);

            var outbox = await db.OutboxMessages.AsNoTracking().Where(m => m.CorrelationId == correlationId).ToListAsync();
            outbox.Should().ContainSingle();
            outbox[0].Type.Should().Be("clearpay.transfer.completed");
            outbox[0].Status.Should().Be(OutboxStatus.Pending);
            outbox[0].ProcessedAt.Should().BeNull();
            outbox[0].Payload.Should().Contain(transferId.ToString());
        }
    }

    [Fact]
    public async Task Rejected_transfer_writes_no_audit_and_no_outbox()
    {
        var client = _factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "noaudit");
        var receiver = await ApiSession.RegisterAsync(client, "noauditto");
        await ApiSession.TopUpAsync(client, sender, 10m);

        (await ApiSession.TransferAsync(client, sender.Token, ApiSession.NewKey("no"), receiver.Email, 11m))
            .StatusCode.Should().Be((HttpStatusCode)422);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        {
            (await db.AuditLogs.CountAsync(a => a.ActorUserId == sender.Id && a.Action == "transfer")).Should().Be(0);
            (await db.OutboxMessages.CountAsync(m => m.Type == "clearpay.transfer.completed" && m.Payload.Contains(sender.Id)))
                .Should().Be(0);
        }
    }

    // ---------- Double-entry invariants after a realistic sequence ----------

    [Fact]
    public async Task Ledger_stays_balanced_and_balances_equal_entry_sums_after_mixed_operations()
    {
        var client = _factory.CreateClient();
        var a = await ApiSession.RegisterAsync(client, "mixa");
        var b = await ApiSession.RegisterAsync(client, "mixb");
        var c = await ApiSession.RegisterAsync(client, "mixc");

        await ApiSession.TopUpAsync(client, a, 200m);
        await ApiSession.TopUpAsync(client, b, 15.55m);
        var replayKey = ApiSession.NewKey("mix");
        (await ApiSession.TransferAsync(client, a.Token, replayKey, b.Email, 70.25m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ApiSession.TransferAsync(client, a.Token, replayKey, b.Email, 70.25m)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiSession.TransferAsync(client, b.Token, ApiSession.NewKey("mix"), c.Email, 40m)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ApiSession.TransferAsync(client, c.Token, ApiSession.NewKey("mix"), a.Email, 100m)).StatusCode.Should().Be((HttpStatusCode)422);
        var withdraw = await ApiSession.PostJsonAsync(
            client, c.Token, "/api/withdraw", JsonSerializer.Serialize(new { amount = 12.5m, account = "****4242" }), ApiSession.NewKey("wd"));
        withdraw.StatusCode.Should().Be(HttpStatusCode.Created);

        (await ApiSession.BalanceAsync(client, a)).Should().Be(129.75m);
        (await ApiSession.BalanceAsync(client, b)).Should().Be(45.80m);
        (await ApiSession.BalanceAsync(client, c)).Should().Be(27.50m);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        {
            var all = await db.LedgerEntries.AsNoTracking().ToListAsync();
            all.Should().NotContain(e => e.Amount == 0m);
            all.Sum(e => e.Amount).Should().Be(0m, "sum of debits must equal sum of credits across the whole ledger");
            foreach (var pair in all.GroupBy(e => e.PairId))
            {
                pair.Should().HaveCount(2, "every movement is exactly one debit and one credit");
                pair.Sum(e => e.Amount).Should().Be(0m);
                pair.Select(e => e.CorrelationId).Distinct().Should().ContainSingle();
                pair.Select(e => e.WalletId).Distinct().Should().HaveCount(2);
            }

            foreach (var user in new[] { a, b, c })
            {
                var walletId = (await db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == user.Id)).Id;
                (await ApiSession.BalanceAsync(client, user))
                    .Should().Be(LedgerPair.NetOf(all, walletId), "the API balance is derived from ledger entries");
            }
        }
    }

    private async Task<Guid> WalletIdAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        {
            return (await db.Wallets.AsNoTracking().SingleAsync(w => w.UserId == userId)).Id;
        }
    }

    private async Task AssertNoTransferFromAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        {
            var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId);
            if (wallet is null)
                return;
            (await db.Transfers.CountAsync(t => t.FromWalletId == wallet.Id)).Should().Be(0);
            (await db.LedgerEntries.CountAsync(e => e.WalletId == wallet.Id && e.Kind == LedgerEntryKind.Transfer))
                .Should().Be(0);
        }
    }

    private static string CreateToken(string userId, string email, string signingKey, DateTime expiresUtc)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "ClearPay",
            audience: "ClearPay",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Role, "Musteri")
            ],
            notBefore: expiresUtc.AddHours(-2),
            expires: expiresUtc,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
