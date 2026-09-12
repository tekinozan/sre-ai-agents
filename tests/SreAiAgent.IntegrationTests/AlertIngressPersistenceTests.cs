using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SreAiAgent.Api.Security;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AlertIngressPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly SreApiPostgresFactory _factory = new(postgres);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await using SreDbContext db = postgres.CreateContext();
        await db.OutboxMessages.ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnAcceptedAlertLeavesExactlyOneOutboxRow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SreDbContext db = postgres.CreateContext())
        {
            await db.OutboxMessages.ExecuteDeleteAsync(ct);
        }

        using HttpClient client = _factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        // Shaped like a real Alertmanager webhook_config delivery: the group
        // envelope, not a bare alert.
        const string ValidPayload = """
            {
              "status": "firing",
              "alerts": [
                {
                  "status": "firing",
                  "fingerprint": "persisted-8a5f2c1d9e",
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
              ]
            }
            """;

        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/alerts/alertmanager")
        {
            Content = new StringContent(ValidPayload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using SreDbContext reader = postgres.CreateContext();
        List<OutboxMessageEntity> rows = await reader.OutboxMessages.ToListAsync(ct);

        rows.Count.ShouldBe(1);
        OutboxMessageEntity row = rows[0];
        row.Topic.ShouldBe(KafkaTopics.AlertReceived);
        row.EventType.ShouldBe("sre.alert.received");
        row.EventId.ShouldNotBe(Guid.Empty);
        row.PartitionKey.ShouldNotBeNullOrWhiteSpace();
        row.OccurredAt.ShouldNotBe(default);

        using JsonDocument doc = JsonDocument.Parse(row.PayloadJson);
        doc.RootElement.GetProperty("event_id").GetGuid().ShouldBe(row.EventId);
        doc.RootElement.GetProperty("event_type").GetString().ShouldBe("sre.alert.received");
        doc.RootElement.GetProperty("event_version").GetInt32().ShouldBe(1);
        doc.RootElement
            .GetProperty("payload")
            .GetProperty("labels")
            .GetProperty("tenant")
            .GetString()
            .ShouldBe("tenant-a");
    }

    [Fact]
    public async Task ATwoAlertEnvelopeLeavesExactlyTwoOutboxRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SreDbContext db = postgres.CreateContext())
        {
            await db.OutboxMessages.ExecuteDeleteAsync(ct);
        }

        using HttpClient client = _factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        const string TwoAlertPayload = """
            {
              "status": "firing",
              "alerts": [
                {
                  "status": "firing",
                  "fingerprint": "persisted-batch-one",
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
                  "fingerprint": "persisted-batch-two",
                  "startsAt": "2026-08-26T09:12:00Z",
                  "labels": {
                    "alertname": "HighCheckoutErrorRate",
                    "service": "checkout",
                    "environment": "production",
                    "severity": "warning"
                  },
                  "annotations": {"summary": "Checkout error rate"}
                }
              ]
            }
            """;

        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/alerts/alertmanager")
        {
            Content = new StringContent(TwoAlertPayload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using SreDbContext reader = postgres.CreateContext();
        List<OutboxMessageEntity> rows = await reader.OutboxMessages.ToListAsync(ct);

        // The batch is atomic: both alerts land in the same commit, as exactly
        // two rows rather than a partial write.
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => row.Topic == KafkaTopics.AlertReceived
            && row.EventType == "sre.alert.received");
    }

    [Fact]
    public async Task ARefusedCrossTenantAlertLeavesNoOutboxRow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SreDbContext db = postgres.CreateContext())
        {
            await db.OutboxMessages.ExecuteDeleteAsync(ct);
        }

        using HttpClient client = _factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        const string CrossTenantPayload = """
            {
              "status": "firing",
              "alerts": [
                {
                  "status": "firing",
                  "fingerprint": "persisted-cross-tenant-1",
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

        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/alerts/alertmanager")
        {
            Content = new StringContent(CrossTenantPayload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using SreDbContext reader = postgres.CreateContext();
        List<OutboxMessageEntity> rows = await reader.OutboxMessages.ToListAsync(ct);
        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ABatchWithOneCrossTenantAlertLeavesZeroOutboxRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SreDbContext db = postgres.CreateContext())
        {
            await db.OutboxMessages.ExecuteDeleteAsync(ct);
        }

        using HttpClient client = _factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        // The first alert is otherwise valid for tenant-a; the second names a
        // different tenant. The whole batch must be rejected, not just the
        // second alert, so the first alert's row must not appear either.
        const string MixedTenantBatchPayload = """
            {
              "status": "firing",
              "alerts": [
                {
                  "status": "firing",
                  "fingerprint": "persisted-mixed-batch-one",
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
                  "fingerprint": "persisted-mixed-batch-two",
                  "startsAt": "2026-08-26T09:12:00Z",
                  "labels": {
                    "alertname": "HighCheckoutErrorRate",
                    "service": "checkout",
                    "environment": "production",
                    "severity": "warning",
                    "tenant": "tenant-b"
                  },
                  "annotations": {"summary": "Checkout error rate"}
                }
              ]
            }
            """;

        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/alerts/alertmanager")
        {
            Content = new StringContent(MixedTenantBatchPayload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using SreDbContext reader = postgres.CreateContext();
        List<OutboxMessageEntity> rows = await reader.OutboxMessages.ToListAsync(ct);
        rows.ShouldBeEmpty();
    }
}
