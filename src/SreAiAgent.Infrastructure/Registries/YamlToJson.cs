using System.Text.Json;
using System.Text.Json.Nodes;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Converts the untyped map YamlDotNet produces into a
/// <see cref="JsonElement"/>.
/// </summary>
/// <remarks>
/// Tool argument schemas are authored in YAML because the rest of the artifact
/// is, but they are JSON Schema and are consumed as JSON by both the schema
/// validator and the model. Converting once at startup means neither consumer
/// pays for it, and the schema is handed to both from the same source so they
/// cannot disagree about what a tool accepts.
///
/// YAML scalars arrive as strings regardless of how they were written, so
/// numbers and booleans are recovered here. Leaving <c>maximum: 360</c> as the
/// string "360" would make the schema silently wrong: a numeric constraint
/// expressed as a string is not applied by any JSON Schema implementation.
/// </remarks>
internal static class YamlToJson
{
    public static JsonElement Convert(object? value)
    {
        JsonNode? node = ToNode(value);
        return node is null
            ? JsonDocument.Parse("{}").RootElement.Clone()
            : JsonSerializer.SerializeToElement(node);
    }

    private static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case IDictionary<object, object> map:
            {
                JsonObject json = [];
                foreach ((object key, object item) in map)
                {
                    string name = System.Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;
                    json[name] = ToNode(item);
                }

                return json;
            }

            case IList<object> list:
            {
                JsonArray json = [];
                foreach (object item in list)
                {
                    json.Add(ToNode(item));
                }

                return json;
            }

            case string text:
                return ParseScalar(text);

            default:
                return JsonValue.Create(value);
        }
    }

    private static JsonValue ParseScalar(string text)
    {
        if (bool.TryParse(text, out bool boolean))
        {
            return JsonValue.Create(boolean);
        }

        // Integers before doubles: a JSON Schema `maximum: 360` written as 360.0
        // is still valid, but round-tripping an integer through a double turns a
        // clean bound into a floating-point one for no reason.
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
        {
            return JsonValue.Create(integer);
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(text);
    }
}
