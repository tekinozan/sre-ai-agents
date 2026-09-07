using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Messaging;

// Payloads carried by EventEnvelope. Each is deliberately thin: a message is a
// notification that something happened plus the key needed to read the current
// state, not a replica of that state. A fat payload goes stale in flight and
// tempts consumers into treating Kafka as the system of record, which
// ARCHITECTURE.md 3.2 explicitly rejects.

// The alert-received topic is the one exception to the thin-payload rule above,
// and it carries a CanonicalAlert rather than a payload declared here. Every other
// message points at state the platform has already stored; an accepted alert has
// no stored state yet, so a notification carrying only identifiers would name
// something the consumer cannot go and read.

/// <summary>A new incident was opened.</summary>
public sealed record IncidentCreatedPayload(
    IncidentId IncidentId,
    string Tenant,
    string Source,
    string Fingerprint,
    string Title,
    string Service,
    string Environment,
    IncidentSeverity Severity,
    DateTimeOffset DetectedAt);

/// <summary>An investigation should start or resume for this incident.</summary>
public sealed record InvestigationRequestedPayload(
    IncidentId IncidentId,
    string Tenant,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    int Attempt);

/// <summary>Something changed that subscribers may care about.</summary>
public sealed record IncidentUpdatedPayload(
    IncidentId IncidentId,
    IncidentStatus Status,
    string Reason);

/// <summary>An investigation finished, successfully or otherwise.</summary>
public sealed record IncidentCompletedPayload(
    IncidentId IncidentId,
    IncidentStatus Status,
    InvestigationOutcome Outcome,
    int ReportVersion,
    ErrorCode? ErrorCode);

/// <summary>An approved remediation is ready to execute.</summary>
public sealed record RemediationRequestedPayload(
    IncidentId IncidentId,
    RemediationActionId RemediationActionId,
    ApprovalRequestId ApprovalRequestId);

/// <summary>A remediation attempt finished.</summary>
public sealed record RemediationCompletedPayload(
    IncidentId IncidentId,
    RemediationActionId RemediationActionId,
    bool Succeeded,
    ErrorCode? ErrorCode);

/// <summary>
/// A message that could not be processed after its bounded retries. Carries the
/// original topic, source identity and payload so it can be replayed once the
/// cause is fixed, rather than being lost with a log line.
/// </summary>
public sealed record DeadLetterPayload(
    string OriginalTopic,
    int SourcePartition,
    long SourceOffset,
    string ConsumerName,
    string? OriginalEventType,
    Guid? OriginalEventId,
    string FailureReason,
    ErrorCode ErrorCode,
    int AttemptCount,
    string PayloadJson);
