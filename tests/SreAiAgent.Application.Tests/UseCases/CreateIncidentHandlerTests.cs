using NSubstitute;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.UseCases;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Tests.UseCases;

/// <summary>
/// Covers alert ingestion and deduplication (ARCHITECTURE.md 9.3, 12.1).
/// </summary>
/// <remarks>
/// The property under test throughout is that repeating an alert is safe. Alert
/// sources retry, Kafka delivers at least once and operators press buttons
/// twice, so "the same alert arrived again" is the ordinary case rather than the
/// exception, and the platform has to answer it the same way every time.
/// </remarks>
public sealed class CreateIncidentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IIncidentRepository _incidents = Substitute.For<IIncidentRepository>();
    private readonly IConfigurationSnapshotStore _snapshots = Substitute.For<IConfigurationSnapshotStore>();
    private readonly IConfigurationPinner _pinner = Substitute.For<IConfigurationPinner>();
    private readonly IIdempotencyStore _idempotency = Substitute.For<IIdempotencyStore>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly CreateIncidentHandler _handler;

    public CreateIncidentHandlerTests()
    {
        _pinner.PinCurrentAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result<ConfigurationSnapshot>.Success(new ConfigurationSnapshot(
                ConfigurationSnapshotId.New(),
                "agents-1",
                "prompts-1",
                SreAiAgent.TestKit.PinnedModelFixtures.Scripted(),
                "tools-1",
                "policies-1",
                new Dictionary<string, string>(StringComparer.Ordinal),
                Now)));

        _idempotency.TryClaimAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _handler = new CreateIncidentHandler(
            _incidents,
            _snapshots,
            _pinner,
            _idempotency,
            _publisher,
            _unitOfWork,
            new SreMetrics(new TestMeterFactory()),
            new FixedClock(Now));
    }

    private static CreateIncidentRequest Request(IncidentSeverity severity = IncidentSeverity.High)
        => new()
        {
            Source = "alertmanager",
            ExternalId = "alert-1",
            Title = "Checkout latency above threshold",
            Service = "checkout",
            Environment = "production",
            Severity = severity,
        };

    [Fact]
    public async Task AFirstAlertOpensAnIncidentAndRequestsAnInvestigation()
    {
        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsDuplicate.ShouldBeFalse();
        result.Value.Status.ShouldBe(IncidentStatus.Investigating);

        _incidents.Received(1).Add(Arg.Any<Incident>());
        _snapshots.Received(1).Add(Arg.Any<ConfigurationSnapshot>());

        // The opening is announced once, by the aggregate's own event. Publishing
        // a hand-built IncidentCreated alongside it put two identical messages on
        // the topic for one incident.
        _publisher.Received(1).Enqueue(
            Arg.Is<DomainEvent>(raised => raised is IncidentOpened),
            Arg.Any<string>(),
            Arg.Any<Guid?>());
        _publisher.DidNotReceive().Enqueue(
            KafkaTopics.IncidentCreated, Arg.Any<string>(),
            Arg.Any<IncidentCreatedPayload>(), Arg.Any<string>(), Arg.Any<Guid?>());

        // The request to investigate is a separate message carrying the pinned
        // snapshot and the attempt, which the aggregate's event does not have. A
        // committed incident whose start message was never published is an
        // investigation that silently never happens.
        _publisher.Received(1).Enqueue(
            KafkaTopics.InvestigationRequested, "sre.incident.investigation.requested",
            Arg.Any<InvestigationRequestedPayload>(), Arg.Any<string>(), Arg.Any<Guid?>());

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARepeatAlertFoldsIntoTheOpenIncident()
    {
        Incident existing = Existing();
        _incidents.FindActiveByFingerprintAsync(
                "acme", "alertmanager", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        // A duplicate is a success, not a client error. Answering 409 would train
        // every integration to treat a healthy platform as broken.
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsDuplicate.ShouldBeTrue();
        result.Value.IncidentId.ShouldBe(existing.Id);

        existing.AlertCount.ShouldBe(2);
        _incidents.DidNotReceive().Add(Arg.Any<Incident>());
        _snapshots.DidNotReceive().Add(Arg.Any<ConfigurationSnapshot>());
    }

    [Fact]
    public async Task AWorseRepeatAlertRaisesTheSeverity()
    {
        Incident existing = Existing(IncidentSeverity.Low);
        _incidents.FindActiveByFingerprintAsync(
                "acme", "alertmanager", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        await _handler.HandleAsync(
            Request(IncidentSeverity.Critical), "acme", Window, TestContext.Current.CancellationToken);

        existing.Severity.ShouldBe(IncidentSeverity.Critical);
    }

    [Fact]
    public async Task LosingTheCreationRaceReturnsTheWinnerRatherThanASecondIncident()
    {
        // Two deliveries of one alert can both find no active incident a
        // microsecond apart. Only the claim breaks the tie, and the loser has to
        // answer with the incident the winner opened.
        _idempotency.TryClaimAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Incident winner = Existing();
        _incidents.FindActiveByFingerprintAsync(
                "acme", "alertmanager", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((Incident?)null, winner);

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IncidentId.ShouldBe(winner.Id);
        _incidents.DidNotReceive().Add(Arg.Any<Incident>());
    }

    [Fact]
    public async Task LosingTheRaceWithNoWinnerVisibleIsRetryableRatherThanASecondIncident()
    {
        _idempotency.TryClaimAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.IsRetryable.ShouldBeTrue();
        _incidents.DidNotReceive().Add(Arg.Any<Incident>());
    }

    [Fact]
    public async Task NotStartingAnInvestigationStagesNoInvestigationRequest()
    {
        CreateIncidentRequest request = Request() with { StartInvestigation = false };

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            request, "acme", Window, TestContext.Current.CancellationToken);

        result.Value.Status.ShouldBe(IncidentStatus.Open);
        _publisher.DidNotReceive().Enqueue(
            KafkaTopics.InvestigationRequested, Arg.Any<string>(),
            Arg.Any<InvestigationRequestedPayload>(), Arg.Any<string>(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task APinFailureRejectsTheRequestAndOpensNoIncident()
    {
        _pinner.PinCurrentAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result<ConfigurationSnapshot>.Failure(Error.Permanent(new ErrorCode("TEST"), "Fail")));

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe("TEST");
        _incidents.DidNotReceive().Add(Arg.Any<Incident>());
        _publisher.DidNotReceive().Enqueue(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<InvestigationRequestedPayload>(), Arg.Any<string>(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task TheTenantReachesThePinner()
    {
        CreateIncidentRequest request = Request();

        await _handler.HandleAsync(request, "acme", Window, TestContext.Current.CancellationToken);

        await _pinner.Received(1).PinCurrentAsync("acme", Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARepeatAlertForAnIncidentThatWasNeverInvestigatedStartsTheInvestigation()
    {
        // BUG-001: an incident opened without an investigation used to absorb
        // every later alert for its identity, so a real, ongoing outage was only
        // counted.
        Incident existing = Existing(status: IncidentStatus.Open);
        FindReturns(existing);

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.Value.IsDuplicate.ShouldBeTrue();
        result.Value.Status.ShouldBe(IncidentStatus.Investigating);
        existing.AlertCount.ShouldBe(2);
        existing.ConfigurationSnapshotId.ShouldNotBeNull();
        _snapshots.Received(1).Add(Arg.Any<ConfigurationSnapshot>());
        _publisher.Received(1).Enqueue(
            KafkaTopics.InvestigationRequested, "sre.incident.investigation.requested",
            Arg.Is<InvestigationRequestedPayload>(payload => payload.Attempt == 1), existing.Id.ToString(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task ARepeatAlertInsideTheCooldownOfAResolvedIncidentIsOnlyCounted()
    {
        // D43: the outage outlived its investigation. One report explains it;
        // a fresh investigation per alert was the TicketHub pilot's six
        // incidents for one SMTP outage.
        Incident existing = Existing(status: IncidentStatus.Resolved);
        FindReturns(existing);

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(IncidentSeverity.Critical), "acme", Window, TestContext.Current.CancellationToken);

        result.Value.IsDuplicate.ShouldBeTrue();
        result.Value.Status.ShouldBe(IncidentStatus.Resolved);
        existing.AlertCount.ShouldBe(2);
        existing.Severity.ShouldBe(IncidentSeverity.Critical);
        await _pinner.DidNotReceive().PinCurrentAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        _publisher.DidNotReceive().Enqueue(
            KafkaTopics.InvestigationRequested, Arg.Any<string>(),
            Arg.Any<InvestigationRequestedPayload>(), Arg.Any<string>(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task ARepeatAlertAfterAFailedIncidentWentQuietRestartsTheInvestigation()
    {
        Incident existing = Existing(status: IncidentStatus.Failed, openedAt: Now - Window - TimeSpan.FromMinutes(1));
        FindReturns(existing);

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.Value.Status.ShouldBe(IncidentStatus.Investigating);
        _publisher.Received(1).Enqueue(
            KafkaTopics.InvestigationRequested, "sre.incident.investigation.requested",
            Arg.Is<InvestigationRequestedPayload>(payload => payload.Attempt == 2), existing.Id.ToString(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task ARepeatAlertWhileAFailedIncidentIsStillAlertingIsOnlyCounted()
    {
        // A pipeline that fails on every attempt costs one investigation per
        // window, not one per alert.
        Incident existing = Existing(status: IncidentStatus.Failed, openedAt: Now - TimeSpan.FromMinutes(5));
        FindReturns(existing);

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.Value.Status.ShouldBe(IncidentStatus.Failed);
        existing.AlertCount.ShouldBe(2);
        await _pinner.DidNotReceive().PinCurrentAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APinFailureOnARecurrenceThatNeedsAnInvestigationChangesNothing()
    {
        // A transient pin failure is redelivered; counting the alert first would
        // count it again on the retry.
        Incident existing = Existing(status: IncidentStatus.Open);
        FindReturns(existing);
        _pinner.PinCurrentAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result<ConfigurationSnapshot>.Failure(Error.Transient(new ErrorCode("TEST"), "Fail")));

        Result<IncidentAcceptedResponse> result = await _handler.HandleAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        existing.AlertCount.ShouldBe(1);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AResolvedDeliveryWithNothingToFoldIntoOpensNoIncident()
    {
        // BUG-001's first mechanism: this used to open an incident that was
        // never investigated and then absorbed the real alerts that followed.
        Result result = await _handler.RecordResolutionAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _incidents.DidNotReceive().Add(Arg.Any<Incident>());
        _incidents.DidNotReceive().AddTimelineEntry(Arg.Any<IncidentTimelineEntry>());
        await _pinner.DidNotReceive().PinCurrentAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AResolvedDeliveryAnnotatesTheIncidentItBelongsToAndChangesNothingElse()
    {
        Incident existing = Existing(status: IncidentStatus.Investigating);
        FindReturns(existing);

        Result result = await _handler.RecordResolutionAsync(
            Request(), "acme", Window, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        existing.AlertCount.ShouldBe(1);
        existing.Status.ShouldBe(IncidentStatus.Investigating);
        _incidents.Received(1).AddTimelineEntry(Arg.Is<IncidentTimelineEntry>(
            entry => entry.IncidentId == existing.Id && entry.EventType == "alert.resolved"));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private void FindReturns(Incident incident)
        => _incidents.FindActiveByFingerprintAsync(
                "acme", "alertmanager", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(incident);

    private static Incident Existing(
        IncidentSeverity severity = IncidentSeverity.High,
        IncidentStatus status = IncidentStatus.Investigating,
        DateTimeOffset? openedAt = null)
    {
        DateTimeOffset at = openedAt ?? Now;
        Incident incident = new(
            IncidentId.New(),
            new NewIncident(
                "acme", "alertmanager", "alert-1", "fingerprint-1",
                "Checkout latency above threshold", null, "checkout", "production", severity, at),
            at);

        if (status is not IncidentStatus.Open)
        {
            incident.StartInvestigation(ConfigurationSnapshotId.New(), at);
        }

        if (status is IncidentStatus.Resolved)
        {
            incident.MarkResolved(at);
        }
        else if (status is IncidentStatus.Failed)
        {
            incident.MarkFailed(new ErrorCode("TEST"), "Failed for the test.", at);
        }

        incident.DequeueEvents();
        return incident;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
