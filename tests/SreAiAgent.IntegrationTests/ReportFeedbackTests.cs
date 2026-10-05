using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SreAiAgent.Api.Security;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Covers verdicts on root cause reports over HTTP and PostgreSQL
/// (incidents-feedback-evals.md F).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReportFeedbackTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly SreApiPostgresFactory _factory = new(postgres);
    private readonly string _tenant = "fb-a-" + Guid.CreateVersion7().ToString("N")[^12..];
    private readonly string _other = "fb-b-" + Guid.CreateVersion7().ToString("N")[^12..];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await using SreDbContext db = postgres.CreateContext();
        List<IncidentId> ids = await db.Incidents
            .Where(incident => incident.Tenant == _tenant || incident.Tenant == _other)
            .Select(incident => incident.Id)
            .ToListAsync(CancellationToken.None);
        await db.ReportFeedback.Where(feedback => ids.Contains(feedback.IncidentId)).ExecuteDeleteAsync(CancellationToken.None);
        await db.RootCauseReports.Where(report => ids.Contains(report.IncidentId)).ExecuteDeleteAsync(CancellationToken.None);
        await db.IncidentEvents.Where(entry => ids.Contains(entry.IncidentId)).ExecuteDeleteAsync(CancellationToken.None);
        await db.Incidents.Where(incident => ids.Contains(incident.Id)).ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APersonsSecondVerdictRevisesTheirFirstAndOthersStandBesideIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IncidentId incident = await SeedAsync(_tenant, InvestigationOutcome.Partial, ct);

        string alice = TestTokens.ForActor("alice", _tenant, SreRoles.Investigator);
        string bob = TestTokens.ForActor("bob", _tenant, SreRoles.Operator);

        (HttpStatusCode first, JsonDocument firstBody) = await SubmitAsync(alice, incident, """
            {"report_version": 1, "verdict": "Confirmed"}
            """, ct);
        first.ShouldBe(HttpStatusCode.OK);
        Guid feedbackId = firstBody.RootElement.GetProperty("feedback_id").GetGuid();
        firstBody.RootElement.GetProperty("outcome").GetString().ShouldBe("Partial");

        (HttpStatusCode revised, JsonDocument revisedBody) = await SubmitAsync(alice, incident, """
            {"report_version": 1, "verdict": "Incorrect", "actual_root_cause": "DNS resolver timeouts", "comment": "pool was a symptom"}
            """, ct);
        revised.ShouldBe(HttpStatusCode.OK);
        revisedBody.RootElement.GetProperty("feedback_id").GetGuid().ShouldBe(feedbackId);
        revisedBody.RootElement.GetProperty("verdict").GetString().ShouldBe("Incorrect");

        (await SubmitAsync(bob, incident, """{"report_version": 1, "verdict": "PartiallyCorrect"}""", ct)).Status
            .ShouldBe(HttpStatusCode.OK);

        using JsonDocument list = await GetAsync(alice, $"/v1/incidents/{incident}/report/feedback", ct);
        list.RootElement.GetArrayLength().ShouldBe(2);
        list.RootElement.EnumerateArray().Select(static item => item.GetProperty("submitted_by").GetString())
            .ShouldBe(["alice", "bob"], ignoreOrder: true);

        // The timeline records that a verdict was given, never what was written.
        await using SreDbContext db = postgres.CreateContext();
        List<string> summaries = await db.IncidentEvents
            .Where(entry => entry.IncidentId == incident && entry.EventType == "report.feedback")
            .Select(entry => entry.Summary)
            .ToListAsync(ct);
        summaries.Count.ShouldBe(3);
        summaries.ShouldAllBe(summary => !summary.Contains("DNS", StringComparison.Ordinal) && !summary.Contains("symptom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVerdictNeedsAnExistingReportOfTheCallersOwnTenantAndTheRightRole()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IncidentId mine = await SeedAsync(_tenant, InvestigationOutcome.Complete, ct);
        IncidentId theirs = await SeedAsync(_other, InvestigationOutcome.Complete, ct);
        string investigator = TestTokens.ForActor("alice", _tenant, SreRoles.Investigator);

        (await SubmitAsync(investigator, mine, """{"report_version": 2, "verdict": "Confirmed"}""", ct)).Body
            .RootElement.GetProperty("code").GetString().ShouldBe("REPORT_NOT_FOUND");
        (await SubmitAsync(investigator, theirs, """{"report_version": 1, "verdict": "Confirmed"}""", ct)).Body
            .RootElement.GetProperty("code").GetString().ShouldBe("INCIDENT_NOT_FOUND");

        (await SubmitAsync(TestTokens.ForActor("vic", _tenant, SreRoles.Viewer), mine, """{"report_version": 1, "verdict": "Confirmed"}""", ct)).Status
            .ShouldBe(HttpStatusCode.Forbidden);

        // An unknown verdict is the caller's mistake in every environment, not a 500.
        (HttpStatusCode status, JsonDocument body) = await SubmitAsync(investigator, mine, """{"report_version": 1, "verdict": "Maybe"}""", ct);
        status.ShouldBe(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");

        (HttpStatusCode tooLong, JsonDocument tooLongBody) = await SubmitAsync(
            investigator, mine, $$"""{"report_version": 1, "verdict": "Incorrect", "comment": "{{new string('x', 2001)}}"}""", ct);
        tooLong.ShouldBe(HttpStatusCode.BadRequest);
        tooLongBody.RootElement.GetProperty("code").GetString().ShouldBe("REPORT_FEEDBACK_INVALID");
    }

    [Fact]
    public async Task TheTenantListFiltersByVerdictAndPagesWithoutRepeats()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        for (int index = 0; index < 5; index++)
        {
            IncidentId incident = await SeedAsync(_tenant, InvestigationOutcome.Complete, ct);
            string verdict = index % 2 == 0 ? "Incorrect" : "Confirmed";
            (await SubmitAsync(
                TestTokens.ForActor("alice", _tenant, SreRoles.Investigator),
                incident,
                $$"""{"report_version": 1, "verdict": "{{verdict}}"}""",
                ct)).Status.ShouldBe(HttpStatusCode.OK);
        }

        IncidentId foreign = await SeedAsync(_other, InvestigationOutcome.Complete, ct);
        (await SubmitAsync(TestTokens.ForActor("eve", _other, SreRoles.Investigator), foreign, """{"report_version": 1, "verdict": "Incorrect"}""", ct)).Status
            .ShouldBe(HttpStatusCode.OK);

        string reader = TestTokens.ForActor("vic", _tenant, SreRoles.Viewer);
        List<Guid> seen = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            using JsonDocument page = await GetAsync(
                reader,
                "/v1/report-feedback?verdict=incorrect&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)),
                ct);
            foreach (JsonElement item in page.RootElement.GetProperty("items").EnumerateArray())
            {
                item.GetProperty("verdict").GetString().ShouldBe("Incorrect");
                item.GetProperty("submitted_by").GetString().ShouldBe("alice");
                seen.Add(item.GetProperty("feedback_id").GetGuid());
            }

            cursor = page.RootElement.TryGetProperty("next_cursor", out JsonElement next) ? next.GetString() : null;
            pages++;
        } while (cursor is not null && pages < 10);

        seen.Count.ShouldBe(3);
        seen.Distinct().Count().ShouldBe(3);
        pages.ShouldBe(2);
    }

    private async Task<IncidentId> SeedAsync(string tenant, InvestigationOutcome outcome, CancellationToken ct)
    {
        Incident incident = new(
            IncidentId.New(),
            new NewIncident(
                tenant, "alertmanager", "alert", "fp-" + Guid.CreateVersion7().ToString("N"),
                "Checkout latency above threshold", null, "checkout", "production", IncidentSeverity.High, Base,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            Base);

        await using SreDbContext db = postgres.CreateContext();
        db.Incidents.Add(incident);
        db.RootCauseReports.Add(RootCauseReport.Create(new NewRootCauseReport
        {
            IncidentId = incident.Id,
            Version = 1,
            ConfigurationSnapshotId = ConfigurationSnapshotId.New(),
            Outcome = outcome,
            Summary = "Connection pool exhausted.",
            Confidence = Confidence.From(0.6m),
            CreatedAt = Base.AddMinutes(5),
        }));
        await db.SaveChangesAsync(ct);
        return incident.Id;
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> SubmitAsync(
        string token,
        IncidentId incident,
        string json,
        CancellationToken ct)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, $"/v1/incidents/{incident}/report/feedback")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, JsonDocument.Parse(body.Length == 0 ? "{}" : body));
    }

    private async Task<JsonDocument> GetAsync(string token, string path, CancellationToken ct)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }
}
