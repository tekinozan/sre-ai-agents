using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Evidence;

/// <summary>
/// A single citable observation of the real world, produced by exactly one tool
/// execution. Evidence is the currency of the platform: ARCHITECTURE.md 23 puts
/// "evidence first" as principle one, and an observation without an evidence
/// reference is unverified by definition.
/// </summary>
/// <remarks>
/// Named <c>EvidenceRecord</c> rather than <c>Evidence</c> because the namespace
/// is already <c>SreAiAgent.Domain.Evidence</c>; a type with the same name as its
/// namespace forces awkward qualification at every use site.
/// </remarks>
public sealed class EvidenceRecord : Entity<EvidenceId>
{
    private const int MaximumSummaryLength = 2048;

    private EvidenceRecord()
    {
    }

    private EvidenceRecord(EvidenceId id, NewEvidence source)
    {
        Id = id;
        IncidentId = source.IncidentId;
        AgentRunId = source.AgentRunId;
        ToolCallId = source.ToolCallId;
        SourceType = Guard.AgainstUndefined(source.SourceType, DomainErrorCodes.EvidenceReferenceRequired);
        Provider = Guard.AgainstNullOrWhiteSpace(source.Provider, DomainErrorCodes.EvidenceReferenceRequired);
        ToolName = Guard.AgainstNullOrWhiteSpace(source.ToolName, DomainErrorCodes.EvidenceReferenceRequired);
        ToolVersion = Guard.AgainstNullOrWhiteSpace(source.ToolVersion, DomainErrorCodes.EvidenceReferenceRequired);
        Summary = Guard.AgainstTooLong(source.Summary, MaximumSummaryLength, DomainErrorCodes.EvidenceReferenceRequired);
        ContentJson = source.ContentJson;
        ContentHash = source.ContentHash;
        RedactionStatus = source.RedactionStatus;
        RedactedValueCount = source.RedactedValueCount;
        IsTruncated = source.IsTruncated;
        RawPayloadReference = source.RawPayloadReference;
        ObservationWindowStart = source.ObservationWindowStart?.ToUniversalTime();
        ObservationWindowEnd = source.ObservationWindowEnd?.ToUniversalTime();
        CollectedAt = source.CollectedAt.ToUniversalTime();
    }

    public IncidentId IncidentId { get; private set; }

    /// <summary>The agent execution that requested it. Null for pre-flight collection.</summary>
    public AgentRunId? AgentRunId { get; private set; }

    /// <summary>The audited tool execution that produced it.</summary>
    public ToolCallId ToolCallId { get; private set; }

    public EvidenceSourceType SourceType { get; private set; }

    /// <summary>The concrete backend, for example <c>prometheus</c> or <c>loki</c>.</summary>
    public string Provider { get; private set; } = null!;

    public string ToolName { get; private set; } = null!;

    /// <summary>Pinned tool version, so a replay knows exactly what ran.</summary>
    public string ToolVersion { get; private set; } = null!;

    /// <summary>Short description suitable for a prompt and for the API.</summary>
    public string Summary { get; private set; } = null!;

    /// <summary>Redacted, size-capped structured content, stored as JSONB.</summary>
    public string ContentJson { get; private set; } = null!;

    /// <summary>
    /// SHA-256 of the content as stored. Lets a reviewer prove that the evidence
    /// behind a published report is byte-for-byte what the tool returned.
    /// </summary>
    public string ContentHash { get; private set; } = null!;

    public RedactionStatus RedactionStatus { get; private set; }

    /// <summary>How many values the redaction pass replaced.</summary>
    public int RedactedValueCount { get; private set; }

    /// <summary>True when the result-size cap dropped part of the payload.</summary>
    public bool IsTruncated { get; private set; }

    /// <summary>
    /// Pointer to object storage for payloads too large for the row
    /// (ARCHITECTURE.md 9.1). The pointer is stored, never the blob.
    /// </summary>
    public string? RawPayloadReference { get; private set; }

    public DateTimeOffset? ObservationWindowStart { get; private set; }

    public DateTimeOffset? ObservationWindowEnd { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>
    /// True when this evidence may be placed in a prompt. Content that was never
    /// redacted, or whose redaction failed, is not usable regardless of how
    /// badly an agent wants it.
    /// </summary>
    public bool IsUsableInPrompt
        => RedactionStatus is RedactionStatus.Clean or RedactionStatus.Redacted;

    /// <summary>Creates evidence from a completed, redacted tool execution.</summary>
    public static EvidenceRecord Create(NewEvidence source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new EvidenceRecord(EvidenceId.New(), source);
    }
}
