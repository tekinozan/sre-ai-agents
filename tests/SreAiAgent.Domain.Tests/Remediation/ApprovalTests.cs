using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Tests.Remediation;

/// <summary>
/// Covers the rule from ARCHITECTURE.md 5.2 that a write or destructive action
/// cannot execute without approval, and the separation in 24.4 between what a
/// human permitted and what policy allows.
/// </summary>
public sealed class ApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    private static ApprovalRequest PendingRequest(RiskLevel risk = RiskLevel.Medium)
        => ApprovalRequest.Create(
            IncidentId.New(),
            recommendationId: null,
            title: "Restart the checkout deployment",
            justification: "pods are wedged on a dead connection pool",
            risk: risk,
            proposedActionJson: "{}",
            requestedAt: Now,
            validFor: TimeSpan.FromMinutes(30));

    [Fact]
    public void RemediationCannotBeBuiltFromAnUndecidedApproval()
    {
        ApprovalRequest request = PendingRequest();

        DomainException exception = Should.Throw<DomainException>(
            () => RemediationAction.FromApproval(request, "kubernetes.restart_deployment", "v1", "{}", Now));

        exception.Code.ShouldBe(DomainErrorCodes.ApprovalRequired);
    }

    [Fact]
    public void RemediationCannotBeBuiltFromARejectedApproval()
    {
        ApprovalRequest request = PendingRequest();
        request.Reject("alice@example.com", "too risky during peak", Now.AddMinutes(1));

        Should.Throw<DomainException>(
            () => RemediationAction.FromApproval(request, "kubernetes.restart_deployment", "v1", "{}", Now));
    }

    [Fact]
    public void ApprovedRequestCarriesTheApproverOntoTheAction()
    {
        ApprovalRequest request = PendingRequest();
        request.Approve("alice@example.com", Now.AddMinutes(1));

        RemediationAction action = RemediationAction.FromApproval(
            request, "kubernetes.restart_deployment", "v1", "{}", Now.AddMinutes(2));

        action.AuthorizedBy.ShouldBe("alice@example.com");
        action.ApprovalRequestId.ShouldBe(request.Id);
        action.Risk.ShouldBe(RiskLevel.Medium);
        action.Status.ShouldBe(RemediationActionStatus.Pending);
    }

    [Fact]
    public void ApprovalAfterTheWindowClosesIsRefusedAndTheRequestExpires()
    {
        ApprovalRequest request = PendingRequest();

        Should.Throw<DomainException>(() => request.Approve("alice@example.com", Now.AddHours(2)));

        request.Status.ShouldBe(ApprovalStatus.Expired);
    }

    [Fact]
    public void ExpiryIsNeverAnImplicitApproval()
    {
        ApprovalRequest request = PendingRequest();

        request.Expire(Now.AddHours(1));

        request.Status.ShouldBe(ApprovalStatus.Expired);
        Should.Throw<DomainException>(
            () => RemediationAction.FromApproval(request, "kubernetes.restart_deployment", "v1", "{}", Now));
    }

    [Fact]
    public void EditingMayLowerRiskButNeverRaiseIt()
    {
        ApprovalRequest request = PendingRequest(RiskLevel.Medium);

        request.Edit("alice@example.com", "{}", RiskLevel.Low, Now.AddMinutes(1));
        request.Risk.ShouldBe(RiskLevel.Low);

        DomainException exception = Should.Throw<DomainException>(
            () => request.Edit("alice@example.com", "{}", RiskLevel.Destructive, Now.AddMinutes(2)));
        exception.Code.ShouldBe(DomainErrorCodes.RemediationRiskNotPermitted);
    }

    [Fact]
    public void AnApprovedActionCanStillBeDeniedByPolicyAtExecutionTime()
    {
        ApprovalRequest request = PendingRequest();
        request.Approve("alice@example.com", Now.AddMinutes(1));
        RemediationAction action = RemediationAction.FromApproval(
            request, "kubernetes.restart_deployment", "v1", "{}", Now.AddMinutes(2));

        action.Deny(new ErrorCode("TOOL_POLICY_DENIED"), "write tools disabled in this environment", Now.AddMinutes(3));

        action.Status.ShouldBe(RemediationActionStatus.Denied);
    }
}
