namespace SreAiAgent.Domain.Incidents;

/// <summary>
/// The normalized facts needed to open an incident. Grouping them keeps the
/// aggregate constructor readable and, more importantly, makes it impossible to
/// transpose two adjacent strings such as service and environment at a call
/// site, which a twelve-argument positional constructor invites.
/// </summary>
/// <param name="Tenant">Owning tenant. Carried into every tool execution context.</param>
/// <param name="Source">Alert source, for example <c>alertmanager</c>.</param>
/// <param name="ExternalId">The source's own identifier for the alert.</param>
/// <param name="Fingerprint">Deduplication key from <see cref="IncidentFingerprint"/>.</param>
/// <param name="Title">Short human-readable summary.</param>
/// <param name="Description">Optional longer description from the alert.</param>
/// <param name="Service">The affected service.</param>
/// <param name="Environment">The affected environment.</param>
/// <param name="Severity">Normalized severity.</param>
/// <param name="DetectedAt">When the source observed the condition.</param>
/// <param name="Labels">Normalized labels, stored for routing and display.</param>
public sealed record NewIncident(
    string Tenant,
    string Source,
    string ExternalId,
    string Fingerprint,
    string Title,
    string? Description,
    string Service,
    string Environment,
    IncidentSeverity Severity,
    DateTimeOffset DetectedAt,
    IReadOnlyDictionary<string, string>? Labels = null);
