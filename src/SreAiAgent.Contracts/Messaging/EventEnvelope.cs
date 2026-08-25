namespace SreAiAgent.Contracts.Messaging;

/// <summary>
/// The envelope every published message carries (ARCHITECTURE.md 10.2).
/// </summary>
/// <remarks>
/// Delivery is at-least-once, so <paramref name="EventId"/> is what makes a
/// consumer idempotent: it is written to the inbox inside the same transaction
/// as the effect, and a redelivery finds it already there. Offsets are committed
/// only after that write.
/// </remarks>
/// <typeparam name="T">The payload type.</typeparam>
/// <param name="EventId">Deduplication key. Unique per logical event, stable across redeliveries.</param>
/// <param name="EventType">Wire type, for example <c>sre.incident.created</c>.</param>
/// <param name="EventVersion">Schema version of the payload.</param>
/// <param name="OccurredAt">When the fact became true, in UTC. Not when it was published.</param>
/// <param name="CorrelationId">Ties every message of one investigation together.</param>
/// <param name="CausationId">The event that caused this one, for causal tracing.</param>
/// <param name="Payload">The event body.</param>
public sealed record EventEnvelope<T>(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    Guid? CausationId,
    T Payload);
