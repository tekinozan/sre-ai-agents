using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Recommendations;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Workflows;

/// <summary>
/// The activity boundaries of ARCHITECTURE.md 11.1.
/// </summary>
/// <remarks>
/// Every workflow step lives here rather than inside a workflow class, and that
/// placement is the design. The Temporal adapter and the development executor
/// both call these same methods, so the investigation logic is engine-agnostic,
/// testable without a workflow runtime, and identical in both. Logic inside a
/// workflow class would have to be reimplemented for the second engine and would
/// drift from the first.
///
/// Each method is a single responsibility, cancellation-aware, idempotent with
/// respect to its side effects, and owns a telemetry span.
/// </remarks>
public sealed partial class InvestigationActivities : IInvestigationActivities
{
    private readonly IIncidentRepository _incidents;
    private readonly IInvestigationStore _store;
    private readonly IConfigurationSnapshotStore _snapshots;
    private readonly IEventPublisher _publisher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IIncidentRouter _router;
    private readonly IAgentExecutor _agent;
    private readonly IAgentRegistry _agents;
    private readonly IPromptRegistry _prompts;
    private readonly IReadOnlyToolRegistry _tools;
    private readonly ILlmGateway _llm;
    private readonly IPromptRenderer _renderer;
    private readonly SreMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InvestigationActivities> _logger;

