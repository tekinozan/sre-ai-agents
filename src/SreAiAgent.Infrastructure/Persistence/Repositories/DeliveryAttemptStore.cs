using System.Text;
using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

/// <summary>
/// Persists consumer delivery attempts and settlements to PostgreSQL.
/// </summary>
internal sealed class DeliveryAttemptStore(SreDbContext context, TimeProvider timeProvider) : IDeliveryAttemptStore
{
    // Max lengths are read once from EF Core's compiled IModel singleton upon store
    // instantiation, keeping diagnostic column bounds aligned with the schema contract.
    private readonly int _maxEventTypeLength =
        context.Model.FindEntityType(typeof(ConsumerDeliveryAttemptEntity))
            ?.FindProperty(nameof(ConsumerDeliveryAttemptEntity.EventType))
            ?.GetMaxLength() ?? 128;

    private readonly int _maxErrorLength =
        context.Model.FindEntityType(typeof(ConsumerDeliveryAttemptEntity))
            ?.FindProperty(nameof(ConsumerDeliveryAttemptEntity.LastError))
            ?.GetMaxLength() ?? 2048;

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
        string? boundedEventType = BoundString(eventType, _maxEventTypeLength);
        string boundedError = BoundString(error, _maxErrorLength) ?? string.Empty;
        FormattableString sql = $"""
            INSERT INTO consumer_delivery_attempts
                (source_topic, source_partition, source_offset, consumer_name, event_id, event_type, attempt_count, first_failed_at, last_failed_at, last_error)
            VALUES ({sourceTopic}, {sourcePartition}, {sourceOffset}, {consumerName}, {eventId}, {boundedEventType}, 1, {now}, {now}, {boundedError})
            ON CONFLICT (source_topic, source_partition, source_offset, consumer_name) DO UPDATE SET
                event_id = COALESCE(EXCLUDED.event_id, consumer_delivery_attempts.event_id),
                event_type = COALESCE(EXCLUDED.event_type, consumer_delivery_attempts.event_type),
                attempt_count = consumer_delivery_attempts.attempt_count + 1,
                last_failed_at = {now},
                last_error = {boundedError}
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

    /// <summary>
    /// Bounds a string to at most <paramref name="maxCharacters"/> Unicode code points (runes).
    /// </summary>
    /// <remarks>
    /// PostgreSQL <c>varchar(N)</c> checks Unicode character count (<c>character_length</c>),
    /// not UTF-16 code units or UTF-8 bytes. Enumerating by <see cref="Rune"/> guarantees that
    /// surrogate pairs are counted as single characters and never split across boundaries.
    /// </remarks>
    private static string? BoundString(string? value, int maxCharacters)
    {
        if (value is null)
        {
            return null;
        }

        if (maxCharacters <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maxCharacters)
        {
            return value;
        }

        int count = 0;
        int charLength = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (count >= maxCharacters)
            {
                break;
            }

            charLength += rune.Utf16SequenceLength;
            count++;
        }

        return value[..charLength];
    }
}
