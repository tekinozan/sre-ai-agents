using Microsoft.EntityFrameworkCore;
using Npgsql;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Budgets;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>
/// Decides, before a model call, whether the tenant may still spend.
/// </summary>
internal interface ITenantBudgetGuard
{
    /// <summary>Succeeds when the tenant has no budget or has budget left.</summary>
    Task<Result> CheckAsync(string tenant, CancellationToken cancellationToken);
}

/// <summary>
/// The guard used where there is no database, such as the eval harness. A
/// tenant with no budget has no ceiling, so this is the honest answer there.
/// </summary>
internal sealed class UnlimitedBudgetGuard : ITenantBudgetGuard
{
    public Task<Result> CheckAsync(string tenant, CancellationToken cancellationToken)
        => Task.FromResult(Result.Success());
}

/// <summary>
/// Tenant budgets backed by PostgreSQL (gemini-budgets-notifications.md B, D47).
/// </summary>
/// <remarks>
/// Spend is the sum of <c>llm_calls.estimated_cost</c> since the period began,
/// failed calls included: a provider charges for a truncated or schema-invalid
/// answer exactly as for a good one. Only committed rows count, and an activity
/// commits its audit rows when it finishes, so concurrent agents can each make
/// one more call than the limit strictly allows. That overshoot is bounded and
/// accepted; locking every model call on a per-tenant counter is not.
/// </remarks>
internal sealed class TenantBudgetService(
    SreDbContext context,
    ModelProviderRepository providers,
    TimeProvider timeProvider) : ITenantBudgetService, ITenantBudgetGuard
{
    public async Task<Result> CheckAsync(string tenant, CancellationToken cancellationToken)
    {
        TenantBudgetStatus? status = await GetAsync(tenant, cancellationToken).ConfigureAwait(false);
        return status is null || !status.IsExhausted
            ? Result.Success()
            : Result.Failure(Error.Permanent(
                ApplicationErrorCodes.BudgetExceeded,
                $"The tenant's {Describe(status.Budget.Period)} model budget of "
                + $"{status.Budget.Limit.ToString("0.##", CultureInfo.InvariantCulture)} {status.Budget.Currency} is spent."));
    }

    public async Task<TenantBudgetStatus?> GetAsync(string tenant, CancellationToken cancellationToken)
    {
        TenantBudgetEntity? entity = await context.TenantBudgets
            .AsNoTracking()
            .SingleOrDefaultAsync(budget => budget.Tenant == tenant, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        TenantBudget budget = ToDomain(entity);
        DateTimeOffset since = budget.PeriodStart(timeProvider.GetUtcNow());
        decimal spent = await context.LlmCalls
            .Where(call => call.Tenant == tenant && call.CalledAt >= since)
            .SumAsync(call => call.EstimatedCost, cancellationToken)
            .ConfigureAwait(false);

        return new TenantBudgetStatus(budget, since, spent);
    }

    public async Task<Result<TenantBudgetStatus>> SetAsync(
        string tenant,
        decimal limit,
        string currency,
        BudgetPeriod period,
        DateTimeOffset? startsAt,
        string actor,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        TenantBudget budget;
        try
        {
            budget = TenantBudget.Create(tenant, limit, currency, period, startsAt ?? now, now, actor);
        }
        catch (DomainException exception)
        {
            return Result<TenantBudgetStatus>.Failure(Error.Permanent(exception.Code, exception.Message));
        }

        // The assignment incidents pin today must be priced in the budget's
        // currency, or its spend could never be compared with the limit.
        PinnedModelAssignment? active = await providers
            .GetActiveSnapshotAsync(TenantScope.TenantWide(tenant), cancellationToken)
            .ConfigureAwait(false);
        if (active is not null && ForeignCurrency(active, budget.Currency) is { } foreign)
        {
            return Result<TenantBudgetStatus>.Failure(Error.Permanent(
                ApplicationErrorCodes.BudgetCurrencyMismatch,
                $"The active model assignment is priced in {foreign}, not {budget.Currency}."));
        }

        TenantBudgetEntity? entity = await context.TenantBudgets
            .AsTracking()
            .SingleOrDefaultAsync(row => row.Tenant == tenant, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            entity = new TenantBudgetEntity { Tenant = tenant };
            context.TenantBudgets.Add(entity);
        }

        entity.LimitAmount = budget.Limit;
        entity.Currency = budget.Currency;
        entity.Period = budget.Period.ToString();
        entity.StartsAt = budget.StartsAt;
        entity.UpdatedAt = budget.UpdatedAt;
        entity.UpdatedBy = budget.UpdatedBy;
        providers.AddAudit(tenant, actor, "set", "tenant_budget", Guid.Empty, BudgetHash(budget), now);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            return Result<TenantBudgetStatus>.Failure(Error.Transient(
                ApplicationErrorCodes.ModelResourceConflict,
                "Another request set this tenant's budget at the same time. Retry."));
        }

        TenantBudgetStatus? status = await GetAsync(tenant, cancellationToken).ConfigureAwait(false);
        return Result<TenantBudgetStatus>.Success(status!);
    }

    public async Task<Result> RemoveAsync(string tenant, string actor, CancellationToken cancellationToken)
    {
        int removed = await context.TenantBudgets
            .Where(budget => budget.Tenant == tenant)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (removed == 0)
        {
            return Result.Failure(Error.Permanent(ApplicationErrorCodes.BudgetNotFound, "The tenant has no budget."));
        }

        providers.AddAudit(tenant, actor, "remove", "tenant_budget", Guid.Empty, string.Empty, timeProvider.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>The first slot priced in a currency other than <paramref name="currency"/>, if any.</summary>
    internal static string? ForeignCurrency(PinnedModelAssignment assignment, string currency)
        => new[] { assignment.Classifier, assignment.Correlator }
            .Concat(assignment.Agents.Values)
            .Select(static candidate => candidate.Currency)
            .FirstOrDefault(candidateCurrency =>
                !string.Equals(candidateCurrency, currency, StringComparison.OrdinalIgnoreCase));

    private static TenantBudget ToDomain(TenantBudgetEntity entity)
        => TenantBudget.Restore(
            entity.Tenant,
            entity.LimitAmount,
            entity.Currency,
            Enum.Parse<BudgetPeriod>(entity.Period),
            entity.StartsAt,
            entity.UpdatedAt,
            entity.UpdatedBy);

    private static string BudgetHash(TenantBudget budget)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('|', budget.Limit.ToString(CultureInfo.InvariantCulture), budget.Currency, budget.Period, budget.StartsAt.ToString("O", CultureInfo.InvariantCulture)))))
            .ToLowerInvariant();

    private static string Describe(BudgetPeriod period) => period == BudgetPeriod.Monthly ? "monthly" : "fixed";
}
