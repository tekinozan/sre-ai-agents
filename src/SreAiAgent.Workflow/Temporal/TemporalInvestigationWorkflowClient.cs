using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using SreWorkflowOptions = SreAiAgent.Application.Abstractions.Workflow.WorkflowOptions;
using TemporalStartOptions = Temporalio.Client.WorkflowOptions;

namespace SreAiAgent.Workflow.Temporal;

/// <summary>
/// Starts, signals and cancels durable investigations through Temporal.
/// </summary>
/// <remarks>
/// The workflow id is the incident id, which is what makes starting an
/// investigation idempotent. Delivery from Kafka is at-least-once, so the same
/// investigation request will eventually arrive twice; with a derived id the
/// second attempt reuses the running workflow instead of launching a competing
/// one that would query the same backends, spend the same budget and write a
/// second report.
///
/// Every method returns a Result rather than throwing. A broker outage or a
/// Temporal restart is an expected operating condition for a platform whose job
/// is to investigate outages, and the caller needs to decide whether to retry
/// rather than discover it through an exception.
/// </remarks>
internal sealed partial class TemporalInvestigationWorkflowClient(
    ITemporalClient client,
    IOptions<SreWorkflowOptions> options,
    ILogger<TemporalInvestigationWorkflowClient> logger) : IInvestigationWorkflow
{
    private readonly SreWorkflowOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<Result> StartAsync(InvestigationWorkflowInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            await client.StartWorkflowAsync(
                (TemporalInvestigationWorkflow workflow) => workflow.RunAsync(input),
                new TemporalStartOptions(WorkflowIdFor(input.IncidentId), _options.TaskQueue)
                {
                    // Join the existing run rather than failing. A redelivered
                    // message is normal, not an error worth alerting on.
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,

                    // A finished incident may legitimately be investigated again
                    // after a retry request, so the id is reusable once closed.
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,

                    // The outermost bound of ARCHITECTURE.md 11.3, enforced by the
                    // engine rather than by anything the workflow code remembers
                    // to check.
                    RunTimeout = TimeSpan.FromSeconds(_options.DeadlineSeconds),
                    Rpc = new RpcOptions { CancellationToken = cancellationToken },
                }).ConfigureAwait(false);

            return Result.Success();
        }
        catch (WorkflowAlreadyStartedException)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.WorkflowAlreadyRunning,
                $"An investigation is already running for incident {input.IncidentId}."));
        }
        catch (RpcException exception)
        {
            LogStartFailed(logger, input.IncidentId.ToString(), exception);

            return Result.Failure(Error.Transient(
                ApplicationErrorCodes.DependencyUnavailable,
                "The workflow engine could not be reached."));
        }
    }

    /// <inheritdoc />
    public async Task<Result> SignalApprovalDecisionAsync(
        IncidentId incidentId,
        ApprovalDecisionSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            await client
                .GetWorkflowHandle<TemporalInvestigationWorkflow>(WorkflowIdFor(incidentId))
                .SignalAsync(
                    workflow => workflow.ApprovalDecidedAsync(signal),
                    new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
                .ConfigureAwait(false);

            return Result.Success();
        }
        catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
        {
            // No running workflow to decide about. The approval record still
            // exists in PostgreSQL, so this reports a stale decision rather than
            // an infrastructure failure.
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ApprovalNotFound,
                "No investigation is waiting for this approval."));
        }
        catch (RpcException exception)
        {
            LogSignalFailed(logger, incidentId.ToString(), exception);

            return Result.Failure(Error.Transient(
                ApplicationErrorCodes.DependencyUnavailable,
                "The workflow engine could not be reached."));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancels rather than terminates. Cancellation lets the workflow run its
    /// cleanup and record why the incident stopped being investigated;
    /// termination would kill it mid-activity and leave the incident in
    /// Investigating forever.
    /// </remarks>
    public async Task<Result> CancelAsync(
        IncidentId incidentId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await client
                .GetWorkflowHandle<TemporalInvestigationWorkflow>(WorkflowIdFor(incidentId))
                .CancelAsync(
                    new WorkflowCancelOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
                .ConfigureAwait(false);

            return Result.Success();
        }
        catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
        {
            // Nothing is running, which is the state the caller asked for.
            return Result.Success();
        }
        catch (RpcException exception)
        {
            LogCancelFailed(logger, incidentId.ToString(), exception);

            return Result.Failure(Error.Transient(
                ApplicationErrorCodes.DependencyUnavailable,
                "The workflow engine could not be reached."));
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsRunningAsync(IncidentId incidentId, CancellationToken cancellationToken)
    {
        try
        {
            WorkflowExecutionDescription description = await client
                .GetWorkflowHandle<TemporalInvestigationWorkflow>(WorkflowIdFor(incidentId))
                .DescribeAsync(
                    new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
                .ConfigureAwait(false);

            return description.Status == WorkflowExecutionStatus.Running;
        }
        catch (RpcException exception) when (exception.Code == RpcException.StatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// The workflow id for an incident. Derived, never generated.
    /// </summary>
    /// <remarks>
    /// A generated id would make every duplicate delivery a new investigation.
    /// Deriving it turns Temporal's own uniqueness constraint into the
    /// deduplication mechanism, which is stronger than anything this process
    /// could check for itself.
    /// </remarks>
    private static string WorkflowIdFor(IncidentId incidentId)
        => string.Create(CultureInfo.InvariantCulture, $"incident-{incidentId}");

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Error,
        Message = "Could not start the investigation workflow for incident {IncidentId}.")]
    private static partial void LogStartFailed(ILogger logger, string incidentId, Exception exception);

    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Error,
        Message = "Could not signal the approval decision for incident {IncidentId}.")]
    private static partial void LogSignalFailed(ILogger logger, string incidentId, Exception exception);

    [LoggerMessage(
        EventId = 6103,
        Level = LogLevel.Error,
        Message = "Could not cancel the investigation workflow for incident {IncidentId}.")]
    private static partial void LogCancelFailed(ILogger logger, string incidentId, Exception exception);
}
