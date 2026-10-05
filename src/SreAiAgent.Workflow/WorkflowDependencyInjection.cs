using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Workflows;
using SreAiAgent.Workflow.Health;
using SreAiAgent.Workflow.Inline;
using SreAiAgent.Workflow.Temporal;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.OpenTelemetry;

namespace SreAiAgent.Workflow;

/// <summary>
/// Registers the workflow engine selected by configuration
/// (docs/DECISIONS.md D08).
/// </summary>
public static class WorkflowDependencyInjection
{
    private const string InlineEngine = "inline";
    private const string TemporalEngine = "temporal";

    /// <summary>
    /// Registers the investigation activities and a client for the configured
    /// engine.
    /// </summary>
    /// <remarks>
    /// The engine is chosen by explicit configuration rather than inferred from
    /// the hosting environment. Inferring it is how a staging deployment quietly
    /// loses durability, and the inline executor already refuses to construct in
    /// Production as a second guard.
    ///
    /// This registers the ability to start and signal investigations, not to
    /// execute them. The API needs the former and must never do the latter: an
    /// LLM fan-out running inside the request host is exactly the latency
    /// coupling ARCHITECTURE.md 19.1 separates the deployments to avoid.
    /// </remarks>
    public static IServiceCollection AddSreWorkflow(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddScoped<IInvestigationActivities, InvestigationActivities>();

        switch (EngineOf(configuration))
        {
            case InlineEngine:
                // Singleton because it tracks running investigations in-process;
                // a scoped instance would lose that map between requests and start
                // a second investigation for a redelivered message.
                services.TryAddSingleton<IInvestigationWorkflow, InlineInvestigationWorkflow>();
                break;

            case TemporalEngine:
                AddTemporalClient(services, configuration);
                services.TryAddSingleton<IInvestigationWorkflow, TemporalInvestigationWorkflowClient>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Workflow:Engine must be '{InlineEngine}' or '{TemporalEngine}'.");
        }

        return services;
    }

    /// <summary>
    /// Registers the worker that actually executes investigations.
    /// </summary>
    /// <remarks>
    /// Only the investigation worker calls this. Under the inline engine it is a
    /// no-op, because that executor runs the sequence in the process that started
    /// it and has no worker to poll a task queue with.
    /// </remarks>
    public static IServiceCollection AddSreWorkflowWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (EngineOf(configuration) != TemporalEngine)
        {
            return services;
        }

        // No interceptor of its own. The worker runs on the client registered
        // above, and Temporal hands a client's worker interceptors to every
        // worker built from it; a second TracingInterceptor here recorded every
        // workflow and activity span twice.
        services
            .AddHostedTemporalWorker(configuration["Workflow:TaskQueue"] ?? "sre-investigation")
            .AddScopedActivities<InvestigationActivityShims>()
            .AddWorkflow<TemporalInvestigationWorkflow>();

        return services;
    }

    /// <summary>Adds a readiness check for the durable engine, when there is one.</summary>
    public static IServiceCollection AddSreWorkflowHealthCheck(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (EngineOf(configuration) != TemporalEngine)
        {
            return services;
        }

        services.AddHealthChecks().AddCheck<TemporalHealthCheck>("temporal", tags: ["ready"]);

        return services;
    }

    private static string EngineOf(IConfiguration configuration)
        => configuration["Workflow:Engine"] ?? TemporalEngine;

    private static void AddTemporalClient(IServiceCollection services, IConfiguration configuration)
        => services.AddTemporalClient(options =>
        {
            options.TargetHost = configuration["Workflow:TemporalHostPort"] ?? "localhost:7233";
            options.Namespace = configuration["Workflow:TemporalNamespace"] ?? "default";

            // Reflection-based serialization is banned platform-wide, and
            // Temporal's default converter uses it. Pointing it at the generated
            // context keeps workflow history on the same serializer as every
            // other wire boundary.
            options.DataConverter = DataConverter.Default with
            {
                PayloadConverter = new DefaultPayloadConverter(WorkflowPayloadJson.Options),
            };

            // Carries W3C trace context into workflow and activity spans, so an
            // investigation started by an HTTP request stays one trace across the
            // engine (ARCHITECTURE.md 16.1).
            options.Interceptors = [new TracingInterceptor()];
        });
}
