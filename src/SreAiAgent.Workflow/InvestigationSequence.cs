using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Workflow;

/// <summary>
/// The activity sequence of ARCHITECTURE.md 11.1, written once and run by both
/// engines.
/// </summary>
/// <remarks>
/// <code>
/// LoadIncident -&gt; ResolveConfiguration -&gt; ClassifyIncident -&gt; SelectAgents
/// -&gt; RunSelectedAgentsInParallel -&gt; CorrelateFindings -&gt; BuildRecommendations
/// -&gt; DecideApproval -&gt; PersistReport -&gt; Complete
/// </code>
///
/// Only two things differ between the durable and the in-process executor: how
/// the fan-out is scheduled, and whether the run can wait for a human. Everything
/// else, including the order of the steps and the merge of their results, is
/// identical, so it lives here rather than being written twice and drifting. A
/// second copy of an investigation sequence is a second set of bugs, and the one
/// that runs in production is the one nobody can test locally.
///
/// There is no ConfigureAwait(false) anywhere in this file, and that is
/// deliberate rather than an omission. A durable engine replays a workflow by
/// running its continuations on its own scheduler; detaching from that scheduler
/// hands the continuation to the thread pool and the replay stops being
/// deterministic. The in-process executor runs on a host with no synchronization
/// context, where the call would be a no-op anyway.
/// </remarks>
public abstract class InvestigationSequence
{
    /// <summary>Runs the full sequence for one incident.</summary>
    public async Task ExecuteAsync(
        IInvestigationActivities activities,
        InvestigationWorkflowInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(input);

        Result<IncidentSnapshot> incident = await activities.LoadIncidentAsync(input.IncidentId, cancellationToken);

        if (incident.IsFailure)
        {
            await FailAsync(activities, input, incident.Error.Code, incident.Error.Message);
            return;
        }

        Result<ResolvedConfiguration> configuration =
            await activities.ResolveConfigurationAsync(input.ConfigurationSnapshotId, cancellationToken);

        if (configuration.IsFailure)
        {
            await FailAsync(activities, input, configuration.Error.Code, configuration.Error.Message);
            return;
        }

        Result<IncidentClassification> classification = await activities.ClassifyIncidentAsync(
            new ClassifyIncidentInput(
                incident.Value, input.ConfigurationSnapshotId, input.CorrelationId, input.Deadline),
            cancellationToken);

        if (classification.IsFailure)
        {
            await FailAsync(activities, input, classification.Error.Code, classification.Error.Message);
            return;
        }

        Result<AgentSelection> selection = await activities.SelectAgentsAsync(
            new SelectAgentsInput(
                incident.Value, classification.Value, input.ConfigurationSnapshotId, input.CorrelationId),
            cancellationToken);

        if (selection.IsFailure)
        {
            await FailAsync(activities, input, selection.Error.Code, selection.Error.Message);
            return;
        }

        if (selection.Value.Agents.Count == 0)
        {
            // A reportable outcome rather than a failure. The operator still needs
            // an incident record explaining that nobody was available to look at
            // it, and the exclusion reasons are already on the timeline
            // (ARCHITECTURE.md 24.3).
            await CompleteWithoutAgentsAsync(activities, input, cancellationToken);
            return;
        }

        IReadOnlyList<AgentRunResult> runs = await RunAgentsAsync(
            activities, input, incident.Value, classification.Value, selection.Value, cancellationToken);

        Result<CorrelationResult> correlation = await activities.CorrelateFindingsAsync(
            new CorrelateFindingsInput(
                incident.Value, runs, input.ConfigurationSnapshotId, input.CorrelationId, input.Deadline),
            cancellationToken);

        if (correlation.IsFailure)
        {
            await FailAsync(activities, input, correlation.Error.Code, correlation.Error.Message);
            return;
        }

        Result<RecommendationSet> recommendations = await activities.BuildRecommendationsAsync(
            new BuildRecommendationsInput(
                incident.Value,
                correlation.Value,
                input.ConfigurationSnapshotId,
                input.CorrelationId,
                input.Deadline),
            cancellationToken);

        // A failed recommendation step does not discard the correlation. The
        // explanation is the expensive part and is worth reporting on its own; an
        // operator can act on a diagnosis without a suggested fix.
        RecommendationSet recommendationSet = recommendations.IsSuccess
            ? recommendations.Value
            : new RecommendationSet([]);

        Result<ApprovalDecision> approval = await activities.DecideApprovalAsync(
            new DecideApprovalInput(
                incident.Value, recommendationSet, input.ConfigurationSnapshotId, input.CorrelationId),
            cancellationToken);

        if (approval.IsSuccess && approval.Value.IsApprovalRequired)
        {
            Result waited = await WaitForApprovalAsync(approval.Value, input, cancellationToken);

            if (waited.IsFailure)
            {
                await FailAsync(activities, input, waited.Error.Code, waited.Error.Message);
                return;
            }
        }

        InvestigationOutcome outcome = DetermineOutcome(runs, recommendations.IsSuccess);

        Result<PersistedReport> report = await activities.PersistReportAsync(
            new PersistReportInput(
                input.IncidentId,
                input.ConfigurationSnapshotId,
                outcome,
                correlation.Value,
                recommendationSet,
                ContributingAgents(runs),
                CitedEvidence(runs),
                input.CorrelationId),
            cancellationToken);

        if (report.IsFailure)
        {
            await FailAsync(activities, input, report.Error.Code, report.Error.Message);
            return;
        }

        await activities.CompleteInvestigationAsync(
            new CompleteInvestigationInput(
                input.IncidentId, outcome, report.Value.Version, input.CorrelationId),
            cancellationToken);
    }

