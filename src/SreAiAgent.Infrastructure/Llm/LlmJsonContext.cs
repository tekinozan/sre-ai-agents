using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>
/// Compile-time serialization metadata for model responses
/// (docs/DECISIONS.md D10).
/// </summary>
/// <remarks>
/// Separate from the wire contract context because these types are not a wire
/// contract: nobody outside the process sees them, they mirror the JSON Schemas
/// rather than the API, and they change whenever a prompt's output shape does.
/// Keeping them apart means a change to a model response cannot accidentally
/// alter what an HTTP client receives.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AgentFindingPayload))]
[JsonSerializable(typeof(AgentTurnPayload))]
[JsonSerializable(typeof(ClassificationPayload))]
[JsonSerializable(typeof(CorrelationPayload))]
[JsonSerializable(typeof(RecommendationSetPayload))]
[JsonSerializable(typeof(ToolRequestPayload))]
public sealed partial class LlmJsonContext : JsonSerializerContext;

/// <summary>Shared options for model response parsing.</summary>
internal static class LlmJson
{
    /// <summary>
    /// Built once and frozen. <see cref="JsonSerializerOptions"/> caches converter
    /// metadata per instance, so constructing one per model call throws that cache
    /// away every time.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(LlmJsonContext.Default.Options)
        {
            // Model output is untrusted. A deeply nested document is a cheap way
            // to burn CPU in the parser, and the schemas here are three levels
            // deep at most.
            MaxDepth = 16,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };

        options.MakeReadOnly();
        return options;
    }
}
