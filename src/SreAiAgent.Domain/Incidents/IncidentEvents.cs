using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Incidents;

/// <summary>Base type for everything an incident can announce about itself.</summary>
/// <param name="IncidentId">The incident the event belongs to.</param>
/// <param name="OccurredAt">When the transition happened, in UTC.</param>
public abstract record IncidentDomainEvent(IncidentId IncidentId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

/// <summary>A new incident was opened from an alert.</summary>
public sealed record IncidentOpened(
    IncidentId IncidentId,
    string Source,
    string Fingerprint,
    string Service,
    string Environment,
    IncidentSeverity Severity,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.incident.created";
}

/// <summary>
/// A duplicate alert arrived for an already active incident. Recorded on the
/// timeline instead of opening a second incident (ARCHITECTURE.md 9.3).
/// </summary>
public sealed record IncidentAlertRecurred(
    IncidentId IncidentId,
    string Source,
    int TotalAlertCount,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.incident.updated";
}

/// <summary>An investigation started against a pinned configuration snapshot.</summary>
public sealed record InvestigationStarted(
    IncidentId IncidentId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    // "started", not "requested". The request is a separate message carrying the
    // pinned snapshot and the attempt number, published by the use case for the
    // worker to act on. This event announces that the incident's state changed,
    // and giving both the same wire type put two different payload shapes on one
    // topic for every incident.
    public override string EventType => "sre.incident.investigation.started";
}

/// <summary>The investigation proposed an action that needs human approval.</summary>
public sealed record IncidentApprovalRequired(
    IncidentId IncidentId,
    ApprovalRequestId ApprovalRequestId,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.incident.updated";
}

/// <summary>An approved remediation began executing.</summary>
public sealed record RemediationStarted(
    IncidentId IncidentId,
    ApprovalRequestId ApprovalRequestId,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.remediation.requested";
}

/// <summary>The incident was closed with a report.</summary>
public sealed record IncidentResolved(
    IncidentId IncidentId,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.incident.completed";
}

/// <summary>The investigation could not complete. Retry remains possible.</summary>
public sealed record IncidentFailed(
    IncidentId IncidentId,
    ErrorCode ErrorCode,
    string Reason,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.incident.completed";
}

/// <summary>An operator abandoned the incident.</summary>
public sealed record IncidentCancelled(
    IncidentId IncidentId,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.incident.completed";
}

/// <summary>A resolved incident was deliberately reopened.</summary>
public sealed record IncidentReopened(
    IncidentId IncidentId,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAt) : IncidentDomainEvent(IncidentId, OccurredAt)
{
    public override string EventType => "sre.incident.updated";
}
