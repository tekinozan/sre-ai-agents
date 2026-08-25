using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Messaging;

/// <summary>
/// Stages a message for publication (ARCHITECTURE.md 9.4).
/// </summary>
/// <remarks>
/// This does not talk to Kafka. It writes an outbox row inside the caller's
/// transaction, and a dispatcher publishes it afterwards. Publishing directly
/// from a use case would mean either a message sent for a transaction that later
/// rolled back, or a committed state change whose notification vanished with the
/// process; the outbox is what removes both cases.
///
/// The method is synchronous because staging is an in-memory change-tracker
/// operation. Making it async would suggest I/O happens here, and someone would
/// eventually put I/O here.
/// </remarks>
public interface IEventPublisher
{
    /// <summary>Stages a domain event for the topic derived from its event type.</summary>
    void Enqueue(DomainEvent domainEvent, string correlationId, Guid? causationId = null);

    /// <summary>
    /// Stages an explicit payload, for messages that are not a direct domain
    /// event, such as an investigation request or a dead letter.
    /// </summary>
    void Enqueue<TPayload>(
        string topic,
        string eventType,
        TPayload payload,
        string correlationId,
        Guid? causationId = null)
        where TPayload : class;
}
