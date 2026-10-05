using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps the outbox to <c>outbox_messages</c>.</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessageEntity>
{
    public void Configure(EntityTypeBuilder<OutboxMessageEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Topic).HasMaxLength(128).IsRequired();
        builder.Property(message => message.EventType).HasMaxLength(128).IsRequired();
        builder.Property(message => message.PartitionKey).HasMaxLength(128).IsRequired();
        builder.Property(message => message.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(message => message.TraceParent).HasMaxLength(64);
        builder.Property(message => message.LastError).HasMaxLength(2048);
        builder.Property(message => message.PayloadJson).HasColumnType("jsonb").IsRequired();

        // The dispatcher's claim query is the hottest query in the system: it runs
        // continuously on every replica. This partial index covers exactly the
        // rows it looks for and, crucially, excludes dispatched ones, so the index
        // stays small no matter how large the table grows.
        builder.HasIndex(message => new { message.NextAttemptAt, message.Id })
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("dispatched_at IS NULL AND is_dead_lettered = false");

        // Deduplication at the source. A use case that stages the same logical
        // event twice, for example on a retried command, produces one row.
        builder.HasIndex(message => message.EventId)
            .IsUnique()
            .HasDatabaseName("ux_outbox_event_id");
    }
}

/// <summary>Maps the inbox to <c>inbox_messages</c>.</summary>
internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessageEntity>
{
    /// <summary>
    /// Named once because the consumer tells a duplicate delivery apart from a
    /// lost race by which table the unique violation came from.
    /// </summary>
    internal const string TableName = "inbox_messages";

    public void Configure(EntityTypeBuilder<InboxMessageEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(TableName);

        // Keyed on event plus consumer, not event alone. Two consumers must each
        // be able to process the same message once; a single-column key would let
        // whichever consumer ran first silently suppress the other.
        builder.HasKey(message => new { message.EventId, message.ConsumerName });

        builder.Property(message => message.ConsumerName).HasMaxLength(128).IsRequired();
        builder.Property(message => message.EventType).HasMaxLength(128).IsRequired();

        builder.HasIndex(message => message.ProcessedAt).HasDatabaseName("ix_inbox_processed_at");
    }
}

/// <summary>Maps idempotency claims to <c>idempotency_keys</c>.</summary>
internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKeyEntity>
{
    public void Configure(EntityTypeBuilder<IdempotencyKeyEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("idempotency_keys");
        builder.HasKey(key => key.Key);

        builder.Property(key => key.Key).HasMaxLength(256);

        // Supports the sweep that reclaims expired keys. Without it the cleanup
        // job scans the whole table, and this table is one of the largest.
        builder.HasIndex(key => key.ExpiresAt).HasDatabaseName("ix_idempotency_expires_at");
    }
}

/// <summary>Maps consumer delivery attempts to <c>consumer_delivery_attempts</c>.</summary>
internal sealed class ConsumerDeliveryAttemptConfiguration : IEntityTypeConfiguration<ConsumerDeliveryAttemptEntity>
{
    public void Configure(EntityTypeBuilder<ConsumerDeliveryAttemptEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("consumer_delivery_attempts");

        builder.HasKey(attempt => new
        {
            attempt.SourceTopic,
            attempt.SourcePartition,
            attempt.SourceOffset,
            attempt.ConsumerName,
        });

        builder.Property(attempt => attempt.SourceTopic).HasMaxLength(128).IsRequired();
        builder.Property(attempt => attempt.ConsumerName).HasMaxLength(128).IsRequired();
        builder.Property(attempt => attempt.EventType).HasMaxLength(128);
        builder.Property(attempt => attempt.LastError).HasMaxLength(2048);
        builder.Property(attempt => attempt.SettlementKind).HasMaxLength(32);

        builder.HasIndex(attempt => attempt.LastFailedAt).HasDatabaseName("ix_delivery_attempts_last_failed_at");
        builder.HasIndex(attempt => attempt.SettledAt).HasDatabaseName("ix_delivery_attempts_settled_at");
    }
}
