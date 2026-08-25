using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Evidence;

/// <summary>
/// Everything the tool gateway knows after a successful execution, packaged for
/// <see cref="EvidenceRecord.Create"/>. The gateway is the only component
/// permitted to construct this: evidence must not appear from anywhere except a
/// completed, policy-checked, redacted tool call.
/// </summary>
public sealed record NewEvidence
{
    public required IncidentId IncidentId { get; init; }

    public AgentRunId? AgentRunId { get; init; }

    public required ToolCallId ToolCallId { get; init; }

    public required EvidenceSourceType SourceType { get; init; }

    public required string Provider { get; init; }

    public required string ToolName { get; init; }

    public required string ToolVersion { get; init; }

    public required string Summary { get; init; }

    public required string ContentJson { get; init; }

    public required string ContentHash { get; init; }

    public required RedactionStatus RedactionStatus { get; init; }

    public int RedactedValueCount { get; init; }

    public bool IsTruncated { get; init; }

    public string? RawPayloadReference { get; init; }

    public DateTimeOffset? ObservationWindowStart { get; init; }

    public DateTimeOffset? ObservationWindowEnd { get; init; }

    public required DateTimeOffset CollectedAt { get; init; }
}
