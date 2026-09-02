using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.Infrastructure.Llm;

internal sealed partial class ModelAssignmentBootstrapper(
    IServiceScopeFactory scopeFactory,
    IOptions<LlmOptions> options,
    IOptions<RegistryOptions> registries,
    IModelProfileRegistry modelProfiles,
    IAgentRegistry agents,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<ModelAssignmentBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LlmOptions.BootstrapOptions? bootstrap = options.Value.Bootstrap;
        if (bootstrap is null)
        {
            return;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        ModelProviderRepository repository = scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ProviderRevisionVerifier verifier = scope.ServiceProvider.GetRequiredService<ProviderRevisionVerifier>();

        PinnedModelAssignment? active = await repository
            .GetActiveSnapshotAsync(bootstrap.Tenant, cancellationToken)
            .ConfigureAwait(false);
        if (active is not null)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            ProviderCapabilityDescriptor descriptor = ProviderClientCatalog.Descriptor(bootstrap.AdapterKind);

            ProviderConnection connection = ProviderConnection.Create(bootstrap.Tenant, bootstrap.ConnectionDisplayName, now);
            ProviderConnectionRevision revision = ProviderConnectionRevision.Create(
                connection.Id,
                bootstrap.Tenant,
                1,
                bootstrap.AdapterKind,
                bootstrap.Endpoint,
                bootstrap.Region,
                bootstrap.ApiVersion,
                bootstrap.Organization,
                bootstrap.CredentialRef,
                descriptor.Capabilities,
                now,
                "bootstrapper");

            repository.AddConnection(connection);
            repository.AddRevision(revision);
            await repository.SaveChangesAsync(ct).ConfigureAwait(false);
            await repository.SetActiveRevisionAsync(bootstrap.Tenant, connection.Id, revision.Id, ct).ConfigureAwait(false);
            HashSet<string> profileRefs =
            [
                bootstrap.ClassifierProfile,
                bootstrap.CorrelatorProfile
            ];

            Result<IReadOnlyList<AgentDefinition>> releasedAgents = await agents
                .GetReleaseAgentsAsync(registries.Value.AgentRelease, ct)
                .ConfigureAwait(false);
            if (releasedAgents.IsSuccess)
            {
                foreach (AgentDefinition agent in releasedAgents.Value)
                {
                    profileRefs.Add(agent.ModelProfileRef);
                }
            }
            else
            {
                throw new InvalidOperationException($"Could not load released agents for bootstrap: {releasedAgents.Error.Message}");
            }

            Dictionary<string, ModelDeploymentVersionId> profileToDeployment = new(StringComparer.Ordinal);
            Dictionary<string, ModelDeploymentVersion> distinctDeployments = new(StringComparer.Ordinal);
            foreach (string profileRef in profileRefs)
            {
                Result<ModelProfile> resolvedProfile = await modelProfiles
                    .ResolveAsync(registries.Value.ModelRelease, profileRef, ct)
                    .ConfigureAwait(false);
                if (resolvedProfile.IsFailure)
                {
                    throw new InvalidOperationException($"Could not load model profile {profileRef}: {resolvedProfile.Error.Message}");
                }

                ModelProfile profile = resolvedProfile.Value;
                if (!distinctDeployments.TryGetValue(profile.Name, out ModelDeploymentVersion? deployment))
                {
                    ModelCapability requiredCapabilities = ModelCapability.SystemMessage;
                    if (profile.SupportsStructuredOutput)
                    {
                        requiredCapabilities |= ModelCapability.StructuredOutput;
                    }
                    if (profile.SupportsToolCalling)
                    {
                        requiredCapabilities |= ModelCapability.ToolCalling;
                    }
                    if (profile.SupportsParallelToolCalls)
                    {
                        requiredCapabilities |= ModelCapability.ParallelToolCalls;
                    }

                    deployment = ModelDeploymentVersion.Create(
                        bootstrap.Tenant,
                        profile.Name,
                        1,
                        revision.Id,
                        profile.ModelName,
                        requiredCapabilities,
                        profile.Temperature,
                        profile.MaxOutputTokens,
                        profile.Timeout,
                        profile.InputCostPerMillionTokens,
                        profile.OutputCostPerMillionTokens,
                        "USD",
                        null, // empty provider parameters
                        now,
                        "bootstrapper");

                    repository.AddDeployment(deployment);
                    distinctDeployments[profile.Name] = deployment;
                }

                profileToDeployment[profileRef] = deployment.Id;
            }

            Dictionary<string, ModelDeploymentVersionId> agentDeployments = new(StringComparer.Ordinal);
            foreach (AgentDefinition agent in releasedAgents.Value)
            {
                agentDeployments[agent.Name] = profileToDeployment[agent.ModelProfileRef];
            }

            ModelAssignmentRelease assignment = ModelAssignmentRelease.Create(
                bootstrap.Tenant,
                bootstrap.AssignmentName,
                1,
                profileToDeployment[bootstrap.ClassifierProfile],
                profileToDeployment[bootstrap.CorrelatorProfile],
                agentDeployments,
                now,
                "bootstrapper");

            repository.AddAssignment(assignment);

            if (environment.IsProduction())
            {
                Result<ProviderVerificationProbe> probe = await verifier
                    .VerifyAsync(revision, ct)
                    .ConfigureAwait(false);
                if (probe.IsFailure)
                {
                    throw new InvalidOperationException($"Bootstrap verification failed in Production: {probe.Error.Message}");
                }

                repository.AddVerification(bootstrap.Tenant, new ProviderRevisionVerification(
                    revision.Id,
                    probe.Value.Capabilities,
                    probe.Value.RequestId,
                    now,
                    "bootstrapper"));
            }
            else
            {
                repository.AddVerification(bootstrap.Tenant, new ProviderRevisionVerification(
                    revision.Id,
                    descriptor.Capabilities,
                    "scripted-verification",
                    now,
                    "bootstrapper"));
            }

            await repository.ActivateAsync(bootstrap.Tenant, assignment.Id, now, "bootstrapper", ct).ConfigureAwait(false);

            LogBootstrapSuccess(logger, bootstrap.Tenant, bootstrap.AssignmentName, 1, assignment.ConfigurationHash);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Bootstrapped tenant '{Tenant}' assignment '{AssignmentName}' version {Version} with hash {ConfigurationHash}")]
    private static partial void LogBootstrapSuccess(
        ILogger logger,
        string tenant,
        string assignmentName,
        int version,
        string configurationHash);
}
