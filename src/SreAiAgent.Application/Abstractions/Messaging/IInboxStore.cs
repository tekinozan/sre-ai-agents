namespace SreAiAgent.Application.Abstractions.Messaging;

/// <summary>
/// Consumer-side deduplication for at-least-once delivery
/// (ARCHITECTURE.md 10.2).
/// </summary>
/// <remarks>
/// Every consumer is idempotent because the broker guarantees only that a
/// message arrives at least once. The record is written in the same transaction
/// as the effect, and the offset is committed only after that transaction, so a
/// crash between the two produces a redelivery that this store recognizes rather
/// than a duplicate side effect.
/// </remarks>
public interface IInboxStore
{
    /// <summary>
    /// Records an event as being processed. Returns false when it was already
    /// recorded, meaning the consumer should acknowledge and do nothing else.
    /// </summary>
    Task<bool> TryBeginAsync(
        Guid eventId,
        string eventType,
        string consumerName,
        CancellationToken cancellationToken);
}
