using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>
/// The correlated result of an investigation, persisted to
/// <c>root_cause_reports</c> and returned by
/// <c>GET /v1/incidents/{id}/report</c>.
/// </summary>
/// <remarks>
/// Versioned rather than overwritten. A retry or a reopen produces version n+1,
/// and the database enforces <c>UNIQUE(incident_id, report_version)</c>
/// (ARCHITECTURE.md 9.3). Keeping earlier versions is what makes it possible to
/// ask why an operator acted on the advice they were given at the time.
/// </remarks>
public sealed class RootCauseReport : Entity<RootCauseReportId>
{
    private const int MaximumSummaryLength = 8192;

    private RootCauseReport()
    {
    }

    private RootCauseReport(RootCauseReportId id, NewRootCauseReport source)
    {
        Id = id;
        IncidentId = source.IncidentId;
        Version = source.Version;
        ConfigurationSnapshotId = source.ConfigurationSnapshotId;
        Outcome = source.Outcome;
        Summary = Guard.AgainstTooLong(
            Guard.AgainstNullOrWhiteSpace(source.Summary, DomainErrorCodes.IncidentFieldRequired),
            MaximumSummaryLength,
            DomainErrorCodes.IncidentFieldRequired);
        Category = source.Category;
        Confidence = source.Confidence;
        KeyObservations = source.KeyObservations;
        RankedHypotheses = source.RankedHypotheses;
        EvidenceGaps = source.EvidenceGaps;
        CitedEvidenceIds = source.CitedEvidenceIds;
        ContributingAgentNames = source.ContributingAgentNames;
        CreatedAt = source.CreatedAt.ToUniversalTime();
    }

    public IncidentId IncidentId { get; private set; }

    /// <summary>1-based. Unique per incident.</summary>
    public int Version { get; private set; }

    /// <summary>The release set this report was produced under.</summary>
    public ConfigurationSnapshotId ConfigurationSnapshotId { get; private set; }

    public InvestigationOutcome Outcome { get; private set; }

    public string Summary { get; private set; } = null!;

    /// <summary>Coarse root cause class, scored as top-1/top-k in the eval suite.</summary>
    public string? Category { get; private set; }

    public Confidence Confidence { get; private set; }

    /// <summary>Observations promoted into the report, stored as JSONB.</summary>
    public IReadOnlyList<Observation> KeyObservations { get; private set; } = [];

    /// <summary>Hypotheses ordered most to least likely.</summary>
    public IReadOnlyList<Hypothesis> RankedHypotheses { get; private set; } = [];

    /// <summary>What the platform could not determine, stated rather than glossed over.</summary>
    public IReadOnlyList<string> EvidenceGaps { get; private set; } = [];

    /// <summary>Every evidence id the report relies on.</summary>
    public IReadOnlyList<EvidenceId> CitedEvidenceIds { get; private set; } = [];

    /// <summary>Which agents contributed. Shadow runs are excluded.</summary>
    public IReadOnlyList<string> ContributingAgentNames { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public static RootCauseReport Create(NewRootCauseReport source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Version < 1)
        {
            throw new DomainException(
                DomainErrorCodes.ReportVersionConflict,
                "Report versions start at 1.");
        }

        return new RootCauseReport(RootCauseReportId.New(), source);
    }
}
