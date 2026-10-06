using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SreAiAgent.Api.Security;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Covers <c>GET /v1/incidents</c> against PostgreSQL: the keyset cursor, the
/// filters, and that a tenant only ever lists its own incidents.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IncidentListingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly SreApiPostgresFactory _factory = new(postgres);
    // The random tail, not the head: a version 7 id begins with its millisecond,
    // so two taken together share a prefix and the "other" tenant was this one.
    private readonly string _tenant = "list-a-" + Guid.CreateVersion7().ToString("N")[^12..];
    private readonly string _other = "list-b-" + Guid.CreateVersion7().ToString("N")[^12..];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await using SreDbContext db = postgres.CreateContext();
        List<IncidentId> ids = await db.Incidents
            .Where(incident => incident.Tenant == _tenant || incident.Tenant == _other)
            .Select(incident => incident.Id)
            .ToListAsync(CancellationToken.None);
        await db.RootCauseReports.Where(report => ids.Contains(report.IncidentId)).ExecuteDeleteAsync(CancellationToken.None);
        await db.IncidentEvents.Where(entry => ids.Contains(entry.IncidentId)).ExecuteDeleteAsync(CancellationToken.None);
        await db.Incidents.Where(incident => ids.Contains(incident.Id)).ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PagingVisitsEveryIncidentOnceNewestFirstAndNeverAnotherTenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Seven incidents, two pairs sharing a detection instant so the page
        // boundary has to break ties on the id.
        List<Incident> seeded = [];
        for (int index = 0; index < 7; index++)
        {
            seeded.Add(NewIncident(_tenant, "fp-" + index, Base.AddMinutes(index / 2 * 10), IncidentSeverity.High));
        }

        await SeedAsync([.. seeded, NewIncident(_other, "foreign", Base.AddHours(1), IncidentSeverity.High)], ct);

        List<Guid> visited = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            using JsonDocument page = await GetAsync(
                "/v1/incidents?limit=3" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), ct);
            visited.AddRange(page.RootElement.GetProperty("items").EnumerateArray()
                .Select(static item => item.GetProperty("incident_id").GetGuid()));
            cursor = page.RootElement.TryGetProperty("next_cursor", out JsonElement next) ? next.GetString() : null;
            pages++;
        } while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        visited.ShouldBe(
            [.. seeded
                .OrderByDescending(static incident => incident.DetectedAt)
                .ThenByDescending(static incident => incident.Id.Value.ToString("D"), StringComparer.Ordinal)
                .Select(static incident => incident.Id.Value)]);
    }

    [Fact]
    public async Task FiltersNarrowTheListAndTheLatestReportOutcomeIsShown()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Incident critical = NewIncident(_tenant, "critical", Base, IncidentSeverity.Critical);
        Incident low = NewIncident(_tenant, "low", Base.AddMinutes(1), IncidentSeverity.Low, service: "billing");
        Incident resolved = NewIncident(_tenant, "resolved", Base.AddMinutes(2), IncidentSeverity.High);
        resolved.StartInvestigation(ConfigurationSnapshotId.New(), Base.AddMinutes(3));
        resolved.MarkResolved(Base.AddMinutes(4));
        await SeedAsync([critical, low, resolved], ct);

        await using (SreDbContext db = postgres.CreateContext())
        {
            db.RootCauseReports.AddRange(
                Report(resolved.Id, 1, InvestigationOutcome.Partial),
                Report(resolved.Id, 2, InvestigationOutcome.BudgetExceeded));
            await db.SaveChangesAsync(ct);
        }

        (await IdsAsync("/v1/incidents?severity=critical&severity=low", ct)).ShouldBe([low.Id.Value, critical.Id.Value]);
        (await IdsAsync("/v1/incidents?service=billing", ct)).ShouldBe([low.Id.Value]);
        (await IdsAsync("/v1/incidents?status=resolved", ct)).ShouldBe([resolved.Id.Value]);
        (await IdsAsync(
            "/v1/incidents?detected_from=" + Uri.EscapeDataString(Base.AddMinutes(1).ToString("O"))
            + "&detected_to=" + Uri.EscapeDataString(Base.AddMinutes(2).ToString("O")), ct)).ShouldBe([low.Id.Value]);

        using JsonDocument all = await GetAsync("/v1/incidents", ct);
        JsonElement first = all.RootElement.GetProperty("items")[0];
        first.GetProperty("incident_id").GetGuid().ShouldBe(resolved.Id.Value);
        first.GetProperty("outcome").GetString().ShouldBe("BudgetExceeded");
        // Null members are omitted on the wire; an incident without a report has none.
        all.RootElement.GetProperty("items")[1].TryGetProperty("outcome", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("status=closed")]
    [InlineData("status=3")]
    [InlineData("severity=urgent")]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("detected_from=yesterday")]
    public async Task ABadFilterIsRefusedRatherThanIgnored(string query)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/incidents?" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(_tenant, SreRoles.Viewer));

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        body.RootElement.GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
    }

    private async Task<List<Guid>> IdsAsync(string path, CancellationToken ct)
    {
        using JsonDocument page = await GetAsync(path, ct);
        return [.. page.RootElement.GetProperty("items").EnumerateArray()
            .Select(static item => item.GetProperty("incident_id").GetGuid())];
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.For(_tenant, SreRoles.Viewer));

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private async Task SeedAsync(IEnumerable<Incident> incidents, CancellationToken ct)
    {
        await using SreDbContext db = postgres.CreateContext();
        db.Incidents.AddRange(incidents);
        await db.SaveChangesAsync(ct);
    }

    private static Incident NewIncident(
        string tenant,
        string fingerprint,
        DateTimeOffset detectedAt,
        IncidentSeverity severity,
        string service = "checkout")
        => new(
            IncidentId.New(),
            new NewIncident(
                tenant, "alertmanager", fingerprint, fingerprint,
                "Checkout latency above threshold", null, service, "production", severity, detectedAt,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            detectedAt);

    private static RootCauseReport Report(IncidentId incidentId, int version, InvestigationOutcome outcome)
        => RootCauseReport.Create(new NewRootCauseReport
        {
            IncidentId = incidentId,
            Version = version,
            ConfigurationSnapshotId = ConfigurationSnapshotId.New(),
            Outcome = outcome,
            Summary = "Connection pool exhausted.",
            Confidence = Confidence.From(0.5m),
            CreatedAt = Base.AddMinutes(5 + version),
        });
}
