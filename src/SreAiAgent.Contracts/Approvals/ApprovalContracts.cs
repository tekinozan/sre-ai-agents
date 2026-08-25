using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Approvals;

/// <summary>An approval request as returned by the approvals endpoints.</summary>
public sealed record ApprovalResponse
{
    public required ApprovalRequestId ApprovalRequestId { get; init; }

    public required IncidentId IncidentId { get; init; }

    public RecommendationId? RecommendationId { get; init; }

    public required string Title { get; init; }

    public required string Justification { get; init; }

    public required RiskLevel Risk { get; init; }

    /// <summary>The typed action being proposed, exactly as it would execute.</summary>
    public required string ProposedActionJson { get; init; }

    public required ApprovalStatus Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>After this instant the request can no longer be granted.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public string? DecidedBy { get; init; }

    public string? DecisionReason { get; init; }
}

/// <summary>Body of <c>POST /v1/approvals/{id}/approve</c>.</summary>
/// <param name="Comment">Optional note recorded on the audit trail.</param>
public sealed record ApproveRequest(string? Comment);

/// <summary>Body of <c>POST /v1/approvals/{id}/reject</c>.</summary>
/// <param name="Reason">Required. A rejection without a reason teaches nobody anything.</param>
public sealed record RejectRequest(string Reason);

/// <summary>
/// Body of <c>POST /v1/approvals/{id}/edit</c>: narrow a proposed action before
/// granting it. Risk may only be lowered.
/// </summary>
public sealed record EditApprovalRequest(string ProposedActionJson, RiskLevel Risk);
