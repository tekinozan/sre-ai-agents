namespace SreAiAgent.Infrastructure.Persistence.Entities;

// The audit trail of ARCHITECTURE.md 9.2: one row for every tool execution and
// every model call, whatever the outcome.
//
// These are append-only and deliberately record the request rather than the
// result. A tool result is already stored as redacted evidence, and a model
// response is the one thing the platform must never keep: section 16.1 forbids
// prompts and completions on spans for the same reason they do not belong in a
// table anyone can query. What is kept is enough to answer "what did this agent
// ask for, under which release, and what did it cost".

/// <summary>One tool execution attempt, including refusals.</summary>
/// <remarks>
/// Denials are recorded, not just successes. "The agent tried to reach the
/// Kubernetes API and was refused" is the single most useful line in a
/// post-incident review of the platform itself, and a trail that only holds
/// successes cannot produce it.
/// </remarks>
public sealed class ToolCallEntity
{
    public Guid Id { get; set; }

    public Guid IncidentId { get; set; }

    /// <summary>Null when a tool ran outside an agent, such as a manual probe.</summary>
    public Guid? AgentRunId { get; set; }

    public string ToolName { get; set; } = string.Empty;

    public string ToolVersion { get; set; } = string.Empty;

    public string AgentName { get; set; } = string.Empty;

    /// <summary><c>succeeded</c>, <c>denied</c> or <c>failed</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Stable error code when the call did not succeed.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The arguments the agent asked for, as JSON.
    /// </summary>
    /// <remarks>
    /// Kept because they are the request, not the response: a denied call's
    /// arguments are exactly what someone reviewing a refusal needs, and they are
    /// already constrained by the tool's own schema and the argument policy.
    /// </remarks>
    public string ArgumentsJson { get; set; } = "{}";

    public int DurationMilliseconds { get; set; }

    public DateTimeOffset ExecutedAt { get; set; }
}

/// <summary>One model call, for cost accounting and reproducibility.</summary>
/// <remarks>
/// The prompt hash is stored rather than the prompt. It identifies exactly which
/// rendered prompt produced a result, so a regression can be attributed to a
/// prompt release, without putting the retrieved evidence into a second place
/// that has to be secured and redacted (ARCHITECTURE.md 8.1).
/// </remarks>
public sealed class LlmCallEntity
{
    public Guid Id { get; set; }

    public Guid IncidentId { get; set; }

    public Guid? AgentRunId { get; set; }

    public string AgentName { get; set; } = string.Empty;

    public string PromptName { get; set; } = string.Empty;

    public string PromptVersion { get; set; } = string.Empty;

    public string PromptHash { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    public Guid AssignmentReleaseId { get; set; }
    public string AssignmentHash { get; set; } = string.Empty;
    public Guid DeploymentVersionId { get; set; }
    public string DeploymentHash { get; set; } = string.Empty;
    public Guid ProviderRevisionId { get; set; }
    public string ProviderRevisionHash { get; set; } = string.Empty;
    public string RequestedProvider { get; set; } = string.Empty;
    public string RequestedModel { get; set; } = string.Empty;
    public string ObservedProvider { get; set; } = string.Empty;
    public string ObservedModel { get; set; } = string.Empty;
    public string ProviderRequestId { get; set; } = string.Empty;
    public string FinishReason { get; set; } = string.Empty;

    /// <summary><c>succeeded</c> or <c>failed</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal EstimatedCost { get; set; }

    public int DurationMilliseconds { get; set; }

    public DateTimeOffset CalledAt { get; set; }
}
