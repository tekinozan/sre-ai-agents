using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Incidents;

/// <summary>
/// One append-only entry on an incident timeline, persisted to
/// <c>incident_events</c>. Written in the same transaction as the state change
/// that produced it (ARCHITECTURE.md 9.4), so the timeline can never disagree
/// with the aggregate.
/// </summary>
/// <remarks>
/// There is no sequence column. Identifiers are version 7 GUIDs, so ordering by
/// id is chronological and needs no extra index or counter.
/// </remarks>
public sealed class IncidentTimelineEntry : Entity<IncidentEventId>
{
    private const int MaximumSummaryLength = 1024;

    private IncidentTimelineEntry()
    {
    }

    private IncidentTimelineEntry(
        IncidentId incidentId,
        string eventType,
        string summary,
        DateTimeOffset occurredAt,
        string? actor,
        string? payloadJson)
    {
        Id = IncidentEventId.New();
        IncidentId = incidentId;
        EventType = Guard.AgainstNullOrWhiteSpace(eventType, DomainErrorCodes.IncidentFieldRequired);
        Summary = Guard.AgainstTooLong(summary, MaximumSummaryLength, DomainErrorCodes.IncidentFieldRequired);
        OccurredAt = occurredAt.ToUniversalTime();
        Actor = actor;
        PayloadJson = payloadJson;
    }

    public IncidentId IncidentId { get; private set; }

    /// <summary>The wire event type, for example <c>sre.incident.created</c>.</summary>
    public string EventType { get; private set; } = null!;

    /// <summary>
    /// Operator-facing text. Already redacted: the timeline is returned by
    /// <c>GET /v1/incidents/{id}/timeline</c>.
    /// </summary>
    public string Summary { get; private set; } = null!;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Who caused the entry. Null when the platform itself did.</summary>
    public string? Actor { get; private set; }

    /// <summary>Optional structured detail, stored as JSONB.</summary>
    public string? PayloadJson { get; private set; }

    /// <summary>Records a lifecycle change that the aggregate announced.</summary>
    public static IncidentTimelineEntry FromDomainEvent(
        IncidentDomainEvent domainEvent,
        string summary,
        string? actor = null,
        string? payloadJson = null)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return new IncidentTimelineEntry(
            domainEvent.IncidentId,
            domainEvent.EventType,
            summary,
            domainEvent.OccurredAt,
            actor,
            payloadJson);
    }

    /// <summary>
    /// Records investigation progress that is not a lifecycle change, such as an
    /// agent starting or a tool completing. These feed the SSE stream in
    /// ARCHITECTURE.md 12.1.
    /// </summary>
    public static IncidentTimelineEntry Progress(
        IncidentId incidentId,
        string eventType,
        string summary,
        DateTimeOffset occurredAt,
        string? actor = null,
        string? payloadJson = null)
        => new(incidentId, eventType, summary, occurredAt, actor, payloadJson);
}
