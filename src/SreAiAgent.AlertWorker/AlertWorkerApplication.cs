using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SreAiAgent.Application;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Observability.Telemetry;

namespace SreAiAgent.AlertWorker;

/// <summary>
/// Composition root for the alert ingestion worker (ARCHITECTURE.md 19.1).
/// </summary>
public static class AlertWorkerApplication
{
    public static WebApplication CreateApp(string[]? args = null, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args ?? []);

        // A health listener, not an API. Kubernetes cannot restart a hung consumer it
        // has no way to ask about, and this host has no other HTTP surface.
        builder.WebHost.UseUrls(builder.Configuration["Health:Url"] ?? "http://0.0.0.0:8081");

        builder.Services
            .AddSreConfiguration(builder.Configuration)
            .AddSreRegistries()
            .AddSreMetrics()
            .AddSreLlm()
            .AddSreObservability(builder.Environment)
            .AddSreTools()
            .AddSrePersistence()
            .AddSreApplication()
            .AddSreAlertIngestion()
            .AddSreAlertConsumer()
            .AddSreOutboxDispatcher()
            .AddSreHealthChecks()
            .AddSreRetention()
            .AddSreTelemetry(builder.Configuration, builder.Environment);

        builder.Services.Configure<HostOptions>(static options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(30));

        configure?.Invoke(builder);

        WebApplication app = builder.Build();

        app.MapGet("/health/live", static () => Results.Ok(new { status = "live" }));
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("ready"),
        });

        return app;
    }
}
