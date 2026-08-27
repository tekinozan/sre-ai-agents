using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.UseCases;

namespace SreAiAgent.Application;

/// <summary>
/// Registers the use cases and the agent runtime (ARCHITECTURE.md 13).
/// </summary>
/// <remarks>
/// Written out rather than scanned, like every other registration in the
/// platform. A handler that runs because its name matched a convention is a
/// handler nobody chose to deploy.
/// </remarks>
public static class ApplicationDependencyInjection
{
    /// <summary>Registers the command handlers and the agent runtime.</summary>
    /// <remarks>
    /// Scoped, not singleton. Every one of these reaches a repository and a unit
    /// of work, which are scoped to a request or to one activity attempt; a
    /// singleton handler would capture the first DbContext it ever saw and keep
    /// using it after the connection behind it had closed.
    /// </remarks>
    public static IServiceCollection AddSreApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<CreateIncidentHandler>();

        services.TryAddScoped<IIncidentRouter, IncidentRouter>();
        services.TryAddScoped<IAgentExecutor, AgentExecutor>();

        return services;
    }

    /// <summary>
    /// Registers the handlers that drive a workflow engine.
    /// </summary>
    /// <remarks>
    /// Separate because not every host has an engine. The alert worker opens
    /// incidents and nothing else; giving it these handlers would mean either
    /// registering a workflow engine in the ingestion process, which is the
    /// coupling ARCHITECTURE.md 19.1 splits the deployments to avoid, or a
    /// container that cannot be built at all.
    ///
    /// Call this only after AddSreWorkflow.
    /// </remarks>
    public static IServiceCollection AddSreInvestigationUseCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<StartInvestigationHandler>();
        services.TryAddScoped<RetryInvestigationHandler>();
        services.TryAddScoped<CancelInvestigationHandler>();
        services.TryAddScoped<ApprovalDecisionHandler>();

        return services;
    }
}
