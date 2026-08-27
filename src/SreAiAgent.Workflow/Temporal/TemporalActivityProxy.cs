using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Shared;
using Temporalio.Common;
using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace SreAiAgent.Workflow.Temporal;

/// <summary>
/// Turns a call on <see cref="IInvestigationActivities"/> into a durable
/// activity invocation.
/// </summary>
/// <remarks>
/// This is what lets one sequence drive both engines. The in-process executor
/// calls the real implementation; inside a workflow the same interface resolves
/// to this, and every call becomes a scheduled activity with its own timeout,
/// retry policy and history entry. The sequence never learns which it is talking
/// to, so the order of the steps cannot diverge between the two.
///
/// The timeouts implement the hierarchy of ARCHITECTURE.md 11.3 from the outside
/// in. ScheduleToClose is whatever remains before the investigation deadline, so
/// an activity scheduled late cannot overrun it; StartToClose is a per-step
/// ceiling, so one hung call cannot silently consume the entire remaining
/// budget while the other steps starve.
///
/// Retries are bounded and deliberately narrow. Activities here return
/// <see cref="Result{T}"/> rather than throwing, so an expected failure is a
/// value that Temporal sees as success and never retries. Only a genuine
/// infrastructure fault reaches the retry policy, which is the only kind of
/// failure a retry can fix.
/// </remarks>
/// <param name="deadline">Absolute UTC instant the investigation must finish by.</param>
internal sealed class TemporalActivityProxy(DateTimeOffset deadline) : IInvestigationActivities
{
    private static readonly TimeSpan QuickCeiling = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ModelCeiling = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AgentCeiling = TimeSpan.FromMinutes(10);

    private static readonly RetryPolicy Retries = new()
    {
        InitialInterval = TimeSpan.FromSeconds(1),
        BackoffCoefficient = 2,
        MaximumInterval = TimeSpan.FromSeconds(30),
        MaximumAttempts = 3,
        NonRetryableErrorTypes = [nameof(DomainException)],
    };

    /// <inheritdoc />
    public Task<Result<IncidentSnapshot>> LoadIncidentAsync(
        IncidentId incidentId,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.LoadIncidentAsync(incidentId),
            Options(QuickCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result<ResolvedConfiguration>> ResolveConfigurationAsync(
        ConfigurationSnapshotId snapshotId,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.ResolveConfigurationAsync(snapshotId),
            Options(QuickCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result<IncidentClassification>> ClassifyIncidentAsync(
        ClassifyIncidentInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.ClassifyIncidentAsync(input),
            Options(ModelCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result<AgentSelection>> SelectAgentsAsync(
        SelectAgentsInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.SelectAgentsAsync(input),
            Options(QuickCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<AgentRunResult> RunAgentAsync(RunAgentInput input, CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.RunAgentAsync(input),
            Options(AgentCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result<CorrelationResult>> CorrelateFindingsAsync(
        CorrelateFindingsInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.CorrelateFindingsAsync(input),
            Options(ModelCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result<RecommendationSet>> BuildRecommendationsAsync(
        BuildRecommendationsInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.BuildRecommendationsAsync(input),
            Options(ModelCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result<ApprovalDecision>> DecideApprovalAsync(
        DecideApprovalInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.DecideApprovalAsync(input),
            Options(QuickCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result<PersistedReport>> PersistReportAsync(
        PersistReportInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.PersistReportAsync(input),
            Options(QuickCeiling, cancellationToken));

    /// <inheritdoc />
    public Task<Result> CompleteInvestigationAsync(
        CompleteInvestigationInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.CompleteInvestigationAsync(input),
            Options(QuickCeiling, cancellationToken));

    /// <inheritdoc />
    /// <remarks>
    /// Scheduled detached from the caller's token. This runs on the way out of a
    /// cancelled or timed-out investigation, and an activity bound to the token
    /// that just fired would be cancelled before it could record why the others
    /// stopped, leaving the incident stuck in Investigating with no explanation.
    /// </remarks>
    public Task<Result> FailInvestigationAsync(
        FailInvestigationInput input,
        CancellationToken cancellationToken)
        => TemporalWorkflow.ExecuteActivityAsync(
            (InvestigationActivityShims shim) => shim.FailInvestigationAsync(input),
            Options(QuickCeiling, CancellationToken.None));

    /// <summary>
    /// Builds the options for one activity, bounded by what remains of the
    /// investigation deadline.
    /// </summary>
    private ActivityOptions Options(TimeSpan ceiling, CancellationToken cancellationToken)
    {
        // Workflow.UtcNow, not the wall clock: on a replay the wall clock has
        // moved on and every timeout would be computed differently from the run
        // being replayed, which is precisely how a replay diverges.
        TimeSpan remaining = deadline.UtcDateTime - TemporalWorkflow.UtcNow;

        // A deadline that has already passed still schedules the activity, with
        // the smallest window Temporal accepts. The activity itself checks the
        // deadline and returns WORKFLOW_DEADLINE_EXCEEDED, which is a reportable
        // outcome; a rejected schedule call would be an exception instead.
        if (remaining < TimeSpan.FromSeconds(1))
        {
            remaining = TimeSpan.FromSeconds(1);
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = ceiling < remaining ? ceiling : remaining,
            ScheduleToCloseTimeout = remaining,
            RetryPolicy = Retries,
            CancellationToken = cancellationToken,
        };
    }
}
