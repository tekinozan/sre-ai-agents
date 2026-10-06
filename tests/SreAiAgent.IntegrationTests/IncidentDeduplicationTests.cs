using Microsoft.EntityFrameworkCore;
using Npgsql;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Messaging;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Covers the identity rules of ARCHITECTURE.md 9.3 against a real database.
/// </summary>
/// <remarks>
/// Deduplication is decided by indexes rather than by code, which is the point:
/// two deliveries of one alert can both read "no active incident" a microsecond
/// apart, and only a constraint can break the tie. None of that is observable
/// without a database that actually enforces one.
/// </remarks>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IncidentDeduplicationTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static Incident NewIncidentFor(string fingerprint, DateTimeOffset now, string tenant = "acme")
        => new(
            IncidentId.New(),
            new NewIncident(
                tenant,
                "alertmanager",
                "alert-1",
                fingerprint,
                "Checkout latency above threshold",
                null,
                "checkout",
                "production",
                IncidentSeverity.High,
                now,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["component"] = "database" }),
            now);

    private async Task<Incident> SaveAsync(Incident incident)
    {
        await using SreDbContext context = postgres.CreateContext();
        context.Incidents.Add(incident);
        incident.DequeueEvents();
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return incident;
    }

    private IncidentRepository Repository(SreDbContext context)
        => new(context, PostgresFixture.DatabaseOptions(postgres.ConnectionString));

    [Fact]
    public async Task AnActiveIncidentIsFoundByItsFingerprint()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident saved = await SaveAsync(NewIncidentFor(fingerprint, Now));

        await using SreDbContext context = postgres.CreateContext();

        Incident? found = await Repository(context).FindActiveByFingerprintAsync(
            "acme", "alertmanager", fingerprint, Now.AddMinutes(-60), TestContext.Current.CancellationToken);

        found.ShouldNotBeNull().Id.ShouldBe(saved.Id);
    }

    [Fact]
    public async Task AnIncidentBelongingToAnotherTenantIsNotFound()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        await SaveAsync(NewIncidentFor(fingerprint, Now, tenant: "acme"));

        await using SreDbContext context = postgres.CreateContext();

        // Identical alerts from two customers are two incidents. Folding them
        // together would put one tenant's evidence in the other's report.
        Incident? found = await Repository(context).FindActiveByFingerprintAsync(
            "globex", "alertmanager", fingerprint, Now.AddMinutes(-60), TestContext.Current.CancellationToken);

        found.ShouldBeNull();
    }

    [Fact]
    public async Task AResolvedIncidentInsideItsCooldownAbsorbsALaterAlert()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident incident = NewIncidentFor(fingerprint, Now.AddMinutes(-10));
        incident.StartInvestigation(ConfigurationSnapshotId.New(), Now.AddMinutes(-10));
        incident.MarkResolved(Now.AddMinutes(-5));

        await SaveAsync(incident);

        await using SreDbContext context = postgres.CreateContext();

        // D43: "resolved" means the investigation concluded, not that the
        // outage ended. An alert that keeps firing inside the cooldown is the
        // outage the report already explains, not a new incident.
        Incident? found = await Repository(context).FindActiveByFingerprintAsync(
            "acme", "alertmanager", fingerprint, Now.AddMinutes(-60), TestContext.Current.CancellationToken);

        found.ShouldNotBeNull().Id.ShouldBe(incident.Id);
    }

    [Fact]
    public async Task AResolvedIncidentOutsideItsCooldownDoesNotAbsorbALaterAlert()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident incident = NewIncidentFor(fingerprint, Now.AddHours(-3));
        incident.StartInvestigation(ConfigurationSnapshotId.New(), Now.AddHours(-3));
        incident.MarkResolved(Now.AddHours(-3));

        await SaveAsync(incident);

        await using SreDbContext context = postgres.CreateContext();

        // A recurrence after the alert has been quiet for the whole cooldown is
        // a new incident, not an addendum to a closed one.
        Incident? found = await Repository(context).FindActiveByFingerprintAsync(
            "acme", "alertmanager", fingerprint, Now.AddMinutes(-60), TestContext.Current.CancellationToken);

        found.ShouldBeNull();
    }

    [Fact]
    public async Task AnActiveIncidentIsFoundHoweverLongItsAlertWasQuiet()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident saved = await SaveAsync(NewIncidentFor(fingerprint, Now.AddHours(-3)));

        await using SreDbContext context = postgres.CreateContext();

        // The unique index allows one active incident per identity, so a second
        // could not be opened beside this one; the alert has to fold into it.
        Incident? found = await Repository(context).FindActiveByFingerprintAsync(
            "acme", "alertmanager", fingerprint, Now.AddMinutes(-60), TestContext.Current.CancellationToken);

        found.ShouldNotBeNull().Id.ShouldBe(saved.Id);
    }

    [Fact]
    public async Task TheActiveIncidentIsPreferredOverAResolvedOneInsideItsCooldown()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident resolved = NewIncidentFor(fingerprint, Now.AddMinutes(-2));
        resolved.StartInvestigation(ConfigurationSnapshotId.New(), Now.AddMinutes(-2));
        resolved.MarkResolved(Now.AddMinutes(-1));
        await SaveAsync(resolved);
        Incident active = await SaveAsync(NewIncidentFor(fingerprint, Now.AddMinutes(-30)));

        await using SreDbContext context = postgres.CreateContext();

        Incident? found = await Repository(context).FindActiveByFingerprintAsync(
            "acme", "alertmanager", fingerprint, Now.AddMinutes(-60), TestContext.Current.CancellationToken);

        found.ShouldNotBeNull().Id.ShouldBe(active.Id);
    }

    [Fact]
    public async Task TheDatabaseRefusesASecondActiveIncidentForOneAlert()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        await SaveAsync(NewIncidentFor(fingerprint, Now));

        // BUG-001: the index used to be an ordinary one, so nothing below the
        // application stopped two open incidents for the same alert.
        DbUpdateException refused = await Should.ThrowAsync<DbUpdateException>(
            async () => await SaveAsync(NewIncidentFor(fingerprint, Now)));

        PostgresException violation = refused.InnerException.ShouldBeOfType<PostgresException>();
        violation.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        violation.ConstraintName.ShouldBe("ix_incidents_active_fingerprint");
    }

    [Fact]
    public async Task ASecondActiveIncidentIsAllowedOnceTheFirstIsResolved()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident first = NewIncidentFor(fingerprint, Now.AddHours(-3));
        first.StartInvestigation(ConfigurationSnapshotId.New(), Now.AddHours(-3));
        first.MarkResolved(Now.AddHours(-3));
        await SaveAsync(first);

        await SaveAsync(NewIncidentFor(fingerprint, Now));
    }

    [Fact]
    public async Task OnlyOneOfTwoRacingClaimsSucceeds()
    {
        string key = $"incident:acme:alertmanager:{Guid.CreateVersion7():N}";

        await using SreDbContext first = postgres.CreateContext();
        await using SreDbContext second = postgres.CreateContext();

        IdempotencyStore firstStore = new(first, TimeProvider.System);
        IdempotencyStore secondStore = new(second, TimeProvider.System);

        // Both read before either writes, which is exactly the interleaving the
        // claim exists to survive. The read is a fast path; the primary key is
        // what actually decides.
        bool firstClaimed = await firstStore.TryClaimAsync(
            key, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        bool secondClaimed = await secondStore.TryClaimAsync(
            key, Now.AddMinutes(1), TestContext.Current.CancellationToken);

        firstClaimed.ShouldBeTrue();
        secondClaimed.ShouldBeTrue();

        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The loser fails at commit, not at the read, and its whole transaction
        // rolls back with it. That is what stops a second incident being opened.
        DbUpdateException lost = await Should.ThrowAsync<DbUpdateException>(
            async () => await second.SaveChangesAsync(TestContext.Current.CancellationToken));

        // And the consumer must not mistake the loser for a redelivery of its
        // own message: it is a different message for the same alert, which has
        // to be retried and folded into the winner, not acknowledged (D42).
        KafkaConsumerService<CanonicalAlert>.IsDuplicateDelivery(lost).ShouldBeFalse();
    }

    [Fact]
    public async Task ASecondInboxRowForOneMessageIsRecognisedAsADuplicateDelivery()
    {
        // The distinction above rests on PostgreSQL reporting which table a
        // unique violation came from. A substitute in a unit test can claim
        // anything; this proves the real server does.
        Guid eventId = Guid.CreateVersion7();

        await using SreDbContext first = postgres.CreateContext();
        await using SreDbContext second = postgres.CreateContext();

        InboxStore firstInbox = new(first, TimeProvider.System);
        InboxStore secondInbox = new(second, TimeProvider.System);

        (await firstInbox.TryBeginAsync(eventId, "sre.alert.received", "alert-worker", TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await secondInbox.TryBeginAsync(eventId, "sre.alert.received", "alert-worker", TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        DbUpdateException duplicate = await Should.ThrowAsync<DbUpdateException>(
            async () => await second.SaveChangesAsync(TestContext.Current.CancellationToken));

        KafkaConsumerService<CanonicalAlert>.IsDuplicateDelivery(duplicate).ShouldBeTrue();
    }

    [Fact]
    public async Task AFindingSurvivesTheRoundTripThroughJsonb()
    {
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident incident = await SaveAsync(NewIncidentFor(fingerprint, Now));

        EvidenceId evidence = EvidenceId.New();
        AgentRun run = AgentRun.Start(
            incident.Id,
            new AgentExecutionIdentity(
                "database-expert", "1.0.0", "investigate", "1.0.0", "hash",
                "scripted", "scripted-analytical", "1.0.0"),
            isShadow: false,
            Now);

        Finding finding = Finding.Create(
            incident.Id,
            run.Id,
            "database-expert",
            "1.0.0",
            new AgentFinding(
                "Connection pool exhaustion.",
                [Observation.Verified("Active connections hit the maximum.", [evidence])],
                [new Hypothesis("A slow query held connections open.", [evidence], [], Confidence.From(0.6m))],
                ["Query logs were not retained."],
                ["Check pg_stat_activity."],
                Confidence.From(0.75m)),
            isShadow: false,
            Now);

        await using (SreDbContext writer = postgres.CreateContext())
        {
            writer.AgentRuns.Add(run);
            writer.Findings.Add(finding);
            await writer.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using SreDbContext reader = postgres.CreateContext();
        Finding restored = await reader.Findings
            .SingleAsync(f => f.Id == finding.Id, TestContext.Current.CancellationToken);

        // Correlation reads every prior finding back through this column, so
        // anything lost here is lost from the report an operator reads.
        restored.Content.Confidence.Value.ShouldBe(0.75m);
        restored.Content.Observations[0].IsVerified.ShouldBeTrue();
        restored.Content.Observations[0].EvidenceIds.ShouldBe([evidence]);
        restored.Content.Hypotheses[0].Confidence.Value.ShouldBe(0.6m);
    }
}
