using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Workflow.Inline;

/// <summary>
/// Runs an investigation in-process, without durability
/// (docs/DECISIONS.md D08).
/// </summary>
/// <remarks>
/// This is not a durable workflow engine and does not pretend to be one. It calls
/// the same <see cref="IInvestigationActivities"/> methods the Temporal adapter
/// calls, in the same order; what it does not have is history, replay, or the
/// ability to resume after a process restart. A worker that dies mid-investigation
/// loses that investigation, and the incident stays in Investigating until a retry
/// is requested.
///
/// It exists so the whole pipeline is runnable and testable without a Temporal
/// server: CI, a laptop and the integration suite all use it. Writing a durable
/// engine ourselves was explicitly rejected; this is a straight-line executor, and
/// the constructor refuses to build it when the host is running in Production so
/// the distinction cannot be lost during a deployment.
/// </remarks>
public sealed partial class InlineInvestigationWorkflow : IInvestigationWorkflow
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRuntimeOptions _agentOptions;
    private readonly WorkflowOptions _workflowOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InlineInvestigationWorkflow> _logger;

    /// <summary>
    /// Investigations currently running in this process, so a redelivered message
    /// joins the existing run instead of starting a second one.
    /// </summary>
    private readonly ConcurrentDictionary<IncidentId, Task> _running = new();

    public InlineInvestigationWorkflow(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        IOptions<AgentRuntimeOptions> agentOptions,
        IOptions<WorkflowOptions> workflowOptions,
        TimeProvider timeProvider,
        ILogger<InlineInvestigationWorkflow> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(agentOptions);
        ArgumentNullException.ThrowIfNull(workflowOptions);

        if (environment.IsProduction())
        {
            // Fail at construction, which means the host will not start. A
            // development convenience that silently degrades durability in
            // production is exactly the kind of thing nobody notices until an
            // incident is lost.
            throw new InvalidOperationException(
                "InlineInvestigationWorkflow has no durability and cannot run in Production. "
                + "Set Workflow:Engine to 'temporal'.");
        }

        _scopeFactory = scopeFactory;
        _agentOptions = agentOptions.Value;
        _workflowOptions = workflowOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> StartAsync(InvestigationWorkflowInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Idempotent by incident id, matching the Temporal adapter's workflow-id
        // deduplication. A redelivered Kafka message must not launch a second
        // investigation for the same incident.
        if (_running.ContainsKey(input.IncidentId))
        {
            return Task.FromResult(Result.Failure(Error.Permanent(
                ApplicationErrorCodes.WorkflowAlreadyRunning,
                $"An investigation is already running for incident {input.IncidentId}.")));
        }

        // Deliberately not awaited by the caller: StartAsync starts, it does not
        // run to completion. The returned task is retained so IsRunningAsync can
        // answer and so the exception, if any, is observed rather than swallowed
        // by the finalizer.
        Task execution = Task.Run(
            () => RunAsync(input, CancellationToken.None), CancellationToken.None);

        _running[input.IncidentId] = execution;

        _ = execution.ContinueWith(
            completed =>
            {
                _running.TryRemove(input.IncidentId, out _);

                if (completed.IsFaulted)
                {
                    LogUnhandled(_logger, input.IncidentId.ToString(), completed.Exception!);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> SignalApprovalDecisionAsync(
        IncidentId incidentId,
        ApprovalDecisionSignal signal,
        CancellationToken cancellationToken)
        // Signals need a durable, addressable workflow instance to deliver to.
        // Simulating one here would be a lie with the same shape as the real
        // thing, so this refuses instead: V1 requires no approval, and a caller
        // that reaches this line under the inline engine has a configuration
        // problem worth surfacing.
        => Task.FromResult(Result.Failure(Error.Permanent(
            ApplicationErrorCodes.ConfigInvalid,
            "The inline executor cannot deliver approval signals. Use the Temporal engine.")));

    /// <inheritdoc />
    public Task<Result> CancelAsync(IncidentId incidentId, string reason, CancellationToken cancellationToken)
        => Task.FromResult(_running.ContainsKey(incidentId)
            ? Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                "The inline executor cannot cancel a running investigation. Use the Temporal engine."))
            : Result.Success());

    /// <inheritdoc />
    public Task<bool> IsRunningAsync(IncidentId incidentId, CancellationToken cancellationToken)
        => Task.FromResult(_running.ContainsKey(incidentId));

    /// <summary>Runs the activity sequence of ARCHITECTURE.md 11.1.</summary>
    private async Task RunAsync(InvestigationWorkflowInput input, CancellationToken cancellationToken)
    {
        // A fresh scope: the investigation outlives the request that started it,
        // and the DbContext from that request is long gone.
        using IServiceScope scope = _scopeFactory.CreateScope();
        IInvestigationActivities activities =
            scope.ServiceProvider.GetRequiredService<IInvestigationActivities>();

        using CancellationTokenSource deadline = new(
            TimeSpan.FromSeconds(_workflowOptions.DeadlineSeconds), _timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        using Activity? activity = SreActivitySources.Investigation.StartActivity(
            SreSpanNames.Investigation, ActivityKind.Internal);

        activity
            .SetSafeTag(TraceAttributes.IncidentId, input.IncidentId.ToString())
            .SetSafeTag(TraceAttributes.Tenant, input.Tenant)
            .SetSafeTag(TraceAttributes.CorrelationId, input.CorrelationId)
            .SetSafeTag(TraceAttributes.WorkflowAttempt, input.Attempt)
            .SetSafeTag(TraceAttributes.ConfigurationSnapshotId, input.ConfigurationSnapshotId.ToString());

        InlineInvestigationSequence sequence = new(_agentOptions.MaxDegreeOfParallelism);

        try
        {
            await sequence.ExecuteAsync(activities, input, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            activity.SetSafeTag(TraceAttributes.ResultStatus, "deadline_exceeded");
            await InvestigationSequence.FailAsync(
                activities,
                input,
                ApplicationErrorCodes.WorkflowDeadlineExceeded,
                "The investigation deadline elapsed.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            LogUnhandled(_logger, input.IncidentId.ToString(), exception);

            // The incident is marked failed rather than left in Investigating
            // forever. A stuck incident is invisible; a failed one is retryable
            // and shows up in sre_incident_failed_total.
            await InvestigationSequence.FailAsync(
                activities,
                input,
                ApplicationErrorCodes.LlmProviderError,
                "The investigation failed unexpectedly.").ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Error,
        Message = "Inline investigation for incident {IncidentId} failed.")]
    private static partial void LogUnhandled(ILogger logger, string incidentId, Exception exception);
}
