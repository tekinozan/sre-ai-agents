using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

internal sealed class ModelProviderRepository(SreDbContext context) : IProviderRevisionReader
{
    // Every method here still takes a bare tenant string because
    // IModelProviderControlPlane's 13 members do too — F1-5 has not cut them
    // over yet. Until that lands, every row this repository touches is
    // TenantWide by construction (D40); constructing the scope explicitly
    // here, instead of leaving Product/Environment at their CLR default, is
    // this staging point: the day a caller has a real TenantScope to pass,
    // this is the line that changes.
    private static TenantScope Scope(string tenant) => TenantScope.TenantWide(tenant);

    public async Task<IReadOnlyList<ProviderConnection>> ListConnectionsAsync(string tenant, CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return (await context.ProviderConnections
                .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product
                    && entity.Environment == scope.Environment && entity.ArchivedAt == null)
                .OrderBy(entity => entity.DisplayName)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();
    }

    public void AddConnection(ProviderConnection connection)
    {
        TenantScope scope = Scope(connection.Tenant);
        context.ProviderConnections.Add(new ProviderConnectionEntity
        {
            Id = connection.Id,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            DisplayName = connection.DisplayName,
            ActiveRevisionId = connection.ActiveRevisionId,
            CreatedAt = connection.CreatedAt,
            ArchivedAt = connection.ArchivedAt,
        });
    }

