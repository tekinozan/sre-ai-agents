namespace SreAiAgent.Domain.Findings;

/// <summary>
/// How an investigation ended. A report always exists, even when the
/// investigation could not finish: ARCHITECTURE.md 11.3 requires a controlled
/// partial result rather than a failure with nothing to show.
/// </summary>
public enum InvestigationOutcome
{
    /// <summary>Every selected agent completed and the findings were correlated.</summary>
    Complete = 0,

    /// <summary>Some agents failed or timed out; the report covers the rest.</summary>
    Partial = 1,

    /// <summary>A token, cost, iteration or runtime budget stopped the work.</summary>
    BudgetExceeded = 2,

    /// <summary>The workflow deadline elapsed.</summary>
    DeadlineExceeded = 3,

    /// <summary>
    /// No agent was both eligible and available for this incident, after flag
    /// evaluation and tool validation (ARCHITECTURE.md 24.3).
    /// </summary>
    InsufficientAgentCapability = 4,

    /// <summary>An operator cancelled the incident mid-investigation.</summary>
    Cancelled = 5,
}