    /// <summary>Records a failure without closing the incident, so a retry stays possible.</summary>
    /// <remarks>
    /// Always passes <see cref="CancellationToken.None"/>. This runs on the way
    /// out of a cancelled or timed-out investigation, and passing the token that
    /// just fired would cancel the one step whose job is to explain why the
    /// others did not finish, leaving the incident stuck in Investigating.
    /// </remarks>
    public static async Task FailAsync(
        IInvestigationActivities activities,
        InvestigationWorkflowInput input,
        ErrorCode code,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(input);

        await activities.FailInvestigationAsync(
            new FailInvestigationInput(input.IncidentId, code, reason, input.CorrelationId),
            CancellationToken.None);
    }

    /// <summary>
    /// Schedules the fan-out. The only step whose implementation differs between
    /// engines.
    /// </summary>
    /// <remarks>
    /// The merge lives in the caller, not here, so an engine can only choose how
    /// its agents are scheduled and never how their results are ordered.
    /// </remarks>
    protected abstract Task ForEachAgentAsync(
        int count,
        Func<int, CancellationToken, Task> body,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs one agent of the fan-out.
    /// </summary>
    /// <remarks>
    /// A hook rather than a direct call, because the two engines differ in what
    /// an agent run may share. The durable engine dispatches each run as its own
    /// activity, so each one already gets a fresh scope on whichever worker picks
    /// it up. The in-process executor runs them concurrently inside one scope,
    /// where sharing the caller's DbContext across threads throws on the second
    /// concurrent query.
    /// </remarks>
    protected virtual Task<AgentRunResult> RunOneAgentAsync(
        IInvestigationActivities activities,
        RunAgentInput input,
        CancellationToken cancellationToken)
        => activities.RunAgentAsync(input, cancellationToken);

    /// <summary>
    /// Waits for a human decision. The durable engine blocks on a signal; the
    /// in-process executor has nowhere to deliver one and refuses.
    /// </summary>
    protected abstract Task<Result> WaitForApprovalAsync(
        ApprovalDecision decision,
        InvestigationWorkflowInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs the selected agents and merges their results deterministically.
    /// </summary>
    /// <remarks>
    /// Results are written into a pre-sized array by index rather than appended
    /// to a concurrent collection. Ordering has to be a function of the selection
    /// and not of which agent happened to finish first: ARCHITECTURE.md 3.3
    /// requires a deterministic merge, and a report whose observations reorder
    /// between runs is impossible to diff and impossible to evaluate. Under a
    /// replaying engine it is also a correctness requirement rather than a
    /// nicety.
    /// </remarks>
    private async Task<IReadOnlyList<AgentRunResult>> RunAgentsAsync(
        IInvestigationActivities activities,
        InvestigationWorkflowInput input,
        IncidentSnapshot incident,
        IncidentClassification classification,
        AgentSelection selection,
        CancellationToken cancellationToken)
    {
        AgentRunResult[] results = new AgentRunResult[selection.Agents.Count];

        await ForEachAgentAsync(
            selection.Agents.Count,
            async (index, token) =>
            {
                SelectedAgent agent = selection.Agents[index];

                // RunAgentAsync never throws: a failing agent is data, so one
                // expert timing out cannot cancel the other four mid-flight.
                results[index] = await RunOneAgentAsync(
                    activities,
                    new RunAgentInput(
                        incident,
                        agent,
                        classification,
                        input.ConfigurationSnapshotId,
                        input.CorrelationId,
                        input.Deadline),
                    token);
            },
            cancellationToken);

        return results;
    }

    private static async Task CompleteWithoutAgentsAsync(
        IInvestigationActivities activities,
        InvestigationWorkflowInput input,
        CancellationToken cancellationToken)
    {
        CorrelationResult empty = new(
            "No agent was both eligible and available for this incident, so nothing was investigated.",
            Category: null,
            Confidence.None,
            KeyObservations: [],
            RankedHypotheses: [],
            EvidenceGaps: ["No expert was available. See the timeline for why each was excluded."]);

        Result<PersistedReport> report = await activities.PersistReportAsync(
            new PersistReportInput(
                input.IncidentId,
                input.ConfigurationSnapshotId,
                InvestigationOutcome.InsufficientAgentCapability,
                empty,
                new RecommendationSet([]),
                ContributingAgents: [],
                CitedEvidenceIds: [],
                input.CorrelationId),
            cancellationToken);

        if (report.IsSuccess)
        {
            await activities.CompleteInvestigationAsync(
                new CompleteInvestigationInput(
                    input.IncidentId,
                    InvestigationOutcome.InsufficientAgentCapability,
                    report.Value.Version,
                    input.CorrelationId),
                cancellationToken);
        }
    }

    /// <summary>Classifies the run as complete or partial.</summary>
    /// <remarks>
    /// Partial is not a failure. Section 11.3 asks for a controlled partial
    /// result, and the report carries the outcome so an operator can see that one
    /// layer went unexamined rather than assuming the investigation was thorough.
    /// </remarks>
    private static InvestigationOutcome DetermineOutcome(
        IReadOnlyList<AgentRunResult> runs,
        bool recommendationsSucceeded)
    {
        bool anyBudget = runs.Any(static run => run.Status is AgentRunStatus.BudgetExceeded);
        bool anyFailed = runs.Any(static run =>
            run.Status is AgentRunStatus.Failed or AgentRunStatus.TimedOut or AgentRunStatus.Cancelled);

        if (anyBudget)
        {
            return InvestigationOutcome.BudgetExceeded;
        }

        return anyFailed || !recommendationsSucceeded
            ? InvestigationOutcome.Partial
            : InvestigationOutcome.Complete;
    }

    private static IReadOnlyList<string> ContributingAgents(IReadOnlyList<AgentRunResult> runs)
        => [.. runs
            .Where(static run => !run.IsShadow && run.Finding is not null)
            .Select(static run => run.AgentName)];

    private static IReadOnlyList<EvidenceId> CitedEvidence(IReadOnlyList<AgentRunResult> runs)
    {
        HashSet<EvidenceId> cited = [];

        foreach (AgentRunResult run in runs.Where(static run => !run.IsShadow))
        {
            foreach (EvidenceId evidenceId in run.EvidenceIds)
            {
                cited.Add(evidenceId);
            }
        }

        return cited.Count == 0 ? [] : [.. cited];
    }
}
