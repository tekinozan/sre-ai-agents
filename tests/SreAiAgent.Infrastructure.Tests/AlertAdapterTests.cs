using System.Text.Json;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Alerts;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers the provider adapters of ARCHITECTURE.md 10.1.
/// </summary>
/// <remarks>
/// The payloads here are shaped like the real thing rather than minimised,
/// because the failure these tests exist to catch is a field that is present but
/// named differently, and a fixture trimmed to the fields the adapter reads can
/// never catch that.
///
/// The identity labels each adapter nominates get their own assertions. They
/// decide what counts as the same alert, and getting them wrong does not throw:
/// it quietly opens one incident per alert during a crash loop, which is exactly
/// when a single incident matters most.
/// </remarks>
public sealed class AlertAdapterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AlertmanagerPayloadsMapOntoTheCanonicalShape()
    {
        const string Payload = """
            {
              "status": "firing",
              "fingerprint": "8a5f2c1d9e",
              "startsAt": "2026-08-26T09:12:00Z",
              "labels": {
                "alertname": "HighCheckoutLatency",
                "service": "checkout",
                "environment": "production",
                "severity": "critical",
                "component": "database"
              },
              "annotations": {
                "summary": "Checkout p99 latency above 2s",
                "description": "Sustained for 5 minutes"
              }
            }
            """;

        Result<CanonicalAlert> result = new AlertmanagerAlertAdapter().Normalize(Parse(Payload));

        CanonicalAlert alert = result.Value;
        alert.Source.ShouldBe("alertmanager");
        alert.Title.ShouldBe("Checkout p99 latency above 2s");
        alert.Service.ShouldBe("checkout");
        alert.Environment.ShouldBe("production");
        alert.Severity.ShouldBe(IncidentSeverity.Critical);
        alert.Fingerprint.ShouldBe("8a5f2c1d9e");
        alert.Labels["component"].ShouldBe("database");
        alert.IsResolved.ShouldBeFalse();
        alert.IdentityLabelKeys.ShouldContain("alertname");
    }

    [Fact]
    public void AnAlertmanagerPayloadWithoutAServiceIsRejectedRatherThanGuessed()
    {
        const string Payload = """
            {"labels": {"alertname": "SomethingBroke", "severity": "warning"}}
            """;

        Result<CanonicalAlert> result = new AlertmanagerAlertAdapter().Normalize(Parse(Payload));

        // Guessing a service would attribute the incident to the wrong team and
        // route the wrong agents at it.
        result.IsFailure.ShouldBeTrue();
        result.Error.IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public void AResolvingAlertIsMarkedResolved()
    {
        const string Payload = """
            {
              "status": "resolved",
              "labels": {"alertname": "HighLatency", "service": "checkout", "severity": "high"}
            }
            """;

        new AlertmanagerAlertAdapter().Normalize(Parse(Payload)).Value.IsResolved.ShouldBeTrue();
    }

    [Fact]
    public void DatadogPayloadsUseTheMonitorIdAsIdentity()
    {
        const string Payload = """
            {
              "id": "4821993",
              "title": "[Triggered] Orders queue depth",
              "body": "Queue depth above 10000",
              "service": "orders",
              "env": "production",
              "priority": "P2",
              "alert_transition": "Triggered",
              "last_updated": "2026-08-26T09:20:00Z",
              "tags": {"team": "fulfilment"}
            }
            """;

        CanonicalAlert alert = new DatadogAlertAdapter().Normalize(Parse(Payload)).Value;

        alert.ExternalId.ShouldBe("4821993");
        alert.Severity.ShouldBe(IncidentSeverity.High);

        // Datadog reuses the monitor id across firings, which is the grouping
        // wanted: one incident per monitor per environment.
        alert.Labels["monitor_id"].ShouldBe("4821993");
        alert.IdentityLabelKeys.ShouldBe(["monitor_id", "env"]);
    }

    [Fact]
    public void GrafanaPayloadsFallBackToTheRuleNameForATitle()
    {
        const string Payload = """
            {
              "status": "firing",
              "ruleName": "PaymentsErrorRate",
              "labels": {"service": "payments", "environment": "staging", "severity": "warning"},
              "annotations": {}
            }
            """;

        CanonicalAlert alert = new GrafanaAlertAdapter().Normalize(Parse(Payload)).Value;

        alert.Title.ShouldBe("PaymentsErrorRate");
        alert.Environment.ShouldBe("staging");
        alert.Severity.ShouldBe(IncidentSeverity.Medium);
    }

    [Fact]
    public void TheWebhookAdapterValidatesRatherThanMaps()
    {
        const string Missing = """{"external_id": "x", "title": "t"}""";

        new WebhookAlertAdapter().Normalize(Parse(Missing)).IsFailure.ShouldBeTrue();

        const string Complete = """
            {
              "external_id": "ext-1",
              "title": "Disk almost full",
              "service": "search",
              "environment": "production",
              "severity": "sev2"
            }
            """;

        CanonicalAlert alert = new WebhookAlertAdapter().Normalize(Parse(Complete)).Value;
        alert.Severity.ShouldBe(IncidentSeverity.High);

        // A generic sender has no label convention to rely on, so identity falls
        // back to source, service, environment and title.
        alert.IdentityLabelKeys.ShouldBeEmpty();
    }

    [Fact]
    public void AnUnknownSeverityBecomesMediumRatherThanBeingRejected()
    {
        const string Payload = """
            {"labels": {"alertname": "Odd", "service": "checkout", "severity": "spicy"}}
            """;

        new AlertmanagerAlertAdapter().Normalize(Parse(Payload)).Value.Severity
            .ShouldBe(IncidentSeverity.Medium);
    }

    [Fact]
    public void AnUnconfiguredSourceIsRefusedRatherThanTreatedAsAGenericWebhook()
    {
        AlertNormalizer normalizer = new(
        [
            new AlertmanagerAlertAdapter(),
            new WebhookAlertAdapter(),
        ]);

        // Falling back to the webhook adapter would silently accept anything and
        // attribute it to a provider nobody configured.
        normalizer.Normalize("pagerduty", Parse("{}")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AResolvedAlertOpensAnIncidentWithoutStartingAnInvestigation()
    {
        const string Payload = """
            {
              "status": "resolved",
              "labels": {"alertname": "HighLatency", "service": "checkout", "severity": "high"}
            }
            """;

        AlertNormalizer normalizer = new([new AlertmanagerAlertAdapter()]);

        // The condition has already cleared, so spending a model budget would
        // produce a report about something that is no longer happening.
        normalizer.Normalize("alertmanager", Parse(Payload)).Value.StartInvestigation.ShouldBe(false);
    }
}
