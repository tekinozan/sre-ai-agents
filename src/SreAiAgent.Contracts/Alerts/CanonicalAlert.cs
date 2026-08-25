using SreAiAgent.Domain.Incidents;

namespace SreAiAgent.Contracts.Alerts;

/// <summary>
/// The single alert shape the platform works with (ARCHITECTURE.md 10.1).
/// Every source adapter, whether Alertmanager, Datadog, Grafana, a generic
/// webhook or Kafka, produces this before anything else happens.
/// </summary>
/// <remarks>
/// The incident use case never sees a provider payload. That is what allows a
/// sixth alert source to be added without touching ingestion, deduplication or
/// routing.
/// </remarks>
/// <param name="Source">Adapter key, for example <c>alertmanager</c>.</param>
/// <param name="ExternalId">The source's own identifier, used for inbox deduplication.</param>
/// <param name="Fingerprint">
/// Deduplication key. An adapter may pass through a fingerprint the provider
/// already computed; otherwise ingestion derives one.
/// </param>
/// <param name="Title">Short human-readable summary.</param>
/// <param name="Description">Optional longer text from the provider.</param>
/// <param name="Service">Affected service.</param>
/// <param name="Environment">Affected environment.</param>
/// <param name="Severity">Severity mapped onto the platform's scale.</param>
/// <param name="StartedAt">When the provider first observed the condition.</param>
/// <param name="Labels">Provider labels, normalized to lowercase keys.</param>
/// <param name="Annotations">Provider annotations. Never used for identity.</param>
/// <param name="RawPayloadReference">
/// Pointer to the archived original payload. The payload itself is not carried
/// through the pipeline (ARCHITECTURE.md 9.1).
/// </param>
public sealed record CanonicalAlert(
    string Source,
    string ExternalId,
    string? Fingerprint,
    string Title,
    string? Description,
    string Service,
    string Environment,
    IncidentSeverity Severity,
    DateTimeOffset? StartedAt,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Annotations,
    string? RawPayloadReference)
{
    /// <summary>
    /// Label keys that identify the alert. Chosen by the adapter, because only
    /// the adapter knows which of its labels are stable: folding in a volatile
    /// label such as a pod name would open a new incident on every restart.
    /// </summary>
    public IReadOnlyList<string> IdentityLabelKeys { get; init; } = [];

    /// <summary>
    /// True when the source is reporting that the condition has cleared, so
    /// ingestion can close rather than open.
    /// </summary>
    public bool IsResolved { get; init; }
}
