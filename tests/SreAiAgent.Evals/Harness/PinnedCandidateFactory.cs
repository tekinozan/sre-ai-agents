using System.Collections.Frozen;
using System.Globalization;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.Evals.Harness;

internal static class PinnedCandidateFactory
{
    public static async Task<(PinnedModelAssignment Assignment, ProviderConnectionRevision Revision)> AssignmentAsync(
        CandidateSnapshot snapshot,
        IEnumerable<string> agentNames,
        IModelProfileRegistry models,
        IAgentRegistry agents,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var connectionId = ProviderConnectionId.New();
        var revisionVersion = int.Parse(snapshot.ProviderConnectionRevision.Version, CultureInfo.InvariantCulture);
        var adapterKind = ParseAdapter(snapshot.ModelDeployment.Provider);

        var revision = ProviderConnectionRevision.Create(
            connectionId,
            snapshot.Tenant,
            revisionVersion,
            adapterKind,
            "https://scripted.invalid",
            null,
            null,
            null,
            "env://MOCK",
            ModelCapability.StructuredOutput | ModelCapability.SystemMessage | ModelCapability.ToolCalling,
            timeProvider.GetUtcNow(),
            "eval_harness");

        var releaseVersion = int.Parse(snapshot.AssignmentRelease.Version, CultureInfo.InvariantCulture);

        var classifierProfile = (await models.ResolveAsync(snapshot.ModelRelease.Id, "fast", cancellationToken)).Value;
        var correlatorProfile = (await models.ResolveAsync(snapshot.ModelRelease.Id, "analytical", cancellationToken)).Value;

        var classifierDep = CreateDeployment(snapshot, "classifier", classifierProfile, revision, timeProvider);
        var correlatorDep = CreateDeployment(snapshot, "correlator", correlatorProfile, revision, timeProvider);

        var agentDeps = new Dictionary<string, ModelDeploymentVersion>();
        var pinnedAgents = new Dictionary<string, PinnedModelCandidate>();

        foreach (string name in agentNames.Distinct())
        {
            var agentDef = (await agents.ResolveAsync(snapshot.AgentRelease.Id, name, cancellationToken)).Value;
            var agentProfile = (await models.ResolveAsync(snapshot.ModelRelease.Id, agentDef.ModelProfileRef, cancellationToken)).Value;
            var dep = CreateDeployment(snapshot, name, agentProfile, revision, timeProvider);
            agentDeps[name] = dep;
        }

        var release = ModelAssignmentRelease.Create(
            snapshot.Tenant,
            snapshot.AssignmentRelease.Id,
            releaseVersion,
            classifierDep.Id,
            correlatorDep.Id,
            agentDeps.ToDictionary(k => k.Key, v => v.Value.Id).AsReadOnly(),
            timeProvider.GetUtcNow(),
            "eval_harness");

        foreach (var kvp in agentDeps)
        {
            pinnedAgents[kvp.Key] = CreateCandidate(snapshot, kvp.Key, kvp.Value, revision, release);
        }

        PinnedModelAssignment assignment = new()
        {
            ReleaseId = release.Id,
            Name = snapshot.AssignmentRelease.Id,
            Version = releaseVersion,
            ConfigurationHash = release.ConfigurationHash,
            Classifier = CreateCandidate(snapshot, "classifier", classifierDep, revision, release),
            Correlator = CreateCandidate(snapshot, "correlator", correlatorDep, revision, release),
            Agents = pinnedAgents.ToFrozenDictionary()
        };
        return (assignment, revision);
    }

    private static ModelDeploymentVersion CreateDeployment(
        CandidateSnapshot snapshot,
        string logicalName,
        ModelProfile profile,
        ProviderConnectionRevision revision,
        TimeProvider timeProvider)
    {
        return ModelDeploymentVersion.Create(
            snapshot.Tenant,
            logicalName,
            1, // version
            revision.Id,
            snapshot.ModelDeployment.Model,
            revision.DeclaredCapabilities, // require everything declared for the scripted mock
            (float)snapshot.Generation.Temperature,
            snapshot.Generation.MaximumOutputTokens,
            TimeSpan.FromSeconds(snapshot.RunSettings.TimeoutSeconds),
            profile.InputCostPerMillionTokens,
            profile.OutputCostPerMillionTokens,
            "USD",
            FrozenDictionary<string, string>.Empty,
            timeProvider.GetUtcNow(),
            "eval_harness");
    }

    private static PinnedModelCandidate CreateCandidate(
        CandidateSnapshot snapshot,
        string slot,
        ModelDeploymentVersion dep,
        ProviderConnectionRevision rev,
        ModelAssignmentRelease release)
    {
        return new PinnedModelCandidate
        {
            Tenant = snapshot.Tenant,
            AssignmentReleaseId = release.Id,
            AssignmentConfigurationHash = release.ConfigurationHash,
            Slot = slot,
            DeploymentVersionId = dep.Id,
            DeploymentLogicalName = dep.LogicalName,
            DeploymentVersion = dep.Version,
            DeploymentConfigurationHash = dep.ConfigurationHash,
            ProviderConnectionRevisionId = rev.Id,
            ProviderConnectionRevision = rev.Revision,
            ProviderConfigurationHash = rev.ConfigurationHash,
            AdapterKind = rev.AdapterKind,
            ProviderModel = dep.ProviderModel,
            Capabilities = dep.RequiredCapabilities,
            Temperature = dep.Temperature,
            MaxOutputTokens = dep.MaxOutputTokens,
            Timeout = dep.Timeout,
            InputCostPerMillionTokens = dep.InputCostPerMillionTokens,
            OutputCostPerMillionTokens = dep.OutputCostPerMillionTokens,
            Currency = dep.Currency,
            ProviderParameters = dep.ProviderParameters
        };
    }

    private static ProviderAdapterKind ParseAdapter(string provider)
        => provider.ToLowerInvariant() switch
        {
            "scripted" => ProviderAdapterKind.Scripted,
            "openai" => ProviderAdapterKind.OpenAi,
            "openai_compatible" => ProviderAdapterKind.OpenAiCompatible,
            "azure_openai" => ProviderAdapterKind.AzureOpenAi,
            "anthropic" => ProviderAdapterKind.Anthropic,
            _ => throw new InvalidDataException($"Unknown pinned provider adapter '{provider}'."),
        };
}
