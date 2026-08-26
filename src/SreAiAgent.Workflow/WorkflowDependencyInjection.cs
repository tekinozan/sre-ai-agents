using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Workflows;
using SreAiAgent.Workflow.Inline;

namespace SreAiAgent.Workflow;

/// <summary>
/// Registers the workflow engine selected by configuration
/// (docs/DECISIONS.md D08).
/// </summary>
public static class WorkflowDependencyInjection
{
    /// <summary>
    /// Registers the investigation activities and the configured engine.
    /// </summary>
    /// <remarks>
    /// The engine is chosen by explicit configuration rather than inferred from
    /// the hosting environment. Inferring it is how a staging deployment quietly
    /// loses durability, and the inline executor already refuses to construct in
    /// Production as a second guard.
    /// </remarks>
    public static IServiceCollection AddSreWorkflow(this IServiceCollection services, string engine)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IInvestigationActivities, InvestigationActivities>();

        switch (engine)
        {
            case "inline":
                // Singleton because it tracks running investigations in-process;
                // a scoped instance would lose that map between requests and start
                // a second investigation for a redelivered message.
                services.TryAddSingleton<IInvestigationWorkflow, InlineInvestigationWorkflow>();
                break;

            case "temporal":
                throw new NotSupportedException(
                    "The Temporal adapter is not implemented yet. Set Workflow:Engine to 'inline' for "
                    + "local development. Failing here is deliberate: silently falling back to the inline "
                    + "executor would run production investigations without durability.");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(engine), engine, "Workflow:Engine must be 'inline' or 'temporal'.");
        }

        return services;
    }
}
