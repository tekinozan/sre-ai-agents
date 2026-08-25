using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Chooses which experts investigate an incident (ARCHITECTURE.md 11.2 and
/// 24.3).
/// </summary>
/// <remarks>
/// Deterministic rules run first and a model classifier only fills the gap they
/// leave. Routing on a <c>component=database</c> label is free, instant,
/// explainable and cannot hallucinate; consulting a model to reach the same
/// conclusion costs money and latency on every incident and occasionally gets it
/// wrong.
/// </remarks>
public interface IIncidentRouter
{
    /// <summary>
    /// Selects agents for an incident.
    /// </summary>
    /// <remarks>
    /// Succeeds with an empty selection when no agent qualifies. That is a
    /// reportable outcome, INSUFFICIENT_AGENT_CAPABILITY, rather than a failure:
    /// the operator still needs an incident record explaining that nobody was
    /// available to look at it.
    /// </remarks>
    Task<Result<AgentSelection>> SelectAsync(
        IncidentSnapshot incident,
        IncidentClassification classification,
        ConfigurationSnapshot snapshot,
        CancellationToken cancellationToken);
}
