using System.Text.Json;

namespace SreAiAgent.Contracts.Serialization;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance used at every wire
/// boundary.
/// </summary>
/// <remarks>
/// One shared, frozen instance on purpose. <see cref="JsonSerializerOptions"/>
/// builds and caches converter metadata per instance, so creating one per call
/// discards that cache every time and is a well known throughput cliff.
/// </remarks>
public static class SreAiAgentJson
{
    /// <summary>Options backed by the generated context, safe to share across threads.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(SreAiAgentJsonContext.Default.Options)
        {
            // Untrusted input arrives from alert webhooks and Kafka. Rejecting
            // unknown members would break on a provider adding a field, so they
            // are ignored; comments and trailing commas are not accepted,
            // because accepting malformed JSON only widens the parser surface.
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 32,
        };

        options.MakeReadOnly();
        return options;
    }
}
