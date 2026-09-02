using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

internal sealed class ModelProviderRepository(SreDbContext context) : IProviderRevisionReader
{
    public async Task<IReadOnlyList<ProviderConnection>> ListConnectionsAsync(string tenant, CancellationToken cancellationToken)
        => (await context.ProviderConnections
                .Where(entity => entity.Tenant == tenant && entity.ArchivedAt == null)
                .OrderBy(entity => entity.DisplayName)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();

    public void AddConnection(ProviderConnection connection)
        => context.ProviderConnections.Add(new ProviderConnectionEntity
        {
            Id = connection.Id,
            Tenant = connection.Tenant,
            DisplayName = connection.DisplayName,
            ActiveRevisionId = connection.ActiveRevisionId,
            CreatedAt = connection.CreatedAt,
            ArchivedAt = connection.ArchivedAt,
        });

    public async Task<ProviderConnection?> FindConnectionAsync(
        string tenant,
        ProviderConnectionId id,
        CancellationToken cancellationToken)
    {
        ProviderConnectionEntity? entity = await context.ProviderConnections
            .FirstOrDefaultAsync(item => item.Tenant == tenant && item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<ProviderConnectionRevision>> ListRevisionsAsync(
        string tenant,
        ProviderConnectionId connectionId,
        CancellationToken cancellationToken)
        => (await context.ProviderConnectionRevisions
                .Where(entity => entity.Tenant == tenant && entity.ProviderConnectionId == connectionId)
                .OrderBy(entity => entity.Revision)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();

    public async Task<int> NextRevisionAsync(string tenant, ProviderConnectionId connectionId, CancellationToken cancellationToken)
        => (await context.ProviderConnectionRevisions
            .Where(entity => entity.Tenant == tenant && entity.ProviderConnectionId == connectionId)
            .Select(entity => (int?)entity.Revision)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;

    public void AddRevision(ProviderConnectionRevision revision)
        => context.ProviderConnectionRevisions.Add(ToEntity(revision));

    public async Task SetActiveRevisionAsync(
        string tenant,
        ProviderConnectionId connectionId,
        ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        // Bootstrap adds the connection and sets active in the same unit of
        // work, so the row is tracked but not yet in the database. Look at the
        // change tracker first; fall back to a database read for the ordinary
        // update path where the connection already exists.
        ProviderConnectionEntity? entity = context.ProviderConnections.Local
            .FirstOrDefault(item => item.Tenant == tenant && item.Id == connectionId);
        if (entity is null)
        {
            entity = await context.ProviderConnections
                .AsTracking()
                .SingleAsync(item => item.Tenant == tenant && item.Id == connectionId, cancellationToken)
                .ConfigureAwait(false);
        }

        entity.ActiveRevisionId = revisionId;
    }

    public async Task<ProviderConnectionRevision?> FindRevisionAsync(
        string tenant,
        ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        ProviderConnectionRevisionEntity? entity = await context.ProviderConnectionRevisions
            .FirstOrDefaultAsync(item => item.Tenant == tenant && item.Id == revisionId, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<ProviderConnectionRevision?> FindRevisionAsync(
        string tenant,
        ProviderConnectionId connectionId,
        int revision,
        CancellationToken cancellationToken)
    {
        ProviderConnectionRevisionEntity? entity = await context.ProviderConnectionRevisions
            .FirstOrDefaultAsync(
                item => item.Tenant == tenant && item.ProviderConnectionId == connectionId && item.Revision == revision,
                cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }


    public async Task<ProviderRevisionVerification?> LatestVerificationAsync(
        string tenant,
        ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        ProviderRevisionVerificationEntity? entity = await context.ProviderRevisionVerifications
            .Where(item => item.Tenant == tenant && item.ProviderConnectionRevisionId == revisionId)
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
        => (await context.ModelDeploymentVersions
                .Where(entity => entity.Tenant == tenant)
                .OrderBy(entity => entity.LogicalName)
                .ThenBy(entity => entity.Version)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();

    public async Task<ModelDeploymentVersion?> FindDeploymentAsync(
        string tenant,
        ModelDeploymentVersionId id,
        CancellationToken cancellationToken)
    {
        ModelDeploymentVersionEntity? entity = await context.ModelDeploymentVersions
            .FirstOrDefaultAsync(item => item.Tenant == tenant && item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<int> NextDeploymentVersionAsync(string tenant, string logicalName, CancellationToken cancellationToken)
        => (await context.ModelDeploymentVersions
            .Where(entity => entity.Tenant == tenant && entity.LogicalName == logicalName)
            .Select(entity => (int?)entity.Version)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;

    public void AddDeployment(ModelDeploymentVersion deployment)
        => context.ModelDeploymentVersions.Add(new ModelDeploymentVersionEntity
        {
            Id = deployment.Id,
            Tenant = deployment.Tenant,
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

    public async Task<IReadOnlyList<ModelAssignmentRelease>> ListAssignmentsAsync(
        string tenant,
        CancellationToken cancellationToken)
    {
        List<ModelAssignmentReleaseEntity> releases = await context.ModelAssignmentReleases
            .Where(entity => entity.Tenant == tenant)
            .OrderBy(entity => entity.Name)
            .ThenBy(entity => entity.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ModelAssignmentAgentSlotEntity> slots = await context.ModelAssignmentAgentSlots
            .Where(entity => entity.Tenant == tenant)
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
        ModelAssignmentReleaseEntity? entity = await context.ModelAssignmentReleases
            .FirstOrDefaultAsync(item => item.Tenant == tenant && item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        List<ModelAssignmentAgentSlotEntity> slots = await context.ModelAssignmentAgentSlots
            .Where(item => item.Tenant == tenant && item.AssignmentReleaseId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToDomain(entity, slots);
    }

    public async Task<int> NextAssignmentVersionAsync(string tenant, string name, CancellationToken cancellationToken)
        => (await context.ModelAssignmentReleases
            .Where(entity => entity.Tenant == tenant && entity.Name == name)
            .Select(entity => (int?)entity.Version)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;

    public void AddAssignment(ModelAssignmentRelease release)
    {
        context.ModelAssignmentReleases.Add(new ModelAssignmentReleaseEntity
        {
            Id = release.Id,
            Tenant = release.Tenant,
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
                Tenant = release.Tenant,
                AgentName = slot.Key,
                DeploymentVersionId = slot.Value,
            }));
    }

    public void AddEvaluation(ModelAssignmentEvaluationEntity evaluation) => context.ModelAssignmentEvaluations.Add(evaluation);
    public Task<ModelAssignmentEvaluationEntity?> FindEvaluationAsync(
        string tenant,
        ModelAssignmentEvaluationId evaluationId,
        CancellationToken cancellationToken)
        => context.ModelAssignmentEvaluations
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Tenant == tenant && item.Id == evaluationId, cancellationToken);

    public void UpdateEvaluation(ModelAssignmentEvaluationEntity evaluation) => context.ModelAssignmentEvaluations.Update(evaluation);

    public Task<bool> HasPassedEvaluationAsync(
        string tenant,
        ModelAssignmentReleaseId releaseId,
        CancellationToken cancellationToken)
        => context.ModelAssignmentEvaluations.AnyAsync(
            item => item.Tenant == tenant && item.AssignmentReleaseId == releaseId && item.Passed,
            cancellationToken);

    public async Task ActivateAsync(
        string tenant,
        ModelAssignmentReleaseId releaseId,
        DateTimeOffset activatedAt,
        string actor,
        CancellationToken cancellationToken)
    {
        TenantActiveModelAssignmentEntity? active = await context.TenantActiveModelAssignments
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Tenant == tenant, cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            context.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
            {
                Tenant = tenant,
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
        ModelAssignmentReleaseId? releaseId = await context.TenantActiveModelAssignments
            .Where(item => item.Tenant == tenant)
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
            .Where(item => item.Tenant == tenant && deploymentIds.Contains(item.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<ModelDeploymentVersionId, ModelDeploymentVersion> deployments = deploymentEntities
            .Select(ToDomain)
            .ToDictionary(item => item.Id);
        HashSet<ProviderConnectionRevisionId> revisionIds = [.. deployments.Values.Select(item => item.ProviderConnectionRevisionId)];
        Dictionary<ProviderConnectionRevisionId, ProviderConnectionRevision> revisions = (await context.ProviderConnectionRevisions
                .Where(item => item.Tenant == tenant && revisionIds.Contains(item.Id))
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
        => context.ProviderRevisionVerifications.Add(new ProviderRevisionVerificationEntity
        {
            Id = Guid.CreateVersion7(),
            Tenant = tenant,
            ProviderConnectionRevisionId = verification.ProviderConnectionRevisionId,
            VerifiedCapabilities = verification.VerifiedCapabilities,
            RequestId = verification.RequestId,
            VerifiedAt = verification.VerifiedAt,
            VerifiedBy = verification.VerifiedBy,
        });

    private static ProviderConnection ToDomain(ProviderConnectionEntity entity)
        => ProviderConnection.Restore(entity.Id, entity.Tenant, entity.DisplayName, entity.ActiveRevisionId,
            entity.CreatedAt, entity.ArchivedAt);

    private static ProviderConnectionRevision ToDomain(ProviderConnectionRevisionEntity entity)
        => ProviderConnectionRevision.Restore(entity.Id, entity.ProviderConnectionId, entity.Tenant, entity.Revision,
            entity.AdapterKind, entity.Endpoint, entity.Region, entity.ApiVersion, entity.Organization,
            entity.CredentialRef, entity.DeclaredCapabilities, entity.ConfigurationHash, entity.CreatedAt, entity.CreatedBy);

    private static ProviderConnectionRevisionEntity ToEntity(ProviderConnectionRevision revision)
        => new()
        {
            Id = revision.Id,
            ProviderConnectionId = revision.ProviderConnectionId,
            Tenant = revision.Tenant,
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
