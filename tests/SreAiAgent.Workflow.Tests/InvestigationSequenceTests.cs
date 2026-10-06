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

    /// <summary>
    /// Builds the in-process sequence with a scope factory that hands every agent
    /// run the same activities instance the test is asserting on.
    /// </summary>
    /// <remarks>
    /// Production resolves a fresh scope per agent, because a DbContext cannot be
    /// shared across a fan-out. These tests care about ordering and merging, so
    /// the factory returns the recording fake rather than a real container.
    /// </remarks>
    private static InlineInvestigationSequence Sequence(IInvestigationActivities activities)
        => new(4, new SingleInstanceScopeFactory(activities));

    [Fact]
    public async Task TheStepsRunInTheOrderTheArchitectureDefines()
    {
        RecordingActivities activities = new();

        await Sequence(activities).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

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

        await Sequence(activities).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.PersistedReport.ShouldNotBeNull().ContributingAgents.ShouldBe(
            ["database-expert", "kubernetes-expert", "log-expert"]);
    }

    [Fact]
    public async Task NoEligibleAgentProducesAReportRatherThanAFailure()
    {
        RecordingActivities activities = new() { Agents = [] };

        await Sequence(activities).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

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

        await Sequence(activities).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

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

        await Sequence(activities).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.Calls.ShouldBe(["load", "fail"]);
        activities.Failed.ShouldNotBeNull().ErrorCode.ShouldBe(ApplicationErrorCodes.IncidentNotFound);
    }

    [Fact]
    public async Task TheInlineExecutorRefusesToPretendAnApprovalHappened()
    {
        RecordingActivities activities = new() { ApprovalRequired = true };

        await Sequence(activities).ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.PersistedReport.ShouldBeNull();
        activities.Failed.ShouldNotBeNull().ErrorCode.ShouldBe(ApplicationErrorCodes.ConfigInvalid);
    }

    [Theory]
    [InlineData("APPROVAL_REJECTED")]
    [InlineData("WORKFLOW_DEADLINE_EXCEEDED")]
    public async Task ARejectedOrUnansweredApprovalStillReportsTheDiagnosis(string code)
    {
        // The rejection is about the proposed fix, not about the correlation.
        // Failing here lost the RCA exactly when a human had engaged with it.
        RecordingActivities activities = new() { ApprovalRequired = true };
        ScriptedApprovalSequence sequence = new(Result.Failure(Error.Permanent(new ErrorCode(code), "No.")));

        await sequence.ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.Failed.ShouldBeNull();
        activities.PersistedReport.ShouldNotBeNull();
        activities.Calls.ShouldBe(
        [
            "load", "resolve", "classify", "select", "agent:database-expert",
            "correlate", "recommend", "approval", "persist", "complete",
        ]);
    }

    [Fact]
    public async Task AnApprovalWaitThatFailsForAnyOtherReasonStillFailsTheInvestigation()
    {
        RecordingActivities activities = new() { ApprovalRequired = true };
        ScriptedApprovalSequence sequence = new(
            Result.Failure(Error.Permanent(ApplicationErrorCodes.WorkflowCancelled, "Cancelled while waiting.")));

        await sequence.ExecuteAsync(activities, Input, TestContext.Current.CancellationToken);

        activities.PersistedReport.ShouldBeNull();
        activities.Failed.ShouldNotBeNull().ErrorCode.ShouldBe(ApplicationErrorCodes.WorkflowCancelled);
    }

    /// <summary>
    /// A sequence whose approval wait returns a fixed answer, standing in for the
    /// durable engine's signal without a Temporal test server.
    /// </summary>
    private sealed class ScriptedApprovalSequence(Result answer) : InvestigationSequence
    {
        protected override async Task ForEachAgentAsync(
            int count,
            Func<int, CancellationToken, Task> body,
            CancellationToken cancellationToken)
        {
            for (int index = 0; index < count; index++)
            {
                await body(index, cancellationToken);
            }
        }

        protected override Task<Result> WaitForApprovalAsync(
            ApprovalDecision decision,
            InvestigationWorkflowInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(answer);
    }
}
