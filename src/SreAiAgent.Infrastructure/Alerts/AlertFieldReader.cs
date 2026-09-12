using System.Text.Json;
using SreAiAgent.Domain.Incidents;

namespace SreAiAgent.Infrastructure.Alerts;

/// <summary>Shared field reading for the source adapters.</summary>
/// <remarks>
/// Every provider disagrees about names and about how severity is spelled, and
/// none of them guarantees a field is present. Reading defensively in one place
/// keeps each adapter to the mapping that is genuinely specific to it.
/// </remarks>
internal static class AlertFieldReader
{
    /// <summary>Reads a string property, or null when it is absent or not a string.</summary>
    public static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    /// <summary>Reads a nested object, or a default element when it is absent.</summary>
    public static JsonElement Object(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    /// <summary>Reads an array of objects, or an empty list when it is absent or not an array.</summary>
    public static IReadOnlyList<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<JsonElement> items = new(value.GetArrayLength());

        foreach (JsonElement item in value.EnumerateArray())
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>Reads a timestamp, or null when it is absent or unparseable.</summary>
    public static DateTimeOffset? Timestamp(JsonElement element, string name)
    {
        string? raw = String(element, name);

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    /// <summary>Flattens a JSON object of string values into a label dictionary.</summary>
    /// <remarks>
    /// Keys are lowercased because providers are inconsistent about casing and the
    /// same label arriving as <c>Component</c> and <c>component</c> would produce
    /// two fingerprints for one alert, and therefore two incidents.
    /// </remarks>
    public static Dictionary<string, string> Map(JsonElement element)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        if (element.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                map[property.Name.ToLowerInvariant()] = property.Value.GetString() ?? string.Empty;
            }
        }

        return map;
    }

    /// <summary>
    /// Maps a provider severity onto the platform's scale.
    /// </summary>
    /// <remarks>
    /// Unknown values become Medium rather than being rejected. A severity nobody
    /// anticipated is not a reason to discard an alert, and defaulting low would
    /// let a provider suppress its own pages by inventing a word.
    /// </remarks>
    public static IncidentSeverity Severity(string? value) => value?.ToLowerInvariant() switch
    {
        "critical" or "crit" or "p1" or "sev1" or "fatal" => IncidentSeverity.Critical,
        "high" or "error" or "p2" or "sev2" or "major" => IncidentSeverity.High,
        "medium" or "warning" or "warn" or "p3" or "sev3" => IncidentSeverity.Medium,
        "low" or "info" or "information" or "p4" or "sev4" or "minor" => IncidentSeverity.Low,
        _ => IncidentSeverity.Medium,
    };
}
