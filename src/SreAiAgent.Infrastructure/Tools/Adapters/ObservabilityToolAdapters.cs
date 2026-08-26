using System.Text.Json;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Domain.Evidence;

namespace SreAiAgent.Infrastructure.Tools.Adapters;

// Typed adapters, one per registered tool.
//
// Each translates validated arguments into a port call and shapes the answer into
// something an agent can reason about. The summary line matters: it is what the
// model reads first and what appears on the evidence row, so an adapter that
// returned raw JSON with no summary would force the model to interpret the shape
// of a payload before it could think about the incident.
//
// None of these is reachable except through ToolGateway, which has already run
// schema validation, authorization, the risk ceiling, the argument policy and the
// rate limiter by the time an Execute here is called.

/// <summary>Shared helpers for reading validated arguments.</summary>
internal static class ToolArguments
{
    public static string GetString(JsonElement arguments, string name, string fallback = "")
        => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static int GetInt32(JsonElement arguments, string name, int fallback)
        => arguments.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.Number
           && value.TryGetInt32(out int parsed)
            ? parsed
            : fallback;

    public static IReadOnlyDictionary<string, string> GetLabels(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value) || value.ValueKind is not JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> labels = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String)
            {
                labels[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return labels;
    }

    public static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);
}

/// <summary>Adapter for <c>prometheus.query_range</c>.</summary>
internal sealed class PrometheusQueryRangeAdapter : IToolAdapter
{
    private readonly IMetricsProvider _metrics;
    private readonly TimeProvider _timeProvider;

    public PrometheusQueryRangeAdapter(IMetricsProvider metrics, TimeProvider timeProvider)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string ToolName => "prometheus.query_range";

    /// <inheritdoc />
    public async Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        string metric = ToolArguments.GetString(arguments, "metric");
        string service = ToolArguments.GetString(arguments, "service");
        int windowMinutes = ToolArguments.GetInt32(arguments, "window_minutes", 30);
        int stepSeconds = ToolArguments.GetInt32(arguments, "step_seconds", 60);

        TimeWindow window = TimeWindow.EndingAt(
            _timeProvider.GetUtcNow(), TimeSpan.FromMinutes(windowMinutes));

        MetricQueryResult result = await _metrics.QueryRangeAsync(
            new MetricRangeQuery(
                service,
                context.Environment,
                metric,
                ToolArguments.GetLabels(arguments, "labels"),
                window,
                TimeSpan.FromSeconds(stepSeconds)),
            cancellationToken).ConfigureAwait(false);

        return new ToolAdapterResult(
            Summary: SummarizeSeries(metric, service, windowMinutes, result),
            ContentJson: ToolArguments.Serialize(
                MetricEnvelope.From(metric, service, result), ToolResultJsonContext.Default.MetricEnvelope),
            SourceType: EvidenceSourceType.Metric,
            Provider: result.Provider,
            WindowStart: window.Start,
            WindowEnd: window.End,
            IsPartial: result.IsPartial);
    }

    /// <summary>
    /// States the direction of travel, not just the endpoints. "Rose from 0 to
    /// 27" is what an agent needs; a raw array of samples makes it re-derive that
    /// and occasionally get it wrong.
    /// </summary>
    private static string SummarizeSeries(
        string metric, string service, int windowMinutes, MetricQueryResult result)
    {
        if (result.Series.Count == 0)
        {
            return $"No series returned for '{metric}' on '{service}' over the last "
                   + $"{windowMinutes.ToString(CultureInfo.InvariantCulture)} minutes. "
                   + "This means the query matched nothing, which is not the same as the value being zero.";
        }

        MetricSeries series = result.Series[0];
        if (series.Points.Count == 0)
        {
            return $"Series for '{metric}' on '{service}' contained no samples.";
        }

        double first = series.Points[0].Value;
        double last = series.Points[^1].Value;
        double peak = series.Points.Max(static point => point.Value);

        string direction = last > first * 1.2 ? "rose" : last < first * 0.8 ? "fell" : "stayed flat";

        return $"'{metric}' on '{service}' {direction} over "
               + $"{windowMinutes.ToString(CultureInfo.InvariantCulture)} minutes: "
               + $"{first.ToString("0.###", CultureInfo.InvariantCulture)} to "
               + $"{last.ToString("0.###", CultureInfo.InvariantCulture)}, "
               + $"peak {peak.ToString("0.###", CultureInfo.InvariantCulture)} "
               + $"across {series.Points.Count.ToString(CultureInfo.InvariantCulture)} samples."
               + (result.IsPartial ? " The backend answered from partial data." : string.Empty);
    }
}

