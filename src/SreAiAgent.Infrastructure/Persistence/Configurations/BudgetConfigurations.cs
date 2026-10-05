using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps tenant budgets to <c>tenant_budgets</c>, one row per tenant.</summary>
internal sealed class TenantBudgetConfiguration : IEntityTypeConfiguration<TenantBudgetEntity>
{
    public void Configure(EntityTypeBuilder<TenantBudgetEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tenant_budgets");
        builder.HasKey(budget => budget.Tenant);
        builder.Property(budget => budget.Tenant).HasMaxLength(128);

        // The same precision as llm_calls.estimated_cost, so a limit and the
        // spend compared with it are the same kind of number.
        builder.Property(budget => budget.LimitAmount).HasPrecision(18, 6);
        builder.Property(budget => budget.Currency).HasMaxLength(3).IsRequired();
        builder.Property(budget => budget.Period).HasMaxLength(16).IsRequired();
        builder.Property(budget => budget.UpdatedBy).HasMaxLength(256).IsRequired();
    }
}
