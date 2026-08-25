using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Workflow;

/// <summary>
/// Starts and signals durable investigations (ARCHITECTURE.md 3.2).
/// </summary>
/// <remarks>
/// The application layer does not know which engine backs this. Temporal is the
/// production choice; a development executor runs the same activities in-process
/// without durability. Keeping the port this narrow is what makes that
/// substitution possible: everything the platform actually needs from a workflow
/// engine, namely resume after restart, fan-out, timeouts, cancellation and
/// signal-driven resume, is expressible in these four methods.
/// </remarks>
public interface IInvestigationWorkflow
{
    /// <summary>
    /// Starts an investigation, or joins the existing one.
    /// </summary>
    /// <remarks>
    /// Idempotent by workflow id, which is derived from the incident id. A
    /// redelivered Kafka message must not launch a second investigation for the
    /// same incident.
    /// </remarks>
    Task<Result> StartAsync(InvestigationWorkflowInput input, CancellationToken cancellationToken);

    /// <summary>Signals an approval decision so a waiting workflow can continue.</summary>
    Task<Result> SignalApprovalDecisionAsync(
        IncidentId incidentId,
        ApprovalDecisionSignal signal,
        CancellationToken cancellationToken);

    /// <summary>Requests cancellation. In-flight activities observe the token.</summary>
    Task<Result> CancelAsync(IncidentId incidentId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Whether an investigation is currently running for this incident, so retry
    /// can refuse rather than start a competing one.
    /// </summary>
    Task<bool> IsRunningAsync(IncidentId incidentId, CancellationToken cancellationToken);
}

/// <summary>What a workflow needs to begin.</summary>
/// <param name="IncidentId">The incident. Also the workflow id and correlation id.</param>
/// <param name="Tenant">Owning tenant, carried into every tool execution context.</param>
/// <param name="ConfigurationSnapshotId">The pinned release set.</param>
/// <param name="Attempt">1 for the first run, higher for a retry.</param>
/// <param name="Deadline">
/// Absolute UTC instant the whole investigation must finish by. The outermost
/// bound of the timeout hierarchy in ARCHITECTURE.md 11.3.
/// </param>
/// <param name="CorrelationId">Correlation id propagated to logs, spans and messages.</param>
public sealed record InvestigationWorkflowInput(
    IncidentId IncidentId,
    string Tenant,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    int Attempt,
    DateTimeOffset Deadline,
    string CorrelationId);

/// <summary>An approval decision delivered to a waiting workflow.</summary>
/// <param name="ApprovalRequestId">Which request was decided.</param>
/// <param name="IsApproved">True to proceed, false to resume without remediating.</param>
/// <param name="Actor">Authenticated approver, recorded on the audit trail.</param>
/// <param name="Reason">Why, required for a rejection.</param>
/// <param name="DecidedAt">When the decision was made, UTC.</param>
public sealed record ApprovalDecisionSignal(
    ApprovalRequestId ApprovalRequestId,
    bool IsApproved,
    string Actor,
    string? Reason,
    DateTimeOffset DecidedAt);
