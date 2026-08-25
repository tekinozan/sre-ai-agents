using SreAiAgent.Contracts.Incidents;
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
    /// Returns the latest report version, or a specific one when
    /// <paramref name="version"/> is supplied.
    /// </summary>
    Task<RootCauseReportResponse?> GetReportAsync(
        string tenant,
        IncidentId incidentId,
        int? version,
        CancellationToken cancellationToken);
}
