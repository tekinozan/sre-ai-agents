namespace SreAiAgent.Application.Abstractions.Observability;

/// <summary>
/// Read-only access to a metrics backend (ARCHITECTURE.md 6.3).
/// </summary>
public interface IMetricsProvider
{
    /// <summary>Evaluates a metric at one instant.</summary>
    Task<MetricQueryResult> QueryAsync(MetricQuery query, CancellationToken cancellationToken);

    /// <summary>Evaluates a metric across a window.</summary>
    Task<MetricQueryResult> QueryRangeAsync(MetricRangeQuery query, CancellationToken cancellationToken);
}
