using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Workflows;

/// <summary>
/// Classifies an incident from labels and text without consulting a model
/// (ARCHITECTURE.md 11.2).
/// </summary>
/// <remarks>
/// This runs first on every incident, and on most of them it is the only
/// classification that happens. Routing on a <c>component=database</c> label is
/// free, instant, explainable and cannot hallucinate; consulting a model to reach
/// the same conclusion costs money and latency on every single incident and
/// occasionally gets it wrong.
///
/// The rules below deliberately look for structure first, because a label is an
/// assertion by whoever configured the alert, and text is a guess. A title
/// keyword only contributes when no label matched.
/// </remarks>
public static class DeterministicClassifier
{
    /// <summary>Label keys that name a component or subsystem directly.</summary>
    private static readonly string[] ComponentLabelKeys =
        ["component", "subsystem", "layer", "category"];

    private static readonly (string Keyword, string IncidentType)[] TextSignals =
    [
        ("connection pool", "database"),
        ("deadlock", "database"),
        ("slow query", "database"),
        ("postgres", "database"),
        ("mysql", "database"),
        ("database", "database"),
        ("oomkilled", "resource-exhaustion"),
        ("crashloop", "kubernetes"),
        ("readiness probe", "kubernetes"),
        ("liveness probe", "kubernetes"),
        ("pod", "kubernetes"),
        ("consumer lag", "queue"),
        ("kafka", "queue"),
        ("redis", "cache"),
        ("5xx", "error-rate"),
        ("error rate", "error-rate"),
        ("exception", "error-rate"),
        ("latency", "latency"),
        ("slo", "latency"),
        ("p99", "latency"),
        ("timeout", "timeout"),
        ("unavailable", "availability"),
        ("down", "availability"),
    ];

    /// <summary>
    /// Attempts a classification. Returns null when nothing matched, which is the
    /// signal for the caller to fall back to a model.
    /// </summary>
    public static IncidentClassification? TryClassify(IncidentSnapshot incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        HashSet<string> types = new(StringComparer.Ordinal);
        bool fromLabel = false;

        // Labels first. A label is an assertion by whoever configured the alert
        // and is worth more than a keyword that happens to appear in a title.
        foreach (string key in ComponentLabelKeys)
        {
            if (incident.Labels.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                types.Add(value.ToLowerInvariant());
                fromLabel = true;
            }
        }

        string haystack = string.Concat(
            incident.Title, " ", incident.Description ?? string.Empty, " ", incident.Service);

        foreach ((string keyword, string incidentType) in TextSignals)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                types.Add(incidentType);
            }
        }

        if (types.Count == 0)
        {
            return null;
        }

        // A label match is a stated fact and scores high. Text alone is inference,
        // and the confidence says so, which matters because routing widens the
        // agent selection below the low-confidence threshold rather than guessing.
        Confidence confidence = fromLabel
            ? Confidence.From(0.9m)
            : types.Count > 1 ? Confidence.From(0.65m) : Confidence.From(0.55m);

        return new IncidentClassification(
            [.. types.Order(StringComparer.Ordinal)],
            ClassificationMethod.Deterministic,
            confidence,
            fromLabel
                ? "Matched a component label on the alert."
                : "Matched keyword signals in the alert title and description.");
    }
}
