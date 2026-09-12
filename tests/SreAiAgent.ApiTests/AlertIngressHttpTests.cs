using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SreAiAgent.Api.Security;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Contracts.Messaging;

namespace SreAiAgent.ApiTests;

/// <summary>
/// Proves the alert ingress HTTP boundary without requiring external infrastructure.
/// </summary>
public sealed class AlertIngressHttpTests(SreApiFactory factory) : IClassFixture<SreApiFactory>
{
    // Shaped like a real Alertmanager webhook_config delivery: the group
    // envelope, not a bare alert. Alertmanager posts this shape even for one
    // firing alert, so a fixture at the alert's own shape would never catch a
    // regression back to reading fields off the envelope root.
    private const string ValidPayload = """
        {
          "receiver": "sre-platform",
          "status": "firing",
          "alerts": [
            {
              "status": "firing",
              "fingerprint": "8a5f2c1d9e",
              "startsAt": "2026-08-26T09:12:00Z",
              "endsAt": "0001-01-01T00:00:00Z",
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
          ],
          "groupLabels": {"alertname": "HighCheckoutLatency"},
          "commonLabels": {"alertname": "HighCheckoutLatency"},
          "commonAnnotations": {},
          "externalURL": "http://alertmanager.internal",
          "version": "4",
          "groupKey": "{}:{alertname=\"HighCheckoutLatency\"}",
          "truncatedAlerts": 0
        }
        """;

    private const string TwoAlertPayload = """
        {
          "receiver": "sre-platform",
          "status": "firing",
          "alerts": [
            {
              "status": "firing",
              "fingerprint": "batch-alert-one",
              "startsAt": "2026-08-26T09:12:00Z",
              "labels": {
                "alertname": "HighCheckoutLatency",
                "service": "checkout",
                "environment": "production",
                "severity": "critical"
              },
              "annotations": {"summary": "Checkout latency"}
            },
            {
              "status": "firing",
              "fingerprint": "batch-alert-two",
              "startsAt": "2026-08-26T09:12:00Z",
              "labels": {
                "alertname": "HighCheckoutErrorRate",
                "service": "checkout",
                "environment": "production",
                "severity": "warning"
              },
              "annotations": {"summary": "Checkout error rate"}
            }
          ],
          "groupLabels": {"alertname": "HighCheckoutLatency"},
          "commonLabels": {},
          "commonAnnotations": {},
          "externalURL": "http://alertmanager.internal",
          "version": "4",
          "groupKey": "{}:{}",
          "truncatedAlerts": 0
        }
        """;

    [Fact]
    public async Task AnAuthorizedAlertIsAcceptedAndStagedOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        using HttpRequestMessage request = CreateRequest("/v1/alerts/alertmanager", ValidPayload, token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Publisher.Events.Count.ShouldBe(1);
        RecordedEvent staged = factory.Publisher.Events[0];
        staged.Topic.ShouldBe(KafkaTopics.AlertReceived);
        staged.EventType.ShouldBe("sre.alert.received");
        CanonicalAlert alert = staged.Payload.ShouldBeOfType<CanonicalAlert>();
        alert.Labels["tenant"].ShouldBe("tenant-a");
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ATwoAlertEnvelopeStagesExactlyTwoMessagesInOneCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        using HttpRequestMessage request = CreateRequest("/v1/alerts/alertmanager", TwoAlertPayload, token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Publisher.Events.Count.ShouldBe(2);
        factory.Publisher.Events.ShouldAllBe(staged => staged.Topic == KafkaTopics.AlertReceived);
        List<string?> fingerprints = [.. factory.Publisher.Events
            .Select(staged => staged.Payload.ShouldBeOfType<CanonicalAlert>().Fingerprint)];
        fingerprints.ShouldBe(["batch-alert-one", "batch-alert-two"]);

        // Both alerts commit in the single SaveChanges call the batch shares:
        // it is atomic, not one commit per alert.
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task AnAlertWithoutATokenIsRefusedBeforeAnythingIsStaged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = CreateRequest("/v1/alerts/alertmanager", ValidPayload, token: null);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.Publisher.Events.ShouldBeEmpty();
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task AViewerMayNotIngestAlerts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Viewer);

        using HttpRequestMessage request = CreateRequest("/v1/alerts/alertmanager", ValidPayload, token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Publisher.Events.ShouldBeEmpty();
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task APayloadNamingAnotherTenantIsRefusedWithForbidden()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        const string CrossTenantPayload = """
            {
              "status": "firing",
              "alerts": [
                {
                  "status": "firing",
                  "fingerprint": "8a5f2c1d9e",
                  "startsAt": "2026-08-26T09:12:00Z",
                  "labels": {
                    "alertname": "HighCheckoutLatency",
                    "service": "checkout",
                    "environment": "production",
                    "severity": "critical",
                    "tenant": "tenant-b"
                  },
                  "annotations": {
                    "summary": "Checkout p99 latency above 2s"
                  }
                }
              ]
            }
            """;

        using HttpRequestMessage request = CreateRequest("/v1/alerts/alertmanager", CrossTenantPayload, token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertProblemCodeAsync(response, "FORBIDDEN", ct);
        factory.Publisher.Events.ShouldBeEmpty();
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ACallerWithNoTenantClaimIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.WithoutTenant(SreRoles.Investigator);

        using HttpRequestMessage request = CreateRequest("/v1/alerts/alertmanager", ValidPayload, token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Publisher.Events.ShouldBeEmpty();
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task AnUnconfiguredSourceIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        using HttpRequestMessage request = CreateRequest("/v1/alerts/nope", ValidPayload, token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCodeAsync(response, "VALIDATION_FAILED", ct);
        factory.Publisher.Events.ShouldBeEmpty();
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task AMalformedPayloadIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        // Omits 'service' and 'job' labels, which the Alertmanager adapter requires.
        const string MalformedPayload = """
            {
              "status": "firing",
              "alerts": [
                {
                  "status": "firing",
                  "fingerprint": "8a5f2c1d9e",
                  "startsAt": "2026-08-26T09:12:00Z",
                  "labels": {
                    "alertname": "HighCheckoutLatency",
                    "environment": "production"
                  }
                }
              ]
            }
            """;

        using HttpRequestMessage request = CreateRequest("/v1/alerts/alertmanager", MalformedPayload, token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCodeAsync(response, "VALIDATION_FAILED", ct);
        factory.Publisher.Events.ShouldBeEmpty();
        factory.UnitOfWork.SaveChangesCallCount.ShouldBe(0);
    }

    private static HttpRequestMessage CreateRequest(string path, string jsonContent, string? token)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, string expectedCode, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("code", out JsonElement codeProperty).ShouldBeTrue();
        codeProperty.GetString().ShouldBe(expectedCode);
    }
}
