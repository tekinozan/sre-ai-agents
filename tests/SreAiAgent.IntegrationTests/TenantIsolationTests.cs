using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.UseCases;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Proves tenant isolation on write paths against a real database (ARCHITECTURE.md 12.1).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TenantIsolationTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    // Deterministic clock: the approval fixture's one-hour validity window is
    // measured from Now, so a handler checking it against the real wall clock
    // (TimeProvider.System) fails once real time drifts past Now + 1h — which it
    // eventually always does. Every handler in this class uses the same fixed
    // instant Now was built from, so no assertion here depends on which day the
    // suite happens to run.
    private static readonly TimeProvider Clock = new TestTimeProvider(Now);

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

    private static ApprovalRequest NewApprovalFor(IncidentId incidentId, DateTimeOffset now)
        => ApprovalRequest.Create(
            incidentId,
            recommendationId: null,
            title: "Restart checkout service",
            justification: "Memory exhaustion observed",
            risk: RiskLevel.Low,
            proposedActionJson: "{\"action\":\"restart\"}",
            requestedAt: now,
            validFor: TimeSpan.FromHours(1));

    private static PinnedModelCandidate CreateCandidate(string tenant, string slot) => new()
    {
        Tenant = tenant,
        AssignmentReleaseId = new ModelAssignmentReleaseId(Guid.CreateVersion7()),
        AssignmentConfigurationHash = "assign-hash",
        Slot = slot,
        DeploymentVersionId = new ModelDeploymentVersionId(Guid.CreateVersion7()),
        DeploymentLogicalName = slot,
        DeploymentVersion = 1,
        DeploymentConfigurationHash = "deploy-hash",
        ProviderConnectionRevisionId = new ProviderConnectionRevisionId(Guid.CreateVersion7()),
        ProviderConnectionRevision = 1,
        ProviderConfigurationHash = "conn-hash",
        AdapterKind = ProviderAdapterKind.Scripted,
        ProviderModel = "scripted-model",
        Capabilities = ModelCapability.None,
        Temperature = 0.5f,
        MaxOutputTokens = 1000,
        Timeout = TimeSpan.FromSeconds(30),
        InputCostPerMillionTokens = 1m,
        OutputCostPerMillionTokens = 2m,
        Currency = "USD",
        ProviderParameters = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static ConfigurationSnapshot CreateSnapshot(string tenant, DateTimeOffset now) => new(
        ConfigurationSnapshotId.New(),
        "agents-1",
        "prompts-1",
        new PinnedModelAssignment
        {
            ReleaseId = new ModelAssignmentReleaseId(Guid.CreateVersion7()),
            Name = "assignment-1",
            Version = 1,
            ConfigurationHash = "assignment-hash",
            Classifier = CreateCandidate(tenant, "classifier"),
            Correlator = CreateCandidate(tenant, "correlator"),
            Agents = new Dictionary<string, PinnedModelCandidate>(StringComparer.Ordinal),
        },
        "tools-1",
        "policies-1",
        new Dictionary<string, string>(StringComparer.Ordinal),
        now);

    private async Task<Incident> SaveIncidentAsync(Incident incident)
    {
        await using SreDbContext context = postgres.CreateContext();
        context.Incidents.Add(incident);
        incident.DequeueEvents();
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return incident;
    }

    private async Task<ApprovalRequest> SaveApprovalAsync(ApprovalRequest request)
    {
        await using SreDbContext context = postgres.CreateContext();
        context.ApprovalRequests.Add(request);
        request.DequeueEvents();
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request;
    }

    [Fact]
    public async Task StartInvestigationEnforcesTenantBoundary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident incident = await SaveIncidentAsync(NewIncidentFor(fingerprint, Now, tenant: "acme"));

        await using SreDbContext context = postgres.CreateContext();
        IncidentRepository incidents = new(context, PostgresFixture.DatabaseOptions(postgres.ConnectionString));
        UnitOfWork unitOfWork = new(context);

        IConfigurationSnapshotStore snapshots = Substitute.For<IConfigurationSnapshotStore>();
        IConfigurationPinner pinner = Substitute.For<IConfigurationPinner>();
        IEventPublisher publisher = Substitute.For<IEventPublisher>();
        IInvestigationWorkflow workflow = Substitute.For<IInvestigationWorkflow>();

        workflow.IsRunningAsync(incident.Id, ct).Returns(Task.FromResult(false));
        pinner.PinCurrentAsync("acme", Arg.Any<DateTimeOffset>(), ct)
            .Returns(Task.FromResult(Result<ConfigurationSnapshot>.Success(CreateSnapshot("acme", Now))));

        StartInvestigationHandler handler = new(
            incidents,
            snapshots,
            pinner,
            publisher,
            unitOfWork,
            workflow,
            Clock);

        // Foreign tenant receives 404 IncidentNotFound
        Result foreignResult = await handler.HandleAsync("globex", incident.Id, ct);
        foreignResult.IsFailure.ShouldBeTrue();
        foreignResult.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.IncidentNotFound.ToString());
        ErrorCatalog.Describe(foreignResult.Error.Code).HttpStatusCode.ShouldBe(404);

        // Owning tenant succeeds
        Result owningResult = await handler.HandleAsync("acme", incident.Id, ct);
        owningResult.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RetryInvestigationEnforcesTenantBoundary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident incident = await SaveIncidentAsync(NewIncidentFor(fingerprint, Now, tenant: "acme"));

        await using SreDbContext context = postgres.CreateContext();
        IncidentRepository incidents = new(context, PostgresFixture.DatabaseOptions(postgres.ConnectionString));
        UnitOfWork unitOfWork = new(context);

        IConfigurationSnapshotStore snapshots = Substitute.For<IConfigurationSnapshotStore>();
        IConfigurationPinner pinner = Substitute.For<IConfigurationPinner>();
        IEventPublisher publisher = Substitute.For<IEventPublisher>();
        IInvestigationWorkflow workflow = Substitute.For<IInvestigationWorkflow>();

        workflow.IsRunningAsync(incident.Id, ct).Returns(Task.FromResult(false));
        pinner.PinCurrentAsync("acme", Arg.Any<DateTimeOffset>(), ct)
            .Returns(Task.FromResult(Result<ConfigurationSnapshot>.Success(CreateSnapshot("acme", Now))));

        RetryInvestigationHandler handler = new(
            incidents,
            snapshots,
            pinner,
            publisher,
            unitOfWork,
            workflow,
            Clock);

        // Foreign tenant receives 404 IncidentNotFound
        Result foreignResult = await handler.HandleAsync("globex", incident.Id, "investigation stalled", ct);
        foreignResult.IsFailure.ShouldBeTrue();
        foreignResult.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.IncidentNotFound.ToString());
        ErrorCatalog.Describe(foreignResult.Error.Code).HttpStatusCode.ShouldBe(404);

        // Owning tenant succeeds
        Result owningResult = await handler.HandleAsync("acme", incident.Id, "investigation stalled", ct);
        owningResult.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task CancelInvestigationEnforcesTenantBoundary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident incident = await SaveIncidentAsync(NewIncidentFor(fingerprint, Now, tenant: "acme"));

        await using SreDbContext context = postgres.CreateContext();
        IncidentRepository incidents = new(context, PostgresFixture.DatabaseOptions(postgres.ConnectionString));
        UnitOfWork unitOfWork = new(context);

        IEventPublisher publisher = Substitute.For<IEventPublisher>();
        IInvestigationWorkflow workflow = Substitute.For<IInvestigationWorkflow>();
        workflow.CancelAsync(incident.Id, "cancelled by operator", ct).Returns(Task.FromResult(Result.Success()));

        CancelInvestigationHandler handler = new(
            incidents,
            publisher,
            unitOfWork,
            workflow,
            Clock);

        // Foreign tenant receives 404 IncidentNotFound
        Result foreignResult = await handler.HandleAsync("globex", incident.Id, "operator", "cancelled by operator", ct);
        foreignResult.IsFailure.ShouldBeTrue();
        foreignResult.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.IncidentNotFound.ToString());
        ErrorCatalog.Describe(foreignResult.Error.Code).HttpStatusCode.ShouldBe(404);

        // Owning tenant succeeds
        Result owningResult = await handler.HandleAsync("acme", incident.Id, "operator", "cancelled by operator", ct);
        owningResult.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ApprovalStoreAndDecisionHandlerEnforceTenantBoundary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        Incident incident = await SaveIncidentAsync(NewIncidentFor(fingerprint, Now, tenant: "acme"));
        ApprovalRequest approval = await SaveApprovalAsync(NewApprovalFor(incident.Id, Now));

        await using SreDbContext context = postgres.CreateContext();
        ApprovalStore approvalStore = new(context);
        UnitOfWork unitOfWork = new(context);

        IInvestigationWorkflow workflow = Substitute.For<IInvestigationWorkflow>();
        workflow.SignalApprovalDecisionAsync(Arg.Any<IncidentId>(), Arg.Any<ApprovalDecisionSignal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        // 1. ApprovalStore.GetAsync isolates by tenant
        ApprovalRequest? foreignApproval = await approvalStore.GetAsync("globex", approval.Id, ct);
        foreignApproval.ShouldBeNull();

        ApprovalRequest? owningApproval = await approvalStore.GetAsync("acme", approval.Id, ct);
        owningApproval.ShouldNotBeNull();
        owningApproval.Id.ShouldBe(approval.Id);

        // 2. ApprovalDecisionHandler.DecideAsync isolates by tenant
        ApprovalDecisionHandler decisionHandler = new(
            approvalStore,
            unitOfWork,
            workflow,
            Clock);

        Result foreignDecision = await decisionHandler.DecideAsync("globex", approval.Id, isApproved: true, "operator", null, ct);
        foreignDecision.IsFailure.ShouldBeTrue();
        foreignDecision.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ApprovalNotFound.ToString());
        ErrorCatalog.Describe(foreignDecision.Error.Code).HttpStatusCode.ShouldBe(404);

        Result owningDecision = await decisionHandler.DecideAsync("acme", approval.Id, isApproved: true, "operator", null, ct);
        owningDecision.IsSuccess.ShouldBeTrue();
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
