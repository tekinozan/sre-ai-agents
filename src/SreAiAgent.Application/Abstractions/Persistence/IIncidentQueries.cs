using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// Read-side queries that project straight to response contracts.
/// </summary>
/// <remarks>
/// Separate from <see cref="IIncidentRepository"/> on purpose. Read paths
/// project directly into the response shape rather than materializing an
/// aggregate and mapping it: <c>GET /v1/incidents/{id}/evidence</c> can face
/// thousands of rows, and loading them as tracked entities to copy six fields off
/// each one wastes both the allocations and the change tracker.
///
/// Implementations must not track entities.
/// </remarks>
public interface IIncidentQueries
{
    Task<IncidentResponse?> GetIncidentAsync(
        string tenant,
        IncidentId incidentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A page of the tenant's incidents, newest detection first.
    /// </summary>
    /// <remarks>
    /// Keyset, not offset: a page boundary is the last row's
    /// <c>(detected_at, id)</c>, so a page deep into a large table costs the same
    /// as the first and a row inserted meanwhile neither shifts nor repeats rows.
    /// </remarks>
    Task<IncidentListResponse> ListIncidentsAsync(
        string tenant,
        IncidentListQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IncidentTimelineEntryResponse>> GetTimelineAsync(
        string tenant,
        IncidentId incidentId,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EvidenceResponse>> GetEvidenceAsync(
        string tenant,
        IncidentId incidentId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Timeline entries after the first <paramref name="delivered"/> of them,
    /// for the server-sent event stream.
    /// </summary>
    /// <remarks>
    /// The cursor is a count rather than an identifier, which makes it a Skip
    /// over an append-only table. That is deliberate: a timeline holds tens of
    /// entries, not millions, and a count survives being round-tripped through
    /// the Last-Event-ID header as plain text without leaking an internal id to
    /// the client.
    ///
    /// The API polls this rather than subscribing, because the process that
    /// writes timeline entries is the investigation worker and the process that
    /// serves the stream is the API. An in-process broadcast would work only
    /// while both were the same process, which they are not in any real
    /// deployment (ARCHITECTURE.md 19.1).
    /// </remarks>
    Task<IReadOnlyList<IncidentTimelineEntryResponse>> GetStreamEventsAsync(
        string tenant,
        IncidentId incidentId,
        int delivered,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>The incident's current status, for deciding when a stream can close.</summary>
    Task<IncidentStatus?> GetStatusAsync(
        string tenant,
        IncidentId incidentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the latest report version, or a specific one when
    /// <paramref name="version"/> is supplied.
    /// </summary>
    Task<RootCauseReportResponse?> GetReportAsync(
        string tenant,
        IncidentId incidentId,
        int? version,
        CancellationToken cancellationToken);
}

/// <summary>Filters and page position for <see cref="IIncidentQueries.ListIncidentsAsync"/>.</summary>
/// <remarks>An empty status or severity list means any.</remarks>
public sealed record IncidentListQuery
{
    public required IReadOnlyList<IncidentStatus> Statuses { get; init; }

    public required IReadOnlyList<IncidentSeverity> Severities { get; init; }

    public string? Service { get; init; }

    public string? Environment { get; init; }

    public string? Source { get; init; }

    public DateTimeOffset? DetectedFrom { get; init; }

    public DateTimeOffset? DetectedTo { get; init; }

    public PageCursor? After { get; init; }

    public required int Limit { get; init; }
}
