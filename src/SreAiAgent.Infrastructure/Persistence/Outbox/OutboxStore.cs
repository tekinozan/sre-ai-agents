using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Outbox;

/// <summary>
/// Claims and completes outbox rows (ARCHITECTURE.md 9.4).
/// </summary>
/// <remarks>
/// Claiming uses <c>FOR UPDATE SKIP LOCKED</c>. That single clause is what lets
/// several dispatcher replicas drain the same table concurrently: each takes rows
/// nobody else holds and steps over the rest instead of blocking behind them.
/// Without it the choice is one dispatcher, which is a single point of failure, or
/// several that serialize on the same rows and publish duplicates.
/// </remarks>
internal sealed class OutboxStore : IOutboxStore
{
    private readonly SreDbContext _context;

    public OutboxStore(SreDbContext context) => _context = context;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Raw SQL because EF Core cannot express SKIP LOCKED, and the alternative
        // is losing the property the whole design depends on.
        //
        // Ordered by id, which is version 7 and therefore chronological, so a
        // partition's messages are published in the order they were produced.
        List<OutboxMessageEntity> claimed = await _context.OutboxMessages
            .FromSql(
                $"""
                 SELECT * FROM outbox_messages
                 WHERE dispatched_at IS NULL
                   AND is_dead_lettered = false
                   AND next_attempt_at <= {now}
                 ORDER BY id
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .AsTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. claimed.Select(static message => new OutboxMessage(
            message.Id,
            message.Topic,
            message.EventType,
            message.EventId,
            message.PayloadJson,
            message.PartitionKey,
            message.CorrelationId,
            message.TraceParent,
            message.OccurredAt,
            message.AttemptCount))];
    }

    /// <inheritdoc />
    public async Task MarkDispatchedAsync(
        IReadOnlyList<Guid> messageIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        // A set-based update rather than loading and saving each row. The
        // dispatcher runs continuously, and a round trip per message would make
        // the acknowledgement more expensive than the publish.
        await _context.OutboxMessages
            .Where(message => messageIds.Contains(message.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(message => message.DispatchedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordFailureAsync(
        Guid messageId,
        Error error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        await _context.OutboxMessages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.AttemptCount, message => message.AttemptCount + 1)
                    .SetProperty(message => message.NextAttemptAt, nextAttemptAt)
                    .SetProperty(message => message.LastError, error.Message),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a message as dead-lettered so the dispatcher stops claiming it.
    /// </summary>
    /// <remarks>
    /// An unbounded retry on a poisoned message stalls every message behind it in
    /// the same partition, so a message that has exhausted its attempts is parked
    /// with its payload intact and republished to the dead-letter topic for
    /// replay once the cause is fixed.
    /// </remarks>
    public async Task MarkDeadLetteredAsync(
        Guid messageId,
        Error error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        await _context.OutboxMessages
            .Where(message => message.Id == messageId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.IsDeadLettered, true)
                    .SetProperty(message => message.LastError, error.Message),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
