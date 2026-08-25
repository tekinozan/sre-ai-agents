using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Runs one expert agent against one incident (ARCHITECTURE.md 7.2).
/// </summary>
/// <remarks>
/// Returns an <see cref="AgentRunResult"/> rather than throwing, including on
/// failure. An agent is one participant in a fan-out, and its timeout, budget
/// exhaustion or invalid output must not take down the other four.
/// </remarks>
public interface IAgentExecutor
{
    Task<AgentRunResult> ExecuteAsync(AgentContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Everything one agent execution needs, resolved and pinned.
/// </summary>
/// <remarks>
/// The prompt, the model profile and the tool descriptors are all passed in
/// already resolved rather than looked up by the executor. Resolution is the step
/// that turns a release name into concrete artifacts, and doing it once at the
/// start of the run is what guarantees every model call inside that run used the
/// same versions, even if a release is promoted while it is executing.
/// </remarks>
public sealed record AgentContext
{
    public required IncidentSnapshot Incident { get; init; }

    public required AgentDefinition Definition { get; init; }

    public required PromptDefinition Prompt { get; init; }

    public required ModelProfile ModelProfile { get; init; }

    /// <summary>Tool descriptors the agent may use, already intersected with its allowlist.</summary>
    public required IReadOnlyList<ToolDefinition> AvailableTools { get; init; }

    /// <summary>
    /// Evidence gathered earlier in this investigation. Only entries that passed
    /// redaction appear here.
    /// </summary>
    public required IReadOnlyList<EvidenceRecord> ExistingEvidence { get; init; }

    /// <summary>Absolute UTC instant this agent must finish by.</summary>
    public required DateTimeOffset Deadline { get; init; }

    public required string CorrelationId { get; init; }

    public required string Tenant { get; init; }

    public required string Environment { get; init; }

    /// <summary>
    /// The highest risk this agent may reach. In V1 always
    /// <see cref="RiskLevel.ReadOnly"/>.
    /// </summary>
    public required RiskLevel RiskCeiling { get; init; }

    /// <summary>True when the output must not influence the operator-facing report.</summary>
    public required bool IsShadow { get; init; }

    public required ConfigurationSnapshotId ConfigurationSnapshotId { get; init; }
}
