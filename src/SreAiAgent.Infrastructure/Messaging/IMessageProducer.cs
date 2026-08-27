using SreAiAgent.Application.Abstractions.Messaging;

namespace SreAiAgent.Infrastructure.Messaging;

/// <summary>
/// Publishes a staged message to the broker.
/// </summary>
/// <remarks>
/// Internal, and deliberately not an application port. Nothing above
/// infrastructure should be able to publish: use cases stage messages through
/// IEventPublisher, which writes an outbox row inside their transaction, and the
/// dispatcher is the only component that turns those rows into broker traffic.
/// Exposing a producer to the application layer would make bypassing the outbox
/// a one-line mistake.
/// </remarks>
internal interface IMessageProducer
{
    /// <summary>Publishes one message, waiting for broker acknowledgement.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
