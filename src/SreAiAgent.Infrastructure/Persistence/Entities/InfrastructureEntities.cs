using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Persistence.Entities;

// Rows that belong to the infrastructure rather than to the domain.
//
// An outbox message, an inbox record and an idempotency claim are mechanisms for
// delivering and de-duplicating work; they carry no business meaning and no
// invariants worth expressing as an aggregate. Modelling them as domain entities
// would put messaging concerns into a layer that ARCHITECTURE.md 1.2 keeps free
// of them.

/// <summary>
/// A staged message awaiting publication (ARCHITECTURE.md 9.4).
/// </summary>
/// <remarks>
/// Written inside the same transaction as the state change that produced it. That
/// is the entire point of the pattern: publishing inside a transaction is
/// impossible, and publishing after it without a durable record loses the message
/// whenever the process dies in between.
/// </remarks>
public sealed class OutboxMessageEntity
{
    /// <summary>Version 7, so the table drains in creation order.</summary>
    public Guid Id { get; set; }

    public string Topic { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    /// <summary>Deduplication key seen by consumers.</summary>
    public Guid EventId { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Usually the incident id, so one incident's messages stay ordered.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// W3C traceparent captured at staging time. Without it the trace ends at the
    /// transaction boundary and the worker that eventually handles the message
    /// starts a disconnected one, which defeats tracing an async pipeline.
    /// </summary>
    public string? TraceParent { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>When the dispatcher may next claim this row. Drives the backoff.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Last failure, kept for diagnosis after a message dead-letters.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Set once the message has exhausted its attempts and been routed to the
    /// dead-letter topic. An unbounded retry on a poisoned message stalls every
    /// message behind it in the same partition.
    /// </summary>
    public bool IsDeadLettered { get; set; }
}

/// <summary>
/// A processed inbound message (ARCHITECTURE.md 10.2).
/// </summary>
/// <remarks>
/// Delivery is at-least-once, so this row is what makes a consumer idempotent. It
/// is written in the same transaction as the effect, and the offset is committed
/// only afterwards, so a crash in between produces a redelivery that is
/// recognised rather than a duplicated side effect.
/// </remarks>
public sealed class InboxMessageEntity
{
    /// <summary>The envelope's event id. Unique per consumer.</summary>
    public Guid EventId { get; set; }

    public string ConsumerName { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>
/// A claimed idempotency key (ARCHITECTURE.md 9.3).
/// </summary>
/// <remarks>
/// Enforced by a unique index rather than a read-then-write check. Two concurrent
/// deliveries of the same alert can both observe "not seen yet" a microsecond
/// apart; only a database constraint decides which one wins.
/// </remarks>
public sealed class IdempotencyKeyEntity
{
    public string Key { get; set; } = string.Empty;

    public DateTimeOffset ClaimedAt { get; set; }

    /// <summary>
    /// When the claim may be reclaimed. Keys are not kept forever: an alert
    /// fingerprint that recurs six months later is a new incident, and an
    /// ever-growing table is its own operational problem.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// A delivery attempt for an inbound Kafka message (ARCHITECTURE.md 10.2).
/// </summary>
/// <remarks>
/// Keyed by Kafka source record (topic, partition, offset) and consumer name.
/// The event id and event type are recorded only when an envelope was successfully
/// decoded; they are nullable because a poison payload that cannot be decoded
/// has no trustworthy event id, and keying on event id would let unparseable
/// messages bypass bounded retry and stall the partition forever.
/// </remarks>
public sealed class ConsumerDeliveryAttemptEntity
{
    public string SourceTopic { get; set; } = string.Empty;

    public int SourcePartition { get; set; }

    public long SourceOffset { get; set; }

    public string ConsumerName { get; set; } = string.Empty;

    public Guid? EventId { get; set; }

    public string? EventType { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset FirstFailedAt { get; set; }

    public DateTimeOffset LastFailedAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? SettledAt { get; set; }

    public string? SettlementKind { get; set; }
}

/// <summary>
/// The immutable release set pinned at investigation start
/// (ARCHITECTURE.md 8.2 and 24.5).
/// </summary>
public sealed class ConfigurationSnapshotEntity
{
    public ConfigurationSnapshotId Id { get; set; }

    public string AgentRelease { get; set; } = string.Empty;

    public string PromptRelease { get; set; } = string.Empty;

    public string ModelAssignmentJson { get; set; } = "{}";

    public string ToolRelease { get; set; } = string.Empty;

    public string PolicyRelease { get; set; } = string.Empty;

    /// <summary>Agent name to evaluated availability state, stored as JSONB.</summary>
    public IReadOnlyDictionary<string, string> FlagSnapshot { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DateTimeOffset CreatedAt { get; set; }
}