    public InvestigationActivities(
        IIncidentRepository incidents,
        IInvestigationStore store,
        IConfigurationSnapshotStore snapshots,
        IUnitOfWork unitOfWork,
        IEventPublisher publisher,
        IIncidentRouter router,
        IAgentExecutor agent,
        IAgentRegistry agents,
        IPromptRegistry prompts,
        IReadOnlyToolRegistry tools,
        ILlmGateway llm,
        IPromptRenderer renderer,
        SreMetrics metrics,
        TimeProvider timeProvider,
        ILogger<InvestigationActivities> logger)
    {
        _incidents = incidents;
        _store = store;
        _snapshots = snapshots;
        _unitOfWork = unitOfWork;
        _publisher = publisher;
        _router = router;
        _agent = agent;
        _agents = agents;
        _prompts = prompts;
        _tools = tools;
        _llm = llm;
        _renderer = renderer;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IncidentSnapshot>> LoadIncidentAsync(
        IncidentId incidentId,
        CancellationToken cancellationToken)
    {
        Incident? incident = await _incidents.GetAsync(incidentId, cancellationToken).ConfigureAwait(false);

        if (incident is null)
        {
            return Result<IncidentSnapshot>.Failure(Error.Permanent(
                ApplicationErrorCodes.IncidentNotFound, $"Incident {incidentId} was not found."));
        }

        // A cancelled incident stops here. Continuing would spend a model budget
        // producing a report nobody asked for on an incident somebody explicitly
        // abandoned.
        if (incident.Status is IncidentStatus.Cancelled)
        {
            return Result<IncidentSnapshot>.Failure(Error.Permanent(
                ApplicationErrorCodes.WorkflowCancelled, "The incident was cancelled."));
        }

        return Result<IncidentSnapshot>.Success(new IncidentSnapshot(
            incident.Id,
            incident.Tenant,
            incident.Source,
            incident.Title,
            incident.Description,
            incident.Service,
            incident.Environment,
            incident.Severity,
            incident.Status,
            incident.DetectedAt,
            incident.Labels));
    }

    /// <inheritdoc />
    public async Task<Result<ResolvedConfiguration>> ResolveConfigurationAsync(
        ConfigurationSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        ConfigurationSnapshot? snapshot = await _snapshots
            .GetAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Result<ResolvedConfiguration>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigurationSnapshotNotFound,
                $"Configuration snapshot {snapshotId} was not found."));
        }

        return Result<ResolvedConfiguration>.Success(new ResolvedConfiguration(
            snapshot,
            snapshot.PromptRelease,
            snapshot.ModelAssignment,
            snapshot.ToolRelease,
            snapshot.PolicyRelease));
    }

    /// <inheritdoc />
    public async Task<Result<IncidentClassification>> ClassifyIncidentAsync(
        ClassifyIncidentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        using Activity? activity = SreActivitySources.Agent.StartActivity(SreSpanNames.Classify);
        activity.SetSafeTag(TraceAttributes.IncidentId, input.Incident.IncidentId.ToString());

        // Deterministic rules first. On most incidents this is where
        // classification ends, and the model is never consulted.
        IncidentClassification? deterministic = DeterministicClassifier.TryClassify(input.Incident);
        if (deterministic is not null)
        {
            activity.SetSafeTag(TraceAttributes.ResultStatus, "deterministic");
            return Result<IncidentClassification>.Success(deterministic);
        }

        Result<IncidentClassification> classified = await ClassifyWithModelAsync(input, cancellationToken)
            .ConfigureAwait(false);

        if (classified.IsSuccess)
        {
            activity.SetSafeTag(TraceAttributes.ResultStatus, "llm");
            return classified;
        }

        // The classifier failing is not the end of the investigation. A default
        // classification with explicit low confidence lets routing widen the agent
        // selection, which is a worse investigation than a correct classification
        // and a far better outcome than none.
        LogClassifierFallback(_logger, input.Incident.IncidentId.ToString(), classified.Error.Message);
        activity.SetSafeTag(TraceAttributes.ResultStatus, "fallback");

        return Result<IncidentClassification>.Success(new IncidentClassification(
            ["latency", "availability"],
            ClassificationMethod.Fallback,
            Confidence.From(0.2m),
            "Neither deterministic rules nor the classifier produced a usable answer."));
    }

    /// <inheritdoc />
    public async Task<Result<AgentSelection>> SelectAgentsAsync(
        SelectAgentsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        using Activity? activity = SreActivitySources.Agent.StartActivity(SreSpanNames.Route);
        activity.SetSafeTag(TraceAttributes.IncidentId, input.Incident.IncidentId.ToString());

        ConfigurationSnapshot? snapshot = await _snapshots
            .GetAsync(input.ConfigurationSnapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Result<AgentSelection>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigurationSnapshotNotFound, "The pinned snapshot is missing."));
        }

        Result<AgentSelection> selection = await _router
            .SelectAsync(input.Incident, input.Classification, snapshot, cancellationToken)
            .ConfigureAwait(false);

        if (selection.IsSuccess)
        {
            // Exclusions are recorded on the timeline, not just logged.
            // ARCHITECTURE.md 24.3 requires the reason an agent was filtered out to
            // become an audit event, and it is usually the answer to why a report
            // looks thin.
            foreach (ExcludedAgent excluded in selection.Value.Excluded)
            {
                _incidents.AddTimelineEntry(IncidentTimelineEntry.Progress(
                    input.Incident.IncidentId,
                    "agent.excluded",
                    $"{excluded.AgentName}:{excluded.AgentVersion} was not selected. {excluded.Reason}",
                    _timeProvider.GetUtcNow()));
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return selection;
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> RunAgentAsync(RunAgentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        Result<AgentContext> context = await BuildAgentContextAsync(input, cancellationToken)
            .ConfigureAwait(false);

        if (context.IsFailure)
        {
            // A resolution failure is reported as a failed run rather than thrown,
            // so the fan-out keeps the other agents' results.
            return new AgentRunResult(
                input.Agent.AgentName,
                input.Agent.AgentVersion,
                AgentRunId.New(),
                AgentRunStatus.Failed,
                input.Agent.IsShadow,
                Finding: null,
                EvidenceIds: [],
                context.Error.Code,
                context.Error.Message);
        }

        AgentRun run = AgentRun.Start(
            input.Incident.IncidentId,
            new AgentExecutionIdentity(
                input.Agent.AgentName,
                input.Agent.AgentVersion,
                context.Value.Prompt.Name,
                context.Value.Prompt.Version,
                context.Value.Prompt.ContentHash,
                context.Value.ModelCandidate.AdapterKind.ToString(),
                context.Value.ModelCandidate.ProviderModel,
                context.Value.ModelCandidate.DeploymentVersion.ToString(CultureInfo.InvariantCulture)),
            input.Agent.IsShadow,
            _timeProvider.GetUtcNow());

        AgentRunResult result = await _agent
            .ExecuteAsync(context.Value, cancellationToken)
            .ConfigureAwait(false);

        // The run records what it consumed before it records how it ended, so a
        // failed run still carries the cost of the work that failed. A budget
        // exhausted halfway is exactly the case where the number matters.
        run.RecordLlmCall(result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.EstimatedCost);
        run.SetCallCounts(result.Usage.LlmCalls, result.Usage.ToolCalls);

        if (result.Finding is not null)
        {
            Finding finding = Finding.Create(
                input.Incident.IncidentId,
                run.Id,
                input.Agent.AgentName,
                input.Agent.AgentVersion,
                result.Finding,
                input.Agent.IsShadow,
                _timeProvider.GetUtcNow());

            _store.AddFinding(finding);
            run.Complete(finding.Id, _timeProvider.GetUtcNow());
        }
        else
        {
            run.Fail(
                result.Status,
                result.ErrorCode ?? ApplicationErrorCodes.LlmProviderError,
                result.ErrorMessage ?? "The agent produced no finding.",
                _timeProvider.GetUtcNow());
        }

        _store.AddAgentRun(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return result with { AgentRunId = run.Id };
    }

    /// <inheritdoc />
    public async Task<Result<CorrelationResult>> CorrelateFindingsAsync(
        CorrelateFindingsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        using Activity? activity = SreActivitySources.Investigation.StartActivity(SreSpanNames.Correlate);
        activity.SetSafeTag(TraceAttributes.IncidentId, input.Incident.IncidentId.ToString());

        // Shadow runs are filtered here as well as in the store. A shadow result
        // reaching a real report is the one failure mode shadow mode exists to
        // prevent, and it is worth two guards.
        List<AgentRunResult> contributing =
            [.. input.AgentRuns.Where(static run => !run.IsShadow && run.Finding is not null)];

        if (contributing.Count == 0)
        {
            return Result<CorrelationResult>.Success(new CorrelationResult(
                "No expert produced a verifiable finding, so no cause could be established.",
                Category: null,
                Confidence.None,
                KeyObservations: [],
                RankedHypotheses: [],
                EvidenceGaps: ["No agent completed successfully."]));
        }

        return await CorrelateWithModelAsync(input, contributing, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<RecommendationSet>> BuildRecommendationsAsync(
        BuildRecommendationsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return await RecommendWithModelAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Result<ApprovalDecision>> DecideApprovalAsync(
        DecideApprovalInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        // V1 executes nothing, so there is nothing to approve. The decision is
        // still made explicitly rather than skipped, so the workflow shape is the
        // one V2 will use and the timeline records why no approval was sought.
        bool anyWrite = input.Recommendations.Recommendations
            .Any(static recommendation => recommendation.Risk > RiskLevel.ReadOnly);

        return Task.FromResult(Result<ApprovalDecision>.Success(new ApprovalDecision(
            IsApprovalRequired: false,
            ApprovalRequestId: null,
            Reason: anyWrite
                ? "Recommendations include write actions, but V1 does not execute remediation, "
                  + "so they are reported for a human to perform."
                : "All recommendations are read-only.")));
    }

    /// <inheritdoc />
    public async Task<Result<PersistedReport>> PersistReportAsync(
        PersistReportInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        int version = await _store
            .GetNextReportVersionAsync(input.IncidentId, cancellationToken)
            .ConfigureAwait(false);

        RootCauseReport report = RootCauseReport.Create(new NewRootCauseReport
        {
            IncidentId = input.IncidentId,
            Version = version,
            ConfigurationSnapshotId = input.ConfigurationSnapshotId,
            Outcome = input.Outcome,
            Summary = input.Correlation.Summary,
            Category = input.Correlation.Category,
            Confidence = input.Correlation.Confidence,
            KeyObservations = input.Correlation.KeyObservations,
            RankedHypotheses = input.Correlation.RankedHypotheses,
            EvidenceGaps = input.Correlation.EvidenceGaps,
            CitedEvidenceIds = input.CitedEvidenceIds,
            ContributingAgentNames = input.ContributingAgents,
            CreatedAt = _timeProvider.GetUtcNow(),
        });

        _store.AddReport(report);

        List<Recommendation> recommendations = [.. input.Recommendations.Recommendations.Select(proposed =>
            Recommendation.Create(
                input.IncidentId,
                report.Id,
                proposed.Rank,
                proposed.Title,
                proposed.Rationale,
                proposed.Risk,
                proposed.Confidence,
                proposed.EvidenceIds,
                _timeProvider.GetUtcNow()))];

        _store.AddRecommendations(recommendations);

        // The report and its recommendations commit together. A report whose
        // recommendations failed to save would be shown to an operator with no
        // next steps and no indication that any were produced.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<PersistedReport>.Success(new PersistedReport(report.Id, version));
    }

    /// <inheritdoc />
    public async Task<Result> CompleteInvestigationAsync(
        CompleteInvestigationInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        Incident? incident = await _incidents.GetAsync(input.IncidentId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.IncidentNotFound, "The incident disappeared mid-investigation."));
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        incident.MarkResolved(now);

        _incidents.AddTimelineEntry(IncidentTimelineEntry.Progress(
            input.IncidentId,
            "incident.completed",
            $"Investigation completed with outcome {input.Outcome}, report version "
            + $"{input.ReportVersion.ToString(CultureInfo.InvariantCulture)}.",
            now));

        PublishCompletion(incident, input.Outcome, input.ReportVersion, errorCode: null);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _metrics.IncidentCompleted(
            incident.Tenant, incident.Environment, input.Outcome.ToString(), now - incident.CreatedAt);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> FailInvestigationAsync(
        FailInvestigationInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        Incident? incident = await _incidents.GetAsync(input.IncidentId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.IncidentNotFound, "The incident disappeared mid-investigation."));
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Failed, not resolved. The incident stays retryable, and the reason is on
        // the timeline where an operator will look for it.
        incident.MarkFailed(input.ErrorCode, input.Reason, now);

        _incidents.AddTimelineEntry(IncidentTimelineEntry.Progress(
            input.IncidentId, "incident.failed", input.Reason, now));

        PublishCompletion(incident, OutcomeOf(input.ErrorCode), reportVersion: 0, input.ErrorCode);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _metrics.IncidentFailed(incident.Tenant, incident.Environment, input.ErrorCode.Value);

        return Result.Success();
    }

    /// <summary>
    /// Announces that the investigation ended, in the same transaction as the
    /// state change.
    /// </summary>
    /// <remarks>
    /// The aggregate raises IncidentResolved and IncidentFailed, but nothing here
    /// ever drained them, so <c>sre.incident.completed.v1</c> was created,
    /// documented and never written to; whoever wanted to know an investigation
    /// had finished had to poll. The payload is explicit rather than derived
    /// from the domain event because the event does not know the outcome or the
    /// report version, which is what a consumer such as the notifier needs.
    /// </remarks>
    private void PublishCompletion(Incident incident, InvestigationOutcome outcome, int reportVersion, ErrorCode? errorCode)
    {
        _ = incident.DequeueEvents();
        _publisher.Enqueue(
            KafkaTopics.IncidentCompleted,
            "sre.incident.completed",
            new IncidentCompletedPayload(incident.Id, incident.Status, outcome, reportVersion, errorCode),
            incident.Id.ToString());
    }

    private static InvestigationOutcome OutcomeOf(ErrorCode code)
        => code == ApplicationErrorCodes.BudgetExceeded ? InvestigationOutcome.BudgetExceeded
            : code == ApplicationErrorCodes.WorkflowDeadlineExceeded ? InvestigationOutcome.DeadlineExceeded
            : code == ApplicationErrorCodes.WorkflowCancelled ? InvestigationOutcome.Cancelled
            : code == ApplicationErrorCodes.InsufficientAgentCapability ? InvestigationOutcome.InsufficientAgentCapability
            : InvestigationOutcome.Partial;

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Warning,
        Message = "Classifier fell back to a default for incident {IncidentId}: {Reason}")]
    private static partial void LogClassifierFallback(ILogger logger, string incidentId, string reason);
}
