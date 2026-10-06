using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// Loads and stores the incident aggregate.
/// </summary>
/// <remarks>
/// Scoped to one aggregate rather than generic. A generic
/// <c>IRepository&lt;T&gt;</c> over an ORM that already provides one is an
/// abstraction over an abstraction: it hides the query capabilities that make the
/// ORM worth using and pushes every caller toward loading whole aggregates when
/// they wanted three columns. Read paths use <see cref="IIncidentQueries"/>
/// instead.
/// </remarks>
public interface IIncidentRepository
{
    /// <summary>Loads an incident for modification, with change tracking on.</summary>
    Task<Incident?> GetAsync(IncidentId incidentId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the incident an alert should fold into: the active incident for the
    /// tenant, source and fingerprint whatever its age, or else the resolved one
    /// whose last alert is at or after <c>activeSince</c>. Returns null when a
    /// new incident is warranted (ARCHITECTURE.md 9.3, docs/DECISIONS.md D43).
    /// </summary>
    Task<Incident?> FindActiveByFingerprintAsync(
        string tenant,
        string source,
        string fingerprint,
        DateTimeOffset activeSince,
        CancellationToken cancellationToken);

    /// <summary>Stages a new incident. Nothing is written until the unit of work commits.</summary>
    void Add(Incident incident);

    /// <summary>Appends a timeline entry.</summary>
    void AddTimelineEntry(IncidentTimelineEntry entry);
}
