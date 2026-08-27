using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Incidents;

/// <summary>
/// Body of <c>POST /v1/incidents</c>. Shaped like a normalized alert rather than
/// like the incident aggregate, because the caller is reporting an observation,
/// not asserting internal state.
/// </summary>
public sealed record CreateIncidentRequest
{
    public required string Source { get; init; }

    public required string ExternalId { get; init; }

    /// <summary>Optional. Derived from the other fields when omitted.</summary>
    public string? Fingerprint { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required string Service { get; init; }

    public required string Environment { get; init; }

    public required IncidentSeverity Severity { get; init; }

    public DateTimeOffset? DetectedAt { get; init; }

    // Every optional member below is nullable, and none of them relies on a
    // property initializer to supply a default.
    //
    // That is not a style choice. System.Text.Json does not run initializers when
    // it materializes a type with required members, so an omitted member arrives
    // as null or default rather than as the value written next to it. Declaring
    // the default here and reading it in the handler looked correct, compiled,
    // and silently produced an incident whose investigation never started.
    // Nullable makes "the caller did not say" a state the handler has to answer
    // for explicitly.

    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>Which label keys participate in identity. Absent means none.</summary>
    public IReadOnlyList<string>? IdentityLabelKeys { get; init; }

    /// <summary>
    /// Start the investigation immediately instead of only opening the incident.
    /// Absent means yes, which is what an alert source reporting a problem
    /// almost always wants.
    /// </summary>
    public bool? StartInvestigation { get; init; }
}

/// <summary>
/// Returned with <c>202 Accepted</c>. The work is asynchronous, so the caller
/// gets an identifier and a location to poll rather than a result
/// (ARCHITECTURE.md 12.1).
/// </summary>
/// <param name="IncidentId">The incident, new or existing.</param>
/// <param name="Status">Current lifecycle status.</param>
/// <param name="IsDuplicate">
/// True when the alert folded into an existing incident. The caller sees the
/// same successful response either way, which is what makes retries safe.
/// </param>
public sealed record IncidentAcceptedResponse(
    IncidentId IncidentId,
    IncidentStatus Status,
    bool IsDuplicate);

/// <summary>Incident detail for <c>GET /v1/incidents/{id}</c>.</summary>
public sealed record IncidentResponse
{
    public required IncidentId IncidentId { get; init; }

    public required string Tenant { get; init; }

    public required string Source { get; init; }

    public required string Fingerprint { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required string Service { get; init; }

    public required string Environment { get; init; }

    public required IncidentSeverity Severity { get; init; }

    public required IncidentStatus Status { get; init; }

    public required int AlertCount { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public ConfigurationSnapshotId? ConfigurationSnapshotId { get; init; }

    public ApprovalRequestId? PendingApprovalRequestId { get; init; }

    /// <summary>Stable failure code when the investigation did not complete.</summary>
    public string? FailureCode { get; init; }

    public string? FailureReason { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>One entry of <c>GET /v1/incidents/{id}/timeline</c>.</summary>
public sealed record IncidentTimelineEntryResponse(
    IncidentEventId EventId,
    string EventType,
    string Summary,
    DateTimeOffset OccurredAt,
    string? Actor);

/// <summary>Body of the retry and cancel endpoints.</summary>
public sealed record IncidentActionRequest(string Reason);
