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
    public Result<CanonicalAlert> Normalize(JsonElement payload)
    {
        JsonElement labels = AlertFieldReader.Object(payload, "labels");
        JsonElement annotations = AlertFieldReader.Object(payload, "annotations");

        Dictionary<string, string> labelMap = AlertFieldReader.Map(labels);
        Dictionary<string, string> annotationMap = AlertFieldReader.Map(annotations);

        string? alertName = Value(labelMap, "alertname");
        string? service = Value(labelMap, "service") ?? Value(labelMap, "job");

        if (alertName is null || service is null)
        {
            return Result<CanonicalAlert>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "An Alertmanager payload requires the 'alertname' and 'service' or 'job' labels."));
        }

        return Result<CanonicalAlert>.Success(new CanonicalAlert(
            Source,
            AlertFieldReader.String(payload, "fingerprint") ?? alertName,
            AlertFieldReader.String(payload, "fingerprint"),
            Value(annotationMap, "summary") ?? alertName,
            Value(annotationMap, "description"),
            service,
            Value(labelMap, "environment") ?? Value(labelMap, "env") ?? "unknown",
            AlertFieldReader.Severity(Value(labelMap, "severity")),
            AlertFieldReader.Timestamp(payload, "startsAt"),
            labelMap,
            annotationMap,
            RawPayloadReference: null)
        {
            // Alertmanager's own grouping is by these labels, so matching them
            // keeps the platform's incidents aligned with the operator's alerts.
            IdentityLabelKeys = ["alertname", "service", "job", "environment", "env"],
            IsResolved = string.Equals(
                AlertFieldReader.String(payload, "status"), "resolved", StringComparison.OrdinalIgnoreCase),
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
    public Result<CanonicalAlert> Normalize(JsonElement payload)
    {
        string? monitorId = AlertFieldReader.String(payload, "id");
        string? title = AlertFieldReader.String(payload, "title");
        string? service = AlertFieldReader.String(payload, "service");

        if (monitorId is null || title is null || service is null)
        {
            return Result<CanonicalAlert>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "A Datadog payload requires 'id', 'title' and 'service'."));
        }

        Dictionary<string, string> tags = AlertFieldReader.Map(AlertFieldReader.Object(payload, "tags"));

        // Datadog reuses the monitor id across firings of the same monitor, which
        // is exactly the grouping wanted: one incident per monitor per
        // environment rather than one per notification.
        tags["monitor_id"] = monitorId;

        return Result<CanonicalAlert>.Success(new CanonicalAlert(
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
        });
    }
}

/// <summary>Normalizes a Grafana unified-alerting webhook.</summary>
internal sealed class GrafanaAlertAdapter : IAlertSourceAdapter
{
    /// <inheritdoc />
    public string Source => "grafana";

    /// <inheritdoc />
    public Result<CanonicalAlert> Normalize(JsonElement payload)
    {
        Dictionary<string, string> labels = AlertFieldReader.Map(AlertFieldReader.Object(payload, "labels"));
        Dictionary<string, string> annotations =
            AlertFieldReader.Map(AlertFieldReader.Object(payload, "annotations"));

        string? ruleName = AlertFieldReader.String(payload, "ruleName")
            ?? (labels.TryGetValue("alertname", out string? alertName) ? alertName : null);

        string? service = labels.TryGetValue("service", out string? fromLabel) ? fromLabel : null;

        if (ruleName is null || string.IsNullOrWhiteSpace(service))
        {
            return Result<CanonicalAlert>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "A Grafana payload requires a rule name and a 'service' label."));
        }

        return Result<CanonicalAlert>.Success(new CanonicalAlert(
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
        });
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
    public Result<CanonicalAlert> Normalize(JsonElement payload)
    {
        string? externalId = AlertFieldReader.String(payload, "external_id");
        string? title = AlertFieldReader.String(payload, "title");
        string? service = AlertFieldReader.String(payload, "service");
        string? environment = AlertFieldReader.String(payload, "environment");

        if (externalId is null || title is null || service is null || environment is null)
        {
            return Result<CanonicalAlert>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "A webhook payload requires 'external_id', 'title', 'service' and 'environment'."));
        }

        Dictionary<string, string> labels = AlertFieldReader.Map(AlertFieldReader.Object(payload, "labels"));

        return Result<CanonicalAlert>.Success(new CanonicalAlert(
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
        });
    }
}
