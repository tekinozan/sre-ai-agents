namespace SreAiAgent.Domain.Incidents;

/// <summary>
/// The single source of truth for which incident transitions are legal.
/// Keeping the table here rather than scattering guard clauses through
/// <see cref="Incident"/> means the lifecycle can be reviewed and tested as one
/// unit, and the workflow cannot invent a path that the domain never sanctioned.
/// </summary>
public static class IncidentStatePolicy
{
    /// <summary>
    /// True when <paramref name="to"/> may be reached directly from
    /// <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// A tuple switch over two small enums compiles to a jump table: no
    /// dictionary lookup, no allocation, no hashing on a path that runs on every
    /// state change of every incident.
    /// </remarks>
    public static bool CanTransition(IncidentStatus from, IncidentStatus to) => (from, to) switch
    {
        _ when from == to => true,

        (IncidentStatus.Open, IncidentStatus.Investigating) => true,
        (IncidentStatus.Open, IncidentStatus.Resolved) => true,
        (IncidentStatus.Open, IncidentStatus.Failed) => true,
        (IncidentStatus.Open, IncidentStatus.Cancelled) => true,

        (IncidentStatus.Investigating, IncidentStatus.WaitingApproval) => true,
        (IncidentStatus.Investigating, IncidentStatus.Resolved) => true,
        (IncidentStatus.Investigating, IncidentStatus.Failed) => true,
        (IncidentStatus.Investigating, IncidentStatus.Cancelled) => true,

        // A rejected approval returns the incident to the investigation, which
        // records the rejection and completes with a read-only report.
        (IncidentStatus.WaitingApproval, IncidentStatus.Investigating) => true,
        (IncidentStatus.WaitingApproval, IncidentStatus.Remediating) => true,
        (IncidentStatus.WaitingApproval, IncidentStatus.Resolved) => true,
        (IncidentStatus.WaitingApproval, IncidentStatus.Failed) => true,
        (IncidentStatus.WaitingApproval, IncidentStatus.Cancelled) => true,

        (IncidentStatus.Remediating, IncidentStatus.Resolved) => true,
        (IncidentStatus.Remediating, IncidentStatus.Failed) => true,
        (IncidentStatus.Remediating, IncidentStatus.Cancelled) => true,

        // Retrying a failed investigation is a supported operation
        // (POST /v1/incidents/{id}/retry).
        (IncidentStatus.Failed, IncidentStatus.Investigating) => true,
        (IncidentStatus.Failed, IncidentStatus.Resolved) => true,
        (IncidentStatus.Failed, IncidentStatus.Cancelled) => true,

        // Reopening a resolved incident is deliberate and audited; it is never
        // an implicit side effect of a duplicate alert.
        (IncidentStatus.Resolved, IncidentStatus.Investigating) => true,

        _ => false,
    };

    /// <summary>True when the incident can no longer change state on its own.</summary>
    public static bool IsTerminal(IncidentStatus status)
        => status is IncidentStatus.Cancelled;

    /// <summary>True when the incident has finished its current investigation.</summary>
    public static bool IsClosed(IncidentStatus status)
        => status is IncidentStatus.Resolved or IncidentStatus.Cancelled;
}
