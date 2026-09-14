using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
using SreAiAgent.Infrastructure.Persistence;
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

        // ModelProviderControlPlane.CreateRevisionAsync enforces this same
        // guard for an operator's request; this path reaches the same
        // ProviderConnectionRevision.Create constructor without going through
        // the control plane at all, so nothing else stands between a
        // misconfigured Llm:Bootstrap:CredentialRef and a literal secret
        // landing, unresolved and unvalidated, in provider_connection_revisions.
        // Checked before any scope or transaction opens, so a bad value never
        // reaches the database to begin with -- consistent with "a
        // misconfigured process must fail at boot, loudly" above. The message
        // never repeats the value: whatever an operator pasted here might be
        // exactly the secret this guard exists to keep out of a log line.
        if (!ExternalReferenceCredentialResolver.IsWellFormedReference(bootstrap.CredentialRef))
        {
            throw new InvalidOperationException(
                "Llm:Bootstrap:CredentialRef must be an external reference (env:// or file://), never a literal secret.");
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        SreDbContext context = scope.ServiceProvider.GetRequiredService<SreDbContext>();
        ModelProviderRepository repository = scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ProviderRevisionVerifier verifier = scope.ServiceProvider.GetRequiredService<ProviderRevisionVerifier>();

        // The bootstrap tenant carries no Product dimension (D41), so
        // TenantWide is the only scope this hosted service can honestly ask
        // for — not a default standing in for data it doesn't have.
        TenantScope tenantScope = TenantScope.TenantWide(bootstrap.Tenant);

        // Fast path outside any transaction: the overwhelmingly common case is
        // that some earlier replica already bootstrapped, and this skips opening
        // one — and taking the advisory lock below — for that case entirely.
        PinnedModelAssignment? active = await repository
            .GetActiveSnapshotAsync(tenantScope, cancellationToken)
            .ConfigureAwait(false);
        if (active is not null)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // API and both workers all register this hosted service, each may run
            // several replicas, and none of them saw an active assignment above —
            // they can all reach this point at once. A PostgreSQL transaction-scoped
            // advisory lock serializes them: the first to acquire it proceeds; every
            // other contender blocks here at the database, then — once it acquires
            // the lock after the winner commits — rechecks immediately below and
            // finds the assignment already active. Scoped to the transaction
            // (`_xact_`) means it releases automatically on commit or rollback, so
            // there is never an explicit unlock or an abandoned lock to sweep.
            long lockKey = ComputeBootstrapLockKey(bootstrap.Tenant);
            await context.Database
                .ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct)
                .ConfigureAwait(false);

            PinnedModelAssignment? activeAfterLock = await repository
                .GetActiveSnapshotAsync(tenantScope, ct)
                .ConfigureAwait(false);
            if (activeAfterLock is not null)
            {
                // Lost the race: another replica created and activated the
                // assignment while this one waited for the lock. Nothing to do —
                // creating rows or running the verification probe here would
                // duplicate work the winner already committed.
                return false;
            }

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

            repository.AddConnection(tenantScope, connection);
            repository.AddRevision(tenantScope, revision);
            await repository.SaveChangesAsync(ct).ConfigureAwait(false);
            await repository.SetActiveRevisionAsync(tenantScope, connection.Id, revision.Id, ct).ConfigureAwait(false);
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

                    repository.AddDeployment(tenantScope, deployment);
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

            repository.AddAssignment(tenantScope, assignment);

            if (environment.IsProduction())
            {
                Result<ProviderVerificationProbe> probe = await verifier
                    .VerifyAsync(revision, ct)
                    .ConfigureAwait(false);
                if (probe.IsFailure)
                {
                    throw new InvalidOperationException($"Bootstrap verification failed in Production: {probe.Error.Message}");
                }

                repository.AddVerification(tenantScope, new ProviderRevisionVerification(
                    revision.Id,
                    probe.Value.Capabilities,
                    probe.Value.RequestId,
                    now,
                    "bootstrapper"));
            }
            else
            {
                repository.AddVerification(tenantScope, new ProviderRevisionVerification(
                    revision.Id,
                    descriptor.Capabilities,
                    "scripted-verification",
                    now,
                    "bootstrapper"));
            }

            await repository.ActivateAsync(tenantScope, assignment.Id, now, "bootstrapper", ct).ConfigureAwait(false);

            LogBootstrapSuccess(logger, bootstrap.Tenant, bootstrap.AssignmentName, 1, assignment.ConfigurationHash);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Derives a stable 64-bit <c>pg_advisory_xact_lock</c> key from the bootstrap
    /// tenant, so every replica bootstrapping the same tenant contends for the
    /// same lock.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="string.GetHashCode()"/>: .NET randomizes string
    /// hash codes per process by default, so two pods would compute two different
    /// keys for the same tenant and never actually contend for one lock. SHA-256
    /// is a fixed, versionless algorithm — the same tenant always yields the same
    /// key, in this process, in any other process, and in any future one.
    /// </remarks>
    private static long ComputeBootstrapLockKey(string tenant)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"sre-ai-agent:model-assignment-bootstrap:{tenant}"));
        return BitConverter.ToInt64(hash, 0);
    }

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
