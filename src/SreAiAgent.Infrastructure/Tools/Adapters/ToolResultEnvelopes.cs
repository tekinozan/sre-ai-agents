using System.Text.Json.Serialization;
using SreAiAgent.Application.Abstractions.Observability;

namespace SreAiAgent.Infrastructure.Tools.Adapters;

// The JSON an agent actually sees for each tool result.
//
// Deliberately narrower than the port results they are built from. Everything
// here has to survive the result-size cap, pass through redaction and then fit
// inside a prompt alongside the incident and every other piece of evidence, so an
// adapter that serialized its entire backend response would crowd out the
// evidence that matters. Downsampling and trimming happen here, where the shape
// of the data is understood, rather than being left to a byte-count truncation
// that would cut a JSON document in half.

/// <summary>One downsampled series point.</summary>
public sealed record MetricPointEnvelope(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("v")] double Value);

/// <summary>A metric result reduced to what a model needs.</summary>
public sealed record MetricEnvelope
{
    [JsonPropertyName("metric")]
    public string Metric { get; init; } = string.Empty;

    [JsonPropertyName("service")]
    public string Service { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("is_partial")]
    public bool IsPartial { get; init; }

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; init; }

    [JsonPropertyName("first_value")]
    public double? FirstValue { get; init; }

    [JsonPropertyName("last_value")]
    public double? LastValue { get; init; }

    [JsonPropertyName("min_value")]
    public double? MinValue { get; init; }

    [JsonPropertyName("max_value")]
    public double? MaxValue { get; init; }

    /// <summary>
    /// At most 30 evenly spaced points. A 6-hour window at 15-second resolution
    /// is 1440 samples; a model does not read a trend more accurately from 1440
    /// numbers than from 30, and the difference is most of a prompt budget.
    /// </summary>
    [JsonPropertyName("samples")]
    public IReadOnlyList<MetricPointEnvelope> Samples { get; init; } = [];

    private const int MaxSamples = 30;

    public static MetricEnvelope From(string metric, string service, MetricQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Series.Count == 0 || result.Series[0].Points.Count == 0)
        {
            return new MetricEnvelope
            {
                Metric = metric,
                Service = service,
                Provider = result.Provider,
                IsPartial = result.IsPartial,
            };
        }

        IReadOnlyList<MetricSample> points = result.Series[0].Points;
        int stride = Math.Max(1, points.Count / MaxSamples);

        List<MetricPointEnvelope> samples = new(Math.Min(points.Count, MaxSamples + 1));
        for (int i = 0; i < points.Count; i += stride)
        {
            samples.Add(new MetricPointEnvelope(points[i].Timestamp, points[i].Value));
        }

        // The last point is always included: a downsample that drops the most
        // recent value would hide the very thing the investigation is about.
        if (samples.Count == 0 || samples[^1].Timestamp != points[^1].Timestamp)
        {
            samples.Add(new MetricPointEnvelope(points[^1].Timestamp, points[^1].Value));
        }

        double min = points[0].Value;
        double max = points[0].Value;
        foreach (MetricSample point in points)
        {
            if (point.Value < min)
            {
                min = point.Value;
            }

            if (point.Value > max)
            {
                max = point.Value;
            }
        }

        return new MetricEnvelope
        {
            Metric = metric,
            Service = service,
            Provider = result.Provider,
            IsPartial = result.IsPartial,
            SampleCount = points.Count,
            FirstValue = points[0].Value,
            LastValue = points[^1].Value,
            MinValue = min,
            MaxValue = max,
            Samples = samples,
        };
    }
}

/// <summary>One log entry as an agent sees it.</summary>
public sealed record LogEntryEnvelope(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message);

/// <summary>A log search result.</summary>
public sealed record LogEnvelope
{
    [JsonPropertyName("service")]
    public string Service { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// The count before truncation, kept separate from the returned entries so an
    /// agent can state "at least N" instead of treating a page as the whole.
    /// </summary>
    [JsonPropertyName("total_matched")]
    public long TotalMatched { get; init; }

    [JsonPropertyName("is_truncated")]
    public bool IsTruncated { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<LogEntryEnvelope> Entries { get; init; } = [];

    public static LogEnvelope From(string service, LogSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new LogEnvelope
        {
            Service = service,
            Provider = result.Provider,
            TotalMatched = result.TotalMatched,
            IsTruncated = result.IsTruncated,
            Entries = [.. result.Entries.Select(static entry =>
                new LogEntryEnvelope(entry.Timestamp, entry.Level, entry.Message))],
        };
    }
}

/// <summary>Pod state as an agent sees it.</summary>
public sealed record PodEnvelope(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("ready")] bool IsReady,
    [property: JsonPropertyName("restart_count")] int RestartCount,
    [property: JsonPropertyName("last_termination_reason")] string? LastTerminationReason,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt);

/// <summary>A pod query result.</summary>
public sealed record KubernetesEnvelope
{
    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("desired_replicas")]
    public int DesiredReplicas { get; init; }

