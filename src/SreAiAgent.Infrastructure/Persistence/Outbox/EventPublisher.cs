using System.Diagnostics;
using System.Text.Json;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Outbox;

/// <summary>
/// Stages messages in the outbox inside the caller's transaction
/// (ARCHITECTURE.md 9.4).
/// </summary>
/// <remarks>
/// This never touches Kafka. It adds a row to the change tracker, which commits
/// with the state change that produced it; a dispatcher publishes it afterwards.
/// Publishing from a use case would mean either a message sent for a transaction
/// that later rolled back, or a committed state change whose notification vanished
/// with the process. The outbox removes both cases at the cost of one row.
/// </remarks>
internal sealed class EventPublisher : IEventPublisher
{
    private readonly SreDbContext _context;
    private readonly TimeProvider _timeProvider;

    public EventPublisher(SreDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public void Enqueue(DomainEvent domainEvent, string correlationId, Guid? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // The topic is derived from the event type rather than chosen by the
        // caller, so a domain event cannot be published to the wrong topic by a
        // use case that forgot the mapping.
        string topic = TopicFor(domainEvent.EventType);

        _context.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(),
            Topic = topic,
            EventType = domainEvent.EventType,
            EventId = domainEvent.EventId,
            PayloadJson = SerializeDomainEvent(domainEvent, correlationId, causationId),
            PartitionKey = PartitionKeyFor(domainEvent),
            CorrelationId = correlationId,
            TraceParent = CurrentTraceParent(),
            OccurredAt = domainEvent.OccurredAt,
            NextAttemptAt = _timeProvider.GetUtcNow(),
        });
    }

    /// <inheritdoc />
    public void Enqueue<TPayload>(
        string topic,
        string eventType,
        TPayload payload,
        string correlationId,
        Guid? causationId = null)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);

        Guid eventId = Guid.CreateVersion7();

        _context.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(),
            Topic = topic,
            EventType = eventType,
            EventId = eventId,
            PayloadJson = SerializeEnvelope(eventId, eventType, payload, correlationId, causationId),
            PartitionKey = PartitionKeyForPayload(payload),
            CorrelationId = correlationId,
            TraceParent = CurrentTraceParent(),
            OccurredAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = _timeProvider.GetUtcNow(),
        });
    }

    /// <summary>
    /// Captures the current W3C traceparent so the consumer can continue the same
    /// trace. Without it a trace ends at the transaction boundary and the worker
    /// that eventually handles the message starts a disconnected one, which is
    /// most of the value of tracing an asynchronous pipeline.
    /// </summary>
    private static string? CurrentTraceParent() => Activity.Current?.Id;

    private static string TopicFor(string eventType) => eventType switch
    {
        "sre.incident.created" => KafkaTopics.IncidentCreated,
        "sre.incident.investigation.requested" => KafkaTopics.InvestigationRequested,
        "sre.incident.investigation.started" => KafkaTopics.IncidentUpdated,
        "sre.incident.updated" => KafkaTopics.IncidentUpdated,
        "sre.incident.completed" => KafkaTopics.IncidentCompleted,
        "sre.remediation.requested" => KafkaTopics.RemediationRequested,
        "sre.remediation.completed" => KafkaTopics.RemediationCompleted,
        _ => KafkaTopics.IncidentUpdated,
    };

    /// <summary>
    /// Partitions by incident. Kafka guarantees order within a partition only, and
    /// a consumer that saw "completed" before "investigation started" for the same
    /// incident would have to reconstruct the order itself.
    /// </summary>
    private static string PartitionKeyFor(DomainEvent domainEvent)
        => domainEvent is Domain.Incidents.IncidentDomainEvent incidentEvent
            ? incidentEvent.IncidentId.ToString()
            : domainEvent.EventId.ToString();

    private static string PartitionKeyForPayload<TPayload>(TPayload payload)
        where TPayload : class
        => payload switch
        {
            InvestigationRequestedPayload requested => requested.IncidentId.ToString(),
            IncidentCreatedPayload created => created.IncidentId.ToString(),
            IncidentCompletedPayload completed => completed.IncidentId.ToString(),
            IncidentUpdatedPayload updated => updated.IncidentId.ToString(),

            // An alert has no incident yet, so it is partitioned by the identity
            // it will deduplicate on. Repeat firings of one alert then land on
            // one partition and are handled in order, which is what stops two
            // consumers racing to open the same incident.
            Contracts.Alerts.CanonicalAlert alert => $"{alert.Source}:{alert.Fingerprint ?? alert.ExternalId}",

            _ => Guid.CreateVersion7().ToString(),
        };

    private static string SerializeDomainEvent(DomainEvent domainEvent, string correlationId, Guid? causationId)
    {
        // Domain events carry no wire payload of their own; the envelope's
        // identifiers plus a status are enough for a consumer to read current
        // state, and a fat payload would go stale in flight.
        IncidentUpdatedPayload payload = domainEvent is Domain.Incidents.IncidentDomainEvent incidentEvent
            ? new IncidentUpdatedPayload(
                incidentEvent.IncidentId,
                Domain.Incidents.IncidentStatus.Investigating,
                domainEvent.EventType)
            : new IncidentUpdatedPayload(default, Domain.Incidents.IncidentStatus.Open, domainEvent.EventType);

        EventEnvelope<IncidentUpdatedPayload> envelope = new(
            domainEvent.EventId,
            domainEvent.EventType,
            EventVersion: 1,
            domainEvent.OccurredAt,
            correlationId,
            causationId,
            payload);

        return JsonSerializer.Serialize(
            envelope, SreAiAgentJsonContext.Default.EventEnvelopeIncidentUpdatedPayload);
    }

    private string SerializeEnvelope<TPayload>(
        Guid eventId,
        string eventType,
        TPayload payload,
        string correlationId,
        Guid? causationId)
        where TPayload : class
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        return payload switch
        {
            Contracts.Alerts.CanonicalAlert alert => JsonSerializer.Serialize(
                new EventEnvelope<Contracts.Alerts.CanonicalAlert>(
                    eventId, eventType, 1, now, correlationId, causationId, alert),
                SreAiAgentJsonContext.Default.EventEnvelopeCanonicalAlert),

            InvestigationRequestedPayload requested => JsonSerializer.Serialize(
                new EventEnvelope<InvestigationRequestedPayload>(
                    eventId, eventType, 1, now, correlationId, causationId, requested),
                SreAiAgentJsonContext.Default.EventEnvelopeInvestigationRequestedPayload),

            IncidentCreatedPayload created => JsonSerializer.Serialize(
                new EventEnvelope<IncidentCreatedPayload>(
                    eventId, eventType, 1, now, correlationId, causationId, created),
                SreAiAgentJsonContext.Default.EventEnvelopeIncidentCreatedPayload),

            IncidentCompletedPayload completed => JsonSerializer.Serialize(
                new EventEnvelope<IncidentCompletedPayload>(
                    eventId, eventType, 1, now, correlationId, causationId, completed),
                SreAiAgentJsonContext.Default.EventEnvelopeIncidentCompletedPayload),

            IncidentUpdatedPayload updated => JsonSerializer.Serialize(
                new EventEnvelope<IncidentUpdatedPayload>(
                    eventId, eventType, 1, now, correlationId, causationId, updated),
                SreAiAgentJsonContext.Default.EventEnvelopeIncidentUpdatedPayload),

            // A payload with no generated metadata is a programming error, not a
            // runtime condition. Falling back to reflection would defeat the
            // source-generation guarantee for every message.
            _ => throw new NotSupportedException(
                $"No serialization metadata is generated for payload '{typeof(TPayload).Name}'. "
                + "Add it to SreAiAgentJsonContext."),
        };
    }
}
