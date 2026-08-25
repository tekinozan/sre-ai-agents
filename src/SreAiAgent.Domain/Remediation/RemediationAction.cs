using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Remediation;

/// <summary>
/// A write action against a real system, persisted to <c>remediation_actions</c>.
/// </summary>
/// <remarks>
/// The table and the invariants exist in V1 even though V1 executes nothing
/// (ARCHITECTURE.md 20). The rule that matters is structural rather than
/// scheduled: an action cannot be constructed without the id of an approval that
/// was actually granted, so there is no code path that reaches execution without
/// one. Adding execution later does not require relaxing anything here.
/// </remarks>
public sealed class RemediationAction : Entity<RemediationActionId>
{
    private const int MaximumTextLength = 4096;

    private RemediationAction()
    {
    }

    private RemediationAction(
        RemediationActionId id,
        IncidentId incidentId,
        ApprovalRequestId approvalRequestId,
        string toolName,
        string toolVersion,
        string argumentsJson,
        RiskLevel risk,
        string authorizedBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        IncidentId = incidentId;
        ApprovalRequestId = approvalRequestId;
        ToolName = Guard.AgainstNullOrWhiteSpace(toolName, DomainErrorCodes.IncidentFieldRequired);
        ToolVersion = Guard.AgainstNullOrWhiteSpace(toolVersion, DomainErrorCodes.IncidentFieldRequired);
        ArgumentsJson = argumentsJson;
        Risk = Guard.AgainstUndefined(risk, DomainErrorCodes.RemediationRiskNotPermitted);
        AuthorizedBy = Guard.AgainstNullOrWhiteSpace(authorizedBy, DomainErrorCodes.ApprovalRequired);
        Status = RemediationActionStatus.Pending;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public IncidentId IncidentId { get; private set; }

    /// <summary>The granted approval that authorizes this action.</summary>
    public ApprovalRequestId ApprovalRequestId { get; private set; }

    public string ToolName { get; private set; } = null!;

    public string ToolVersion { get; private set; } = null!;

    /// <summary>The typed, schema-validated arguments, as JSONB.</summary>
    public string ArgumentsJson { get; private set; } = null!;

    public RiskLevel Risk { get; private set; }

    /// <summary>Identity of the human who approved it.</summary>
    public string AuthorizedBy { get; private set; } = null!;

    /// <summary>
    /// Idempotency key sent to the target system. ARCHITECTURE.md 14 requires
    /// one for any retryable external write.
    /// </summary>
    public string? IdempotencyKey { get; private set; }

    public RemediationActionStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Redacted outcome description.</summary>
    public string? ResultSummary { get; private set; }

    public ErrorCode? ErrorCode { get; private set; }

    /// <summary>
    /// Records an authorized action. The approval must already be granted; this
    /// is the single construction path, so an unapproved action is not
    /// representable.
    /// </summary>
    /// <exception cref="DomainException">The approval was not granted.</exception>
    public static RemediationAction FromApproval(
        ApprovalRequest approval,
        string toolName,
        string toolVersion,
        string argumentsJson,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(approval);

        if (approval.Status is not ApprovalStatus.Approved)
        {
            throw new DomainException(
                DomainErrorCodes.ApprovalRequired,
                $"A remediation action requires a granted approval but request {approval.Id} is {approval.Status}.");
        }

        return new RemediationAction(
            RemediationActionId.New(),
            approval.IncidentId,
            approval.Id,
            toolName,
            toolVersion,
            argumentsJson,
            approval.Risk,
            approval.DecidedBy!,
            createdAt);
    }

    public void Start(string idempotencyKey, DateTimeOffset now)
    {
        if (Status is not RemediationActionStatus.Pending)
        {
            throw new DomainException(
                DomainErrorCodes.ApprovalInvalidState,
                $"A remediation action can only start from Pending but is {Status}.");
        }

        IdempotencyKey = Guard.AgainstNullOrWhiteSpace(idempotencyKey, DomainErrorCodes.ApprovalInvalidState);
        Status = RemediationActionStatus.Executing;
        StartedAt = now.ToUniversalTime();
    }

    public void Succeed(string resultSummary, DateTimeOffset now)
    {
        Status = RemediationActionStatus.Succeeded;
        ResultSummary = Guard.AgainstTooLong(
            resultSummary ?? string.Empty,
            MaximumTextLength,
            DomainErrorCodes.IncidentFieldRequired);
        CompletedAt = now.ToUniversalTime();
    }

    public void Fail(ErrorCode errorCode, string reason, DateTimeOffset now)
    {
        Status = RemediationActionStatus.Failed;
        ErrorCode = errorCode;
        ResultSummary = Guard.AgainstTooLong(
            reason ?? string.Empty,
            MaximumTextLength,
            DomainErrorCodes.IncidentFieldRequired);
        CompletedAt = now.ToUniversalTime();
    }

    /// <summary>
    /// Records that policy refused the action at execution time. An approval is
    /// permission from a human; it is not a bypass of the policy engine
    /// (ARCHITECTURE.md 24.4).
    /// </summary>
    public void Deny(ErrorCode errorCode, string reason, DateTimeOffset now)
    {
        Status = RemediationActionStatus.Denied;
        ErrorCode = errorCode;
        ResultSummary = Guard.AgainstTooLong(
            reason ?? string.Empty,
            MaximumTextLength,
            DomainErrorCodes.IncidentFieldRequired);
        CompletedAt = now.ToUniversalTime();
    }
}
