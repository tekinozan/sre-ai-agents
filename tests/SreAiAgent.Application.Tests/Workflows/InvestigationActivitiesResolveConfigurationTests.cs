using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Application.Workflows;
using SreAiAgent.Domain.Shared;
using SreAiAgent.TestKit;

namespace SreAiAgent.Application.Tests.Workflows;

/// <summary>
/// Proves the other half of D41: a workflow resuming from an already-pinned
/// snapshot resolves exactly the assignment that snapshot pinned, not a live
/// re-resolution of whatever scope happens to be active now.
/// </summary>
/// <remarks>
/// <see cref="InvestigationActivities.ResolveConfigurationAsync"/> reads only
/// <see cref="IConfigurationSnapshotStore"/> — it never reaches
/// <c>ModelProviderRepository</c> or <c>GetActiveSnapshotAsync</c> at all, so
/// D41's no-fallback decision cannot affect a resumed workflow: there is
/// nothing here to fall back from or to.
/// </remarks>
public sealed class InvestigationActivitiesResolveConfigurationTests
{
    private readonly IConfigurationSnapshotStore _snapshots = Substitute.For<IConfigurationSnapshotStore>();
    private readonly InvestigationActivities _activities;

    public InvestigationActivitiesResolveConfigurationTests()
    {
        _activities = new InvestigationActivities(
            Substitute.For<IIncidentRepository>(),
            Substitute.For<IInvestigationStore>(),
            _snapshots,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IEventPublisher>(),
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
    public async Task AResumedWorkflowResolvesExactlyTheAssignmentItsOwnSnapshotPinned()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Stands in for "whatever was active when this incident's snapshot was
        // pinned" — a specific, already-frozen assignment for a tenant that
        // need not have anything active now, or ever again.
        ConfigurationSnapshot pinned = new(
            ConfigurationSnapshotId.New(),
            "agents-1",
            "prompts-1",
            PinnedModelFixtures.Scripted("tenant-resumed"),
            "tools-1",
            "policies-1",
            new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow.AddDays(-30));

        _snapshots.GetAsync(pinned.Id, Arg.Any<CancellationToken>()).Returns(pinned);

        Result<ResolvedConfiguration> resolved = await _activities.ResolveConfigurationAsync(pinned.Id, ct);

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ModelAssignment.ShouldBe(pinned.ModelAssignment);
        resolved.Value.ModelAssignment.ReleaseId.ShouldBe(pinned.ModelAssignment.ReleaseId);
        resolved.Value.ModelAssignment.Classifier.Tenant.ShouldBe("tenant-resumed");
        resolved.Value.ModelAssignment.Classifier.ProviderConfigurationHash
            .ShouldBe(pinned.ModelAssignment.Classifier.ProviderConfigurationHash);

        // The snapshot store is the only thing this activity ever asks. There
        // is no second call anywhere in it that could re-resolve a live
        // active assignment instead of the one already pinned.
        await _snapshots.Received(1).GetAsync(pinned.Id, Arg.Any<CancellationToken>());
    }
}
