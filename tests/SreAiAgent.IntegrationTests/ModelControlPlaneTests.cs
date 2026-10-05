using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Budgets;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;
using SreAiAgent.Infrastructure.Persistence.Repositories;
using SreAiAgent.Infrastructure.Registries;

namespace SreAiAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelControlPlaneTests : IAsyncLifetime
{
    private const string DefaultBootstrapTenant = "boot-tenant";

    private readonly PostgresFixture _postgres;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public ModelControlPlaneTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _provider = CreateServiceProvider();
        _scope = _provider.CreateScope();
    }

    private ServiceProvider CreateServiceProvider(IInterceptor interceptor)
        => CreateServiceProvider(bootstrapTenant: null, interceptor);

    private ServiceProvider CreateServiceProvider(string? bootstrapTenant = null, IInterceptor? interceptor = null)
    {
        ServiceCollection services = new();

        services.AddSingleton(PostgresFixture.DatabaseOptions(_postgres.ConnectionString));
        if (interceptor is not null)
        {
            services.AddDbContextPool<SreDbContext>((provider, options) =>
            {
                DatabaseOptions database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
                options.UseNpgsql(database.ConnectionString, npgsql =>
                {
                    npgsql.CommandTimeout(database.CommandTimeoutSeconds);
                    npgsql.MigrationsHistoryTable("__migrations_history");
                });
                options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);
                options.AddInterceptors(interceptor);
            });
            services.AddHostedService<DatabaseMigrator>();
            services.AddHostedService<ModelAssignmentBootstrapper>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<ModelProviderRepository>();
            services.AddScoped<IProviderRevisionReader>(p => p.GetRequiredService<ModelProviderRepository>());
            services.AddScoped<IConfigurationPinner, ConfigurationPinner>();
        }
        else
        {
            services.AddSrePersistence();
        }

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);

        services.AddSingleton(Options.Create(new RegistryOptions
        {
            AgentRelease = "release",
            PromptRelease = "release",
            ModelRelease = "release",
            ToolRelease = "release",
            PolicyRelease = "release",
            FlagRelease = "release",
        }));

        services.AddSingleton<IModelProfileRegistry>(new StubModelProfileRegistry());
        services.AddSingleton<IAgentRegistry>(new StubAgentRegistry());

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILogger<ModelAssignmentBootstrapper>>(NullLogger<ModelAssignmentBootstrapper>.Instance);

        // Control plane and its collaborators.
        services.AddScoped<IModelProviderControlPlane, ModelProviderControlPlane>();
        services.AddScoped<ProviderRevisionVerifier>();
        services.AddScoped<ProviderEndpointPolicy>();
        services.AddSingleton(Substitute.For<ICredentialResolver>());
        services.AddHttpClient();

        services.AddSingleton(Options.Create(new LlmOptions
        {
            AllowedCustomEndpointHosts = Array.Empty<string>(),
            Bootstrap = new LlmOptions.BootstrapOptions
            {
                Tenant = bootstrapTenant ?? DefaultBootstrapTenant,
                ConnectionDisplayName = "default",
                AdapterKind = ProviderAdapterKind.Scripted,
                Endpoint = "https://test",
                CredentialRef = "env://TEST_KEY",
                AssignmentName = "bootstrap",
                ClassifierProfile = "fast",
                CorrelatorProfile = "analytical",
            },
        }));

        return services.BuildServiceProvider(validateScopes: true);
    }

    private IModelProviderControlPlane Plane => _scope.ServiceProvider.GetRequiredService<IModelProviderControlPlane>();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_scope is IAsyncDisposable ad)
        {
            await ad.DisposeAsync();
        }
        else
        {
            _scope.Dispose();
        }

        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task TheMigrationAppliesCleanly()
    {
        await using var context = _postgres.CreateContext();
        bool canConnect = await context.Database.CanConnectAsync(TestContext.Current.CancellationToken);
        canConnect.ShouldBeTrue();
    }

    [Fact]
    public async Task TenantACannotReadOrReferenceTenantBsData()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenantA = "tenant-a-" + Guid.NewGuid();
        string tenantB = "tenant-b-" + Guid.NewGuid();
        TenantScope scopeA = TenantScope.TenantWide(tenantA);
        TenantScope scopeB = TenantScope.TenantWide(tenantB);

        CreateProviderRevisionCommand revCmd = new(
            ProviderAdapterKind.Scripted,
            "https://test",
            null,
            null,
            null,
            "env://TEST_KEY",
            ModelCapability.None);

        // 1. Connection isolation
        var createConnA = await Plane.CreateConnectionAsync(scopeA, "Connection A", "actor", ct);
        createConnA.IsSuccess.ShouldBeTrue();
        ProviderConnection connA = createConnA.Value;

        IReadOnlyList<ProviderConnection> listConnB = await Plane.ListConnectionsAsync(scopeB, ct);
        listConnB.ShouldBeEmpty();

        var revBOnConnA = await Plane.CreateRevisionAsync(scopeB, connA.Id, revCmd, "actor", ct);
        revBOnConnA.IsSuccess.ShouldBeFalse();
        revBOnConnA.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ProviderConnectionNotFound.ToString());

        // 2. Revision isolation
        var createRevA = await Plane.CreateRevisionAsync(scopeA, connA.Id, revCmd, "actor", ct);
        createRevA.IsSuccess.ShouldBeTrue();
        ProviderConnectionRevision revA = createRevA.Value;

        IReadOnlyList<ProviderConnectionRevision> listRevB = await Plane.ListRevisionsAsync(scopeB, connA.Id, ct);
        listRevB.ShouldBeEmpty();

        var verifyB = await Plane.VerifyRevisionAsync(scopeB, connA.Id, revA.Revision, "actor", ct);
        verifyB.IsSuccess.ShouldBeFalse();
        verifyB.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ProviderRevisionNotFound.ToString());

        CreateModelDeploymentVersionCommand deployCmdWithRevA = new(
            "model-a",
            revA.Id,
            "model-1",
            ModelCapability.None,
            0.5f,
            1000,
            TimeSpan.FromSeconds(30),
            0m,
            0m,
            "USD",
            new Dictionary<string, string>());
        var deployBOnRevA = await Plane.CreateDeploymentVersionAsync(scopeB, deployCmdWithRevA, "actor", ct);
        deployBOnRevA.IsSuccess.ShouldBeFalse();
        deployBOnRevA.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ProviderRevisionNotFound.ToString());

        // 3. Deployment isolation
        var createDeployA = await Plane.CreateDeploymentVersionAsync(scopeA, deployCmdWithRevA, "actor", ct);
        createDeployA.IsSuccess.ShouldBeTrue();
        ModelDeploymentVersion deployA = createDeployA.Value;

        IReadOnlyList<ModelDeploymentVersion> listDeployB = await Plane.ListDeploymentVersionsAsync(scopeB, ct);
        listDeployB.ShouldBeEmpty();

        CreateModelDeploymentVersionCommand deployCmdNext = deployCmdWithRevA with { Temperature = 0.8f };
        var nextDeployB = await Plane.CreateNextDeploymentVersionAsync(scopeB, deployA.Id, deployCmdNext, "actor", ct);
        nextDeployB.IsSuccess.ShouldBeFalse();
        nextDeployB.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelDeploymentNotFound.ToString());

        CreateModelAssignmentReleaseCommand assignCmdWithDeployA = new(
            "assignment-b",
            deployA.Id,
            deployA.Id,
            new Dictionary<string, ModelDeploymentVersionId> { ["test-expert"] = deployA.Id });
        var assignBOnDeployA = await Plane.CreateAssignmentReleaseAsync(scopeB, assignCmdWithDeployA, "actor", ct);
        assignBOnDeployA.IsSuccess.ShouldBeFalse();
        assignBOnDeployA.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelDeploymentNotFound.ToString());

        // 4. Assignment Release isolation
        CreateModelAssignmentReleaseCommand assignCmdA = new(
            "assignment-a",
            deployA.Id,
            deployA.Id,
            new Dictionary<string, ModelDeploymentVersionId> { ["test-expert"] = deployA.Id });
        var createAssignA = await Plane.CreateAssignmentReleaseAsync(scopeA, assignCmdA, "actor", ct);
        createAssignA.IsSuccess.ShouldBeTrue();
        ModelAssignmentRelease assignA = createAssignA.Value;

        IReadOnlyList<ModelAssignmentRelease> listAssignB = await Plane.ListAssignmentReleasesAsync(scopeB, ct);
        listAssignB.ShouldBeEmpty();

        var evalB = await Plane.RequestEvaluationAsync(scopeB, assignA.Id, "dataset", "baseline", "actor", ct);
        evalB.IsSuccess.ShouldBeFalse();
        evalB.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelAssignmentNotFound.ToString());

        var activateB = await Plane.ActivateAsync(scopeB, assignA.Id, "actor", ct);
        activateB.IsSuccess.ShouldBeFalse();
        activateB.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelAssignmentNotFound.ToString());
    }

    [Fact]
    public async Task AProductScopedCallerCannotCreateReadUpdateOrActivateAnotherProductsData()
    {
        // D40's authorization matrix, exercised through the four verbs the
        // product-adaptability guide names, within one tenant: a ProductWide
        // caller for "checkout" never creates, reads, updates or activates
        // anything scoped to "payments", even though both share a tenant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "product-iso-" + Guid.NewGuid();
        TenantScope checkout = TenantScope.ProductWide(tenant, "checkout");
        TenantScope payments = TenantScope.ProductWide(tenant, "payments");

        CreateProviderRevisionCommand revCmd = new(
            ProviderAdapterKind.Scripted,
            "https://test",
            null,
            null,
            null,
            "env://TEST_KEY",
            ModelCapability.None);

        // CREATE + READ: a connection created under checkout is invisible to
        // payments and cannot be referenced by it.
        var createConnCheckout = await Plane.CreateConnectionAsync(checkout, "Checkout connection", "actor", ct);
        createConnCheckout.IsSuccess.ShouldBeTrue();
        ProviderConnection connection = createConnCheckout.Value;

        IReadOnlyList<ProviderConnection> paymentsConnections = await Plane.ListConnectionsAsync(payments, ct);
        paymentsConnections.ShouldBeEmpty();

        var revisionFromPayments = await Plane.CreateRevisionAsync(payments, connection.Id, revCmd, "actor", ct);
        revisionFromPayments.IsSuccess.ShouldBeFalse();
        revisionFromPayments.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ProviderConnectionNotFound.ToString());

        var revisionCheckout = await Plane.CreateRevisionAsync(checkout, connection.Id, revCmd, "actor", ct);
        revisionCheckout.IsSuccess.ShouldBeTrue();

        CreateModelDeploymentVersionCommand deployCmd = new(
            "product-iso-model",
            revisionCheckout.Value.Id,
            "model-1",
            ModelCapability.None,
            0.5f,
            1000,
            TimeSpan.FromSeconds(30),
            0m,
            0m,
            "USD",
            new Dictionary<string, string>());
        var deployCheckout = await Plane.CreateDeploymentVersionAsync(checkout, deployCmd, "actor", ct);
        deployCheckout.IsSuccess.ShouldBeTrue();

        // UPDATE: versioning checkout's deployment from payments' scope is
        // refused — the same deployment id is simply not visible there.
        CreateModelDeploymentVersionCommand deployCmdNext = deployCmd with { Temperature = 0.9f };
        var nextFromPayments = await Plane.CreateNextDeploymentVersionAsync(
            payments, deployCheckout.Value.Id, deployCmdNext, "actor", ct);
        nextFromPayments.IsSuccess.ShouldBeFalse();
        nextFromPayments.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelDeploymentNotFound.ToString());

        CreateModelAssignmentReleaseCommand assignCmd = new(
            "product-iso-release",
            deployCheckout.Value.Id,
            deployCheckout.Value.Id,
            new Dictionary<string, ModelDeploymentVersionId> { ["test-expert"] = deployCheckout.Value.Id });
        var releaseCheckout = await Plane.CreateAssignmentReleaseAsync(checkout, assignCmd, "actor", ct);
        releaseCheckout.IsSuccess.ShouldBeTrue();

        IReadOnlyList<ModelAssignmentRelease> paymentsReleases = await Plane.ListAssignmentReleasesAsync(payments, ct);
        paymentsReleases.ShouldBeEmpty();

        // ACTIVATE: activating checkout's release from payments' scope is
        // refused — the release simply does not exist at that scope.
        var activateFromPayments = await Plane.ActivateAsync(payments, releaseCheckout.Value.Id, "actor", ct);
        activateFromPayments.IsSuccess.ShouldBeFalse();
        activateFromPayments.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelAssignmentNotFound.ToString());
    }

    [Fact]
    public async Task ActiveAssignmentResolutionNeverFallsBackToABroaderOrNarrowerScope()
    {
        // D41: exact match only. A release activated under exactly one scope
        // is resolved by that scope and by no other, in either direction —
        // proven both ways in one test rather than trusting a single
        // assertion to stand in for "no fallback."
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "no-fallback-" + Guid.NewGuid();
        TenantScope tenantWide = TenantScope.TenantWide(tenant);
        TenantScope productWide = TenantScope.ProductWide(tenant, "checkout");
        TenantScope full = TenantScope.Full(tenant, "checkout", "production");

        ModelAssignmentReleaseId tenantWideReleaseId = await CreateAndActivateMinimalReleaseAsync(tenantWide, ct);

        ModelProviderRepository repository = _scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();

        // Direction 1: the scope with its own activated release resolves it.
        PinnedModelAssignment? tenantWideSnapshot = await repository.GetActiveSnapshotAsync(tenantWide, ct);
        tenantWideSnapshot.ShouldNotBeNull();
        tenantWideSnapshot.ReleaseId.ShouldBe(tenantWideReleaseId);

        // Direction 2: narrower scopes with nothing activated at their own
        // exact scope resolve nothing — never silently the TenantWide
        // release, even though it exists and even though Full and
        // ProductWide are, in the ordinary sense, "narrower" than TenantWide.
        (await repository.GetActiveSnapshotAsync(productWide, ct)).ShouldBeNull();
        (await repository.GetActiveSnapshotAsync(full, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task AProductReleaseCannotBeActivatedOnATenantWithNoTenantWideAssignment()
    {
        // BUG-002 (b): incidents carry no product, so they only ever resolve
        // the tenant-wide assignment (D41). This activation used to succeed and
        // leave every incident for the tenant unable to pin, silently.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "unreachable-" + Guid.NewGuid();
        TenantScope checkout = TenantScope.ProductWide(tenant, "checkout");

        ModelAssignmentReleaseId release = await CreateVerifiedEvaluatedReleaseAsync(checkout, ct);

        var activated = await Plane.ActivateAsync(checkout, release, "actor", ct);

        activated.IsSuccess.ShouldBeFalse();
        activated.Error.Code.ShouldBe(ApplicationErrorCodes.ModelAssignmentScopeUnreachable);
        ErrorCatalog.Describe(activated.Error.Code).HttpStatusCode.ShouldBe(409);

        ModelProviderRepository repository = _scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
        (await repository.GetActiveSnapshotAsync(checkout, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task AProductReleaseActivatedBesideATenantWideOneLeavesTheTenantsIncidentsPinnable()
    {
        // BUG-002 (a): with a tenant-wide assignment active, narrowing one
        // product is allowed, and a new incident for the tenant still pins.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "reachable-" + Guid.NewGuid();
        TenantScope tenantWide = TenantScope.TenantWide(tenant);
        TenantScope checkout = TenantScope.ProductWide(tenant, "checkout");

        ModelAssignmentReleaseId tenantWideRelease = await CreateAndActivateMinimalReleaseAsync(tenantWide, ct);
        ModelAssignmentReleaseId productRelease = await CreateVerifiedEvaluatedReleaseAsync(checkout, ct);

        var activated = await Plane.ActivateAsync(checkout, productRelease, "actor", ct);
        activated.IsSuccess.ShouldBeTrue();

        // The lookup ConfigurationPinner makes for every incident: the
        // tenant-wide scope, exactly (D41). It still resolves, and the product
        // release sits beside it rather than replacing it.
        ModelProviderRepository repository = _scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
        (await repository.GetActiveSnapshotAsync(tenantWide, ct)).ShouldNotBeNull().ReleaseId.ShouldBe(tenantWideRelease);
        (await repository.GetActiveSnapshotAsync(checkout, ct)).ShouldNotBeNull().ReleaseId.ShouldBe(productRelease);
    }

    [Fact]
    public async Task AMonthlyBudgetCountsThisMonthsSpendAndRefusesOnceItIsReached()
    {
        // gemini-budgets-notifications.md B: spend is llm_calls.estimated_cost
        // since the period began, failed calls included; last month's does not
        // count against a monthly budget.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "budget-monthly-" + Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset monthStart = new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        TenantBudgetService budgets = _scope.ServiceProvider.GetRequiredService<TenantBudgetService>();
        (await budgets.CheckAsync(tenant, ct)).IsSuccess.ShouldBeTrue("No budget means no ceiling.");

        (await budgets.SetAsync(tenant, 1.00m, "usd", BudgetPeriod.Monthly, null, "alice", ct)).IsSuccess.ShouldBeTrue();

        await RecordSpendAsync(tenant, monthStart.AddDays(-3), 5.00m, "succeeded", ct);
        await RecordSpendAsync(tenant, monthStart.AddMinutes(1), 0.60m, "failed", ct);

        TenantBudgetStatus? status = await budgets.GetAsync(tenant, ct);
        status.ShouldNotBeNull();
        status.PeriodStartedAt.ShouldBe(monthStart);
        status.Spent.ShouldBe(0.60m);
        status.Remaining.ShouldBe(0.40m);
        (await budgets.CheckAsync(tenant, ct)).IsSuccess.ShouldBeTrue();

        await RecordSpendAsync(tenant, monthStart.AddMinutes(2), 0.40m, "succeeded", ct);

        Result refused = await budgets.CheckAsync(tenant, ct);
        refused.IsFailure.ShouldBeTrue();
        refused.Error.Code.ShouldBe(ApplicationErrorCodes.BudgetExceeded);
        ErrorCatalog.Describe(refused.Error.Code).IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public async Task AFixedBudgetCountsEverythingSinceItsStartAcrossMonths()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "budget-fixed-" + Guid.NewGuid();
        // Whole seconds: PostgreSQL keeps microseconds, .NET keeps 100 ns ticks.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero)
            .AddDays(-45);

        TenantBudgetService budgets = _scope.ServiceProvider.GetRequiredService<TenantBudgetService>();
        (await budgets.SetAsync(tenant, 10m, "USD", BudgetPeriod.Fixed, start, "alice", ct)).IsSuccess.ShouldBeTrue();

        await RecordSpendAsync(tenant, start.AddDays(-1), 100m, "succeeded", ct);
        await RecordSpendAsync(tenant, start.AddDays(1), 4m, "succeeded", ct);
        await RecordSpendAsync(tenant, DateTimeOffset.UtcNow.AddMinutes(-1), 5m, "succeeded", ct);

        TenantBudgetStatus? status = await budgets.GetAsync(tenant, ct);
        status.ShouldNotBeNull().Spent.ShouldBe(9m);
        status.PeriodStartedAt.ShouldBe(start.ToUniversalTime());
    }

    [Fact]
    public async Task ABudgetAndTheModelsItCountsMustShareACurrency()
    {
        // D47: spend priced in USD can never approach a limit in EUR, so the
        // pair is refused from either side instead of going uncounted.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "budget-currency-" + Guid.NewGuid();
        TenantScope tenantWide = TenantScope.TenantWide(tenant);
        await CreateAndActivateMinimalReleaseAsync(tenantWide, ct);

        TenantBudgetService budgets = _scope.ServiceProvider.GetRequiredService<TenantBudgetService>();
        Result<TenantBudgetStatus> euro = await budgets.SetAsync(tenant, 10m, "EUR", BudgetPeriod.Monthly, null, "alice", ct);
        euro.IsFailure.ShouldBeTrue();
        euro.Error.Code.ShouldBe(ApplicationErrorCodes.BudgetCurrencyMismatch);

        (await budgets.SetAsync(tenant, 10m, "USD", BudgetPeriod.Monthly, null, "alice", ct)).IsSuccess.ShouldBeTrue();

        TenantScope checkout = TenantScope.ProductWide(tenant, "checkout");
        ModelAssignmentReleaseId release = await CreateVerifiedEvaluatedReleaseAsync(checkout, ct, currency: "EUR");
        var activated = await Plane.ActivateAsync(checkout, release, "actor", ct);
        activated.IsFailure.ShouldBeTrue();
        activated.Error.Code.ShouldBe(ApplicationErrorCodes.BudgetCurrencyMismatch);
    }

    private async Task RecordSpendAsync(string tenant, DateTimeOffset calledAt, decimal cost, string outcome, CancellationToken ct)
    {
        await using SreDbContext context = _postgres.CreateContext();
        context.LlmCalls.Add(new LlmCallEntity
        {
            Id = Guid.CreateVersion7(),
            IncidentId = Guid.CreateVersion7(),
            Tenant = tenant,
            AgentName = "classifier",
            PromptName = "classify",
            PromptVersion = "1",
            PromptHash = "hash",
            Outcome = outcome,
            EstimatedCost = cost,
            CalledAt = calledAt,
        });
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Takes a release through the control plane's own pipeline, verification
    /// and a passing evaluation included, so that the only thing left to decide
    /// activation is the rule under test.
    /// </summary>
    private async Task<ModelAssignmentReleaseId> CreateVerifiedEvaluatedReleaseAsync(
        TenantScope scope, CancellationToken ct, string currency = "USD")
    {
        var connection = await Plane.CreateConnectionAsync(scope, "connection-" + Guid.NewGuid(), "actor", ct);
        connection.IsSuccess.ShouldBeTrue();

        var revision = await Plane.CreateRevisionAsync(
            scope,
            connection.Value.Id,
            new CreateProviderRevisionCommand(
                ProviderAdapterKind.Scripted, "https://test", null, null, null, "env://TEST_KEY", ModelCapability.None),
            "actor",
            ct);
        revision.IsSuccess.ShouldBeTrue();

        var verified = await Plane.VerifyRevisionAsync(scope, connection.Value.Id, revision.Value.Revision, "actor", ct);
        verified.IsSuccess.ShouldBeTrue();

        var deployment = await Plane.CreateDeploymentVersionAsync(
            scope,
            new CreateModelDeploymentVersionCommand(
                "model-" + Guid.NewGuid(),
                revision.Value.Id,
                "model-1",
                ModelCapability.None,
                0f,
                1000,
                TimeSpan.FromSeconds(30),
                0m,
                0m,
                currency,
                new Dictionary<string, string>()),
            "actor",
            ct);
        deployment.IsSuccess.ShouldBeTrue();

        var release = await Plane.CreateAssignmentReleaseAsync(
            scope,
            new CreateModelAssignmentReleaseCommand(
                "release-" + Guid.NewGuid(),
                deployment.Value.Id,
                deployment.Value.Id,
                new Dictionary<string, ModelDeploymentVersionId> { ["test-expert"] = deployment.Value.Id }),
            "actor",
            ct);
        release.IsSuccess.ShouldBeTrue();

        var requested = await Plane.RequestEvaluationAsync(scope, release.Value.Id, "dataset", "baseline", "actor", ct);
        requested.IsSuccess.ShouldBeTrue();
        var completed = await Plane.CompleteEvaluationAsync(
            scope, requested.Value.EvaluationId, passed: true, new string('a', 64), "actor", ct);
        completed.IsSuccess.ShouldBeTrue();

        return release.Value.Id;
    }

    /// <summary>
    /// Builds and activates the smallest possible release directly through
    /// EF, bypassing <see cref="IModelProviderControlPlane"/>'s evaluation and
    /// verification checks entirely: this test is about
    /// <c>GetActiveSnapshotAsync</c>'s resolution rule, not the activation
    /// pipeline's validation, and the control plane's own checks are already
    /// covered elsewhere.
    /// </summary>
    private async Task<ModelAssignmentReleaseId> CreateAndActivateMinimalReleaseAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using SreDbContext context = _postgres.CreateContext();

        ProviderConnectionEntity connection = new()
        {
            Id = ProviderConnectionId.New(),
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            DisplayName = "fallback-connection-" + Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.ProviderConnections.Add(connection);
        await context.SaveChangesAsync(cancellationToken);

        ProviderConnectionRevisionEntity revision = new()
        {
            Id = ProviderConnectionRevisionId.New(),
            ProviderConnectionId = connection.Id,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            Revision = 1,
            AdapterKind = ProviderAdapterKind.Scripted,
            Endpoint = "https://test",
            CredentialRef = "env://TEST_KEY",
            ConfigurationHash = "fallback-revision-hash",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };
        context.ProviderConnectionRevisions.Add(revision);
        connection.ActiveRevisionId = revision.Id;
        await context.SaveChangesAsync(cancellationToken);

        ModelDeploymentVersionEntity deployment = new()
        {
            Id = ModelDeploymentVersionId.New(),
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            LogicalName = "fallback-deployment-" + Guid.NewGuid(),
            Version = 1,
            ProviderConnectionRevisionId = revision.Id,
            ProviderModel = "scripted-model",
            MaxOutputTokens = 512,
            TimeoutMilliseconds = 30_000,
            ProviderParametersJson = "{}",
            Currency = "USD",
            ConfigurationHash = "fallback-deployment-hash",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };
        context.ModelDeploymentVersions.Add(deployment);
        await context.SaveChangesAsync(cancellationToken);

        ModelAssignmentReleaseEntity release = new()
        {
            Id = ModelAssignmentReleaseId.New(),
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            Name = "fallback-release-" + Guid.NewGuid(),
            Version = 1,
            ClassifierDeploymentVersionId = deployment.Id,
            CorrelatorDeploymentVersionId = deployment.Id,
            ConfigurationHash = "fallback-release-hash",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };
        context.ModelAssignmentReleases.Add(release);
        context.ModelAssignmentAgentSlots.Add(new ModelAssignmentAgentSlotEntity
        {
            AssignmentReleaseId = release.Id,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            AgentName = "test-expert",
            DeploymentVersionId = deployment.Id,
        });
        await context.SaveChangesAsync(cancellationToken);

        context.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
        {
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            AssignmentReleaseId = release.Id,
            ActivatedAt = DateTimeOffset.UtcNow,
            ActivatedBy = "test",
        });
        await context.SaveChangesAsync(cancellationToken);

        return release.Id;
    }

    [Fact]
    public async Task AnUpdateOnProviderConnectionRevisionsRaisesTheTrigger()
    {
        string tenant = "trigger-tenant-" + Guid.NewGuid();
        TenantScope scope = TenantScope.TenantWide(tenant);
        var create = await Plane.CreateConnectionAsync(scope, "Conn", "actor", TestContext.Current.CancellationToken);
        CreateProviderRevisionCommand commandRev = new(
            ProviderAdapterKind.Scripted,
            "https://test",
            null,
            null,
            null,
            "env://TEST_KEY",
            ModelCapability.None);
        var rev = await Plane.CreateRevisionAsync(scope, create.Value.Id, commandRev, "actor", TestContext.Current.CancellationToken);

        await using var context = _postgres.CreateContext();
        Npgsql.PostgresException ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE provider_connection_revisions SET endpoint = 'https://hacked' WHERE id = {0}", rev.Value.Id.Value);
        });
        ex.Message.ShouldContain("immutable");
        ex.Message.ShouldContain("MODEL_CONFIGURATION_INVALID");
    }

    [Fact]
    public async Task CreatingADuplicateOrConcurrentDeploymentVersionReturnsConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "race-tenant-" + Guid.NewGuid();
        TenantScope scope = TenantScope.TenantWide(tenant);

        var createConn = await Plane.CreateConnectionAsync(scope, "Conn", "actor", ct);
        createConn.IsSuccess.ShouldBeTrue();

        CreateProviderRevisionCommand commandRev = new(
            ProviderAdapterKind.Scripted,
            "https://test",
            null,
            null,
            null,
            "env://TEST_KEY",
            ModelCapability.None);
        var createRev = await Plane.CreateRevisionAsync(scope, createConn.Value.Id, commandRev, "actor", ct);
        createRev.IsSuccess.ShouldBeTrue();

        TaskCompletionSource plane1ReadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource plane2ReadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int queryCount = 0;

        RaceSyncInterceptor interceptor = new(async cmd =>
        {
            if (cmd.CommandText.Contains("model_deployment_versions", StringComparison.OrdinalIgnoreCase)
                && cmd.CommandText.Contains("max", StringComparison.OrdinalIgnoreCase))
            {
                int count = Interlocked.Increment(ref queryCount);
                if (count == 1)
                {
                    plane1ReadTcs.TrySetResult();
                    await plane2ReadTcs.Task.ConfigureAwait(false);
                }
                else if (count == 2)
                {
                    plane2ReadTcs.TrySetResult();
                }
            }
        });

        await using ServiceProvider customProvider = CreateServiceProvider(interceptor);
        await using AsyncServiceScope scope1 = customProvider.CreateAsyncScope();
        await using AsyncServiceScope scope2 = customProvider.CreateAsyncScope();
        IModelProviderControlPlane plane1 = scope1.ServiceProvider.GetRequiredService<IModelProviderControlPlane>();
        IModelProviderControlPlane plane2 = scope2.ServiceProvider.GetRequiredService<IModelProviderControlPlane>();

        CreateModelDeploymentVersionCommand deployCmd = new(
            "test-deploy",
            createRev.Value.Id,
            "gpt-4",
            ModelCapability.None,
            0.5f,
            1000,
            TimeSpan.FromSeconds(30),
            10m,
            30m,
            "USD",
            new Dictionary<string, string>());

        Task<Result<ModelDeploymentVersion>> task1 = plane1.CreateDeploymentVersionAsync(scope, deployCmd, "actor1", ct);
        await plane1ReadTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Task<Result<ModelDeploymentVersion>> task2 = plane2.CreateDeploymentVersionAsync(scope, deployCmd, "actor2", ct);

        Result<ModelDeploymentVersion>[] results = await Task.WhenAll(task1, task2);

        results.Count(static r => r.IsSuccess).ShouldBe(1);
        results.Count(static r => !r.IsSuccess).ShouldBe(1);

        Result<ModelDeploymentVersion> winner = results.Single(static r => r.IsSuccess);
        Result<ModelDeploymentVersion> loser = results.Single(static r => !r.IsSuccess);

        winner.Value.Version.ShouldBe(1);
        loser.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelResourceConflict.ToString());

        await using AsyncServiceScope scope3 = customProvider.CreateAsyncScope();
        IModelProviderControlPlane plane3 = scope3.ServiceProvider.GetRequiredService<IModelProviderControlPlane>();
        var result3 = await plane3.CreateDeploymentVersionAsync(scope, deployCmd, "actor3", ct);
        result3.IsSuccess.ShouldBeTrue();
        result3.Value.Version.ShouldBe(2);
    }

    [Fact]
    public async Task TheBootstrapperIsANoOpOnASecondRun()
    {
        string tenant = "boot-noop-" + Guid.NewGuid().ToString("N");
        await using ServiceProvider customProvider = CreateServiceProvider(tenant);
        await using AsyncServiceScope scope = customProvider.CreateAsyncScope();
        ModelAssignmentBootstrapper bootstrapper = new(
            customProvider.GetRequiredService<IServiceScopeFactory>(),
            customProvider.GetRequiredService<IOptions<LlmOptions>>(),
            customProvider.GetRequiredService<IOptions<RegistryOptions>>(),
            customProvider.GetRequiredService<IModelProfileRegistry>(),
            customProvider.GetRequiredService<IAgentRegistry>(),
            customProvider.GetRequiredService<IHostEnvironment>(),
            customProvider.GetRequiredService<TimeProvider>(),
            customProvider.GetRequiredService<ILogger<ModelAssignmentBootstrapper>>());

        CancellationToken ct = TestContext.Current.CancellationToken;

        await bootstrapper.StartAsync(ct);

        ModelProviderRepository repository = scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
        PinnedModelAssignment? firstSnapshot = await repository.GetActiveSnapshotAsync(TenantScope.TenantWide(tenant), ct);
        firstSnapshot.ShouldNotBeNull();

        await using SreDbContext contextBefore = _postgres.CreateContext();
        int connectionsBefore = await contextBefore.ProviderConnections
            .CountAsync(entity => entity.Tenant == tenant, ct);
        int revisionsBefore = await contextBefore.ProviderConnectionRevisions
            .CountAsync(entity => entity.Tenant == tenant, ct);
        int deploymentsBefore = await contextBefore.ModelDeploymentVersions
            .CountAsync(entity => entity.Tenant == tenant, ct);
        int assignmentsBefore = await contextBefore.ModelAssignmentReleases
            .CountAsync(entity => entity.Tenant == tenant, ct);

        deploymentsBefore.ShouldBe(2);

        await bootstrapper.StartAsync(ct);

        await using SreDbContext contextAfter = _postgres.CreateContext();
        int connectionsAfter = await contextAfter.ProviderConnections
            .CountAsync(entity => entity.Tenant == tenant, ct);
        int revisionsAfter = await contextAfter.ProviderConnectionRevisions
            .CountAsync(entity => entity.Tenant == tenant, ct);
        int deploymentsAfter = await contextAfter.ModelDeploymentVersions
            .CountAsync(entity => entity.Tenant == tenant, ct);
        int assignmentsAfter = await contextAfter.ModelAssignmentReleases
            .CountAsync(entity => entity.Tenant == tenant, ct);

        PinnedModelAssignment? secondSnapshot = await repository.GetActiveSnapshotAsync(TenantScope.TenantWide(tenant), ct);
        secondSnapshot.ShouldNotBeNull();
        secondSnapshot.ReleaseId.ShouldBe(firstSnapshot.ReleaseId);
        assignmentsAfter.ShouldBe(assignmentsBefore);
        connectionsAfter.ShouldBe(connectionsBefore);
        revisionsAfter.ShouldBe(revisionsBefore);
        deploymentsAfter.ShouldBe(deploymentsBefore);
    }

    [Fact]
    public async Task ConcurrentBootstrapAttemptsForTheSameTenantPersistExactlyOneAssignment()
    {
        string tenant = "boot-race-" + Guid.NewGuid().ToString("N");
        CancellationToken ct = TestContext.Current.CancellationToken;

        TaskCompletionSource winnerHoldsLock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseWinner = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AdvisoryLockRaceInterceptor interceptor = new(
            static command => command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal),
            async order =>
            {
                if (order == 1)
                {
                    // The command already executed against PostgreSQL by this point,
                    // so the winner genuinely holds the transaction-scoped advisory
                    // lock — pausing here, not before the command is sent, is what
                    // makes the second bootstrapper's own lock attempt a real,
                    // server-side block rather than a race the test wins by luck.
                    winnerHoldsLock.TrySetResult();
                    await releaseWinner.Task.ConfigureAwait(false);
                }
            });

        await using ServiceProvider customProvider = CreateServiceProvider(tenant, interceptor);

        ModelAssignmentBootstrapper CreateBootstrapper() => new(
            customProvider.GetRequiredService<IServiceScopeFactory>(),
            customProvider.GetRequiredService<IOptions<LlmOptions>>(),
            customProvider.GetRequiredService<IOptions<RegistryOptions>>(),
            customProvider.GetRequiredService<IModelProfileRegistry>(),
            customProvider.GetRequiredService<IAgentRegistry>(),
            customProvider.GetRequiredService<IHostEnvironment>(),
            customProvider.GetRequiredService<TimeProvider>(),
            customProvider.GetRequiredService<ILogger<ModelAssignmentBootstrapper>>());

        // Two independently scoped bootstrapper instances: each StartAsync call
        // opens its own IServiceScope (and therefore its own SreDbContext and
        // transaction) internally, exactly as two separate replicas would.
        ModelAssignmentBootstrapper bootstrapperA = CreateBootstrapper();
        ModelAssignmentBootstrapper bootstrapperB = CreateBootstrapper();

        Task startA = bootstrapperA.StartAsync(ct);
        await winnerHoldsLock.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // A holds the lock and is paused; B's own fast-path check (outside any
        // transaction) still sees no active assignment, so it proceeds into its
        // own transaction and attempts the same advisory lock.
        Task startB = bootstrapperB.StartAsync(ct);

        // Prove B is genuinely blocked at PostgreSQL, not merely running after A
        // by chance: it must still be incomplete after a real wait while A
        // continues to hold the lock.
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        startB.IsCompleted.ShouldBeFalse(
            "The second bootstrapper must be genuinely blocked on the PostgreSQL advisory lock while the "
                + "winner still holds it, not merely running after it by chance.");

        releaseWinner.TrySetResult();

        await Task.WhenAll(startA, startB).WaitAsync(TimeSpan.FromSeconds(30), ct);

        await using SreDbContext db = _postgres.CreateContext();
        int connections = await db.ProviderConnections.CountAsync(entity => entity.Tenant == tenant, ct);
        int revisions = await db.ProviderConnectionRevisions.CountAsync(entity => entity.Tenant == tenant, ct);
        int deployments = await db.ModelDeploymentVersions.CountAsync(entity => entity.Tenant == tenant, ct);
        int assignments = await db.ModelAssignmentReleases.CountAsync(entity => entity.Tenant == tenant, ct);
        int verifications = await db.ProviderRevisionVerifications.CountAsync(entity => entity.Tenant == tenant, ct);

        connections.ShouldBe(1, "Only the lock winner may create a provider connection; the lock-waiter's "
            + "in-lock recheck must exit before creating anything.");
        revisions.ShouldBe(1);
        deployments.ShouldBe(2, "One deployment per distinct model profile (fast, analytical), created once.");
        assignments.ShouldBe(1);
        verifications.ShouldBe(1, "Only the lock winner may run the verification/probe path; a lock-waiter "
            + "that exits after its in-lock recheck must never repeat it.");

        await using AsyncServiceScope verifyScope = customProvider.CreateAsyncScope();
        ModelProviderRepository repository = verifyScope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
        PinnedModelAssignment? snapshot = await repository.GetActiveSnapshotAsync(TenantScope.TenantWide(tenant), ct);
        snapshot.ShouldNotBeNull();
    }

    [Fact]
    public async Task BootstrapWithProfileAliasesDeduplicatesDeploymentsAndMapsCorrelatorAndAgentSlotsToSameDeployment()
    {
        string tenant = "boot-alias-" + Guid.NewGuid().ToString("N");
        await using ServiceProvider customProvider = CreateServiceProvider(tenant);
        await using AsyncServiceScope scope = customProvider.CreateAsyncScope();

        IModelProfileRegistry profileRegistry = customProvider.GetRequiredService<IModelProfileRegistry>();
        IAgentRegistry agentRegistry = customProvider.GetRequiredService<IAgentRegistry>();
        IOptions<LlmOptions> llmOptions = customProvider.GetRequiredService<IOptions<LlmOptions>>();
        IOptions<RegistryOptions> registryOptions = customProvider.GetRequiredService<IOptions<RegistryOptions>>();

        CancellationToken ct = TestContext.Current.CancellationToken;

        llmOptions.Value.Bootstrap!.CorrelatorProfile.ShouldBe("analytical");
        llmOptions.Value.Bootstrap.ClassifierProfile.ShouldBe("fast");

        Result<IReadOnlyList<AgentDefinition>> agents = await agentRegistry.GetReleaseAgentsAsync(registryOptions.Value.AgentRelease, ct);
        agents.IsSuccess.ShouldBeTrue();
        AgentDefinition agent = agents.Value.Single(a => a.Name == "test-expert");
        agent.ModelProfileRef.ShouldBe("analytical:v1");

        Result<ModelProfile> correlatorProfile = await profileRegistry.ResolveAsync(registryOptions.Value.ModelRelease, "analytical", ct);
        correlatorProfile.IsSuccess.ShouldBeTrue();
        correlatorProfile.Value.Name.ShouldBe("analytical");

        Result<ModelProfile> agentProfile = await profileRegistry.ResolveAsync(registryOptions.Value.ModelRelease, "analytical:v1", ct);
        agentProfile.IsSuccess.ShouldBeTrue();
        agentProfile.Value.Name.ShouldBe("analytical");

        ModelAssignmentBootstrapper bootstrapper = new(
            customProvider.GetRequiredService<IServiceScopeFactory>(),
            llmOptions,
            registryOptions,
            profileRegistry,
            agentRegistry,
            customProvider.GetRequiredService<IHostEnvironment>(),
            customProvider.GetRequiredService<TimeProvider>(),
            customProvider.GetRequiredService<ILogger<ModelAssignmentBootstrapper>>());

        await bootstrapper.StartAsync(ct);

        await using SreDbContext db = _postgres.CreateContext();
        List<ModelDeploymentVersionEntity> deployments = await db.ModelDeploymentVersions
            .Where(d => d.Tenant == tenant)
            .ToListAsync(ct);
        deployments.Count.ShouldBe(2);
        deployments.Count(d => d.LogicalName == "analytical").ShouldBe(1);
        deployments.Count(d => d.LogicalName == "fast").ShouldBe(1);

        ModelDeploymentVersionEntity analyticalDeployment = deployments.Single(d => d.LogicalName == "analytical");
        ModelDeploymentVersionEntity fastDeployment = deployments.Single(d => d.LogicalName == "fast");

        ModelProviderRepository repository = scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
        PinnedModelAssignment? snapshot = await repository.GetActiveSnapshotAsync(TenantScope.TenantWide(tenant), ct);
        snapshot.ShouldNotBeNull();
        snapshot.Correlator.DeploymentVersionId.ShouldBe(analyticalDeployment.Id);
        snapshot.Correlator.DeploymentLogicalName.ShouldBe("analytical");

        snapshot.Agents.ContainsKey("test-expert").ShouldBeTrue();
        snapshot.Agents["test-expert"].DeploymentVersionId.ShouldBe(analyticalDeployment.Id);
        snapshot.Agents["test-expert"].DeploymentLogicalName.ShouldBe("analytical");

        snapshot.Correlator.DeploymentVersionId.ShouldBe(snapshot.Agents["test-expert"].DeploymentVersionId);
        snapshot.Classifier.DeploymentVersionId.ShouldBe(fastDeployment.Id);
        snapshot.Classifier.DeploymentLogicalName.ShouldBe("fast");

        ModelAssignmentReleaseEntity assignment = await db.ModelAssignmentReleases
            .SingleAsync(a => a.Tenant == tenant, ct);
        assignment.CorrelatorDeploymentVersionId.ShouldBe(analyticalDeployment.Id);
        assignment.ClassifierDeploymentVersionId.ShouldBe(fastDeployment.Id);

        List<ModelAssignmentAgentSlotEntity> agentSlots = await db.ModelAssignmentAgentSlots
            .Where(s => s.Tenant == tenant && s.AssignmentReleaseId == assignment.Id)
            .ToListAsync(ct);
        agentSlots.Count.ShouldBe(1);
        agentSlots[0].AgentName.ShouldBe("test-expert");
        agentSlots[0].DeploymentVersionId.ShouldBe(analyticalDeployment.Id);
    }
}

