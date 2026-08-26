using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

/// <summary>
/// Loads and stores the incident aggregate.
/// </summary>
/// <remarks>
/// The two queries here are compiled. Both run on the ingestion path, which is
/// the highest-frequency path in the system: every alert from every source hits
/// the fingerprint lookup, including the many that turn out to be duplicates of
/// something already open. A compiled query skips expression-tree translation on
/// each call, and at that rate the translation is a measurable share of the work.
/// </remarks>
internal sealed class IncidentRepository : IIncidentRepository
{
    private static readonly Func<SreDbContext, IncidentId, CancellationToken, Task<Incident?>> GetTracked =
        EF.CompileAsyncQuery((SreDbContext context, IncidentId id, CancellationToken _) =>
            context.Incidents.AsTracking().FirstOrDefault(incident => incident.Id == id));

    private static readonly Func<SreDbContext, string, string, string, DateTimeOffset, CancellationToken, Task<Incident?>>
        FindActive = EF.CompileAsyncQuery(
            (SreDbContext context, string tenant, string source, string fingerprint, DateTimeOffset since, CancellationToken _) =>
                context.Incidents
                    .AsTracking()
                    .Where(incident => incident.Tenant == tenant
                                       && incident.Source == source
                                       && incident.Fingerprint == fingerprint
                                       && incident.Status != IncidentStatus.Resolved
                                       && incident.Status != IncidentStatus.Cancelled
                                       && incident.DetectedAt >= since)
                    .OrderByDescending(incident => incident.DetectedAt)
                    .FirstOrDefault());

    private readonly SreDbContext _context;
    private readonly DatabaseOptions _options;

    public IncidentRepository(SreDbContext context, IOptions<DatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _context = context;
        _options = options.Value;
    }

    /// <inheritdoc />
    public Task<Incident?> GetAsync(IncidentId incidentId, CancellationToken cancellationToken)
        => GetTracked(_context, incidentId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The deduplication window is configuration rather than a constant. Too short
    /// and a flapping alert opens an incident per flap; too long and a genuine
    /// recurrence is folded into an investigation somebody already closed.
    /// </remarks>
    public Task<Incident?> FindActiveByFingerprintAsync(
        string tenant,
        string source,
        string fingerprint,
        DateTimeOffset activeSince,
        CancellationToken cancellationToken)
        => FindActive(_context, tenant, source, fingerprint, activeSince, cancellationToken);

    /// <inheritdoc />
    public void Add(Incident incident) => _context.Incidents.Add(incident);

    /// <inheritdoc />
    public void AddTimelineEntry(IncidentTimelineEntry entry) => _context.IncidentEvents.Add(entry);

    /// <summary>The configured deduplication window, for callers computing activeSince.</summary>
    public TimeSpan DeduplicationWindow => TimeSpan.FromMinutes(_options.IncidentDeduplicationWindowMinutes);
}
