namespace SreAiAgent.Domain.Incidents;

/// <summary>
/// Normalized severity. Provider-specific scales (Alertmanager labels, Datadog
/// priorities, Grafana states) are mapped onto this by their adapter, so the
/// domain never sees a vendor vocabulary (ARCHITECTURE.md 10.1).
/// </summary>
/// <remarks>
/// Ordered from least to most urgent so comparisons and thresholds read
/// naturally, for example <c>severity >= IncidentSeverity.High</c>.
/// </remarks>
public enum IncidentSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}
