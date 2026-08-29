using SreAiAgent.Application;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Observability;
using SreAiAgent.Workflow;

// Composition root for the investigation worker (ARCHITECTURE.md 19.1).
//
// This is the process that actually spends money: it runs the agents, calls the
// model and queries the observability backends. Isolating it is what keeps a
// long fan-out from affecting API latency, and it is the only host that both
// consumes investigation requests and executes workflows.

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
    .AddSreWorkflow(builder.Configuration)
    .AddSreInvestigationUseCases()
    .AddSreWorkflowWorker(builder.Configuration)
    .AddSreInvestigationConsumer()
    .AddSreOutboxDispatcher()
    .AddSreTelemetry(builder.Configuration, builder.Environment);

// Longer than the alert worker's. An investigation activity can be mid-model
// call, and cutting it off loses the tokens already paid for as well as the
// evidence gathered so far (ARCHITECTURE.md 19.2).
builder.Services.Configure<HostOptions>(static options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(60));

using IHost host = builder.Build();

await host.RunAsync(CancellationToken.None).ConfigureAwait(false);
