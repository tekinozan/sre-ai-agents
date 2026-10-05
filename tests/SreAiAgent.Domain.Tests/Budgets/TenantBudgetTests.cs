using SreAiAgent.Domain.Budgets;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Tests.Budgets;

public sealed class TenantBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 15, 30, 0, TimeSpan.FromHours(3));

    [Fact]
    public void AMonthlyBudgetCountsFromTheFirstOfTheUtcMonth()
    {
        TenantBudget budget = TenantBudget.Create("acme", 100m, "usd", BudgetPeriod.Monthly, Now, Now, "alice");

        budget.PeriodStart(Now).ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        budget.Currency.ShouldBe("USD");
    }

    [Fact]
    public void AMonthlyBudgetUsesTheUtcMonthEvenWhenTheLocalDateIsAlreadyTheNext()
    {
        // 1 November 01:00 at +03:00 is still 31 October in UTC.
        DateTimeOffset local = new(2026, 11, 1, 1, 0, 0, TimeSpan.FromHours(3));
        TenantBudget budget = TenantBudget.Create("acme", 100m, "USD", BudgetPeriod.Monthly, local, local, "alice");

        budget.PeriodStart(local).ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AFixedBudgetNeverResets()
    {
        DateTimeOffset start = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        TenantBudget budget = TenantBudget.Create("acme", 500m, "EUR", BudgetPeriod.Fixed, start, Now, "alice");

        budget.PeriodStart(Now).ShouldBe(start);
        budget.PeriodStart(Now.AddYears(1)).ShouldBe(start);
    }

    [Theory]
    [InlineData(0, "USD")]
    [InlineData(-1, "USD")]
    [InlineData(10, "US")]
    [InlineData(10, "U$D")]
    public void AnInvalidLimitOrCurrencyIsRefused(int limit, string currency)
        => Should.Throw<DomainException>(() =>
                TenantBudget.Create("acme", limit, currency, BudgetPeriod.Monthly, Now, Now, "alice"))
            .Code.ShouldBe(DomainErrorCodes.BudgetInvalid);
}
