using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace SreAiAgent.Workflow.Temporal;

/// <summary>
/// The durable investigation (docs/DECISIONS.md D08).
/// </summary>
/// <remarks>
/// The workflow itself is almost empty, which is the intent. It owns three
/// things a durable engine has to own: the signal that delivers a human
/// decision, the status a curious operator can query in the Temporal UI, and the
/// guarantee that a failed run still records why. The investigation itself lives
/// in InvestigationSequence, unchanged from the one the in-process executor
/// runs.
///
/// Business state is not kept here. ARCHITECTURE.md 3.3 puts it in PostgreSQL,
/// and duplicating it into workflow history would make the engine a second
/// system of record: recovering an incident would then mean replaying a workflow,
/// and a report would have two sources that could disagree.
/// </remarks>
[Workflow("SreInvestigation")]
internal sealed class TemporalInvestigationWorkflow
{
    private ApprovalDecisionSignal? _approval;
    private InvestigationStatus _status = InvestigationStatus.Started;

    /// <summary>
    /// Coarse progress, for the Temporal UI and for debugging a stuck run.
    /// </summary>
    /// <remarks>
    /// Only what the workflow genuinely knows. Detailed progress, evidence and
    /// findings are read from PostgreSQL through the incident endpoints; a query
    /// that returned a half-populated copy of them would be worse than no query,
    /// because someone would eventually trust it.
    /// </remarks>
    [WorkflowQuery("Status")]
    public InvestigationStatus Status => _status;

    /// <summary>Runs the investigation.</summary>
    [WorkflowRun]
    public async Task RunAsync(InvestigationWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        TemporalActivityProxy activities = new(input.Deadline);
        TemporalInvestigationSequence sequence = new(WaitForApprovalAsync);

        try
        {
            await sequence.ExecuteAsync(activities, input, TemporalWorkflow.CancellationToken);
            _status = InvestigationStatus.Completed;
        }
        catch (Exception exception) when (TemporalException.IsCanceledException(exception))
        {
            // A cancellation still has to leave the incident in a state someone
            // can act on. Without this the incident stays in Investigating with
            // no explanation, which is indistinguishable from a hung worker.
            _status = InvestigationStatus.Cancelled;

            await InvestigationSequence.FailAsync(
                activities,
                input,
                ApplicationErrorCodes.WorkflowCancelled,
                "The investigation was cancelled.");

            throw;
        }
        catch (Exception)
        {
            _status = InvestigationStatus.Failed;

            await InvestigationSequence.FailAsync(
                activities,
                input,
                ApplicationErrorCodes.DependencyUnavailable,
                "The investigation failed unexpectedly.");

            throw;
        }
    }

    /// <summary>Delivers a human approval decision to a waiting run.</summary>
    [WorkflowSignal("ApprovalDecided")]
    public Task ApprovalDecidedAsync(ApprovalDecisionSignal signal)
    {
        _approval = signal;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Suspends until the approval signal arrives or the deadline passes.
    /// </summary>
    private async Task<ApprovalDecisionSignal?> WaitForApprovalAsync(CancellationToken cancellationToken)
    {
        _status = InvestigationStatus.AwaitingApproval;

        try
        {
            // WaitConditionAsync is the durable wait: the workflow is evicted
            // from memory while it holds, and a worker restart resumes it from
            // history rather than restarting the investigation.
            await TemporalWorkflow.WaitConditionAsync(
                () => _approval is not null,
                cancellationToken);

            return _approval;
        }
        finally
        {
            _status = InvestigationStatus.Remediating;
        }
    }
}
