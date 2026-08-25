namespace SreAiAgent.Application.Abstractions.Observability;

/// <summary>A distributed trace search.</summary>
/// <param name="Service">Service to scope to.</param>
/// <param name="Environment">Environment to scope to.</param>
/// <param name="Window">Time range to search.</param>
/// <param name="OperationName">Optional operation to filter on.</param>
/// <param name="MinimumDuration">Optional floor, for finding slow requests.</param>
/// <param name="ErrorsOnly">Restrict to traces that recorded an error.</param>
/// <param name="Limit">Maximum traces to return.</param>
public sealed record TraceSearchQuery(
    string Service,
    string Environment,
    TimeWindow Window,
    string? OperationName,
    TimeSpan? MinimumDuration,
    bool ErrorsOnly,
    int Limit);

/// <summary>A trace summary. Full span trees are not pulled into a prompt.</summary>
/// <param name="TraceId">W3C trace id.</param>
/// <param name="RootOperation">Name of the root span.</param>
/// <param name="StartedAt">When the trace began, UTC.</param>
/// <param name="Duration">End-to-end duration.</param>
/// <param name="SpanCount">How many spans it contains.</param>
/// <param name="HasError">Whether any span recorded an error.</param>
/// <param name="SlowestSpanOperation">The span that dominated the duration.</param>
public sealed record TraceSummary(
    string TraceId,
    string RootOperation,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int SpanCount,
    bool HasError,
    string? SlowestSpanOperation);

/// <summary>The result of a trace search.</summary>
public sealed record TraceSearchResult(
    IReadOnlyList<TraceSummary> Traces,
    bool IsTruncated,
    string Provider);

/// <summary>Read-only access to a tracing backend (ARCHITECTURE.md 6.3).</summary>
public interface ITraceProvider
{
    Task<TraceSearchResult> SearchAsync(TraceSearchQuery query, CancellationToken cancellationToken);
}
