namespace SreAiAgent.Contracts.Budgets;

/// <summary>Body of <c>PUT /v1/budget</c>: the tenant's model-cost ceiling.</summary>
/// <remarks>
/// <c>period</c> is <c>monthly</c>, counted per UTC calendar month, or
/// <c>fixed</c>, a total counted from <c>starts_at</c> that never resets.
/// <c>starts_at</c> is optional and defaults to now; a monthly budget ignores it.
/// </remarks>
public sealed record SetBudgetRequest
{
    public required decimal Limit { get; init; }

    public required string Currency { get; init; }

    public required string Period { get; init; }

    public DateTimeOffset? StartsAt { get; init; }
}

/// <summary>The tenant's budget and what has been spent against it.</summary>
public sealed record BudgetResponse
{
    public required decimal Limit { get; init; }

    public required string Currency { get; init; }

    public required string Period { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    /// <summary>Where the current period's spend is counted from.</summary>
    public required DateTimeOffset PeriodStartedAt { get; init; }

    /// <summary>Estimated model cost since <see cref="PeriodStartedAt"/>, failed calls included.</summary>
    public required decimal Spent { get; init; }

    public required decimal Remaining { get; init; }

    /// <summary>True once model calls are being refused.</summary>
    public required bool Exhausted { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required string UpdatedBy { get; init; }
}
