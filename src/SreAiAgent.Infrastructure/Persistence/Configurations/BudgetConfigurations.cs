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

/// <summary>Maps notification channels to <c>notification_channels</c>.</summary>
internal sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannelEntity>
{
    public void Configure(EntityTypeBuilder<NotificationChannelEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_channels");
        builder.HasKey(channel => channel.Id);
        builder.Property(channel => channel.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(channel => channel.Kind).HasMaxLength(16).IsRequired();
        builder.Property(channel => channel.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(channel => channel.WebhookRef).HasMaxLength(512).IsRequired();
        builder.Property(channel => channel.CreatedBy).HasMaxLength(256).IsRequired();

        // A name an operator can tell apart from the tenant's other channels.
        builder.HasIndex(channel => new { channel.Tenant, channel.DisplayName })
            .IsUnique()
            .HasDatabaseName("ux_notification_channels_tenant_name");
    }
}

/// <summary>Maps delivery claims to <c>notification_deliveries</c>.</summary>
internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDeliveryEntity>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_deliveries");

        // The primary key is the claim: two replicas or two deliveries of one
        // event cannot both insert it, so only one of them sends.
        builder.HasKey(delivery => new { delivery.DeliveryKey, delivery.ChannelId });
        builder.Property(delivery => delivery.DeliveryKey).HasMaxLength(256);
        builder.Property(delivery => delivery.Tenant).HasMaxLength(128).IsRequired();
        builder.HasIndex(delivery => delivery.ClaimedAt).HasDatabaseName("ix_notification_deliveries_claimed_at");
    }
}
