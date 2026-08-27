using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.UseCases;

/// <summary>
/// Records a human decision on a proposed remediation and releases the workflow
/// waiting for it.
/// </summary>
/// <remarks>
/// The decision is written before the signal is sent, and that order is the
/// point. If the process dies between the two, the durable record says a human
/// approved and the workflow can be resignalled; the reverse order would let a
/// remediation proceed on an approval nobody can prove was given.
/// </remarks>
public sealed class ApprovalDecisionHandler(
    IApprovalStore approvals,
    IUnitOfWork unitOfWork,
    IInvestigationWorkflow workflow,
    TimeProvider timeProvider)
{
    /// <summary>Approves or rejects a request.</summary>
    public async Task<Result> DecideAsync(
        ApprovalRequestId approvalRequestId,
        bool isApproved,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ApprovalRequest? request = await approvals
            .GetAsync(approvalRequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ApprovalNotFound, "No such approval request."));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (isApproved)
        {
            request.Approve(actor, now);
        }
        else
        {
            request.Reject(actor, reason ?? "No reason given.", now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await workflow.SignalApprovalDecisionAsync(
            request.IncidentId,
            new ApprovalDecisionSignal(approvalRequestId, isApproved, actor, reason, now),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Amends the proposed action before a decision is made.
    /// </summary>
    /// <remarks>
    /// An edit re-states the risk rather than inheriting it. Narrowing a command
    /// usually lowers the risk and widening it raises the risk, and carrying the
    /// original level forward would let an edit smuggle a higher-risk action
    /// past a ceiling that was checked against the original.
    /// </remarks>
    public async Task<Result> EditAsync(
        ApprovalRequestId approvalRequestId,
        string actor,
        string proposedActionJson,
        RiskLevel risk,
        CancellationToken cancellationToken)
    {
        ApprovalRequest? request = await approvals
            .GetAsync(approvalRequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ApprovalNotFound, "No such approval request."));
        }

        request.Edit(actor, proposedActionJson, risk, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
