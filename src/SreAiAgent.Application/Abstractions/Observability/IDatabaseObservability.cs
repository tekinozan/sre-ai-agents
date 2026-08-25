namespace SreAiAgent.Application.Abstractions.Observability;

/// <summary>Which database to inspect.</summary>
/// <param name="ClusterKey">Configuration key for the instance. Never a connection string.</param>
/// <param name="Environment">Environment it belongs to.</param>
/// <param name="Service">Owning service, checked against the allowlist.</param>
public sealed record DatabaseScope(string ClusterKey, string Environment, string Service);

/// <summary>Connection pool state, the usual explanation for sudden latency.</summary>
/// <param name="ActiveConnections">Connections currently executing.</param>
/// <param name="IdleConnections">Connections open but idle.</param>
/// <param name="MaxConnections">Configured ceiling.</param>
/// <param name="WaitingClients">Clients queued for a connection.</param>
public sealed record ConnectionPoolState(
    int ActiveConnections,
    int IdleConnections,
    int MaxConnections,
    int WaitingClients)
{
    /// <summary>Fraction of the pool in use, in [0, 1].</summary>
    public double Utilization => MaxConnections == 0
        ? 0
        : (double)(ActiveConnections + IdleConnections) / MaxConnections;
}

/// <summary>
/// A slow statement.
/// </summary>
/// <remarks>
/// <paramref name="NormalizedStatement"/> is the parameter-stripped form. Bound
/// values are frequently customer data, and this text is destined for a prompt
/// and an evidence row, so the literals are removed at the adapter rather than
/// relied on to be caught later by redaction.
/// </remarks>
/// <param name="NormalizedStatement">Statement with literals replaced by placeholders.</param>
/// <param name="Calls">How often it ran in the window.</param>
/// <param name="TotalTime">Cumulative execution time.</param>
/// <param name="MeanTime">Mean execution time.</param>
public sealed record SlowQuery(
    string NormalizedStatement,
    long Calls,
    TimeSpan TotalTime,
    TimeSpan MeanTime);

/// <summary>Database health for a scope.</summary>
/// <param name="Pool">Connection pool state.</param>
/// <param name="SlowQueries">Slowest statements in the window.</param>
/// <param name="ReplicationLag">Replica lag, when the instance has replicas.</param>
/// <param name="IsAcceptingConnections">Whether the instance is reachable at all.</param>
/// <param name="Provider">Backend that answered.</param>
public sealed record DatabaseHealth(
    ConnectionPoolState Pool,
    IReadOnlyList<SlowQuery> SlowQueries,
    TimeSpan? ReplicationLag,
    bool IsAcceptingConnections,
    string Provider);

/// <summary>
/// Read-only database inspection (ARCHITECTURE.md 6.3).
/// </summary>
/// <remarks>
/// No method accepts SQL. Section 15.2 requires allowlisted typed queries
/// instead of arbitrary statements, so the questions the platform can ask are
/// fixed at compile time.
/// </remarks>
public interface IDatabaseObservability
{
    Task<DatabaseHealth> GetHealthAsync(
        DatabaseScope scope,
        TimeWindow window,
        CancellationToken cancellationToken);
}
