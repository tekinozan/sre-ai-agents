using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Whether an agent may run right now, for this incident (ARCHITECTURE.md 24.3).
/// </summary>
/// <remarks>
/// This answers availability, not authorization. A flag decides whether an agent
/// participates; the policy engine decides what it is allowed to do once it does.
/// Section 24.4 is explicit that <c>enabled: true</c> never implies permission to
/// write, and keeping the two in separate components is what stops a flag flip
/// from becoming a privilege escalation.
/// </remarks>
public interface IAgentFlagEvaluator
{
    /// <summary>Evaluates availability against the incident and the pinned snapshot.</summary>
    ValueTask<AgentAvailability> EvaluateAsync(
        AgentDefinition agent,
        IncidentSnapshot incident,
        ConfigurationSnapshot snapshot,
        CancellationToken cancellationToken);
}

/// <summary>The result of a flag evaluation.</summary>
/// <param name="AgentName">Agent evaluated.</param>
/// <param name="Version">Version evaluated.</param>
/// <param name="State">The resulting availability.</param>
/// <param name="Reason">
/// Why, in operator-readable terms. Written to the audit trail so "the database
/// expert did not run" always has an answer.
/// </param>
/// <param name="EvaluatedAt">When the decision was made, UTC.</param>
public sealed record AgentAvailability(
    string AgentName,
    string Version,
    AgentAvailabilityState State,
    string? Reason,
    DateTimeOffset EvaluatedAt)
{
    /// <summary>True when the agent should be selected for normal execution.</summary>
    public bool IsSelectable => State is AgentAvailabilityState.Active
        or AgentAvailabilityState.Canary
        or AgentAvailabilityState.ReadOnly;

    /// <summary>True when it runs but its output must not reach the report.</summary>
    public bool IsShadow => State is AgentAvailabilityState.Shadow;
}

/// <summary>Availability states from ARCHITECTURE.md 24.1.</summary>
public enum AgentAvailabilityState
{
    /// <summary>Never selected.</summary>
    Disabled = 0,

    /// <summary>Runs, but its result reaches evaluation only, never the report.</summary>
    Shadow = 1,

    /// <summary>Active for a limited set of tenants, environments or a percentage.</summary>
    Canary = 2,

    /// <summary>Active but restricted to safe read-only tools.</summary>
    ReadOnly = 3,

    /// <summary>Fully active.</summary>
    Active = 4,

    /// <summary>
    /// Stopped for an urgent safety, quality or cost reason. The one flag state
    /// that overrides a pinned snapshot and can halt work already in flight
    /// (ARCHITECTURE.md 24.5).
    /// </summary>
    KillSwitched = 5,
}
