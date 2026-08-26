using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Workflow.Inline;

// The activity sequence of ARCHITECTURE.md 11.1:
//
//   LoadIncident -> ResolveConfiguration -> ClassifyIncident -> SelectAgents
//   -> RunSelectedAgentsInParallel -> CorrelateFindings -> BuildRecommendations
//   -> DecideApproval -> PersistReport -> Complete
//
// The Temporal adapter runs the same sequence against the same activities. This
// file is the sequence; everything it calls is engine-agnostic.

public sealed partial class InlineInvestigationWorkflow
{
    private async Task ExecuteAsync(
        IInvestigationActivities activities,
        InvestigationWorkflowInput input,
        CancellationToken cancellationToken)
    {
        Result<IncidentSnapshot> incident = await activities
            .LoadIncidentAsync(input.IncidentId, cancellationToken)
            .ConfigureAwait(false);

        if (incident.IsFailure)
        {
            await FailAsync(activities, input, incident.Error.Code, incident.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        Result<ResolvedConfiguration> configuration = await activities
            .ResolveConfigurationAsync(input.ConfigurationSnapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (configuration.IsFailure)
        {
            await FailAsync(activities, input, configuration.Error.Code, configuration.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        Result<IncidentClassification> classification = await activities
            .ClassifyIncidentAsync(
                new ClassifyIncidentInput(
                    incident.Value, input.ConfigurationSnapshotId, input.CorrelationId, input.Deadline),
                cancellationToken)
            .ConfigureAwait(false);

        if (classification.IsFailure)
        {
            await FailAsync(activities, input, classification.Error.Code, classification.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        Result<AgentSelection> selection = await activities
            .SelectAgentsAsync(
                new SelectAgentsInput(
                    incident.Value, classification.Value, input.ConfigurationSnapshotId, input.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (selection.IsFailure)
        {
            await FailAsync(activities, input, selection.Error.Code, selection.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        if (selection.Value.Agents.Count == 0)
        {
            // A reportable outcome rather than a failure. The operator still needs
            // an incident record explaining that nobody was available to look at
            // it, and the exclusion reasons are already on the timeline
            // (ARCHITECTURE.md 24.3).
            await CompleteWithoutAgentsAsync(activities, input, incident.Value, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<AgentRunResult> runs = await RunAgentsAsync(
            activities, input, incident.Value, classification.Value, selection.Value, cancellationToken)
            .ConfigureAwait(false);

        Result<CorrelationResult> correlation = await activities
            .CorrelateFindingsAsync(
                new CorrelateFindingsInput(
                    incident.Value, runs, input.ConfigurationSnapshotId, input.CorrelationId, input.Deadline),
                cancellationToken)
            .ConfigureAwait(false);

        if (correlation.IsFailure)
        {
            await FailAsync(activities, input, correlation.Error.Code, correlation.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        Result<RecommendationSet> recommendations = await activities
            .BuildRecommendationsAsync(
                new BuildRecommendationsInput(
                    incident.Value,
                    correlation.Value,
                    input.ConfigurationSnapshotId,
                    input.CorrelationId,
                    input.Deadline),
                cancellationToken)
            .ConfigureAwait(false);

        // A failed recommendation step does not discard the correlation. The
        // explanation is the expensive part and is worth reporting on its own; an
        // operator can act on a diagnosis without a suggested fix.
        RecommendationSet recommendationSet = recommendations.IsSuccess
            ? recommendations.Value
            : new RecommendationSet([]);

        Result<ApprovalDecision> approval = await activities
            .DecideApprovalAsync(
                new DecideApprovalInput(
                    incident.Value, recommendationSet, input.ConfigurationSnapshotId, input.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (approval.IsSuccess && approval.Value.IsApprovalRequired)
        {
            // The inline executor cannot wait for a signal, so it stops here with
            // the report unwritten rather than proceeding as though a human had
            // approved. V1 never reaches this branch, and if it ever does the
            // failure is loud.
            await FailAsync(
                activities,
                input,
                ApplicationErrorCodes.ConfigInvalid,
                "An approval is required, which the inline executor cannot wait for.")
                .ConfigureAwait(false);
            return;
        }

        InvestigationOutcome outcome = DetermineOutcome(runs, recommendations.IsSuccess);

        Result<PersistedReport> report = await activities
            .PersistReportAsync(
                new PersistReportInput(
                    input.IncidentId,
                    input.ConfigurationSnapshotId,
                    outcome,
                    correlation.Value,
                    recommendationSet,
                    ContributingAgents(runs),
                    CitedEvidence(runs),
                    input.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (report.IsFailure)
        {
            await FailAsync(activities, input, report.Error.Code, report.Error.Message).ConfigureAwait(false);
            return;
        }

        await activities.CompleteInvestigationAsync(
            new CompleteInvestigationInput(
                input.IncidentId, outcome, report.Value.Version, input.CorrelationId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the selected agents concurrently and merges their results
    /// deterministically.
    /// </summary>
    /// <remarks>
    /// Results are written into a pre-sized array by index rather than appended to
    /// a concurrent collection. Ordering has to be a function of the selection,
    /// not of which agent happened to finish first: ARCHITECTURE.md 3.3 requires a
    /// deterministic merge, and a report whose observations reorder between runs is
    /// impossible to diff and impossible to evaluate.
    ///
    /// Parallelism is bounded. Each agent holds a model connection and a database
    /// connection, and an unbounded fan-out on a wide incident is a self-inflicted
    /// outage of the backends being investigated.
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

        await Parallel.ForEachAsync(
            Enumerable.Range(0, selection.Agents.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _agentOptions.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                SelectedAgent agent = selection.Agents[index];

                // RunAgentAsync never throws: a failing agent is data, so one
                // expert timing out cannot cancel the other four mid-flight.
                results[index] = await activities
                    .RunAgentAsync(
                        new RunAgentInput(
                            incident,
                            agent,
                            classification,
                            input.ConfigurationSnapshotId,
                            input.CorrelationId,
                            input.Deadline),
                        token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        return results;
    }

    private static async Task CompleteWithoutAgentsAsync(
        IInvestigationActivities activities,
        InvestigationWorkflowInput input,
        IncidentSnapshot incident,
        CancellationToken cancellationToken)
    {
        CorrelationResult empty = new(
            "No agent was both eligible and available for this incident, so nothing was investigated.",
            Category: null,
            Confidence.None,
            KeyObservations: [],
            RankedHypotheses: [],
            EvidenceGaps: ["No expert was available. See the timeline for why each was excluded."]);

        Result<PersistedReport> report = await activities
            .PersistReportAsync(
                new PersistReportInput(
                    input.IncidentId,
                    input.ConfigurationSnapshotId,
                    InvestigationOutcome.InsufficientAgentCapability,
                    empty,
                    new RecommendationSet([]),
                    ContributingAgents: [],
                    CitedEvidenceIds: [],
                    input.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (report.IsSuccess)
        {
            await activities.CompleteInvestigationAsync(
                new CompleteInvestigationInput(
                    input.IncidentId,
                    InvestigationOutcome.InsufficientAgentCapability,
                    report.Value.Version,
                    input.CorrelationId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Classifies the run as complete or partial.
    /// </summary>
    /// <remarks>
    /// Partial is not a failure. Section 11.3 asks for a controlled partial result,
    /// and the report carries the outcome so an operator can see that one layer
    /// went unexamined rather than assuming the investigation was thorough.
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
