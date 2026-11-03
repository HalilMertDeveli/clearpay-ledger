using ClearPay.Infrastructure.Persistence;
using ClearPay.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Tests;

/// <summary>Transaction rollback on failure, against an in-memory SQLite database.</summary>
public sealed class TransferRollbackTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public TransferRollbackTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = new ClearPayDbContext(
            new DbContextOptionsBuilder<ClearPayDbContext>().UseSqlite(_connection).Options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public Task Failure_after_rows_reach_the_database_rolls_back_everything_and_the_key_stays_usable() =>
        TransferRollbackScenario.RunAsync(interceptor =>
        {
            var options = new DbContextOptionsBuilder<ClearPayDbContext>().UseSqlite(_connection);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            return new ClearPayDbContext(options.Options);
        });

    public void Dispose() => _connection.Dispose();
}
