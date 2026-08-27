using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Workflow.Temporal;

/// <summary>
/// Schedules the fan-out on the durable engine's own scheduler.
/// </summary>
/// <remarks>
/// Every agent is started before any is awaited, so the whole fan-out is
/// recorded as one deterministic block of commands. Parallel.ForEachAsync is
/// deliberately not used here even though the in-process executor uses it: it
/// dispatches to the thread pool, and a workflow whose continuations run off the
/// engine's scheduler cannot be replayed. There is no concurrency to manage
/// either, because a workflow runs single-threaded and the real parallelism
/// happens in the worker processes executing the activities.
///
/// Fan-out width is therefore not bounded here. The bound that matters is the
/// worker's activity concurrency limit, which applies across every investigation
/// in flight rather than per investigation, and is the number that actually
/// protects the backends being queried.
/// </remarks>
/// <param name="waitForApproval">
/// Delegates the human wait back to the workflow, which owns the signal.
/// </param>
internal sealed class TemporalInvestigationSequence(
    Func<CancellationToken, Task<ApprovalDecisionSignal?>> waitForApproval) : InvestigationSequence
{
    /// <inheritdoc />
    protected override Task ForEachAgentAsync(
        int count,
        Func<int, CancellationToken, Task> body,
        CancellationToken cancellationToken)
    {
        Task[] runs = new Task[count];

        for (int index = 0; index < count; index++)
        {
            runs[index] = body(index, cancellationToken);
        }

        return Task.WhenAll(runs);
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is the capability the in-process executor cannot have. The workflow
    /// suspends here for as long as it takes, survives a worker restart, and
    /// resumes on the signal. A rejection resumes the investigation without the
    /// remediation rather than failing it: the diagnosis is still worth
    /// reporting, and the operator has already said what they think of the fix.
    /// </remarks>
    protected override async Task<Result> WaitForApprovalAsync(
        ApprovalDecision decision,
        InvestigationWorkflowInput input,
        CancellationToken cancellationToken)
    {
        ApprovalDecisionSignal? signal = await waitForApproval(cancellationToken);

        if (signal is null)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.WorkflowDeadlineExceeded,
                "The investigation deadline elapsed while waiting for an approval decision."));
        }

        return signal.IsApproved
            ? Result.Success()
            : Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ApprovalRejected,
                signal.Reason ?? "The proposed remediation was rejected."));
    }
}
