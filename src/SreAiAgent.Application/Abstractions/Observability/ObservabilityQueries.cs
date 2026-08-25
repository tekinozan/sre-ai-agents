using System.Runtime.InteropServices;

namespace SreAiAgent.Application.Abstractions.Observability;

// Query shapes for the observability ports (ARCHITECTURE.md 6.3).
//
// There is no PromQL, no Elasticsearch DSL and no kubectl argument anywhere in
// this file, and that is the whole point of the layer. Query translation lives
// inside each adapter, so swapping Prometheus for another metrics backend does
// not require touching an agent, a prompt or a tool schema. It also means a
// model can never smuggle a raw query through: it selects a metric and a window
// from a typed schema, and the adapter builds the query string.

/// <summary>The time span an observability question is asked about.</summary>
/// <param name="Start">Inclusive start, UTC.</param>
/// <param name="End">Exclusive end, UTC.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct TimeWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    /// <summary>Builds a window ending at a given instant.</summary>
    public static TimeWindow EndingAt(DateTimeOffset end, TimeSpan duration)
        => new(end - duration, end);
}

/// <summary>A point-in-time metric question.</summary>
/// <param name="Service">Service to scope to. Checked against the allowlist.</param>
/// <param name="Environment">Environment to scope to.</param>
/// <param name="MetricSelector">Registry key for the metric, not a backend expression.</param>
/// <param name="Labels">Additional label filters, already validated by the tool schema.</param>
/// <param name="At">The instant to evaluate at, UTC.</param>
public sealed record MetricQuery(
    string Service,
    string Environment,
    string MetricSelector,
    IReadOnlyDictionary<string, string> Labels,
    DateTimeOffset At);

/// <summary>A metric question over a window.</summary>
/// <param name="Service">Service to scope to. Checked against the allowlist.</param>
/// <param name="Environment">Environment to scope to.</param>
/// <param name="MetricSelector">Registry key for the metric, not a backend expression.</param>
/// <param name="Labels">Additional label filters, already validated by the tool schema.</param>
/// <param name="Window">The window to evaluate over.</param>
/// <param name="Step">Resolution. Bounded by the adapter so a wide window cannot return millions of points.</param>
public sealed record MetricRangeQuery(
    string Service,
    string Environment,
    string MetricSelector,
    IReadOnlyDictionary<string, string> Labels,
    TimeWindow Window,
    TimeSpan Step);

/// <summary>One labelled time series.</summary>
/// <param name="Labels">Series identity.</param>
/// <param name="Points">Samples, ordered by timestamp.</param>
public sealed record MetricSeries(
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<MetricSample> Points);

/// <summary>One sample.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct MetricSample(DateTimeOffset Timestamp, double Value);

/// <summary>The result of a metric query.</summary>
/// <param name="Series">Matching series, possibly empty.</param>
/// <param name="IsPartial">
/// True when the backend answered from incomplete data. Surfaced rather than
/// hidden: an agent concluding "no errors" from a partial scrape is exactly the
/// kind of confident wrong answer this platform exists to avoid.
/// </param>
/// <param name="Provider">Backend that answered.</param>
public sealed record MetricQueryResult(
    IReadOnlyList<MetricSeries> Series,
    bool IsPartial,
    string Provider);
