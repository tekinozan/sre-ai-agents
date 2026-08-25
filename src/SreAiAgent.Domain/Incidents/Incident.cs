using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Incidents;

/// <summary>
/// The incident aggregate (ARCHITECTURE.md 5.2). It owns the lifecycle and
/// nothing else: evidence, findings, reports and approvals are separate
/// aggregates referenced by <see cref="IncidentId"/>, because an investigation
/// writes thousands of evidence rows and loading them to change a status would
/// be indefensible.
/// </summary>
/// <remarks>
/// All timestamps are stored in UTC. The current time is always a parameter
/// rather than an ambient read, which is what makes the lifecycle tests
/// deterministic and is enforced by the banned-API list.
/// </remarks>
public sealed class Incident : Entity<IncidentId>
{
    private const int MaximumTitleLength = 512;
    private const int MaximumReasonLength = 1024;

    /// <summary>Rehydration constructor for the persistence layer.</summary>
    private Incident()
    {
    }

    /// <summary>Opens a new incident from a normalized alert.</summary>
    /// <exception cref="DomainException">A required field is missing.</exception>
    public Incident(IncidentId id, NewIncident alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        Id = id;
        Tenant = Guard.AgainstNullOrWhiteSpace(alert.Tenant, DomainErrorCodes.IncidentFieldRequired);
        Source = Guard.AgainstNullOrWhiteSpace(alert.Source, DomainErrorCodes.IncidentFieldRequired);
        ExternalId = alert.ExternalId ?? string.Empty;
        Fingerprint = Guard.AgainstNullOrWhiteSpace(alert.Fingerprint, DomainErrorCodes.IncidentFingerprintRequired);
        Title = Guard.AgainstTooLong(
            Guard.AgainstNullOrWhiteSpace(alert.Title, DomainErrorCodes.IncidentFieldRequired),
            MaximumTitleLength,
            DomainErrorCodes.IncidentFieldRequired);
        Description = alert.Description;
        Service = Guard.AgainstNullOrWhiteSpace(alert.Service, DomainErrorCodes.IncidentFieldRequired);
        Environment = Guard.AgainstNullOrWhiteSpace(alert.Environment, DomainErrorCodes.IncidentFieldRequired);
        Severity = Guard.AgainstUndefined(alert.Severity, DomainErrorCodes.IncidentFieldRequired);
        Labels = alert.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal);

        DetectedAt = alert.DetectedAt.ToUniversalTime();
        CreatedAt = DetectedAt;
        UpdatedAt = DetectedAt;
        LastAlertAt = DetectedAt;
        AlertCount = 1;
        Status = IncidentStatus.Open;