/// <summary>Adapter for <c>prometheus.query</c>.</summary>
internal sealed class PrometheusQueryAdapter : IToolAdapter
{
    private readonly IMetricsProvider _metrics;
    private readonly TimeProvider _timeProvider;

    public PrometheusQueryAdapter(IMetricsProvider metrics, TimeProvider timeProvider)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string ToolName => "prometheus.query";

    /// <inheritdoc />
    public async Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        string metric = ToolArguments.GetString(arguments, "metric");
        string service = ToolArguments.GetString(arguments, "service");
        DateTimeOffset at = _timeProvider.GetUtcNow();

        MetricQueryResult result = await _metrics.QueryAsync(
            new MetricQuery(service, context.Environment, metric, ToolArguments.GetLabels(arguments, "labels"), at),
            cancellationToken).ConfigureAwait(false);

        double? current = result.Series.Count > 0 && result.Series[0].Points.Count > 0
            ? result.Series[0].Points[^1].Value
            : null;

        return new ToolAdapterResult(
            Summary: current is null
                ? $"No current value for '{metric}' on '{service}'."
                : $"'{metric}' on '{service}' is currently "
                  + $"{current.Value.ToString("0.###", CultureInfo.InvariantCulture)}.",
            ContentJson: ToolArguments.Serialize(
                MetricEnvelope.From(metric, service, result), ToolResultJsonContext.Default.MetricEnvelope),
            SourceType: EvidenceSourceType.Metric,
            Provider: result.Provider,
            WindowStart: at,
            WindowEnd: at,
            IsPartial: result.IsPartial);
    }
}

/// <summary>Adapter for <c>logs.search</c>.</summary>
internal sealed class LogSearchAdapter : IToolAdapter
{
    private readonly ILogProvider _logs;
    private readonly TimeProvider _timeProvider;

    public LogSearchAdapter(ILogProvider logs, TimeProvider timeProvider)
    {
        _logs = logs;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string ToolName => "logs.search";

    /// <inheritdoc />
    public async Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        string service = ToolArguments.GetString(arguments, "service");
        int windowMinutes = ToolArguments.GetInt32(arguments, "window_minutes", 30);
        string minimumLevel = ToolArguments.GetString(arguments, "minimum_level", "error");
        int limit = ToolArguments.GetInt32(arguments, "limit", 50);

        List<string> terms = [];
        if (arguments.TryGetProperty("terms", out JsonElement termsElement)
            && termsElement.ValueKind is JsonValueKind.Array)
        {
            foreach (JsonElement term in termsElement.EnumerateArray())
            {
                if (term.ValueKind is JsonValueKind.String && term.GetString() is { Length: > 0 } text)
                {
                    terms.Add(text);
                }
            }
        }

        TimeWindow window = TimeWindow.EndingAt(
            _timeProvider.GetUtcNow(), TimeSpan.FromMinutes(windowMinutes));

        LogSearchResult result = await _logs.SearchAsync(
            new LogSearchQuery(service, context.Environment, window, minimumLevel, terms, limit),
            cancellationToken).ConfigureAwait(false);

        return new ToolAdapterResult(
            Summary: $"{result.TotalMatched.ToString(CultureInfo.InvariantCulture)} log entries at "
                     + $"{minimumLevel} or above for '{service}' in the last "
                     + $"{windowMinutes.ToString(CultureInfo.InvariantCulture)} minutes"
                     + (result.IsTruncated
                         ? $", showing the first {result.Entries.Count.ToString(CultureInfo.InvariantCulture)}."
                         : "."),
            ContentJson: ToolArguments.Serialize(
                LogEnvelope.From(service, result), ToolResultJsonContext.Default.LogEnvelope),
            SourceType: EvidenceSourceType.Log,
            Provider: result.Provider,
            WindowStart: window.Start,
            WindowEnd: window.End);
    }
}
