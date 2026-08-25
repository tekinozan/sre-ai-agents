namespace SreAiAgent.Application.Agents;

/// <summary>
/// An agent as declared by a versioned artifact (ARCHITECTURE.md 7.1).
/// </summary>
/// <remarks>
/// Most agent behaviour is configuration, not a C# class. Adding an expert means
/// adding a definition, a prompt release, a model profile, a tool allowlist, a
/// feature flag and evaluation cases; it does not mean deploying code. That is
/// what makes a controlled rollout possible, and it is why every field below is
/// validated at startup rather than discovered during an incident.
/// </remarks>
public sealed record AgentDefinition
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    /// <summary>Prompt reference in <c>name:version</c> form.</summary>
    public required string PromptRef { get; init; }

    /// <summary>Model profile reference in <c>name:version</c> form.</summary>
    public required string ModelProfileRef { get; init; }

    /// <summary>What the agent knows about, for example <c>database</c> or <c>sql</c>.</summary>
    public required IReadOnlySet<string> Capabilities { get; init; }

    /// <summary>Incident types this agent is a candidate for.</summary>
    public required IReadOnlySet<string> IncidentTypes { get; init; }

    /// <summary>
    /// Tools the agent may request. An allowlist, never a denylist: a new tool is
    /// unavailable to every existing agent until someone grants it deliberately.
    /// </summary>
    public required IReadOnlySet<string> AllowedTools { get; init; }

    public required AgentExecutionBudget Budget { get; init; }

    /// <summary>Name of the schema its output must satisfy, normally <c>AgentFinding</c>.</summary>
    public required string OutputSchema { get; init; }

    /// <summary>Stable reference in <c>name:version</c> form.</summary>
    public string Reference => $"{Name}:{Version}";
}

/// <summary>
/// The ceilings one agent execution may not exceed (ARCHITECTURE.md 11.3).
/// </summary>
/// <remarks>
/// Every field is a hard stop rather than a target. An agent that hits one
/// returns whatever it has as a partial result; the alternative is a loop between
/// a model and a tool that ends only when someone notices the bill.
/// </remarks>
/// <param name="MaxIterations">Reason-and-act cycles allowed.</param>
/// <param name="Timeout">Wall-clock ceiling, below the workflow deadline.</param>
/// <param name="MaxToolCalls">Tool executions allowed.</param>
/// <param name="MaxLlmCalls">Model calls allowed.</param>
/// <param name="MaxTotalTokens">Combined input and output tokens allowed.</param>
/// <param name="MaxEstimatedCost">Spend ceiling in the accounting currency.</param>
public sealed record AgentExecutionBudget(
    int MaxIterations,
    TimeSpan Timeout,
    int MaxToolCalls,
    int MaxLlmCalls,
    long MaxTotalTokens,
    decimal MaxEstimatedCost);
