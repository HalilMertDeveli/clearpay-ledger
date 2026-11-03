using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ClearPay.Tests;

public sealed class ClearPayWebFactory : WebApplicationFactory<Program>
{
    /// <summary>Test-only HMAC key (public, not a secret).</summary>
    public const string TestSigningKey = "test-only-jwt-signing-key-not-a-secret-0123456789";

    /// <summary>Test-only password for the Development seed admin.</summary>
    public const string SeedAdminPassword = "Deneme123";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var identityPath = Path.Combine(Path.GetTempPath(), $"clearpay-identity-{Guid.NewGuid():N}.db");
        var ledgerPath = Path.Combine(Path.GetTempPath(), $"clearpay-ledger-{Guid.NewGuid():N}.db");
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Identity", $"Data Source={identityPath}");
        builder.UseSetting("ConnectionStrings:ClearPay", $"Data Source={ledgerPath}");
        builder.UseSetting("ClearPay:UseSqliteLedger", "true");
        builder.UseSetting("ClearPay:ApplyLedgerMigrations", "true");
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("ConnectionStrings:RabbitMq", "");
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Seed:AdminPassword", SeedAdminPassword);
        builder.UseSetting("Hangfire:Enabled", "false");
        builder.UseSetting("Firebase:ProjectId", "clearpay-test");
        builder.UseSetting("Firebase:AllowTestTokens", "true");
    }
}
