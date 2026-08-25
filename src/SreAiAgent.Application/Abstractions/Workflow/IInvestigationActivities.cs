using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Workflow;

/// <summary>
/// The activity boundaries of ARCHITECTURE.md 11.1, as one interface.
/// </summary>
/// <remarks>
/// Every step is here rather than inside a workflow class, and that placement is
/// the design. The Temporal adapter and the development executor both call these
/// same methods, so the investigation logic is engine-agnostic, unit-testable
/// without a workflow runtime, and identical in both. A workflow class that
/// contained this logic would have to be reimplemented for the second engine and
/// would drift from the first.
///
/// Each method is a single responsibility, cancellation-aware, idempotent with
/// respect to its side effects, and owns a telemetry span.
/// </remarks>
public interface IInvestigationActivities
{
    /// <summary>Loads the incident and confirms it is still worth investigating.</summary>
    Task<Result<IncidentSnapshot>> LoadIncidentAsync(
        IncidentId incidentId,
        CancellationToken cancellationToken);

    /// <summary>Resolves the pinned release set for this investigation.</summary>
    Task<Result<ResolvedConfiguration>> ResolveConfigurationAsync(
        ConfigurationSnapshotId snapshotId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Classifies the incident, trying deterministic rules before any model.
    /// </summary>
    Task<Result<IncidentClassification>> ClassifyIncidentAsync(
        ClassifyIncidentInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Chooses agents that are eligible, available and have their tools present.
    /// Returns an empty selection rather than failing, so the caller can produce
    /// INSUFFICIENT_AGENT_CAPABILITY as a report instead of an exception.
    /// </summary>
    Task<Result<AgentSelection>> SelectAgentsAsync(
        SelectAgentsInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs one agent. The workflow calls this once per selected agent and merges
    /// the returned results deterministically; the activity itself never touches
    /// shared state.
    /// </summary>
    Task<AgentRunResult> RunAgentAsync(RunAgentInput input, CancellationToken cancellationToken);

    /// <summary>Correlates findings into a single explanation.</summary>
    Task<Result<CorrelationResult>> CorrelateFindingsAsync(
        CorrelateFindingsInput input,
        CancellationToken cancellationToken);

    /// <summary>Turns the correlated explanation into ranked recommendations.</summary>
    Task<Result<RecommendationSet>> BuildRecommendationsAsync(
        BuildRecommendationsInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Decides whether a human must authorize anything before completion. In V1
    /// this always answers "no": every tool is read-only, so there is nothing to
    /// approve.
    /// </summary>
    Task<Result<ApprovalDecision>> DecideApprovalAsync(
        DecideApprovalInput input,
        CancellationToken cancellationToken);

    /// <summary>Writes the versioned report and its recommendations.</summary>
    Task<Result<PersistedReport>> PersistReportAsync(
        PersistReportInput input,
        CancellationToken cancellationToken);

    /// <summary>Closes the incident and publishes the completion event.</summary>
    Task<Result> CompleteInvestigationAsync(
        CompleteInvestigationInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a failure without closing the incident, so a retry remains
    /// possible and the timeline explains what happened.
    /// </summary>
    Task<Result> FailInvestigationAsync(
        FailInvestigationInput input,
        CancellationToken cancellationToken);
}
