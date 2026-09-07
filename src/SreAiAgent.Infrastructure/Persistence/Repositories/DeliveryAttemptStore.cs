using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Messaging;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

/// <summary>
/// Persists consumer delivery attempts and settlements to PostgreSQL.
/// </summary>
internal sealed class DeliveryAttemptStore(SreDbContext context, TimeProvider timeProvider) : IDeliveryAttemptStore
{
    private const int MaxErrorLength = 2048;

    /// <inheritdoc />
    public async Task<int> RecordFailureAsync(
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        string consumerName,
        Guid? eventId,
        string? eventType,
        string error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceTopic);
        ArgumentNullException.ThrowIfNull(consumerName);
        ArgumentNullException.ThrowIfNull(error);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string truncated = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

        FormattableString sql = $"""
            INSERT INTO consumer_delivery_attempts
                (source_topic, source_partition, source_offset, consumer_name, event_id, event_type, attempt_count, first_failed_at, last_failed_at, last_error)
            VALUES ({sourceTopic}, {sourcePartition}, {sourceOffset}, {consumerName}, {eventId}, {eventType}, 1, {now}, {now}, {truncated})
            ON CONFLICT (source_topic, source_partition, source_offset, consumer_name) DO UPDATE SET
                event_id = COALESCE(EXCLUDED.event_id, consumer_delivery_attempts.event_id),
                event_type = COALESCE(EXCLUDED.event_type, consumer_delivery_attempts.event_type),
                attempt_count = consumer_delivery_attempts.attempt_count + 1,
                last_failed_at = {now},
                last_error = {truncated}
            RETURNING attempt_count
            """;

        await foreach (int count in context.Database
                           .SqlQuery<int>(sql)
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            return count;
        }

        throw new InvalidOperationException("The failure attempt insert returned no count.");
    }

    /// <inheritdoc />
    public async Task SettleAsync(
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        string consumerName,
        SettlementKind settlementKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceTopic);
        ArgumentNullException.ThrowIfNull(consumerName);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string kindString = settlementKind switch
        {
            SettlementKind.Succeeded => "succeeded",
            SettlementKind.DeadLetter => "dead_letter",
            _ => throw new ArgumentOutOfRangeException(nameof(settlementKind), settlementKind, "Invalid settlement kind."),
        };

        await context.ConsumerDeliveryAttempts
            .Where(attempt => attempt.SourceTopic == sourceTopic
                && attempt.SourcePartition == sourcePartition
                && attempt.SourceOffset == sourceOffset
                && attempt.ConsumerName == consumerName)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.SettledAt, now)
                .SetProperty(attempt => attempt.SettlementKind, kindString),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
