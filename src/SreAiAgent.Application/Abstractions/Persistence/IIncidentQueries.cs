using System.Diagnostics.CodeAnalysis;
using System.Text;
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

    public IncidentListCursor? After { get; init; }

    public required int Limit { get; init; }
}

/// <summary>The position after which the next page of incidents starts.</summary>
/// <remarks>
/// Opaque to callers. The encoding carries the instant as UTC ticks so the
/// round trip is exact; a formatted timestamp would lose the sub-millisecond
/// part PostgreSQL stores and skip or repeat rows that share a millisecond.
/// </remarks>
public sealed record IncidentListCursor(DateTimeOffset DetectedAt, IncidentId IncidentId)
{
    public string Encode()
    {
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{DetectedAt.UtcTicks}:{IncidentId.Value:N}");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? value, [NotNullWhen(true)] out IncidentListCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');

        Span<byte> bytes = stackalloc byte[96];
        if (!Convert.TryFromBase64String(padded, bytes, out int written))
        {
            return false;
        }

        string raw = Encoding.ASCII.GetString(bytes[..written]);
        int separator = raw.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !long.TryParse(raw.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(raw.AsSpan(separator + 1), "N", out Guid id))
        {
            return false;
        }

        cursor = new IncidentListCursor(new DateTimeOffset(ticks, TimeSpan.Zero), IncidentId.From(id));
        return true;
    }
}
