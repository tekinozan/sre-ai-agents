using System.Text.Json;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Alerts;

// One adapter per supported provider (ARCHITECTURE.md 10.1).
//
// The identity label keys each adapter nominates are the important decision in
// this file. They decide what counts as "the same alert", and choosing a label
// that changes on every restart, a pod name being the usual example, opens a new
// incident on every restart during exactly the crash loop that most needs to be
// one incident.

/// <summary>Normalizes a Prometheus Alertmanager webhook.</summary>
internal sealed class AlertmanagerAlertAdapter : IAlertSourceAdapter
{
    /// <inheritdoc />
    public string Source => "alertmanager";

    /// <inheritdoc />
    public Result<IReadOnlyList<CanonicalAlert>> Normalize(JsonElement payload)
    {
        // Alertmanager's webhook_config always posts the group envelope
        // ({"status":...,"alerts":[...],"groupLabels":...,...}), even for a
        // single firing alert. Reading fields off the envelope root, as this
        // adapter once did, silently matches nothing on real traffic; every
        // field this adapter needs lives on each element of `alerts`.
        IReadOnlyList<JsonElement> alerts = AlertFieldReader.Array(payload, "alerts");

        if (alerts.Count == 0)
        {
            return Result<IReadOnlyList<CanonicalAlert>>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "An Alertmanager webhook payload requires a non-empty 'alerts' array."));
        }

        List<CanonicalAlert> canonical = new(alerts.Count);

        foreach (JsonElement alert in alerts)
        {
            Result<CanonicalAlert> mapped = NormalizeOne(alert);

            if (mapped.IsFailure)
            {
                // A partially valid batch fails as a whole. Dropping the one bad
                // alert and opening incidents for the rest would mean a firing
                // condition Alertmanager told this platform about never opens an
                // incident, with nothing in the response saying so — exactly the
                // silent loss the Result contract on this port exists to avoid.
                // Alertmanager retries a webhook_config delivery it never gets a
                // 2xx for, so refusing the whole group is recoverable; silently
                // accepting part of it is not.
                return Result<IReadOnlyList<CanonicalAlert>>.Failure(mapped.Error);
            }

            canonical.Add(mapped.Value);
        }

        return Result<IReadOnlyList<CanonicalAlert>>.Success(canonical);
    }

    private static Result<CanonicalAlert> NormalizeOne(JsonElement alert)
    {
        JsonElement labels = AlertFieldReader.Object(alert, "labels");
        JsonElement annotations = AlertFieldReader.Object(alert, "annotations");

        Dictionary<string, string> labelMap = AlertFieldReader.Map(labels);
        Dictionary<string, string> annotationMap = AlertFieldReader.Map(annotations);

        string? alertName = Value(labelMap, "alertname");
        string? service = Value(labelMap, "service") ?? Value(labelMap, "job");

        if (alertName is null || service is null)
        {
            return Result<CanonicalAlert>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "An Alertmanager alert requires the 'alertname' and 'service' or 'job' labels."));
        }

        return Result<CanonicalAlert>.Success(new CanonicalAlert(
            "alertmanager",
            AlertFieldReader.String(alert, "fingerprint") ?? alertName,
            AlertFieldReader.String(alert, "fingerprint"),
            Value(annotationMap, "summary") ?? alertName,
            Value(annotationMap, "description"),
            service,
            Value(labelMap, "environment") ?? Value(labelMap, "env") ?? "unknown",
            AlertFieldReader.Severity(Value(labelMap, "severity")),
            AlertFieldReader.Timestamp(alert, "startsAt"),
            labelMap,
            annotationMap,
            RawPayloadReference: null)
        {
            // Alertmanager's own grouping is by these labels, so matching them
            // keeps the platform's incidents aligned with the operator's alerts.
            IdentityLabelKeys = ["alertname", "service", "job", "environment", "env"],
            // Each alert element carries its own status. The envelope also has a
            // top-level status, but that is the group's status, not necessarily
            // this alert's — Alertmanager groups by shared labels, not by shared
            // resolution state, and only the per-alert field is true for the
            // alert being mapped here.
            IsResolved = string.Equals(
                AlertFieldReader.String(alert, "status"), "resolved", StringComparison.OrdinalIgnoreCase),
        });
    }

    private static string? Value(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>Normalizes a Datadog monitor webhook.</summary>
internal sealed class DatadogAlertAdapter : IAlertSourceAdapter
{
    /// <inheritdoc />
    public string Source => "datadog";

    /// <inheritdoc />
    public Result<IReadOnlyList<CanonicalAlert>> Normalize(JsonElement payload)
    {
        string? monitorId = AlertFieldReader.String(payload, "id");
        string? title = AlertFieldReader.String(payload, "title");
        string? service = AlertFieldReader.String(payload, "service");

        if (monitorId is null || title is null || service is null)
        {
            return Result<IReadOnlyList<CanonicalAlert>>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "A Datadog payload requires 'id', 'title' and 'service'."));
        }

        Dictionary<string, string> tags = AlertFieldReader.Map(AlertFieldReader.Object(payload, "tags"));

        // Datadog reuses the monitor id across firings of the same monitor, which
        // is exactly the grouping wanted: one incident per monitor per
        // environment rather than one per notification.
        tags["monitor_id"] = monitorId;

        return Result<IReadOnlyList<CanonicalAlert>>.Success([new CanonicalAlert(
            Source,
            monitorId,
            Fingerprint: null,
            title,
            AlertFieldReader.String(payload, "body"),
            service,
            AlertFieldReader.String(payload, "env") ?? "unknown",
            AlertFieldReader.Severity(AlertFieldReader.String(payload, "priority")
                ?? AlertFieldReader.String(payload, "alert_type")),
            AlertFieldReader.Timestamp(payload, "last_updated"),
            tags,
            new Dictionary<string, string>(StringComparer.Ordinal),
            RawPayloadReference: null)
        {
            IdentityLabelKeys = ["monitor_id", "env"],
            IsResolved = string.Equals(
                AlertFieldReader.String(payload, "alert_transition"),
                "Recovered",
                StringComparison.OrdinalIgnoreCase),
        }]);
    }
}

