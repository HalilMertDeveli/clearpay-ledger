using System.Globalization;
using System.Text;
using ClearPay.Application.Ports;
using ClearPay.Messaging;
using ClearPay.Operations.Consumers;
using ClearPay.Operations.Data;
using ClearPay.Operations.Notifications;
using ClearPay.Operations.Reconciliation;
using ClearPay.ServiceDefaults;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// ClearPay.Operations: notifications + nightly reconciliation as a separate service.
// In: RabbitMQ events from the ledger host and PSP settlement files. Out: notifications and reconciliation reports.
var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
builder.AddClearPayObservability("clearpay-operations");

var connection = configuration.GetConnectionString("Operations")
    ?? throw new InvalidOperationException("ConnectionStrings:Operations is required (the service's own database).");
var useSqlite = configuration.GetValue("Operations:UseSqlite", false);
builder.Services.AddDbContext<OperationsDbContext>(o =>
{
    if (useSqlite)
        o.UseSqlite(connection);
    else
        o.UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", OperationsDbContext.Schema));
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

// Messaging: same building block as the ledger host, own queues.
var rabbit = RabbitOptions.From(configuration);
builder.Services.AddSingleton(rabbit);
builder.Services.AddSingleton<RabbitConnectionProvider>();
builder.Services.AddSingleton<RabbitEventPublisher>();
builder.Services.AddScoped(typeof(InboxProcessor<>));
builder.Services.AddScoped<PaymentProjectionHandler>();
builder.Services.AddScoped<NotificationHandler>();
builder.Services.AddSingleton(new MessageHandlerRegistry().Add<PaymentProjectionHandler>().Add<NotificationHandler>());
builder.Services.AddSingleton<RabbitConsumerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitConsumerService>());

builder.Services.AddSingleton<INotificationSender, LogNotificationSender>();
builder.Services.AddSingleton<ISettlementFileSource, FileSystemSettlementFileSource>();
builder.Services.AddScoped<ILedgerSettlementSource, ProjectionSettlementSource>();
builder.Services.AddScoped<IReconciliationService, OperationsReconciliationService>();
builder.Services.AddScoped<ReconciliationJob>();

var hangfire = configuration.GetValue("Hangfire:Enabled", true);
if (hangfire)
{
    builder.Services.AddHangfire(c =>
    {
        c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
        if (useSqlite || configuration.GetValue("Hangfire:UseMemoryStorage", false))
            c.UseMemoryStorage();
        else
            c.UseSqlServerStorage(connection, new Hangfire.SqlServer.SqlServerStorageOptions { SchemaName = "ops_hangfire", DisableGlobalLocks = true });
    });
    builder.Services.AddHangfireServer();
}

// Same JWT as the ledger host (issuer, audience, key); only the Admin role may read reports.
var signingKey = configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Jwt:SigningKey is required outside Development.");
    signingKey = "dev-only-placeholder-jwt-key-NOT-A-SECRET-0000"; // public placeholder shared with the ledger host
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = configuration["Jwt:Issuer"] ?? "ClearPay",
        ValidAudience = configuration["Jwt:Audience"] ?? "ClearPay",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
    if (useSqlite)
        await db.Database.EnsureCreatedAsync();
    else if (configuration.GetValue("Operations:ApplyMigrations", true))
        await db.Database.MigrateAsync();
}

if (hangfire)
{
    app.Services.GetRequiredService<IRecurringJobManager>()
        .AddOrUpdate<ReconciliationJob>("clearpay-ops-reconciliation-nightly", j => j.Run(), Cron.Daily(2, 30));
}

app.UseClearPayObservability();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", (RabbitConnectionProvider broker) =>
    Results.Ok(new { status = "ok", service = "ClearPay.Operations", rabbit = broker.Status }));

var api = app.MapGroup("/api").RequireAuthorization("Admin");
api.MapGet("/reconciliation/runs", async (IReconciliationService s, int? take, CancellationToken ct) =>
    Results.Ok(await s.ListAsync(take ?? 20, ct)));
api.MapGet("/reconciliation/runs/{id:guid}", async (IReconciliationService s, Guid id, CancellationToken ct) =>
    await s.GetAsync(id, ct) is { } found ? Results.Ok(new { run = found.Run, items = found.Items }) : Results.NotFound());
api.MapPost("/reconciliation/runs", async (IReconciliationService s, string? date, CancellationToken ct) =>
{
    DateOnly businessDate;
    if (string.IsNullOrWhiteSpace(date))
        businessDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
    else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out businessDate))
        return Results.Problem(title: "Invalid date", detail: "Use yyyy-MM-dd.", statusCode: StatusCodes.Status400BadRequest);
    var summary = await s.RunAsync(businessDate, force: true, ct);
    return Results.Created($"/api/reconciliation/runs/{summary.RunId}", summary);
});
api.MapGet("/notifications", async (OperationsDbContext db, string? recipient, int? take, CancellationToken ct) =>
{
    var query = db.Notifications.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(recipient))
        query = query.Where(n => n.Recipient == recipient);
    var rows = await query.ToListAsync(ct);
    return Results.Ok(rows.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take ?? 50, 1, 200)));
});

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in ClearPay.Operations.Tests.</summary>
public partial class Program;
