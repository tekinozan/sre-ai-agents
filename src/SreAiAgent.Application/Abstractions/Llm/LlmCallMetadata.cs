using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>
/// Attribution for one model call: who asked, for which incident, under which
/// release. Written to <c>llm_calls</c> and used for cost accounting and span
/// attributes.
/// </summary>
/// <remarks>
/// Carries identifiers and a hash only. The prompt text is deliberately absent,
/// so it cannot reach a log field or a span attribute by accident
/// (ARCHITECTURE.md 16.1).
/// </remarks>
public sealed record LlmCallMetadata
{
    public required IncidentId IncidentId { get; init; }

    public AgentRunId? AgentRunId { get; init; }

    public required string AgentName { get; init; }

    public required string PromptName { get; init; }

    public required string PromptVersion { get; init; }

    /// <summary>SHA-256 of the rendered prompt. Identifies it without revealing it.</summary>
    public required string PromptHash { get; init; }

    public required string Tenant { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Attempt number, so retries stay distinguishable in the audit trail.</summary>
    public int Attempt { get; init; } = 1;
}
