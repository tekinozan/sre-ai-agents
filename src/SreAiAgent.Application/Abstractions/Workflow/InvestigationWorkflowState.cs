using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Workflow;

/// <summary>
/// Runtime state of a running investigation (ARCHITECTURE.md 3.3).
/// </summary>
/// <remarks>
/// Deliberately not the <c>Incident</c> aggregate. Workflow state is orchestration
/// bookkeeping that lives in the engine's history; business state lives in
/// PostgreSQL. Conflating them would make the workflow engine the system of
/// record, and recovering an incident would then mean replaying a workflow.
///
/// It is also immutable. Fan-out results are returned by activities and merged
/// here deterministically rather than accumulated in a shared mutable list, which
/// is what makes a replay produce the same state it produced the first time.
/// </remarks>
/// <param name="IncidentId">The incident under investigation.</param>
/// <param name="ConfigurationSnapshotId">The pinned release set.</param>
/// <param name="Classification">Result of classification, once it has run.</param>
/// <param name="SelectedAgents">Agents chosen by routing, in a stable order.</param>
/// <param name="CompletedAgentRuns">Finished runs, ordered by their position in the fan-out.</param>
/// <param name="EvidenceIds">Evidence gathered so far.</param>
/// <param name="Errors">Non-fatal failures, kept so the report can state what went wrong.</param>
/// <param name="Status">Where the workflow currently is.</param>
public sealed record InvestigationWorkflowState(
    IncidentId IncidentId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    IncidentClassification? Classification,
    IReadOnlyList<string> SelectedAgents,
    IReadOnlyList<AgentRunResult> CompletedAgentRuns,
    IReadOnlyList<EvidenceId> EvidenceIds,
    IReadOnlyList<WorkflowError> Errors,
    InvestigationStatus Status)
{
    /// <summary>Initial state, before any activity has run.</summary>
    public static InvestigationWorkflowState Initial(
        IncidentId incidentId,
        ConfigurationSnapshotId snapshotId)
        => new(incidentId, snapshotId, null, [], [], [], [], InvestigationStatus.Started);
}

/// <summary>Where a workflow currently is.</summary>
public enum InvestigationStatus
{
    Started = 0,
    Classifying = 1,
    Routing = 2,
    RunningAgents = 3,
    Correlating = 4,
    AwaitingApproval = 5,
    Remediating = 6,
    Persisting = 7,
    Completed = 8,
    Failed = 9,
    Cancelled = 10,
}

/// <summary>
/// A failure that did not end the investigation.
/// </summary>
/// <remarks>
/// Collected rather than thrown. One agent timing out is not a reason to discard
/// the work of the other four; ARCHITECTURE.md 11.3 asks for a controlled partial
/// result, and the report says which agent failed and why.
/// </remarks>
/// <param name="Stage">Which activity failed, for example <c>agent.execute</c>.</param>
/// <param name="Subject">What it was working on, such as an agent or tool name.</param>
/// <param name="ErrorCode">Stable code from the error taxonomy.</param>
/// <param name="Message">Redacted, operator-facing explanation.</param>
/// <param name="OccurredAt">When it happened, UTC.</param>
public sealed record WorkflowError(
    string Stage,
    string Subject,
    ErrorCode ErrorCode,
    string Message,
    DateTimeOffset OccurredAt);
