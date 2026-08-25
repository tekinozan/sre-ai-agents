using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Incidents;

/// <summary>
/// One evidence item from <c>GET /v1/incidents/{id}/evidence</c>.
/// </summary>
/// <remarks>
/// The redaction status and counter are exposed deliberately. An operator
/// reading a report needs to know that a value was removed from the content they
/// are looking at, and an auditor needs to distinguish "clean" from "never
/// inspected" (ARCHITECTURE.md 15.3).
/// </remarks>
public sealed record EvidenceResponse
{
    public required EvidenceId EvidenceId { get; init; }

    public required EvidenceSourceType SourceType { get; init; }

    public required string Provider { get; init; }

    public required string ToolName { get; init; }

    public required string ToolVersion { get; init; }

    public required string Summary { get; init; }

    /// <summary>Redacted, size-capped structured content.</summary>
    public required string ContentJson { get; init; }

    /// <summary>SHA-256 of the content as stored, so a reader can verify it.</summary>
    public required string ContentHash { get; init; }

    public required RedactionStatus RedactionStatus { get; init; }

    public required int RedactedValueCount { get; init; }

    public required bool IsTruncated { get; init; }

    public DateTimeOffset? ObservationWindowStart { get; init; }

    public DateTimeOffset? ObservationWindowEnd { get; init; }

    public required DateTimeOffset CollectedAt { get; init; }
}
