namespace SreAiAgent.Application.Abstractions.Messaging;

/// <summary>Outcome kind recorded when an attempt counter is settled.</summary>
public enum SettlementKind
{
    Succeeded,
    DeadLetter,
}

/// <summary>
/// Persists consumer delivery attempts and settlements (ARCHITECTURE.md 10.2).
/// </summary>
/// <remarks>
/// Keyed by Kafka source record (topic, partition, offset) and consumer name.
/// The attempt counter is updated independently of the handler's transaction,
/// so a poison message that causes a rollback still increments its counter.
/// </remarks>
public interface IDeliveryAttemptStore
{
    /// <summary>
    /// Records one failed delivery and returns the new attempt count.
    /// </summary>
    Task<int> RecordFailureAsync(
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        string consumerName,
        Guid? eventId,
        string? eventType,
        string error,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks an attempt counter as settled upon successful handling or dead-letter publication.
    /// </summary>
    Task SettleAsync(
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        string consumerName,
        SettlementKind settlementKind,
        CancellationToken cancellationToken);
}
