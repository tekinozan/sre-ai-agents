using System.Text.Json;
using System.Text.Json.Serialization;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Persistence;

/// <summary>
/// Serialization for the JSONB columns (ARCHITECTURE.md 9.1).
/// </summary>
/// <remarks>
/// Source-generated, like every other serialization path in the platform. This
/// one runs per row on read as well as write, so the reflection-based path would
/// be paid once per evidence row on a report that returns hundreds of them.
///
/// JSONB is used for genuinely schemaless payloads only: labels, structured
/// findings and the flag snapshot. Anything queried or indexed gets a real
/// column, because a filter over a JSONB path is a sequential scan unless someone
/// remembers to add a GIN index, and nobody does until it is slow.
/// </remarks>
internal static class JsonbConverters
{
    public static string Serialize(IReadOnlyDictionary<string, string> value)
        => JsonSerializer.Serialize(value, JsonbJsonContext.Default.IReadOnlyDictionaryStringString);

    public static IReadOnlyDictionary<string, string> DeserializeLabels(string json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize(json, JsonbJsonContext.Default.IReadOnlyDictionaryStringString)
              ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public static string SerializeFinding(AgentFinding value)
        => JsonSerializer.Serialize(value, JsonbJsonContext.Default.AgentFinding);

    public static AgentFinding DeserializeFinding(string json)
        => JsonSerializer.Deserialize(json, JsonbJsonContext.Default.AgentFinding)
           ?? AgentFinding.Empty("unreadable");

    public static string SerializeObservations(IReadOnlyList<Observation> value)
        => JsonSerializer.Serialize(value, JsonbJsonContext.Default.IReadOnlyListObservation);

    public static IReadOnlyList<Observation> DeserializeObservations(string json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, JsonbJsonContext.Default.IReadOnlyListObservation) ?? [];

    public static string SerializeHypotheses(IReadOnlyList<Hypothesis> value)
        => JsonSerializer.Serialize(value, JsonbJsonContext.Default.IReadOnlyListHypothesis);

    public static IReadOnlyList<Hypothesis> DeserializeHypotheses(string json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, JsonbJsonContext.Default.IReadOnlyListHypothesis) ?? [];

    public static string SerializeStrings(IReadOnlyList<string> value)
        => JsonSerializer.Serialize(value, JsonbJsonContext.Default.IReadOnlyListString);

    public static IReadOnlyList<string> DeserializeStrings(string json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, JsonbJsonContext.Default.IReadOnlyListString) ?? [];

    public static string SerializeEvidenceIds(IReadOnlyList<EvidenceId> value)
        => JsonSerializer.Serialize(value, JsonbJsonContext.Default.IReadOnlyListEvidenceId);

    public static IReadOnlyList<EvidenceId> DeserializeEvidenceIds(string json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize(json, JsonbJsonContext.Default.IReadOnlyListEvidenceId) ?? [];
}

/// <summary>Serialization metadata for JSONB columns.</summary>
/// <remarks>
/// Observation has no public constructor, so a converter supplies the factory
/// call rather than letting the serializer bypass the invariant. Without it a row
/// could be read back as a verified observation with no evidence, which is the
/// exact state the domain refuses to construct.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<EvidenceId>))]
[JsonSerializable(typeof(IReadOnlyList<Observation>))]
[JsonSerializable(typeof(IReadOnlyList<Hypothesis>))]
[JsonSerializable(typeof(AgentFinding))]
internal sealed partial class JsonbJsonContext : JsonSerializerContext;
