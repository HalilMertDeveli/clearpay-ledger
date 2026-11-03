using ClearPay.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ClearPay.Tests.Support;

/// <summary>
/// Runs only when <c>CLEARPAY_TEST_SQLSERVER</c> holds a SQL Server connection string
/// (server + credentials, no database). CI provides it through a
/// <c>mcr.microsoft.com/mssql/server</c> service container; locally see README "Tests &amp; CI".
/// Without it the test is reported as skipped, never as passed.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CLEARPAY_TEST_SQLSERVER";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"SQL Server integration test: set {EnvironmentVariable} to run it.";
    }
}

/// <summary>
/// Creates a throw-away database on the configured SQL Server, boots the real host
/// against it (EF migrations for Identity + ledger, Serializable transactions,
/// unique indexes, CHECK constraints) and drops the database afterwards.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private SqlServerWebFactory? _factory;

    public string? ConnectionString { get; private set; }

    public SqlServerWebFactory Factory =>
        _factory ?? throw new InvalidOperationException("SQL Server is not configured.");

    public Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(server))
            return Task.CompletedTask;

        var builder = new SqlConnectionStringBuilder(server)
        {
            InitialCatalog = "ClearPayTest_" + Guid.NewGuid().ToString("N")[..12],
            TrustServerCertificate = true
        };
        ConnectionString = builder.ConnectionString;
        _factory = new SqlServerWebFactory(ConnectionString);
        _ = _factory.Server; // boot the host: runs Identity + ledger migrations
        return Task.CompletedTask;
    }

    public ClearPayDbContext CreateContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ClearPayDbContext>()
            .UseSqlServer(ConnectionString ?? throw new InvalidOperationException("SQL Server is not configured."));
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new ClearPayDbContext(options.Options);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (ConnectionString is null)
            return;

        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class SqlServerWebFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public SqlServerWebFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:ClearPay", _connectionString);
        builder.UseSetting("ConnectionStrings:Identity", _connectionString);
        builder.UseSetting("ClearPay:UseSqliteLedger", "false");
        builder.UseSetting("ClearPay:ApplyLedgerMigrations", "true");
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("ConnectionStrings:RabbitMq", "");
        builder.UseSetting("Jwt:SigningKey", ClearPayWebFactory.TestSigningKey);
        builder.UseSetting("Seed:AdminPassword", ClearPayWebFactory.SeedAdminPassword);
        builder.UseSetting("Hangfire:Enabled", "false");
        builder.UseSetting("Firebase:ProjectId", "clearpay-test");
        builder.UseSetting("Firebase:AllowTestTokens", "true");
    }
}
