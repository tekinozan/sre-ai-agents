using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Application.Workflows;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Tests.Workflows;

/// <summary>
/// Covers the event that tells the rest of the platform an investigation ended.
/// </summary>
/// <remarks>
/// <c>sre.incident.completed.v1</c> was declared, created and documented, and
/// nothing ever wrote to it: the aggregate's events were raised and dropped.
/// The notifier was the first consumer to notice.
/// </remarks>
public sealed class InvestigationCompletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly IIncidentRepository _incidents = Substitute.For<IIncidentRepository>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly InvestigationActivities _activities;
    private readonly Incident _incident;

    public InvestigationCompletionTests()
    {
        _incident = new Incident(
            IncidentId.New(),
            new NewIncident(
                "acme", "alertmanager", "alert-1", "fingerprint-1",
                "Checkout latency above threshold", null, "checkout", "production", IncidentSeverity.High, Now),
            Now);
        _incident.StartInvestigation(ConfigurationSnapshotId.New(), Now);
        _incident.DequeueEvents();
        _incidents.GetAsync(_incident.Id, Arg.Any<CancellationToken>()).Returns(_incident);

        _activities = new InvestigationActivities(
            _incidents,
            Substitute.For<IInvestigationStore>(),
            Substitute.For<IConfigurationSnapshotStore>(),
            _unitOfWork,
            _publisher,
            Substitute.For<IIncidentRouter>(),
            Substitute.For<IAgentExecutor>(),
            Substitute.For<IAgentRegistry>(),
            Substitute.For<IPromptRegistry>(),
            Substitute.For<IReadOnlyToolRegistry>(),
            Substitute.For<ILlmGateway>(),
            Substitute.For<IPromptRenderer>(),
            new SreMetrics(new TestMeterFactory()),
            TimeProvider.System,
            NullLogger<InvestigationActivities>.Instance);
    }

    [Fact]
    public async Task ACompletedInvestigationAnnouncesItsOutcomeAndReport()
    {
        (await _activities.CompleteInvestigationAsync(
            new CompleteInvestigationInput(_incident.Id, InvestigationOutcome.Partial, 3, "corr"),
            TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        _publisher.Received(1).Enqueue(
            KafkaTopics.IncidentCompleted,
            "sre.incident.completed",
            Arg.Is<IncidentCompletedPayload>(payload =>
                payload.IncidentId == _incident.Id
                && payload.Status == IncidentStatus.Resolved
                && payload.Outcome == InvestigationOutcome.Partial
                && payload.ReportVersion == 3
                && payload.ErrorCode == null),
            _incident.Id.ToString(),
            Arg.Any<Guid?>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedInvestigationAnnouncesWhyItFailed()
    {
        (await _activities.FailInvestigationAsync(
            new FailInvestigationInput(_incident.Id, ApplicationErrorCodes.BudgetExceeded, "Budget spent.", "corr"),
            TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        _publisher.Received(1).Enqueue(
            KafkaTopics.IncidentCompleted,
            "sre.incident.completed",
            Arg.Is<IncidentCompletedPayload>(payload =>
                payload.Status == IncidentStatus.Failed
                && payload.Outcome == InvestigationOutcome.BudgetExceeded
                && payload.ErrorCode == ApplicationErrorCodes.BudgetExceeded),
            _incident.Id.ToString(),
            Arg.Any<Guid?>());
    }
}
