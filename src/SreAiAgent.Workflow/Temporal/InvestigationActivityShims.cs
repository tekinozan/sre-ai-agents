using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Shared;
using Temporalio.Activities;

namespace SreAiAgent.Workflow.Temporal;

/// <summary>
/// Exposes <see cref="IInvestigationActivities"/> to the Temporal worker.
/// </summary>
/// <remarks>
/// Every method here is one line, and that is the point. Temporal needs
/// attributed methods with names it can record in history and a cancellation
/// token it supplies through an ambient context rather than a parameter; none of
/// that belongs in the application layer, which has no business knowing which
/// engine is calling it. Keeping the shim this thin also means there is nowhere
/// for engine-specific behaviour to accumulate.
///
/// Registered as scoped, so each activity attempt resolves a fresh DbContext.
/// An activity can be retried hours after the workflow started, and a
/// long-lived scope would hand it a connection that closed long ago.
///
/// Activity names are pinned explicitly. They appear in workflow history, and a
/// history written by one release has to stay replayable by the next; a name
/// derived from the method would rename every activity in flight the moment
/// someone refactors.
/// </remarks>
/// <param name="activities">The engine-agnostic implementation.</param>
internal sealed class InvestigationActivityShims(IInvestigationActivities activities)
{
    private static CancellationToken Cancellation => ActivityExecutionContext.Current.CancellationToken;

    [Activity("LoadIncident")]
    public Task<Result<IncidentSnapshot>> LoadIncidentAsync(IncidentId incidentId)
        => activities.LoadIncidentAsync(incidentId, Cancellation);

    [Activity("ResolveConfiguration")]
    public Task<Result<ResolvedConfiguration>> ResolveConfigurationAsync(ConfigurationSnapshotId snapshotId)
        => activities.ResolveConfigurationAsync(snapshotId, Cancellation);

    [Activity("ClassifyIncident")]
    public Task<Result<IncidentClassification>> ClassifyIncidentAsync(ClassifyIncidentInput input)
        => activities.ClassifyIncidentAsync(input, Cancellation);

    [Activity("SelectAgents")]
    public Task<Result<AgentSelection>> SelectAgentsAsync(SelectAgentsInput input)
        => activities.SelectAgentsAsync(input, Cancellation);

    [Activity("RunAgent")]
    public Task<AgentRunResult> RunAgentAsync(RunAgentInput input)
        => activities.RunAgentAsync(input, Cancellation);

    [Activity("CorrelateFindings")]
    public Task<Result<CorrelationResult>> CorrelateFindingsAsync(CorrelateFindingsInput input)
        => activities.CorrelateFindingsAsync(input, Cancellation);

    [Activity("BuildRecommendations")]
    public Task<Result<RecommendationSet>> BuildRecommendationsAsync(BuildRecommendationsInput input)
        => activities.BuildRecommendationsAsync(input, Cancellation);

    [Activity("DecideApproval")]
    public Task<Result<ApprovalDecision>> DecideApprovalAsync(DecideApprovalInput input)
        => activities.DecideApprovalAsync(input, Cancellation);

    [Activity("PersistReport")]
    public Task<Result<PersistedReport>> PersistReportAsync(PersistReportInput input)
        => activities.PersistReportAsync(input, Cancellation);

    [Activity("CompleteInvestigation")]
    public Task<Result> CompleteInvestigationAsync(CompleteInvestigationInput input)
        => activities.CompleteInvestigationAsync(input, Cancellation);

    [Activity("FailInvestigation")]
    public Task<Result> FailInvestigationAsync(FailInvestigationInput input)
        => activities.FailInvestigationAsync(input, Cancellation);
}
