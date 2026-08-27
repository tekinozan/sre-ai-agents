using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Messaging;

/// <summary>
/// One staged message awaiting publication.
/// </summary>
/// <remarks>
/// <paramref name="TraceParent"/> carries the W3C trace context captured when the
/// message was staged. Without it the trace ends at the transaction boundary and
/// the worker that eventually handles the message starts a disconnected one,
/// which defeats the purpose of tracing an asynchronous pipeline.
/// </remarks>
/// <param name="Id">Message identity. Version 7, so the outbox drains in order.</param>
/// <param name="Topic">Destination topic.</param>
/// <param name="EventType">Wire event type.</param>
/// <param name="EventId">Deduplication key seen by consumers.</param>
/// <param name="PayloadJson">Serialized envelope.</param>
/// <param name="PartitionKey">Usually the incident id, so one incident's messages stay ordered.</param>
/// <param name="CorrelationId">Correlation id for the investigation.</param>
/// <param name="TraceParent">W3C traceparent captured at staging time.</param>
/// <param name="OccurredAt">When the fact became true, UTC.</param>
/// <param name="AttemptCount">
/// Publication attempts so far. Carried on the message because the dispatcher
/// has to decide between another retry and a dead letter, and a decision made
/// from a value the dispatcher held separately would drift from the row.
/// </param>
public sealed record OutboxMessage(
    Guid Id,
    string Topic,
    string EventType,
    Guid EventId,
    string PayloadJson,
    string PartitionKey,
    string CorrelationId,
    string? TraceParent,
    DateTimeOffset OccurredAt,
    int AttemptCount);

/// <summary>
/// Claims and completes outbox rows. Implemented with
/// <c>FOR UPDATE SKIP LOCKED</c> so several dispatcher replicas can drain the
/// table concurrently without blocking each other or double-publishing.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Claims up to <paramref name="batchSize"/> undispatched messages.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Marks messages as published.</summary>
    Task MarkDispatchedAsync(
        IReadOnlyList<Guid> messageIds,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt and schedules the next one. After the configured
    /// ceiling the message is routed to the dead-letter topic rather than retried
    /// forever, because an unbounded retry on a poisoned message stalls every
    /// message behind it.
    /// </summary>
    Task RecordFailureAsync(
        Guid messageId,
        Error error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Parks a message that has exhausted its attempts.
    /// </summary>
    /// <remarks>
    /// The row keeps its payload rather than being deleted, so the message can be
    /// replayed once the cause is fixed. A poisoned message that is retried
    /// forever stalls every message behind it in the same partition, and one that
    /// is dropped takes the evidence of the problem with it.
    /// </remarks>
    Task MarkDeadLetteredAsync(Guid messageId, Error error, CancellationToken cancellationToken);
}