    public async Task<ProviderConnection?> FindConnectionAsync(
        string tenant,
        ProviderConnectionId id,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ProviderConnectionEntity? entity = await context.ProviderConnections
            .FirstOrDefaultAsync(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<ProviderConnectionRevision>> ListRevisionsAsync(
        string tenant,
        ProviderConnectionId connectionId,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return (await context.ProviderConnectionRevisions
                .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product
                    && entity.Environment == scope.Environment && entity.ProviderConnectionId == connectionId)
                .OrderBy(entity => entity.Revision)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();
    }

    public async Task<int> NextRevisionAsync(string tenant, ProviderConnectionId connectionId, CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return (await context.ProviderConnectionRevisions
            .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product
                && entity.Environment == scope.Environment && entity.ProviderConnectionId == connectionId)
            .Select(entity => (int?)entity.Revision)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;
    }

    public void AddRevision(ProviderConnectionRevision revision)
        => context.ProviderConnectionRevisions.Add(ToEntity(revision));

    public async Task SetActiveRevisionAsync(
        string tenant,
        ProviderConnectionId connectionId,
        ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);

        // Bootstrap adds the connection and sets active in the same unit of
        // work, so the row is tracked but not yet in the database. Look at the
        // change tracker first; fall back to a database read for the ordinary
        // update path where the connection already exists.
        ProviderConnectionEntity? entity = context.ProviderConnections.Local
            .FirstOrDefault(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.Id == connectionId);
        if (entity is null)
        {
            entity = await context.ProviderConnections
                .AsTracking()
                .SingleAsync(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                    && item.Environment == scope.Environment && item.Id == connectionId, cancellationToken)
                .ConfigureAwait(false);
        }

        entity.ActiveRevisionId = revisionId;
    }

    public async Task<ProviderConnectionRevision?> FindRevisionAsync(
        string tenant,
        ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ProviderConnectionRevisionEntity? entity = await context.ProviderConnectionRevisions
            .FirstOrDefaultAsync(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.Id == revisionId, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<ProviderConnectionRevision?> FindRevisionAsync(
        string tenant,
        ProviderConnectionId connectionId,
        int revision,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ProviderConnectionRevisionEntity? entity = await context.ProviderConnectionRevisions
            .FirstOrDefaultAsync(
                item => item.Tenant == scope.Tenant && item.Product == scope.Product && item.Environment == scope.Environment
                    && item.ProviderConnectionId == connectionId && item.Revision == revision,
                cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }


    public async Task<ProviderRevisionVerification?> LatestVerificationAsync(
        string tenant,
        ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ProviderRevisionVerificationEntity? entity = await context.ProviderRevisionVerifications
            .Where(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.ProviderConnectionRevisionId == revisionId)
            .OrderByDescending(item => item.VerifiedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return entity is null
            ? null
            : new ProviderRevisionVerification(entity.ProviderConnectionRevisionId, entity.VerifiedCapabilities,
                entity.RequestId, entity.VerifiedAt, entity.VerifiedBy);
    }

    public async Task<IReadOnlyList<ModelDeploymentVersion>> ListDeploymentVersionsAsync(
        string tenant,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return (await context.ModelDeploymentVersions
                .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product && entity.Environment == scope.Environment)
                .OrderBy(entity => entity.LogicalName)
                .ThenBy(entity => entity.Version)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();
    }

    public async Task<ModelDeploymentVersion?> FindDeploymentAsync(
        string tenant,
        ModelDeploymentVersionId id,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ModelDeploymentVersionEntity? entity = await context.ModelDeploymentVersions
            .FirstOrDefaultAsync(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<int> NextDeploymentVersionAsync(string tenant, string logicalName, CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return (await context.ModelDeploymentVersions
            .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product
                && entity.Environment == scope.Environment && entity.LogicalName == logicalName)
            .Select(entity => (int?)entity.Version)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;
    }

    public void AddDeployment(ModelDeploymentVersion deployment)
    {
        TenantScope scope = Scope(deployment.Tenant);
        context.ModelDeploymentVersions.Add(new ModelDeploymentVersionEntity
        {
            Id = deployment.Id,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            LogicalName = deployment.LogicalName,
            Version = deployment.Version,
            ProviderConnectionRevisionId = deployment.ProviderConnectionRevisionId,
            ProviderModel = deployment.ProviderModel,
            RequiredCapabilities = deployment.RequiredCapabilities,
            Temperature = deployment.Temperature,
            MaxOutputTokens = deployment.MaxOutputTokens,
            TimeoutMilliseconds = checked((int)deployment.Timeout.TotalMilliseconds),
            InputCostPerMillionTokens = deployment.InputCostPerMillionTokens,
            OutputCostPerMillionTokens = deployment.OutputCostPerMillionTokens,
            Currency = deployment.Currency,
            ProviderParametersJson = JsonbConverters.Serialize(deployment.ProviderParameters),
            ConfigurationHash = deployment.ConfigurationHash,
            CreatedAt = deployment.CreatedAt,
            CreatedBy = deployment.CreatedBy,
        });
    }

    public async Task<IReadOnlyList<ModelAssignmentRelease>> ListAssignmentsAsync(
        string tenant,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        List<ModelAssignmentReleaseEntity> releases = await context.ModelAssignmentReleases
            .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product && entity.Environment == scope.Environment)
            .OrderBy(entity => entity.Name)
            .ThenBy(entity => entity.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ModelAssignmentAgentSlotEntity> slots = await context.ModelAssignmentAgentSlots
            .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product && entity.Environment == scope.Environment)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ILookup<ModelAssignmentReleaseId, ModelAssignmentAgentSlotEntity> byRelease = slots.ToLookup(item => item.AssignmentReleaseId);
        return releases.Select(entity => ToDomain(entity, byRelease[entity.Id])).ToArray();
    }

    public async Task<ModelAssignmentRelease?> FindAssignmentAsync(
        string tenant,
        ModelAssignmentReleaseId id,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ModelAssignmentReleaseEntity? entity = await context.ModelAssignmentReleases
            .FirstOrDefaultAsync(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        List<ModelAssignmentAgentSlotEntity> slots = await context.ModelAssignmentAgentSlots
            .Where(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.AssignmentReleaseId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToDomain(entity, slots);
    }

    public async Task<int> NextAssignmentVersionAsync(string tenant, string name, CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return (await context.ModelAssignmentReleases
            .Where(entity => entity.Tenant == scope.Tenant && entity.Product == scope.Product
                && entity.Environment == scope.Environment && entity.Name == name)
            .Select(entity => (int?)entity.Version)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;
    }

    public void AddAssignment(ModelAssignmentRelease release)
    {
        TenantScope scope = Scope(release.Tenant);
        context.ModelAssignmentReleases.Add(new ModelAssignmentReleaseEntity
        {
            Id = release.Id,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            Name = release.Name,
            Version = release.Version,
            ClassifierDeploymentVersionId = release.ClassifierDeploymentVersionId,
            CorrelatorDeploymentVersionId = release.CorrelatorDeploymentVersionId,
            ConfigurationHash = release.ConfigurationHash,
            CreatedAt = release.CreatedAt,
            CreatedBy = release.CreatedBy,
        });
        context.ModelAssignmentAgentSlots.AddRange(release.AgentDeploymentVersionIds.Select(slot =>
            new ModelAssignmentAgentSlotEntity
            {
                AssignmentReleaseId = release.Id,
                Tenant = scope.Tenant,
                Product = scope.Product,
                Environment = scope.Environment,
                AgentName = slot.Key,
                DeploymentVersionId = slot.Value,
            }));
    }

    public void AddEvaluation(ModelAssignmentEvaluationEntity evaluation) => context.ModelAssignmentEvaluations.Add(evaluation);
    public Task<ModelAssignmentEvaluationEntity?> FindEvaluationAsync(
        string tenant,
        ModelAssignmentEvaluationId evaluationId,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return context.ModelAssignmentEvaluations
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment && item.Id == evaluationId, cancellationToken);
    }

    public void UpdateEvaluation(ModelAssignmentEvaluationEntity evaluation) => context.ModelAssignmentEvaluations.Update(evaluation);

    public Task<bool> HasPassedEvaluationAsync(
        string tenant,
        ModelAssignmentReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        return context.ModelAssignmentEvaluations.AnyAsync(
            item => item.Tenant == scope.Tenant && item.Product == scope.Product && item.Environment == scope.Environment
                && item.AssignmentReleaseId == releaseId && item.Passed,
            cancellationToken);
    }

    public async Task ActivateAsync(
        string tenant,
        ModelAssignmentReleaseId releaseId,
        DateTimeOffset activatedAt,
        string actor,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        TenantActiveModelAssignmentEntity? active = await context.TenantActiveModelAssignments
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Tenant == scope.Tenant && item.Product == scope.Product
                && item.Environment == scope.Environment, cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            context.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
            {
                Tenant = scope.Tenant,
                Product = scope.Product,
                Environment = scope.Environment,
                AssignmentReleaseId = releaseId,
                ActivatedAt = activatedAt,
                ActivatedBy = actor,
            });
        }
        else
        {
            active.AssignmentReleaseId = releaseId;
            active.ActivatedAt = activatedAt;
            active.ActivatedBy = actor;
        }
    }

    public async Task<PinnedModelAssignment?> GetActiveSnapshotAsync(string tenant, CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ModelAssignmentReleaseId? releaseId = await context.TenantActiveModelAssignments
            .Where(item => item.Tenant == scope.Tenant && item.Product == scope.Product && item.Environment == scope.Environment)
            .Select(item => (ModelAssignmentReleaseId?)item.AssignmentReleaseId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return releaseId is null ? null : await BuildSnapshotAsync(tenant, releaseId.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PinnedModelAssignment?> BuildSnapshotAsync(
        string tenant,
        ModelAssignmentReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        TenantScope scope = Scope(tenant);
        ModelAssignmentRelease? release = await FindAssignmentAsync(tenant, releaseId, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return null;
        }

        HashSet<ModelDeploymentVersionId> deploymentIds =
        [
            release.ClassifierDeploymentVersionId,
            release.CorrelatorDeploymentVersionId,
            .. release.AgentDeploymentVersionIds.Values,
        ];
        List<ModelDeploymentVersionEntity> deploymentEntities = await context.ModelDeploymentVersions
            .Where(item => item.Tenant == scope.Tenant && item.Product == scope.Product && item.Environment == scope.Environment
                && deploymentIds.Contains(item.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<ModelDeploymentVersionId, ModelDeploymentVersion> deployments = deploymentEntities
            .Select(ToDomain)
            .ToDictionary(item => item.Id);
        HashSet<ProviderConnectionRevisionId> revisionIds = [.. deployments.Values.Select(item => item.ProviderConnectionRevisionId)];
        Dictionary<ProviderConnectionRevisionId, ProviderConnectionRevision> revisions = (await context.ProviderConnectionRevisions
                .Where(item => item.Tenant == scope.Tenant && item.Product == scope.Product && item.Environment == scope.Environment
                    && revisionIds.Contains(item.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToDictionary(item => item.Id);

        PinnedModelCandidate? Candidate(string slot, ModelDeploymentVersionId id)
        {
            if (!deployments.TryGetValue(id, out ModelDeploymentVersion? deployment) ||
                !revisions.TryGetValue(deployment.ProviderConnectionRevisionId, out ProviderConnectionRevision? revision))
            {
                return null;
            }

            return new PinnedModelCandidate
            {
                Tenant = tenant,
                AssignmentReleaseId = release.Id,
                AssignmentConfigurationHash = release.ConfigurationHash,
                Slot = slot,
                DeploymentVersionId = deployment.Id,
                DeploymentLogicalName = deployment.LogicalName,
                DeploymentVersion = deployment.Version,
                DeploymentConfigurationHash = deployment.ConfigurationHash,
                ProviderConnectionRevisionId = revision.Id,
                ProviderConnectionRevision = revision.Revision,
                ProviderConfigurationHash = revision.ConfigurationHash,
                AdapterKind = revision.AdapterKind,
                ProviderModel = deployment.ProviderModel,
                Capabilities = deployment.RequiredCapabilities,
                Temperature = deployment.Temperature,
                MaxOutputTokens = deployment.MaxOutputTokens,
                Timeout = deployment.Timeout,
                InputCostPerMillionTokens = deployment.InputCostPerMillionTokens,
                OutputCostPerMillionTokens = deployment.OutputCostPerMillionTokens,
                Currency = deployment.Currency,
                ProviderParameters = deployment.ProviderParameters,
            };
        }

        PinnedModelCandidate? classifier = Candidate("classifier", release.ClassifierDeploymentVersionId);
        PinnedModelCandidate? correlator = Candidate("correlator", release.CorrelatorDeploymentVersionId);
        if (classifier is null || correlator is null)
        {
            return null;
        }

        Dictionary<string, PinnedModelCandidate> agents = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ModelDeploymentVersionId> slot in release.AgentDeploymentVersionIds)
        {
            PinnedModelCandidate? agent = Candidate(slot.Key, slot.Value);
            if (agent is null)
            {
                return null;
            }
            agents[slot.Key] = agent;
        }

        return new PinnedModelAssignment
        {
            ReleaseId = release.Id,
            Name = release.Name,
            Version = release.Version,
            ConfigurationHash = release.ConfigurationHash,
            Classifier = classifier,
            Correlator = correlator,
            Agents = agents
        };
    }

    public void AddAudit(
        string tenant,
        string actor,
        string action,
        string resourceType,
        Guid resourceId,
        string configurationHash,
        DateTimeOffset occurredAt)
        => context.ModelControlPlaneAudits.Add(new ModelControlPlaneAuditEntity
        {
            Id = Guid.CreateVersion7(),
            Tenant = tenant,
            Actor = actor,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            ConfigurationHash = configurationHash,
            OccurredAt = occurredAt,
        });

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);


    public void AddVerification(string tenant, ProviderRevisionVerification verification)
    {
        TenantScope scope = Scope(tenant);
        context.ProviderRevisionVerifications.Add(new ProviderRevisionVerificationEntity
        {
            Id = Guid.CreateVersion7(),
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            ProviderConnectionRevisionId = verification.ProviderConnectionRevisionId,
            VerifiedCapabilities = verification.VerifiedCapabilities,
            RequestId = verification.RequestId,
            VerifiedAt = verification.VerifiedAt,
            VerifiedBy = verification.VerifiedBy,
        });
    }

    private static ProviderConnection ToDomain(ProviderConnectionEntity entity)
        => ProviderConnection.Restore(entity.Id, entity.Tenant, entity.DisplayName, entity.ActiveRevisionId,
            entity.CreatedAt, entity.ArchivedAt);

    private static ProviderConnectionRevision ToDomain(ProviderConnectionRevisionEntity entity)
        => ProviderConnectionRevision.Restore(entity.Id, entity.ProviderConnectionId, entity.Tenant, entity.Revision,
            entity.AdapterKind, entity.Endpoint, entity.Region, entity.ApiVersion, entity.Organization,
            entity.CredentialRef, entity.DeclaredCapabilities, entity.ConfigurationHash, entity.CreatedAt, entity.CreatedBy);

    private static ProviderConnectionRevisionEntity ToEntity(ProviderConnectionRevision revision)
    {
        TenantScope scope = Scope(revision.Tenant);
        return new()
        {
            Id = revision.Id,
            ProviderConnectionId = revision.ProviderConnectionId,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            Revision = revision.Revision,
            AdapterKind = revision.AdapterKind,
            Endpoint = revision.Endpoint,
            Region = revision.Region,
            ApiVersion = revision.ApiVersion,
            Organization = revision.Organization,
            CredentialRef = revision.CredentialRef,
            DeclaredCapabilities = revision.DeclaredCapabilities,
            ConfigurationHash = revision.ConfigurationHash,
            CreatedAt = revision.CreatedAt,
            CreatedBy = revision.CreatedBy,
        };
    }

    private static ModelDeploymentVersion ToDomain(ModelDeploymentVersionEntity entity)
        => ModelDeploymentVersion.Restore(entity.Id, entity.Tenant, entity.LogicalName, entity.Version,
            entity.ProviderConnectionRevisionId, entity.ProviderModel, entity.RequiredCapabilities,
            entity.Temperature, entity.MaxOutputTokens, TimeSpan.FromMilliseconds(entity.TimeoutMilliseconds),
            entity.InputCostPerMillionTokens, entity.OutputCostPerMillionTokens, entity.Currency,
            JsonbConverters.DeserializeLabels(entity.ProviderParametersJson), entity.ConfigurationHash,
            entity.CreatedAt, entity.CreatedBy);

    private static ModelAssignmentRelease ToDomain(
        ModelAssignmentReleaseEntity entity,
        IEnumerable<ModelAssignmentAgentSlotEntity> slots)
        => ModelAssignmentRelease.Restore(entity.Id, entity.Tenant, entity.Name, entity.Version,
            entity.ClassifierDeploymentVersionId, entity.CorrelatorDeploymentVersionId,
            slots.ToDictionary(item => item.AgentName, item => item.DeploymentVersionId, StringComparer.Ordinal),
            entity.ConfigurationHash, entity.CreatedAt, entity.CreatedBy);
}
