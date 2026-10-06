using SreAiAgent.Domain.Budgets;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>A tenant's budget together with what has been spent against it.</summary>
/// <param name="Budget">The policy.</param>
/// <param name="PeriodStartedAt">Where the current period's count begins.</param>
/// <param name="Spent">Estimated model cost recorded since then, failed calls included.</param>
public sealed record TenantBudgetStatus(TenantBudget Budget, DateTimeOffset PeriodStartedAt, decimal Spent)
{
    /// <summary>What is left before calls are refused; never negative.</summary>
    public decimal Remaining => Math.Max(0m, Budget.Limit - Spent);

    /// <summary>Whether the next model call would be refused.</summary>
    public bool IsExhausted => Spent >= Budget.Limit;
}

/// <summary>Manages a tenant's model-cost budget (gemini-budgets-notifications.md B).</summary>
public interface ITenantBudgetService
{
    /// <summary>The tenant's budget and spend, or null when it has none.</summary>
    Task<TenantBudgetStatus?> GetAsync(string tenant, CancellationToken cancellationToken);

    /// <summary>Creates or replaces the tenant's budget.</summary>
    Task<Result<TenantBudgetStatus>> SetAsync(
        string tenant,
        decimal limit,
        string currency,
        BudgetPeriod period,
        DateTimeOffset? startsAt,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>Removes the tenant's budget, which lifts the ceiling.</summary>
    Task<Result> RemoveAsync(string tenant, string actor, CancellationToken cancellationToken);
}
