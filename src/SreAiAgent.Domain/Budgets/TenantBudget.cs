using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Budgets;

/// <summary>How a tenant's model spend is measured against its limit.</summary>
public enum BudgetPeriod
{
    /// <summary>The limit applies to each UTC calendar month and resets on the first.</summary>
    Monthly = 0,

    /// <summary>The limit applies to everything spent since <see cref="TenantBudget.StartsAt"/>; it never resets.</summary>
    Fixed = 1,
}

/// <summary>
/// A tenant's ceiling on estimated model cost (gemini-budgets-notifications.md B).
/// </summary>
/// <remarks>
/// One per tenant, and tenant-wide: incidents carry no product (D41), so a
/// narrower budget would have nothing to attribute spend to. No budget means no
/// ceiling, which is how every tenant behaved before this existed.
/// </remarks>
public sealed record TenantBudget
{
    private TenantBudget(
        string tenant,
        decimal limit,
        string currency,
        BudgetPeriod period,
        DateTimeOffset startsAt,
        DateTimeOffset updatedAt,
        string updatedBy)
    {
        Tenant = tenant;
        Limit = limit;
        Currency = currency;
        Period = period;
        StartsAt = startsAt;
        UpdatedAt = updatedAt;
        UpdatedBy = updatedBy;
    }

    public string Tenant { get; }
    public decimal Limit { get; }

    /// <summary>ISO 4217 code, upper case. Spend in another currency is not comparable and is refused upstream.</summary>
    public string Currency { get; }

    public BudgetPeriod Period { get; }

    /// <summary>When a <see cref="BudgetPeriod.Fixed"/> budget starts counting. Ignored for monthly budgets.</summary>
    public DateTimeOffset StartsAt { get; }

    public DateTimeOffset UpdatedAt { get; }
    public string UpdatedBy { get; }

    /// <summary>Validates and creates a budget.</summary>
    /// <exception cref="DomainException">A field is out of range.</exception>
    public static TenantBudget Create(
        string tenant,
        decimal limit,
        string currency,
        BudgetPeriod period,
        DateTimeOffset startsAt,
        DateTimeOffset updatedAt,
        string updatedBy)
    {
        Guard.AgainstNullOrWhiteSpace(tenant, DomainErrorCodes.BudgetInvalid);
        Guard.AgainstNullOrWhiteSpace(updatedBy, DomainErrorCodes.BudgetInvalid);
        if (limit <= 0m || limit > 1_000_000_000m)
        {
            throw new DomainException(DomainErrorCodes.BudgetInvalid, "The budget limit must be positive and at most 1,000,000,000.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new DomainException(DomainErrorCodes.BudgetInvalid, "The budget currency must be a three-letter ISO 4217 code.");
        }

        if (!Enum.IsDefined(period))
        {
            throw new DomainException(DomainErrorCodes.BudgetInvalid, "The budget period is not known.");
        }

        return new TenantBudget(
            tenant,
            limit,
            currency.ToUpperInvariant(),
            period,
            startsAt.ToUniversalTime(),
            updatedAt.ToUniversalTime(),
            updatedBy);
    }

    /// <summary>Recreates a budget read from storage.</summary>
    public static TenantBudget Restore(
        string tenant,
        decimal limit,
        string currency,
        BudgetPeriod period,
        DateTimeOffset startsAt,
        DateTimeOffset updatedAt,
        string updatedBy)
        => new(tenant, limit, currency, period, startsAt, updatedAt, updatedBy);

    /// <summary>The instant spend is counted from at <paramref name="now"/>.</summary>
    public DateTimeOffset PeriodStart(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        return Period == BudgetPeriod.Monthly
            ? new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero)
            : StartsAt;
    }
}
