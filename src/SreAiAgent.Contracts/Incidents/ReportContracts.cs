using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Incidents;

/// <summary>An observation as returned to a caller.</summary>
/// <param name="Statement">The claim.</param>
/// <param name="EvidenceIds">Evidence supporting it, resolvable via the evidence endpoint.</param>
/// <param name="IsVerified">
/// False when the platform could not substantiate the claim. Surfaced rather
/// than hidden so an operator can weigh it accordingly.
/// </param>
public sealed record ObservationResponse(
    string Statement,
    IReadOnlyList<EvidenceId> EvidenceIds,
    bool IsVerified);

/// <summary>A candidate explanation with the evidence on both sides.</summary>
public sealed record HypothesisResponse(
    string Statement,
    IReadOnlyList<EvidenceId> SupportingEvidenceIds,
    IReadOnlyList<EvidenceId> ContradictingEvidenceIds,
    decimal Confidence);

/// <summary>A proposed next step.</summary>
public sealed record RecommendationResponse(
    RecommendationId RecommendationId,
    int Rank,
    string Title,
    string Rationale,
    RiskLevel Risk,
    decimal Confidence,
    IReadOnlyList<EvidenceId> EvidenceIds,
    bool RequiresApproval);

/// <summary>
/// Body of <c>GET /v1/incidents/{id}/report</c>.
/// </summary>
/// <remarks>
/// Contains no reasoning trace. ARCHITECTURE.md 7.3 and 12.1 are explicit that
/// chain-of-thought is neither stored nor exposed; what a caller receives is the
/// summary, the citations, the hypotheses, the stated uncertainties and the
/// recommendations.
/// </remarks>
public sealed record RootCauseReportResponse
{
    public required RootCauseReportId ReportId { get; init; }

    public required IncidentId IncidentId { get; init; }

    public required int Version { get; init; }

    public required InvestigationOutcome Outcome { get; init; }

    public required string Summary { get; init; }

    public string? Category { get; init; }

    public required decimal Confidence { get; init; }

    public IReadOnlyList<ObservationResponse> KeyObservations { get; init; } = [];

    public IReadOnlyList<HypothesisResponse> RankedHypotheses { get; init; } = [];

    /// <summary>What could not be determined, stated instead of glossed over.</summary>
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];

    public IReadOnlyList<RecommendationResponse> Recommendations { get; init; } = [];

    public IReadOnlyList<string> ContributingAgents { get; init; } = [];

    /// <summary>The release set that produced this report, for reproducibility.</summary>
    public required ConfigurationSnapshotId ConfigurationSnapshotId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
