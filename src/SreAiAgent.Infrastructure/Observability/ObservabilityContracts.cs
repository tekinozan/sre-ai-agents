using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Infrastructure.Observability;

// The Prometheus HTTP API response, modelled only as far as this platform reads
// it (docs/DECISIONS.md D10: reflection-based serialization is banned, so every
// shape that crosses a boundary needs generated metadata).
//
// Samples arrive as heterogeneous arrays — [1693000000.5, "0.42"] — which no
// typed model represents honestly, so they stay as JsonElement and are read
// positionally. Inventing a tuple type here would only move the same two casts
// somewhere less obvious.

internal sealed class PrometheusResponse
{
    public string? Status { get; init; }

    public PrometheusData? Data { get; init; }

    /// <summary>
    /// Non-fatal problems: a federated query that lost a shard, a rule that has
    /// not evaluated yet. The answer is real but incomplete, which is exactly the
    /// case an agent must not read as "nothing is wrong".
    /// </summary>
    public List<string>? Warnings { get; init; }

    public string? ErrorType { get; init; }

    public string? Error { get; init; }
}

internal sealed class PrometheusData
{
    public string? ResultType { get; init; }

    public List<PrometheusSeries>? Result { get; init; }
}

internal sealed class PrometheusSeries
{
    public Dictionary<string, string>? Metric { get; init; }

    /// <summary>One sample, for an instant query.</summary>
    public List<JsonElement>? Value { get; init; }

    /// <summary>Samples over a window, for a range query.</summary>
    public List<List<JsonElement>>? Values { get; init; }
}

/// <summary>
/// Compile-time serialization metadata for the observability backends.
/// </summary>
/// <remarks>
/// Separate from the wire contract context because none of this is a wire
/// contract: it is the shape of somebody else's API, it changes when they change
/// it, and it must never influence what an HTTP client of this platform receives.
///
/// Names are camelCase, because that is what Prometheus and Loki emit.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(PrometheusResponse))]
[JsonSerializable(typeof(LokiResponse))]
internal sealed partial class ObservabilityJsonContext : JsonSerializerContext;

// Loki's response, modelled only as far as this platform reads it. Log lines
// arrive as ["<nanoseconds as string>", "<line>"] and metric samples as
// [<seconds as number>, "<value>"], so both stay as JsonElement.

internal sealed class LokiResponse
{
    public string? Status { get; init; }

    public LokiData? Data { get; init; }
}

internal sealed class LokiData
{
    public string? ResultType { get; init; }

    public List<LokiStream>? Result { get; init; }
}

internal sealed class LokiStream
{
    /// <summary>Stream labels, present on a log query.</summary>
    public Dictionary<string, string>? Stream { get; init; }

    /// <summary>Series labels, present on a metric query such as the count.</summary>
    public Dictionary<string, string>? Metric { get; init; }

    public List<List<JsonElement>>? Values { get; init; }

    public List<JsonElement>? Value { get; init; }
}
