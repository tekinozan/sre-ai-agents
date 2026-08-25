using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Workflow;

// Inputs and outputs for IInvestigationActivities.
//
// Every type here is a plain immutable record. Activity arguments cross a
// serialization boundary in the Temporal adapter, so anything with behaviour,
// an ambient dependency or a mutable collection would either fail to round-trip
// or, worse, round-trip into something subtly different on replay.

/// <summary>The incident facts an investigation needs, without the aggregate.</summary>
public sealed record IncidentSnapshot(
    IncidentId IncidentId,
    string Tenant,
    string Source,
    string Title,
    string? Description,
    string Service,
    string Environment,
    IncidentSeverity Severity,
    IncidentStatus Status,
    DateTimeOffset DetectedAt,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>The pinned release set, resolved into usable objects.</summary>
public sealed record ResolvedConfiguration(
    ConfigurationSnapshot Snapshot,
    string PromptRelease,
    string ModelRelease,
    string ToolRelease,
    string PolicyRelease);

/// <summary>Input to classification.</summary>
public sealed record ClassifyIncidentInput(
    IncidentSnapshot Incident,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    string CorrelationId,
    DateTimeOffset Deadline);

/// <summary>Input to routing.</summary>
public sealed record SelectAgentsInput(
    IncidentSnapshot Incident,
    IncidentClassification Classification,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    string CorrelationId);

/// <summary>
/// The agents routing chose, plus the ones it rejected and why.
/// </summary>
/// <remarks>
/// Exclusions are returned rather than logged and forgotten. ARCHITECTURE.md
/// 24.3 requires the reason an agent was filtered out to become an audit event,
/// and "the database expert was disabled" is often the answer to why a report
/// looks thin.
/// </remarks>
public sealed record AgentSelection(
    IReadOnlyList<SelectedAgent> Agents,
    IReadOnlyList<ExcludedAgent> Excluded);

/// <summary>One agent chosen to run.</summary>
/// <param name="AgentName">Registry name.</param>
/// <param name="AgentVersion">Pinned version.</param>
/// <param name="IsShadow">
/// True when the run is a shadow evaluation whose output must not influence the
/// report an operator sees (ARCHITECTURE.md 24.6).
/// </param>
public sealed record SelectedAgent(string AgentName, string AgentVersion, bool IsShadow);

/// <summary>An agent that was considered and rejected.</summary>
public sealed record ExcludedAgent(string AgentName, string AgentVersion, string Reason);

/// <summary>Input to one agent execution.</summary>
public sealed record RunAgentInput(
    IncidentSnapshot Incident,
    SelectedAgent Agent,
    IncidentClassification Classification,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    string CorrelationId,
    DateTimeOffset Deadline);

/// <summary>
/// The outcome of one agent execution.
/// </summary>
/// <remarks>
/// Never a failed <see cref="Result{T}"/>. A failing agent is an expected
/// outcome of a fan-out, and the workflow needs the failure as data so the report
/// can say which expert could not contribute; an exception would collapse the
/// whole parallel step.
/// </remarks>
public sealed record AgentRunResult(
    string AgentName,
    string AgentVersion,
    AgentRunId AgentRunId,
    AgentRunStatus Status,
    bool IsShadow,
    AgentFinding? Finding,
    IReadOnlyList<EvidenceId> EvidenceIds,
    ErrorCode? ErrorCode,
    string? ErrorMessage);

/// <summary>Input to correlation. Shadow runs are filtered out before this point.</summary>
public sealed record CorrelateFindingsInput(
    IncidentSnapshot Incident,
    IReadOnlyList<AgentRunResult> AgentRuns,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    string CorrelationId,
    DateTimeOffset Deadline);

/// <summary>The correlated explanation.</summary>
public sealed record CorrelationResult(
    string Summary,
    string? Category,
    Confidence Confidence,
    IReadOnlyList<Observation> KeyObservations,
    IReadOnlyList<Hypothesis> RankedHypotheses,
    IReadOnlyList<string> EvidenceGaps);

/// <summary>Input to recommendation building.</summary>
public sealed record BuildRecommendationsInput(
    IncidentSnapshot Incident,
    CorrelationResult Correlation,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    string CorrelationId,
    DateTimeOffset Deadline);

/// <summary>Ranked recommendations, most important first.</summary>
public sealed record RecommendationSet(IReadOnlyList<ProposedRecommendation> Recommendations);

/// <summary>A recommendation before it is persisted.</summary>
public sealed record ProposedRecommendation(
    int Rank,
    string Title,
    string Rationale,
    RiskLevel Risk,
    Confidence Confidence,
    IReadOnlyList<EvidenceId> EvidenceIds);

/// <summary>Input to the approval decision.</summary>
public sealed record DecideApprovalInput(
    IncidentSnapshot Incident,
    RecommendationSet Recommendations,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    string CorrelationId);

/// <summary>Whether the investigation must pause for a human.</summary>
public sealed record ApprovalDecision(
    bool IsApprovalRequired,
    ApprovalRequestId? ApprovalRequestId,
    string Reason);

/// <summary>Input to report persistence.</summary>
public sealed record PersistReportInput(
    IncidentId IncidentId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    InvestigationOutcome Outcome,
    CorrelationResult Correlation,
    RecommendationSet Recommendations,
    IReadOnlyList<string> ContributingAgents,
    IReadOnlyList<EvidenceId> CitedEvidenceIds,
    string CorrelationId);

/// <summary>The persisted report.</summary>
public sealed record PersistedReport(RootCauseReportId ReportId, int Version);

/// <summary>Input to completion.</summary>
public sealed record CompleteInvestigationInput(
    IncidentId IncidentId,
    InvestigationOutcome Outcome,
    int ReportVersion,
    string CorrelationId);

/// <summary>Input to failure recording.</summary>
public sealed record FailInvestigationInput(
    IncidentId IncidentId,
    ErrorCode ErrorCode,
    string Reason,
    string CorrelationId);