    [JsonPropertyName("ready_replicas")]
    public int ReadyReplicas { get; init; }

    [JsonPropertyName("pods")]
    public IReadOnlyList<PodEnvelope> Pods { get; init; } = [];

    public static KubernetesEnvelope From(string ns, PodSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new KubernetesEnvelope
        {
            Namespace = ns,
            Provider = summary.Provider,
            DesiredReplicas = summary.DesiredReplicas,
            ReadyReplicas = summary.ReadyReplicas,
            Pods = [.. summary.Pods.Select(static pod => new PodEnvelope(
                pod.Name, pod.Phase, pod.IsReady, pod.RestartCount, pod.LastTerminationReason, pod.StartedAt))],
        };
    }
}

/// <summary>A cluster event.</summary>
public sealed record KubernetesEventEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("object")] string InvolvedObject,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt);

/// <summary>A list of cluster events.</summary>
public sealed record KubernetesEventsEnvelope
{
    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = string.Empty;

    [JsonPropertyName("events")]
    public IReadOnlyList<KubernetesEventEnvelope> Events { get; init; } = [];
}

/// <summary>Database health as an agent sees it.</summary>
public sealed record DatabaseEnvelope
{
    [JsonPropertyName("service")]
    public string Service { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("accepting_connections")]
    public bool IsAcceptingConnections { get; init; }

    [JsonPropertyName("pool_active")]
    public int PoolActive { get; init; }

    [JsonPropertyName("pool_idle")]
    public int PoolIdle { get; init; }

    [JsonPropertyName("pool_max")]
    public int PoolMax { get; init; }

    /// <summary>
    /// Reported alongside utilisation because the two together decide the
    /// diagnosis: a pool at 100% with zero waiting clients is correctly sized and
    /// busy, not exhausted.
    /// </summary>
    [JsonPropertyName("pool_waiting_clients")]
    public int PoolWaitingClients { get; init; }

    [JsonPropertyName("pool_utilization")]
    public double PoolUtilization { get; init; }

    [JsonPropertyName("replication_lag_ms")]
    public double? ReplicationLagMilliseconds { get; init; }

    [JsonPropertyName("slow_queries")]
    public IReadOnlyList<SlowQueryEnvelope> SlowQueries { get; init; } = [];

    public static DatabaseEnvelope From(string service, DatabaseHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return new DatabaseEnvelope
        {
            Service = service,
            Provider = health.Provider,
            IsAcceptingConnections = health.IsAcceptingConnections,
            PoolActive = health.Pool.ActiveConnections,
            PoolIdle = health.Pool.IdleConnections,
            PoolMax = health.Pool.MaxConnections,
            PoolWaitingClients = health.Pool.WaitingClients,
            PoolUtilization = Math.Round(health.Pool.Utilization, 3),
            ReplicationLagMilliseconds = health.ReplicationLag?.TotalMilliseconds,
            SlowQueries = [.. health.SlowQueries.Select(static query => new SlowQueryEnvelope(
                query.NormalizedStatement,
                query.Calls,
                Math.Round(query.TotalTime.TotalMilliseconds, 1),
                Math.Round(query.MeanTime.TotalMilliseconds, 2)))],
        };
    }
}

/// <summary>A slow statement, with literals already stripped by the adapter.</summary>
public sealed record SlowQueryEnvelope(
    [property: JsonPropertyName("statement")] string NormalizedStatement,
    [property: JsonPropertyName("calls")] long Calls,
    [property: JsonPropertyName("total_ms")] double TotalMilliseconds,
    [property: JsonPropertyName("mean_ms")] double MeanMilliseconds);

/// <summary>Serialization metadata for tool result envelopes.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MetricEnvelope))]
[JsonSerializable(typeof(LogEnvelope))]
[JsonSerializable(typeof(KubernetesEnvelope))]
[JsonSerializable(typeof(KubernetesEventsEnvelope))]
[JsonSerializable(typeof(DatabaseEnvelope))]
public sealed partial class ToolResultJsonContext : JsonSerializerContext;
