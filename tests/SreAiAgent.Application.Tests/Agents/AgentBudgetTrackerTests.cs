using SreAiAgent.Application.Agents;

namespace SreAiAgent.Application.Tests.Agents;

/// <summary>
/// Covers the budget enforcement of ARCHITECTURE.md 11.3.
/// </summary>
/// <remarks>
/// The property under test throughout is that a limit is checked before the work,
/// not after it. A budget discovered to be exhausted once the tokens are spent is
/// an audit trail, not a budget.
/// </remarks>
public sealed class AgentBudgetTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    private static AgentExecutionBudget Budget(
        int iterations = 3,
        int toolCalls = 4,
        int llmCalls = 4,
        long tokens = 10_000,
        decimal cost = 1m)
        => new(iterations, TimeSpan.FromSeconds(90), toolCalls, llmCalls, tokens, cost);

    private static AgentBudgetTracker Tracker(
        AgentExecutionBudget? budget = null,
        TimeSpan? untilDeadline = null,
        FakeClock? clock = null)
    {
        FakeClock time = clock ?? new FakeClock(Now);
        return new AgentBudgetTracker(
            budget ?? Budget(),
            Now + (untilDeadline ?? TimeSpan.FromMinutes(5)),
            time);
    }

    [Fact]
    public void IterationsAreCappped()
    {
        AgentBudgetTracker tracker = Tracker(Budget(iterations: 2));

        tracker.TryBeginIteration().IsAllowed.ShouldBeTrue();
        tracker.TryBeginIteration().IsAllowed.ShouldBeTrue();

        BudgetVerdict third = tracker.TryBeginIteration();
        third.IsAllowed.ShouldBeFalse();
        third.Reason.ShouldNotBeNull().ShouldContain("iteration limit");
    }

    [Fact]
    public void ModelCallsAreCapped()
    {
        AgentBudgetTracker tracker = Tracker(Budget(llmCalls: 1));

        tracker.TryBeginLlmCall().IsAllowed.ShouldBeTrue();
        tracker.TryBeginLlmCall().IsAllowed.ShouldBeFalse();
        tracker.LlmCallCount.ShouldBe(1);
    }

    [Fact]
    public void ToolCallsAreCapped()
    {
        AgentBudgetTracker tracker = Tracker(Budget(toolCalls: 2));

        tracker.TryBeginToolCall().IsAllowed.ShouldBeTrue();
        tracker.TryBeginToolCall().IsAllowed.ShouldBeTrue();
        tracker.TryBeginToolCall().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void TheTokenBudgetStopsTheNextCallRatherThanTheCurrentOne()
    {
        AgentBudgetTracker tracker = Tracker(Budget(tokens: 1000));

        tracker.TryBeginLlmCall().IsAllowed.ShouldBeTrue();
        tracker.RecordUsage(inputTokens: 900, outputTokens: 400, estimatedCost: 0.01m);

        // A model call cannot be aborted mid-flight, so the budget is enforced at
        // the next boundary. The overshoot is bounded by one call, which is the
        // best any token budget can do.
        BudgetVerdict next = tracker.TryBeginLlmCall();
        next.IsAllowed.ShouldBeFalse();
        next.Reason.ShouldNotBeNull().ShouldContain("token budget");
        tracker.TotalTokens.ShouldBe(1300);
    }

    [Fact]
    public void TheCostBudgetStopsFurtherCalls()
    {
        AgentBudgetTracker tracker = Tracker(Budget(cost: 0.50m));

        tracker.TryBeginLlmCall().IsAllowed.ShouldBeTrue();
        tracker.RecordUsage(100, 100, estimatedCost: 0.60m);

        tracker.TryBeginLlmCall().IsAllowed.ShouldBeFalse();
        tracker.EstimatedCost.ShouldBe(0.60m);
    }

    [Fact]
    public void ThePassingDeadlineStopsFurtherIterations()
    {
        FakeClock clock = new(Now);
        AgentBudgetTracker tracker = Tracker(untilDeadline: TimeSpan.FromSeconds(60), clock: clock);

        tracker.TryBeginIteration().IsAllowed.ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));

        BudgetVerdict verdict = tracker.TryBeginIteration();
        verdict.IsAllowed.ShouldBeFalse();
        verdict.Reason.ShouldNotBeNull().ShouldContain("deadline");
    }

    [Fact]
    public void RemainingTimeNeverGoesNegative()
    {
        FakeClock clock = new(Now);
        AgentBudgetTracker tracker = Tracker(untilDeadline: TimeSpan.FromSeconds(30), clock: clock);

        clock.Advance(TimeSpan.FromMinutes(5));

        // A negative timeout passed to a CancellationTokenSource throws, which
        // would turn an expired budget into an unhandled exception on the fan-out.
        tracker.RemainingTime().ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ExhaustionIsReportedAsABudgetCodeNotAFailure()
        => BudgetVerdict.Code.Value.ShouldBe("BUDGET_EXCEEDED");

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