        Raise(new IncidentOpened(Id, Source, Fingerprint, Service, Environment, Severity, DetectedAt));
    }

    public string Tenant { get; private set; } = null!;

    public string Source { get; private set; } = null!;

    public string ExternalId { get; private set; } = null!;

    public string Fingerprint { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    public string? Description { get; private set; }

    public string Service { get; private set; } = null!;

    public string Environment { get; private set; } = null!;

    public IncidentSeverity Severity { get; private set; }

    public IncidentStatus Status { get; private set; }

    public IReadOnlyDictionary<string, string> Labels { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DateTimeOffset DetectedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset LastAlertAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>How many alerts have folded into this incident.</summary>
    public int AlertCount { get; private set; }

    /// <summary>
    /// The release set pinned when the current investigation started. An
    /// in-flight incident is unaffected by later configuration changes
    /// (ARCHITECTURE.md 24.5).
    /// </summary>
    public ConfigurationSnapshotId? ConfigurationSnapshotId { get; private set; }

    /// <summary>Set while <see cref="Status"/> is <see cref="IncidentStatus.WaitingApproval"/>.</summary>
    public ApprovalRequestId? PendingApprovalRequestId { get; private set; }

    /// <summary>Populated when the incident ends in <see cref="IncidentStatus.Failed"/>.</summary>
    public ErrorCode? FailureCode { get; private set; }

    /// <summary>Safe, non-sensitive explanation of a failure or cancellation.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// Folds a duplicate alert into this incident instead of opening a second
    /// one. Deliberately does not change <see cref="Status"/>: a repeated page
    /// is new information for the timeline, not a lifecycle transition.
    /// </summary>
    public void RecordAlertRecurrence(DateTimeOffset now)
    {
        AlertCount++;
        LastAlertAt = now.ToUniversalTime();
        Touch(now);
        Raise(new IncidentAlertRecurred(Id, Source, AlertCount, LastAlertAt));
    }

    /// <summary>
    /// Raises severity when a later alert is worse than the one that opened the
    /// incident. Severity is never lowered automatically; that is an operator
    /// decision.
    /// </summary>
    public void EscalateSeverity(IncidentSeverity severity, DateTimeOffset now)
    {
        if (severity <= Severity)
        {
            return;
        }

        Severity = Guard.AgainstUndefined(severity, DomainErrorCodes.IncidentFieldRequired);
        Touch(now);
    }

    /// <summary>Starts an investigation against an immutable configuration snapshot.</summary>
    /// <exception cref="InvalidIncidentStateException">The current state forbids it.</exception>
    public void StartInvestigation(ConfigurationSnapshotId snapshotId, DateTimeOffset now)
    {
        // The transition table permits Resolved -> Investigating because Reopen
        // needs it. Reachability and authorization are different questions:
        // reopening a closed incident is a deliberate, audited operation that
        // records an actor and a reason, so StartInvestigation must not become a
        // quiet back door into it (ARCHITECTURE.md 5.2).
        if (IncidentStatePolicy.IsClosed(Status))
        {
            throw new InvalidIncidentStateException(Status, nameof(StartInvestigation));
        }

        Transition(IncidentStatus.Investigating, nameof(StartInvestigation), now);

        ConfigurationSnapshotId = snapshotId;
        PendingApprovalRequestId = null;
        FailureCode = null;
        FailureReason = null;

        Raise(new InvestigationStarted(Id, snapshotId, UpdatedAt));
    }

    /// <summary>Pauses the investigation until a human decides.</summary>
    public void RequireApproval(ApprovalRequestId approvalRequestId, DateTimeOffset now)
    {
        Transition(IncidentStatus.WaitingApproval, nameof(RequireApproval), now);

        PendingApprovalRequestId = approvalRequestId;
        Raise(new IncidentApprovalRequired(Id, approvalRequestId, UpdatedAt));
    }

    /// <summary>Resumes the investigation after an approval was rejected.</summary>
    public void ResumeAfterApprovalRejected(DateTimeOffset now)
    {
        Transition(IncidentStatus.Investigating, nameof(ResumeAfterApprovalRejected), now);
        PendingApprovalRequestId = null;
    }

    /// <summary>Begins executing an approved remediation.</summary>
    public void StartRemediation(ApprovalRequestId approvalRequestId, DateTimeOffset now)
    {
        Transition(IncidentStatus.Remediating, nameof(StartRemediation), now);

        PendingApprovalRequestId = approvalRequestId;
        Raise(new RemediationStarted(Id, approvalRequestId, UpdatedAt));
    }

    /// <summary>Closes the incident with a report.</summary>
    public void MarkResolved(DateTimeOffset now)
    {
        Transition(IncidentStatus.Resolved, nameof(MarkResolved), now);

        ResolvedAt = UpdatedAt;
        PendingApprovalRequestId = null;
        Raise(new IncidentResolved(Id, UpdatedAt));
    }

    /// <summary>
    /// Records that the investigation could not complete. The reason must
    /// already be redacted: it is returned by the API and published to Kafka.
    /// </summary>
    public void MarkFailed(ErrorCode errorCode, string reason, DateTimeOffset now)
    {
        Transition(IncidentStatus.Failed, nameof(MarkFailed), now);

        FailureCode = errorCode;
        FailureReason = Guard.AgainstTooLong(
            reason ?? string.Empty,
            MaximumReasonLength,
            DomainErrorCodes.IncidentFieldRequired);
        PendingApprovalRequestId = null;
        Raise(new IncidentFailed(Id, errorCode, FailureReason, UpdatedAt));
    }

    /// <summary>Abandons the incident. Terminal.</summary>
    public void Cancel(string actor, string reason, DateTimeOffset now)
    {
        Transition(IncidentStatus.Cancelled, nameof(Cancel), now);

        FailureReason = Guard.AgainstTooLong(
            reason ?? string.Empty,
            MaximumReasonLength,
            DomainErrorCodes.IncidentFieldRequired);
        PendingApprovalRequestId = null;
        Raise(new IncidentCancelled(Id, actor, FailureReason, UpdatedAt));
    }

    /// <summary>
    /// Reopens a resolved incident. Explicit and audited: a resolved incident is
    /// never returned to Open implicitly (ARCHITECTURE.md 5.2).
    /// </summary>
    public void Reopen(string actor, string reason, DateTimeOffset now)
    {
        if (Status is not IncidentStatus.Resolved)
        {
            throw new InvalidIncidentStateException(Status, nameof(Reopen));
        }

        Transition(IncidentStatus.Investigating, nameof(Reopen), now);

        ResolvedAt = null;
        FailureCode = null;
        FailureReason = null;
        Raise(new IncidentReopened(Id, actor, reason, UpdatedAt));
    }

    private void Transition(IncidentStatus target, string operation, DateTimeOffset now)
    {
        if (!IncidentStatePolicy.CanTransition(Status, target))
        {
            throw new InvalidIncidentStateException(Status, operation);
        }

        Status = target;
        Touch(now);
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();
}
