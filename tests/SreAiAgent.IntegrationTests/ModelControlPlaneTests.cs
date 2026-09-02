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

        CreateProviderRevisionCommand revCmd = new(
            ProviderAdapterKind.Scripted,
            "https://test",
            null,
            null,
            null,
            "env://TEST_KEY",
            ModelCapability.None);

        // 1. Connection isolation
        var createConnA = await Plane.CreateConnectionAsync(tenantA, "Connection A", "actor", ct);
        createConnA.IsSuccess.ShouldBeTrue();
        ProviderConnection connA = createConnA.Value;

        IReadOnlyList<ProviderConnection> listConnB = await Plane.ListConnectionsAsync(tenantB, ct);
        listConnB.ShouldBeEmpty();

        var revBOnConnA = await Plane.CreateRevisionAsync(tenantB, connA.Id, revCmd, "actor", ct);
        revBOnConnA.IsSuccess.ShouldBeFalse();
        revBOnConnA.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ProviderConnectionNotFound.ToString());

        // 2. Revision isolation
        var createRevA = await Plane.CreateRevisionAsync(tenantA, connA.Id, revCmd, "actor", ct);
        createRevA.IsSuccess.ShouldBeTrue();
        ProviderConnectionRevision revA = createRevA.Value;

        IReadOnlyList<ProviderConnectionRevision> listRevB = await Plane.ListRevisionsAsync(tenantB, connA.Id, ct);
        listRevB.ShouldBeEmpty();

        var verifyB = await Plane.VerifyRevisionAsync(tenantB, connA.Id, revA.Revision, "actor", ct);
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
        var deployBOnRevA = await Plane.CreateDeploymentVersionAsync(tenantB, deployCmdWithRevA, "actor", ct);
        deployBOnRevA.IsSuccess.ShouldBeFalse();
        deployBOnRevA.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ProviderRevisionNotFound.ToString());

        // 3. Deployment isolation
        var createDeployA = await Plane.CreateDeploymentVersionAsync(tenantA, deployCmdWithRevA, "actor", ct);
        createDeployA.IsSuccess.ShouldBeTrue();
        ModelDeploymentVersion deployA = createDeployA.Value;

        IReadOnlyList<ModelDeploymentVersion> listDeployB = await Plane.ListDeploymentVersionsAsync(tenantB, ct);
        listDeployB.ShouldBeEmpty();

        CreateModelDeploymentVersionCommand deployCmdNext = deployCmdWithRevA with { Temperature = 0.8f };
        var nextDeployB = await Plane.CreateNextDeploymentVersionAsync(tenantB, deployA.Id, deployCmdNext, "actor", ct);
        nextDeployB.IsSuccess.ShouldBeFalse();
        nextDeployB.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelDeploymentNotFound.ToString());

        CreateModelAssignmentReleaseCommand assignCmdWithDeployA = new(
            "assignment-b",
            deployA.Id,
            deployA.Id,
            new Dictionary<string, ModelDeploymentVersionId> { ["test-expert"] = deployA.Id });
        var assignBOnDeployA = await Plane.CreateAssignmentReleaseAsync(tenantB, assignCmdWithDeployA, "actor", ct);
        assignBOnDeployA.IsSuccess.ShouldBeFalse();
        assignBOnDeployA.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelDeploymentNotFound.ToString());

        // 4. Assignment Release isolation
        CreateModelAssignmentReleaseCommand assignCmdA = new(
            "assignment-a",
            deployA.Id,
            deployA.Id,
            new Dictionary<string, ModelDeploymentVersionId> { ["test-expert"] = deployA.Id });
        var createAssignA = await Plane.CreateAssignmentReleaseAsync(tenantA, assignCmdA, "actor", ct);
        createAssignA.IsSuccess.ShouldBeTrue();
        ModelAssignmentRelease assignA = createAssignA.Value;

        IReadOnlyList<ModelAssignmentRelease> listAssignB = await Plane.ListAssignmentReleasesAsync(tenantB, ct);
        listAssignB.ShouldBeEmpty();

        var evalB = await Plane.RequestEvaluationAsync(tenantB, assignA.Id, "dataset", "baseline", "actor", ct);
        evalB.IsSuccess.ShouldBeFalse();
        evalB.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelAssignmentNotFound.ToString());

        var activateB = await Plane.ActivateAsync(tenantB, assignA.Id, "actor", ct);
        activateB.IsSuccess.ShouldBeFalse();
        activateB.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelAssignmentNotFound.ToString());
    }

    [Fact]
    public async Task AnUpdateOnProviderConnectionRevisionsRaisesTheTrigger()
    {
        string tenant = "trigger-tenant-" + Guid.NewGuid();
        var create = await Plane.CreateConnectionAsync(tenant, "Conn", "actor", TestContext.Current.CancellationToken);
        CreateProviderRevisionCommand commandRev = new(
            ProviderAdapterKind.Scripted,
            "https://test",
            null,
            null,
            null,
            "env://TEST_KEY",
            ModelCapability.None);
        var rev = await Plane.CreateRevisionAsync(tenant, create.Value.Id, commandRev, "actor", TestContext.Current.CancellationToken);

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

        var createConn = await Plane.CreateConnectionAsync(tenant, "Conn", "actor", ct);
        createConn.IsSuccess.ShouldBeTrue();

        CreateProviderRevisionCommand commandRev = new(
            ProviderAdapterKind.Scripted,
            "https://test",
            null,
            null,
            null,
            "env://TEST_KEY",
            ModelCapability.None);
        var createRev = await Plane.CreateRevisionAsync(tenant, createConn.Value.Id, commandRev, "actor", ct);
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

        Task<Result<ModelDeploymentVersion>> task1 = plane1.CreateDeploymentVersionAsync(tenant, deployCmd, "actor1", ct);
        await plane1ReadTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Task<Result<ModelDeploymentVersion>> task2 = plane2.CreateDeploymentVersionAsync(tenant, deployCmd, "actor2", ct);

        Result<ModelDeploymentVersion>[] results = await Task.WhenAll(task1, task2);

        results.Count(static r => r.IsSuccess).ShouldBe(1);
        results.Count(static r => !r.IsSuccess).ShouldBe(1);

        Result<ModelDeploymentVersion> winner = results.Single(static r => r.IsSuccess);
        Result<ModelDeploymentVersion> loser = results.Single(static r => !r.IsSuccess);

        winner.Value.Version.ShouldBe(1);
        loser.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.ModelResourceConflict.ToString());

        await using AsyncServiceScope scope3 = customProvider.CreateAsyncScope();
        IModelProviderControlPlane plane3 = scope3.ServiceProvider.GetRequiredService<IModelProviderControlPlane>();
        var result3 = await plane3.CreateDeploymentVersionAsync(tenant, deployCmd, "actor3", ct);
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
        PinnedModelAssignment? firstSnapshot = await repository.GetActiveSnapshotAsync(tenant, ct);
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

        PinnedModelAssignment? secondSnapshot = await repository.GetActiveSnapshotAsync(tenant, ct);
        secondSnapshot.ShouldNotBeNull();
        secondSnapshot.ReleaseId.ShouldBe(firstSnapshot.ReleaseId);
        assignmentsAfter.ShouldBe(assignmentsBefore);
        connectionsAfter.ShouldBe(connectionsBefore);
        revisionsAfter.ShouldBe(revisionsBefore);
        deploymentsAfter.ShouldBe(deploymentsBefore);
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
        PinnedModelAssignment? snapshot = await repository.GetActiveSnapshotAsync(tenant, ct);
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