internal sealed class RaceSyncInterceptor(Func<DbCommand, Task> onCommand) : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await onCommand(command).ConfigureAwait(false);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Signals when a matching command has actually executed against PostgreSQL —
/// after acquisition for <c>pg_advisory_xact_lock</c>, not before the command is
/// sent — so a test can pause the winner while it genuinely holds the lock.
/// </summary>
internal sealed class AdvisoryLockRaceInterceptor(
    Predicate<DbCommand> isMatch,
    Func<int, Task> onMatchExecuted) : DbCommandInterceptor
{
    private int _order;

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (isMatch(command))
        {
            int order = Interlocked.Increment(ref _order);
            await onMatchExecuted(order).ConfigureAwait(false);
        }

        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class StubModelProfileRegistry : IModelProfileRegistry
{
    public IReadOnlyCollection<ModelProfile> All { get; } = Array.Empty<ModelProfile>();

    public ValueTask<Result<ModelProfile>> ResolveAsync(
        string releaseName,
        string profileReference,
        CancellationToken cancellationToken)
    {
        string name = profileReference.Contains(':')
            ? profileReference.Split(':')[0]
            : profileReference;
        string version = profileReference.Contains(':')
            ? profileReference.Split(':')[1]
            : "1";

        return ValueTask.FromResult(Result<ModelProfile>.Success(new ModelProfile
        {
            Name = name,
            Version = version,
            Provider = "scripted",
            ModelName = "scripted-model",
            Temperature = 0.0f,
            Timeout = TimeSpan.FromSeconds(30),
            MaxOutputTokens = 512,
            SupportsStructuredOutput = false,
            SupportsToolCalling = false,
            SupportsParallelToolCalls = false,
        }));
    }
}

internal sealed class StubAgentRegistry : IAgentRegistry
{
    private static readonly AgentDefinition StubAgent = new()
    {
        Name = "test-expert",
        Version = "v1",
        PromptRef = "test.investigate:v1",
        ModelProfileRef = "analytical:v1",
        Capabilities = new HashSet<string>(StringComparer.Ordinal) { "test" },
        IncidentTypes = new HashSet<string>(StringComparer.Ordinal) { "test" },
        AllowedTools = new HashSet<string>(StringComparer.Ordinal) { "test.tool" },
        Budget = new AgentExecutionBudget(1, TimeSpan.FromSeconds(30), 1, 1, 1000, 0.01m),
        OutputSchema = "AgentFinding",
    };

    public IReadOnlyCollection<AgentDefinition> All { get; } = new[] { StubAgent };

    public ValueTask<Result<AgentDefinition>> ResolveAsync(
        string releaseName,
        string agentName,
        CancellationToken cancellationToken)
        => agentName == StubAgent.Name
            ? ValueTask.FromResult(Result<AgentDefinition>.Success(StubAgent))
            : ValueTask.FromResult(Result<AgentDefinition>.Failure(Error.Permanent(
                new ErrorCode("AGENT_NOT_FOUND"),
                "No such agent in the stub registry.")));

    public ValueTask<Result<IReadOnlyList<AgentDefinition>>> GetReleaseAgentsAsync(
        string releaseName,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(Result<IReadOnlyList<AgentDefinition>>.Success(new List<AgentDefinition> { StubAgent }));
}
