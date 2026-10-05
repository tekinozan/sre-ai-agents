using NSubstitute;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.UseCases;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Tests.UseCases;

/// <summary>
/// Covers the audit trail of retrying an investigation (ARCHITECTURE.md 5.2).
/// </summary>
public sealed class RetryInvestigationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReopeningAResolvedIncidentRecordsWhoAskedRatherThanAFixedName()
    {
        // Reopening is the audited way back from Resolved. Recording every
        // reopen as "operator" left the audit trail unable to say who did it.
        Incident incident = new(
            IncidentId.New(),
            new NewIncident(
                "acme", "alertmanager", "alert-1", "fingerprint-1",
                "Checkout latency above threshold", null, "checkout", "production", IncidentSeverity.High, Now),
            Now);
        incident.StartInvestigation(ConfigurationSnapshotId.New(), Now);
        incident.MarkResolved(Now);
        incident.DequeueEvents();

        IIncidentRepository incidents = Substitute.For<IIncidentRepository>();
        incidents.GetAsync(incident.Id, Arg.Any<CancellationToken>()).Returns(incident);
        IInvestigationWorkflow workflow = Substitute.For<IInvestigationWorkflow>();
        workflow.IsRunningAsync(incident.Id, Arg.Any<CancellationToken>()).Returns(false);

        RetryInvestigationHandler handler = new(
            incidents,
            Substitute.For<IConfigurationSnapshotStore>(),
            Substitute.For<IConfigurationPinner>(),
            Substitute.For<IEventPublisher>(),
            Substitute.For<IUnitOfWork>(),
            workflow,
            new FixedClock(Now));

        Result result = await handler.HandleAsync(
            "acme", incident.Id, "alice@example.com", "The fix is deployed.", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        incident.Status.ShouldBe(IncidentStatus.Investigating);
        incidents.Received(1).AddTimelineEntry(Arg.Is<IncidentTimelineEntry>(entry =>
            entry.EventType == "sre.incident.updated"
            && entry.Actor == "alice@example.com"
            && entry.Summary == "Reopened by alice@example.com."));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
