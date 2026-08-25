namespace SreAiAgent.Domain.Findings;

/// <summary>Outcome of one agent execution.</summary>
public enum AgentRunStatus
{
    Running = 0,

    /// <summary>Completed within budget and produced a valid finding.</summary>
    Succeeded = 1,

    /// <summary>
    /// Hit an iteration, token, cost or time budget. Whatever was gathered is
    /// kept: ARCHITECTURE.md 11.3 requires a controlled partial result rather
    /// than an unbounded loop.
    /// </summary>
    BudgetExceeded = 2,

    /// <summary>Exceeded its own timeout inside the workflow deadline.</summary>
    TimedOut = 3,

    /// <summary>Failed. Other agents in the same fan-out are unaffected.</summary>
    Failed = 4,

    /// <summary>Cancelled because the incident or workflow was cancelled.</summary>
    Cancelled = 5,
}
