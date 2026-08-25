using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Remediation;

/// <summary>
/// A request for a human to authorize an action, persisted to
/// <c>approval_requests</c>. The workflow blocks on it and resumes only when a
/// decision is signalled (ARCHITECTURE.md 11.1).
/// </summary>
/// <remarks>
/// A request can be edited before it is granted, so the record shows what was
/// actually authorized rather than what was originally proposed.
/// </remarks>
public sealed class ApprovalRequest : Entity<ApprovalRequestId>
{
    private const int MaximumTitleLength = 512;
    private const int MaximumTextLength = 4096;

    private ApprovalRequest()
    {
    }

    private ApprovalRequest(
        ApprovalRequestId id,
        IncidentId incidentId,
        RecommendationId? recommendationId,
        string title,
        string justification,
        RiskLevel risk,
        string proposedActionJson,
        DateTimeOffset requestedAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        IncidentId = incidentId;
        RecommendationId = recommendationId;
        Title = Guard.AgainstTooLong(
            Guard.AgainstNullOrWhiteSpace(title, DomainErrorCodes.IncidentFieldRequired),
            MaximumTitleLength,
            DomainErrorCodes.IncidentFieldRequired);
        Justification = Guard.AgainstTooLong(justification, MaximumTextLength, DomainErrorCodes.IncidentFieldRequired);
        Risk = Guard.AgainstUndefined(risk, DomainErrorCodes.RemediationRiskNotPermitted);
        ProposedActionJson = proposedActionJson;
        Status = ApprovalStatus.Pending;
        RequestedAt = requestedAt.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    public IncidentId IncidentId { get; private set; }

    public RecommendationId? RecommendationId { get; private set; }

    public string Title { get; private set; } = null!;

    public string Justification { get; private set; } = null!;

    public RiskLevel Risk { get; private set; }

    /// <summary>The typed tool request being proposed, as JSONB.</summary>
    public string ProposedActionJson { get; private set; } = null!;

    public ApprovalStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Authenticated identity of the approver. Required for audit.</summary>
    public string? DecidedBy { get; private set; }

    public string? DecisionReason { get; private set; }

    /// <summary>True while a decision is still possible.</summary>
    public bool IsOpen => Status is ApprovalStatus.Pending;

    public static ApprovalRequest Create(
        IncidentId incidentId,
        RecommendationId? recommendationId,
        string title,
        string justification,
        RiskLevel risk,
        string proposedActionJson,
        DateTimeOffset requestedAt,
        TimeSpan validFor)
        => new(
            ApprovalRequestId.New(),
            incidentId,
            recommendationId,
            title,
            justification,
            risk,
            proposedActionJson,
            requestedAt,
            requestedAt + validFor);

    /// <summary>
    /// Grants the request. Expiry is checked here rather than left to a sweeper,
    /// so a stale decision cannot authorize an action after the window closed
    /// even when the background job is behind.
    /// </summary>
    public void Approve(string approver, DateTimeOffset now)
    {
        RequireOpen(nameof(Approve));

        if (now.ToUniversalTime() > ExpiresAt)
        {
            Expire(now);
            throw new DomainException(
                DomainErrorCodes.ApprovalInvalidState,
                "The approval window has already closed.");
        }

        Status = ApprovalStatus.Approved;
        Decide(approver, reason: null, now);
    }

    public void Reject(string approver, string reason, DateTimeOffset now)
    {
        RequireOpen(nameof(Reject));
        Status = ApprovalStatus.Rejected;
        Decide(approver, reason, now);
    }

    /// <summary>
    /// Narrows the proposed action before it is granted, for the edit-then-approve
    /// flow of <c>POST /v1/approvals/{id}/edit</c>. Risk may only be lowered:
    /// editing must not become a way to escalate past the original review.
    /// </summary>
    public void Edit(string editor, string proposedActionJson, RiskLevel risk, DateTimeOffset now)
    {
        RequireOpen(nameof(Edit));

        if (risk > Risk)
        {
            throw new DomainException(
                DomainErrorCodes.RemediationRiskNotPermitted,
                "An edit may lower the risk of a proposed action but never raise it.");
        }

        ProposedActionJson = proposedActionJson;
        Risk = risk;
        DecidedBy = editor;
        DecidedAt = now.ToUniversalTime();
    }

    /// <summary>Closes an undecided request once its window has passed.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return;
        }

        Status = ApprovalStatus.Expired;
        DecidedAt = now.ToUniversalTime();
    }

    /// <summary>Withdraws the request because the incident no longer needs it.</summary>
    public void Withdraw(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return;
        }

        Status = ApprovalStatus.Withdrawn;
        DecidedAt = now.ToUniversalTime();
    }

    private void Decide(string approver, string? reason, DateTimeOffset now)
    {
        DecidedBy = Guard.AgainstNullOrWhiteSpace(approver, DomainErrorCodes.ApprovalInvalidState);
        DecisionReason = reason is null
            ? null
            : Guard.AgainstTooLong(reason, MaximumTextLength, DomainErrorCodes.ApprovalInvalidState);
        DecidedAt = now.ToUniversalTime();
    }

    private void RequireOpen(string operation)
    {
        if (!IsOpen)
        {
            throw new DomainException(
                DomainErrorCodes.ApprovalInvalidState,
                $"Operation '{operation}' requires a pending approval but the request is {Status}.");
        }
    }
}
