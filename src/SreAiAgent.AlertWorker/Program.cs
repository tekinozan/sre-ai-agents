using SreAiAgent.Application;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Observability.Telemetry;

// Composition root for the alert ingestion worker (ARCHITECTURE.md 19.1).
//
// It consumes accepted alerts and opens incidents, using the same handler the
// HTTP path uses. It deliberately hosts no workflow engine and no agent
// execution: ingestion has to stay responsive under a webhook storm, and sharing
// a process with an LLM fan-out is how a burst of alerts becomes a burst of
// timeouts.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

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
    .AddSreTelemetry(builder.Configuration, builder.Environment);

// In-flight work is given time to reach a safe point before the process exits
// (ARCHITECTURE.md 19.2). The default five seconds is shorter than a database
// round trip under load, which turns a rolling deploy into a stream of
// redeliveries.
builder.Services.Configure<HostOptions>(static options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(30));

using IHost host = builder.Build();

// The host installs its own SIGTERM/SIGINT handling for graceful shutdown, so no
// external token participates here.
await host.RunAsync(CancellationToken.None).ConfigureAwait(false);
