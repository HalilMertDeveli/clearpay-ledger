using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace ClearPay.ServiceDefaults;

/// <summary>
/// One call per host: structured logs (Serilog -> console + Seq) carrying TraceId/SpanId, and
/// OpenTelemetry traces + metrics over OTLP. A payment is one trace: HTTP request -> PSP calls -> SQL ->
/// (later) outbox publish -> RabbitMQ -> consumer in the Operations service -> its SQL.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>Activity sources ClearPay code writes spans to (payments, PSP calls, outbox, messaging).</summary>
    public static readonly string[] ActivitySources = { "ClearPay.Payments", "ClearPay.Outbox", "ClearPay.Messaging" };

    /// <summary>Meter with the business counters (payments, PSP outcomes, reconciliation mismatches).</summary>
    public const string MeterName = "ClearPay";

    public static WebApplicationBuilder AddClearPayObservability(this WebApplicationBuilder builder, string serviceName)
    {
        var configuration = builder.Configuration;
        var seqUrl = configuration["Seq:Url"];
        var otlp = configuration["Otel:Endpoint"];
        var environment = builder.Environment.EnvironmentName;

        builder.Host.UseSerilog((context, services, logger) =>
        {
            logger
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Hangfire", LogEventLevel.Warning)
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName)
                .Enrich.WithProperty("Environment", environment)
                .WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {Service} {Message:lj} {TraceId}{NewLine}{Exception}");
            if (!string.IsNullOrWhiteSpace(seqUrl))
                logger.WriteTo.Seq(seqUrl, apiKey: configuration["Seq:ApiKey"]);
        }, preserveStaticLogger: false);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes(new KeyValuePair<string, object>[] { new("deployment.environment", environment) }))
            .WithTracing(t =>
            {
                t.AddSource(ActivitySources)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                        // Health probes and static files only add noise to traces.
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                                          && !ctx.Request.Path.StartsWithSegments("/api/health")
                                          && !ctx.Request.Path.StartsWithSegments("/css")
                                          && !ctx.Request.Path.StartsWithSegments("/js")
                                          && !ctx.Request.Path.StartsWithSegments("/hangfire");
                    })
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation(o =>
                    {
                        o.RecordException = true;
                    });
                if (!string.IsNullOrWhiteSpace(otlp))
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
            })
            .WithMetrics(m =>
            {
                m.AddMeter(MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlp))
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
            });

        _ = telemetry;
        return builder;
    }

    /// <summary>Request log line per HTTP call (with TraceId) and the trace id echoed to the client.</summary>
    public static WebApplication UseClearPayObservability(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (Activity.Current is { } activity)
                context.Response.Headers["traceparent"] = activity.Id;
            await next();
        });
        app.UseSerilogRequestLogging(o =>
        {
            o.GetLevel = (ctx, _, ex) =>
                ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : ctx.Response.StatusCode >= 400 ? LogEventLevel.Warning
                : ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/api/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        });
        return app;
    }
}
