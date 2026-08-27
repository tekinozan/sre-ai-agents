using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Tracks one agent execution against its budget (ARCHITECTURE.md 11.3).
/// </summary>
/// <remarks>
/// Every limit is checked before the work rather than after it. Discovering that
/// the token budget is exhausted once the tokens have been spent is an audit
/// trail, not a budget; the point is that the call does not happen.
///
/// Exhausting a budget is never an error. Section 11.3 asks for a controlled
/// partial result, so the tracker reports that the agent should stop and the
/// executor returns whatever was gathered. An agent that found three useful
/// observations before running out of iterations is worth more than nothing.
/// </remarks>
public sealed class AgentBudgetTracker
{
    private readonly AgentExecutionBudget _budget;
    private readonly DateTimeOffset _deadline;
    private readonly TimeProvider _timeProvider;

    private int _iterations;
    private int _llmCalls;
    private int _toolCalls;
    private long _tokens;
    private long _inputTokens;
    private long _outputTokens;
    private decimal _cost;

    public AgentBudgetTracker(AgentExecutionBudget budget, DateTimeOffset deadline, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _budget = budget;
        _deadline = deadline;
        _timeProvider = timeProvider;
    }

    public int LlmCallCount => _llmCalls;

    public int ToolCallCount => _toolCalls;

    public long TotalTokens => _tokens;

    /// <summary>What the run consumed, for the audit trail and the run record.</summary>
    public AgentRunUsage Snapshot()
        => new(_llmCalls, _toolCalls, _inputTokens, _outputTokens, _cost);

    public decimal EstimatedCost => _cost;

    /// <summary>
    /// Whether another reason-and-act cycle may start, and if not, why.
    /// </summary>
    public BudgetVerdict TryBeginIteration()
    {
        if (_timeProvider.GetUtcNow() >= _deadline)
        {
            return BudgetVerdict.Exhausted("The agent deadline elapsed.");
        }

        if (_iterations >= _budget.MaxIterations)
        {
            return BudgetVerdict.Exhausted(
                $"Reached the iteration limit of {_budget.MaxIterations.ToString(CultureInfo.InvariantCulture)}.");
        }

        _iterations++;
        return BudgetVerdict.Allowed;
    }

    /// <summary>Whether another model call may be made.</summary>
    public BudgetVerdict TryBeginLlmCall()
    {
        if (_llmCalls >= _budget.MaxLlmCalls)
        {
            return BudgetVerdict.Exhausted(
                $"Reached the model call limit of {_budget.MaxLlmCalls.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (_tokens >= _budget.MaxTotalTokens)
        {
            return BudgetVerdict.Exhausted(
                $"Reached the token budget of {_budget.MaxTotalTokens.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (_cost >= _budget.MaxEstimatedCost)
        {
            return BudgetVerdict.Exhausted(
                $"Reached the cost budget of {_budget.MaxEstimatedCost.ToString(CultureInfo.InvariantCulture)}.");
        }

        _llmCalls++;
        return BudgetVerdict.Allowed;
    }

    /// <summary>Whether another tool call may be made.</summary>
    public BudgetVerdict TryBeginToolCall()
    {
        if (_toolCalls >= _budget.MaxToolCalls)
        {
            return BudgetVerdict.Exhausted(
                $"Reached the tool call limit of {_budget.MaxToolCalls.ToString(CultureInfo.InvariantCulture)}.");
        }

        _toolCalls++;
        return BudgetVerdict.Allowed;
    }

    /// <summary>Records what a completed model call actually consumed.</summary>
    public void RecordUsage(long inputTokens, long outputTokens, decimal estimatedCost)
    {
        _tokens += inputTokens + outputTokens;
        _inputTokens += inputTokens;
        _outputTokens += outputTokens;
        _cost += estimatedCost;
    }

    /// <summary>The remaining time, for computing a per-call timeout.</summary>
    public TimeSpan RemainingTime()
    {
        TimeSpan remaining = _deadline - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

/// <summary>Whether an operation is within budget.</summary>
/// <param name="IsAllowed">True when the operation may proceed.</param>
/// <param name="Reason">Operator-readable explanation when it may not.</param>
public readonly record struct BudgetVerdict(bool IsAllowed, string? Reason)
{
    public static BudgetVerdict Allowed => new(true, null);

    public static BudgetVerdict Exhausted(string reason) => new(false, reason);

    /// <summary>The stable code for a budget stop, for the report and the metrics.</summary>
    public static ErrorCode Code => ApplicationErrorCodes.BudgetExceeded;
}