/// <summary>Normalizes a Grafana unified-alerting webhook.</summary>
internal sealed class GrafanaAlertAdapter : IAlertSourceAdapter
{
    /// <inheritdoc />
    public string Source => "grafana";

    /// <inheritdoc />
    public Result<IReadOnlyList<CanonicalAlert>> Normalize(JsonElement payload)
    {
        Dictionary<string, string> labels = AlertFieldReader.Map(AlertFieldReader.Object(payload, "labels"));
        Dictionary<string, string> annotations =
            AlertFieldReader.Map(AlertFieldReader.Object(payload, "annotations"));

        string? ruleName = AlertFieldReader.String(payload, "ruleName")
            ?? (labels.TryGetValue("alertname", out string? alertName) ? alertName : null);

        string? service = labels.TryGetValue("service", out string? fromLabel) ? fromLabel : null;

        if (ruleName is null || string.IsNullOrWhiteSpace(service))
        {
            return Result<IReadOnlyList<CanonicalAlert>>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "A Grafana payload requires a rule name and a 'service' label."));
        }

        return Result<IReadOnlyList<CanonicalAlert>>.Success([new CanonicalAlert(
            Source,
            AlertFieldReader.String(payload, "fingerprint") ?? ruleName,
            AlertFieldReader.String(payload, "fingerprint"),
            annotations.TryGetValue("summary", out string? summary) ? summary : ruleName,
            annotations.TryGetValue("description", out string? description) ? description : null,
            service,
            labels.TryGetValue("environment", out string? environment) ? environment : "unknown",
            AlertFieldReader.Severity(labels.TryGetValue("severity", out string? severity) ? severity : null),
            AlertFieldReader.Timestamp(payload, "startsAt"),
            labels,
            annotations,
            RawPayloadReference: null)
        {
            IdentityLabelKeys = ["alertname", "service", "environment"],
            IsResolved = string.Equals(
                AlertFieldReader.String(payload, "status"), "resolved", StringComparison.OrdinalIgnoreCase),
        }]);
    }
}

/// <summary>
/// Normalizes a payload that is already in the canonical shape.
/// </summary>
/// <remarks>
/// The escape hatch for a source nobody has written an adapter for. It validates
/// rather than maps, so an integration that cannot wait for a first-class adapter
/// has a supported path in and still cannot put a malformed alert into the
/// pipeline.
/// </remarks>
internal sealed class WebhookAlertAdapter : IAlertSourceAdapter
{
    /// <inheritdoc />
    public string Source => "webhook";

    /// <inheritdoc />
    public Result<IReadOnlyList<CanonicalAlert>> Normalize(JsonElement payload)
    {
        string? externalId = AlertFieldReader.String(payload, "external_id");
        string? title = AlertFieldReader.String(payload, "title");
        string? service = AlertFieldReader.String(payload, "service");
        string? environment = AlertFieldReader.String(payload, "environment");

        if (externalId is null || title is null || service is null || environment is null)
        {
            return Result<IReadOnlyList<CanonicalAlert>>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "A webhook payload requires 'external_id', 'title', 'service' and 'environment'."));
        }

        Dictionary<string, string> labels = AlertFieldReader.Map(AlertFieldReader.Object(payload, "labels"));

        return Result<IReadOnlyList<CanonicalAlert>>.Success([new CanonicalAlert(
            Source,
            externalId,
            AlertFieldReader.String(payload, "fingerprint"),
            title,
            AlertFieldReader.String(payload, "description"),
            service,
            environment,
            AlertFieldReader.Severity(AlertFieldReader.String(payload, "severity")),
            AlertFieldReader.Timestamp(payload, "started_at"),
            labels,
            AlertFieldReader.Map(AlertFieldReader.Object(payload, "annotations")),
            RawPayloadReference: null)
        {
            // No identity labels are assumed. A generic webhook has no convention
            // to rely on, so identity falls back to source, service, environment
            // and title, which is stable for any well-behaved sender.
            IdentityLabelKeys = [],
        }]);
    }
}
