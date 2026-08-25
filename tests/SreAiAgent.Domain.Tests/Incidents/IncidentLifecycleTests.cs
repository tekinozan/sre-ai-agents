using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Tests.Incidents;

/// <summary>
/// Covers the status transition rules of ARCHITECTURE.md 5.2 and 18 ("Unit,
/// Domain: status transitions").
/// </summary>
public sealed class IncidentLifecycleTests
{
    private static readonly DateTimeOffset Detected = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewIncidentIsOpenAndAnnouncesItself()
    {
        Incident incident = TestIncident.Open();

        incident.Status.ShouldBe(IncidentStatus.Open);
        incident.AlertCount.ShouldBe(1);
        incident.ResolvedAt.ShouldBeNull();

        IReadOnlyList<DomainEvent> events = incident.DequeueEvents();
        events.ShouldHaveSingleItem().ShouldBeOfType<IncidentOpened>();
    }

    [Fact]
    public void DequeuedEventsAreNotReplayedOnTheNextDrain()
    {
        Incident incident = TestIncident.Open();

        incident.DequeueEvents().Count.ShouldBe(1);
        incident.DequeueEvents().ShouldBeEmpty();
    }

    [Fact]
    public void TimestampsAreNormalizedToUtc()
    {
        Incident incident = TestIncident.Open(detectedAt: new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.FromHours(3)));

        incident.DetectedAt.Offset.ShouldBe(TimeSpan.Zero);
        incident.DetectedAt.Hour.ShouldBe(9);
    }

    [Fact]
    public void StartInvestigationPinsTheConfigurationSnapshot()
    {
        Incident incident = TestIncident.Open();
        ConfigurationSnapshotId snapshot = ConfigurationSnapshotId.New();

        incident.StartInvestigation(snapshot, Detected.AddSeconds(5));

        incident.Status.ShouldBe(IncidentStatus.Investigating);
        incident.ConfigurationSnapshotId.ShouldBe(snapshot);
        incident.DequeueEvents().OfType<InvestigationStarted>().ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(IncidentStatus.Resolved)]
    [InlineData(IncidentStatus.Cancelled)]
    public void StartInvestigationIsRejectedFromClosedStates(IncidentStatus closedStatus)
    {
        Incident incident = TestIncident.InStatus(closedStatus);

        InvalidIncidentStateException exception = Should.Throw<InvalidIncidentStateException>(
            () => incident.StartInvestigation(ConfigurationSnapshotId.New(), Detected.AddMinutes(1)));

        exception.Code.ShouldBe(DomainErrorCodes.IncidentInvalidState);
        exception.CurrentStatus.ShouldBe(closedStatus);
    }

    [Fact]
    public void ResolveIsRejectedAfterCancellation()
    {
        Incident incident = TestIncident.InStatus(IncidentStatus.Cancelled);

        Should.Throw<InvalidIncidentStateException>(() => incident.MarkResolved(Detected.AddMinutes(1)));
    }

    [Fact]
    public void ResolvedIncidentIsNotReturnedToOpenImplicitly()
    {
        Incident incident = TestIncident.InStatus(IncidentStatus.Resolved);

        IncidentStatePolicy.CanTransition(IncidentStatus.Resolved, IncidentStatus.Open).ShouldBeFalse();
        incident.Status.ShouldBe(IncidentStatus.Resolved);
    }

    [Fact]
    public void ReopenIsExplicitAuditedAndClearsResolution()
    {
        Incident incident = TestIncident.InStatus(IncidentStatus.Resolved);

        incident.Reopen("alice@example.com", "alert fired again", Detected.AddHours(1));

        incident.Status.ShouldBe(IncidentStatus.Investigating);
        incident.ResolvedAt.ShouldBeNull();

        IncidentReopened reopened = incident.DequeueEvents().OfType<IncidentReopened>().ShouldHaveSingleItem();
        reopened.Actor.ShouldBe("alice@example.com");
        reopened.Reason.ShouldBe("alert fired again");
    }

    [Fact]
    public void ReopenOnlyAppliesToResolvedIncidents()
    {
        Incident incident = TestIncident.InStatus(IncidentStatus.Investigating);

        Should.Throw<InvalidIncidentStateException>(
            () => incident.Reopen("alice@example.com", "because", Detected.AddHours(1)));
    }

    [Fact]
    public void DuplicateAlertExtendsTheTimelineWithoutChangingStatus()
    {
        Incident incident = TestIncident.InStatus(IncidentStatus.Investigating);
        incident.DequeueEvents();

        incident.RecordAlertRecurrence(Detected.AddMinutes(2));

        incident.Status.ShouldBe(IncidentStatus.Investigating);
        incident.AlertCount.ShouldBe(2);
        incident.LastAlertAt.ShouldBe(Detected.AddMinutes(2));
        incident.DequeueEvents().OfType<IncidentAlertRecurred>().ShouldHaveSingleItem();
    }

    [Fact]
    public void SeverityEscalatesButNeverDeescalatesAutomatically()
    {
        Incident incident = TestIncident.Open(severity: IncidentSeverity.Medium);

        incident.EscalateSeverity(IncidentSeverity.Critical, Detected.AddMinutes(1));
        incident.Severity.ShouldBe(IncidentSeverity.Critical);

        incident.EscalateSeverity(IncidentSeverity.Low, Detected.AddMinutes(2));
        incident.Severity.ShouldBe(IncidentSeverity.Critical);
    }

    [Fact]
    public void FailedInvestigationCanBeRetried()
    {
        Incident incident = TestIncident.InStatus(IncidentStatus.Investigating);
        incident.MarkFailed(new ErrorCode("LLM_TIMEOUT"), "model timed out", Detected.AddMinutes(3));
        incident.Status.ShouldBe(IncidentStatus.Failed);

        incident.StartInvestigation(ConfigurationSnapshotId.New(), Detected.AddMinutes(4));

        incident.Status.ShouldBe(IncidentStatus.Investigating);
        incident.FailureCode.ShouldBeNull();
        incident.FailureReason.ShouldBeNull();
    }

    [Fact]
    public void CancellationIsTerminal()
    {
        Incident incident = TestIncident.InStatus(IncidentStatus.Investigating);
        incident.Cancel("bob@example.com", "duplicate of INC-1", Detected.AddMinutes(5));

        IncidentStatePolicy.IsTerminal(incident.Status).ShouldBeTrue();
        Should.Throw<InvalidIncidentStateException>(
            () => incident.StartInvestigation(ConfigurationSnapshotId.New(), Detected.AddMinutes(6)));
    }

    [Fact]
    public void MissingFingerprintIsRejected()
    {
        DomainException exception = Should.Throw<DomainException>(
            () => TestIncident.Open(fingerprint: "   "));

        exception.Code.ShouldBe(DomainErrorCodes.IncidentFingerprintRequired);
    }
}
