using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Recommendations;

/// <summary>
/// A proposed next step attached to a report, persisted to
/// <c>recommendations</c>.
/// </summary>
/// <remarks>
/// A recommendation is advice, never an authorization. Whether it may execute is
/// decided later by the approval policy and the tool gateway; producing one at
/// <see cref="RiskLevel.Destructive"/> does not make it executable, and V1 does
/// not execute anything at all.
/// </remarks>
public sealed class Recommendation : Entity<RecommendationId>
{
    private const int MaximumTitleLength = 512;
    private const int MaximumRationaleLength = 4096;

    private Recommendation()
    {
    }

    private Recommendation(
        RecommendationId id,
        IncidentId incidentId,
        RootCauseReportId reportId,
        int rank,
        string title,
        string rationale,
        RiskLevel risk,
        Confidence confidence,
        IReadOnlyList<EvidenceId> evidenceIds,
        DateTimeOffset createdAt)
    {
        Id = id;
        IncidentId = incidentId;
        RootCauseReportId = reportId;
        Rank = rank;
        Title = Guard.AgainstTooLong(
            Guard.AgainstNullOrWhiteSpace(title, DomainErrorCodes.IncidentFieldRequired),
            MaximumTitleLength,
            DomainErrorCodes.IncidentFieldRequired);
        Rationale = Guard.AgainstTooLong(rationale, MaximumRationaleLength, DomainErrorCodes.IncidentFieldRequired);
        Risk = Guard.AgainstUndefined(risk, DomainErrorCodes.RemediationRiskNotPermitted);
        Confidence = confidence;
        EvidenceIds = evidenceIds;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public IncidentId IncidentId { get; private set; }

    public RootCauseReportId RootCauseReportId { get; private set; }

    /// <summary>1-based ordering, most important first.</summary>
    public int Rank { get; private set; }

    public string Title { get; private set; } = null!;

    public string Rationale { get; private set; } = null!;

    public RiskLevel Risk { get; private set; }

    public Confidence Confidence { get; private set; }

    /// <summary>Evidence justifying the recommendation.</summary>
    public IReadOnlyList<EvidenceId> EvidenceIds { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Anything that writes needs a human. Read-only advice does not.
    /// ARCHITECTURE.md 5.2: a destructive or write action cannot execute without
    /// approval.
    /// </summary>
    public bool RequiresApproval => Risk > RiskLevel.ReadOnly;

    public static Recommendation Create(
        IncidentId incidentId,
        RootCauseReportId reportId,
        int rank,
        string title,
        string rationale,
        RiskLevel risk,
        Confidence confidence,
        IReadOnlyList<EvidenceId> evidenceIds,
        DateTimeOffset createdAt)
        => new(
            RecommendationId.New(),
            incidentId,
            reportId,
            rank,
            title,
            rationale,
            risk,
            confidence,
            evidenceIds ?? [],
            createdAt);
}
