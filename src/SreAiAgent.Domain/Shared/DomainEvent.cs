namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Something that has already happened inside an aggregate. Events are raised
/// during a state transition and drained by the persistence layer, which writes
/// them to the timeline and the transactional outbox in the same transaction
/// (ARCHITECTURE.md 9.4).
/// </summary>
/// <param name="OccurredAt">
/// When the transition happened, in UTC. Passed in rather than read from an
/// ambient clock so aggregates stay deterministic under test.
/// </param>
public abstract record DomainEvent(DateTimeOffset OccurredAt)
{
    /// <summary>
    /// Deduplication key for at-least-once delivery. Consumers use it against
    /// the inbox table (ARCHITECTURE.md 10.2).
    /// </summary>
    public Guid EventId { get; } = Guid.CreateVersion7();

    /// <summary>
    /// The wire event type, for example <c>sre.incident.created</c>. Used to
    /// route the outbox record onto a topic without reflecting over the CLR type.
    /// </summary>
    public abstract string EventType { get; }
}
