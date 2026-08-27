using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Workflow.Inline;

namespace SreAiAgent.Workflow.Tests;

/// <summary>
/// Covers the shared sequence of ARCHITECTURE.md 11.1.
/// </summary>
/// <remarks>
/// These run against the in-process fan-out, which is the one that can actually
/// interleave: it dispatches to the thread pool, so if the merge depended on
/// completion order this is where it would show. The durable strategy runs
/// single-threaded on the engine's scheduler and merges through the same code.
/// </remarks>
public sealed class InvestigationSequenceTests
{
    private static readonly InvestigationWorkflowInput Input = new(
        RecordingActivities.Incident,
        "acme",
        RecordingActivities.Snapshot,
        Attempt: 1,
        DateTimeOffset.UnixEpoch.AddYears(100),
        "corr-1");

    [Fact]
    public async Task TheStepsRunInTheOrderTheArchitectureDefines()
    {
        RecordingActivities activities = new();

        await new InlineInvestigationSequence(4).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.Calls.ShouldBe(
        [
            "load", "resolve", "classify", "select", "agent:database-expert",
            "correlate", "recommend", "approval", "persist", "complete",
        ]);
    }

    [Fact]
    public async Task ResultsAreMergedBySelectionOrderNotByCompletionOrder()
    {
        // The first agent finishes last. A merge that appended as results
        // arrived would report them backwards, and a report whose contributors
        // reorder between runs cannot be diffed or evaluated.
        RecordingActivities activities = new()
        {
            Agents =
            [
                new SelectedAgent("database-expert", "1.0.0", IsShadow: false),
                new SelectedAgent("kubernetes-expert", "1.0.0", IsShadow: false),
                new SelectedAgent("log-expert", "1.0.0", IsShadow: false),
            ],
        };

        activities.OnRunAgent = async input =>
        {
            int delay = input.Agent.AgentName switch
            {
                "database-expert" => 60,
                "kubernetes-expert" => 30,
                _ => 1,
            };

            await Task.Delay(delay, TestContext.Current.CancellationToken);
            return RecordingActivities.Succeeded(input.Agent);
        };

        await new InlineInvestigationSequence(4).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.PersistedReport.ShouldNotBeNull().ContributingAgents.ShouldBe(
            ["database-expert", "kubernetes-expert", "log-expert"]);
    }

    [Fact]
    public async Task NoEligibleAgentProducesAReportRatherThanAFailure()
    {
        RecordingActivities activities = new() { Agents = [] };

        await new InlineInvestigationSequence(4).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.Failed.ShouldBeNull();
        activities.PersistedReport.ShouldNotBeNull().Outcome
            .ShouldBe(InvestigationOutcome.InsufficientAgentCapability);
        activities.Completed.ShouldNotBeNull();
    }

    [Fact]
    public async Task OneFailingAgentDowngradesTheOutcomeInsteadOfEndingTheRun()
    {
        RecordingActivities activities = new()
        {
            Agents =
            [
                new SelectedAgent("database-expert", "1.0.0", IsShadow: false),
                new SelectedAgent("log-expert", "1.0.0", IsShadow: false),
            ],
        };

        activities.OnRunAgent = input => Task.FromResult(input.Agent.AgentName == "log-expert"
            ? RecordingActivities.FailedWith(input.Agent, AgentRunStatus.TimedOut)
            : RecordingActivities.Succeeded(input.Agent));

        await new InlineInvestigationSequence(4).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        PersistReportInput report = activities.PersistedReport.ShouldNotBeNull();
        report.Outcome.ShouldBe(InvestigationOutcome.Partial);
        report.ContributingAgents.ShouldBe(["database-expert"]);
    }

    [Fact]
    public async Task ALoadFailureStopsBeforeSpendingAnything()
    {
        RecordingActivities activities = new()
        {
            LoadIncidentError = Error.Permanent(ApplicationErrorCodes.IncidentNotFound, "Gone."),
        };

        await new InlineInvestigationSequence(4).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.Calls.ShouldBe(["load", "fail"]);
        activities.Failed.ShouldNotBeNull().ErrorCode.ShouldBe(ApplicationErrorCodes.IncidentNotFound);
    }

    [Fact]
    public async Task TheInlineExecutorRefusesToPretendAnApprovalHappened()
    {
        RecordingActivities activities = new() { ApprovalRequired = true };

        await new InlineInvestigationSequence(4).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.PersistedReport.ShouldBeNull();
        activities.Failed.ShouldNotBeNull().ErrorCode.ShouldBe(ApplicationErrorCodes.ConfigInvalid);
    }
}
