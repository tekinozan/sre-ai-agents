using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.TestKit;

public static class PinnedModelFixtures
{
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static PinnedModelAssignment Scripted(string tenant = "acme")
    {
        var releaseId = new ModelAssignmentReleaseId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var providerConnectionId = new ProviderConnectionId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var providerRevisionId = new ProviderConnectionRevisionId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var deploymentClassifierId = new ModelDeploymentVersionId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var deploymentCorrelatorId = new ModelDeploymentVersionId(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var deploymentDatabaseExpertId = new ModelDeploymentVersionId(Guid.Parse("66666666-6666-6666-6666-666666666666"));

        var adapterKind = ProviderAdapterKind.Scripted;
        var declaredCapabilities = ModelCapability.StructuredOutput | ModelCapability.SystemMessage | ModelCapability.ToolCalling;

        string providerHashCanonical = string.Join('|',
            tenant, providerConnectionId.ToString(), "1", adapterKind,
            "https://scripted", "", "", "", "test-cred", ((int)declaredCapabilities).ToString(CultureInfo.InvariantCulture));
        string providerHash = Hash(providerHashCanonical);

        string ComputeDeploymentHash(string logicalName, int maxTokens)
        {
            string canonical = string.Join('|', tenant, logicalName, "1", providerRevisionId,
                "scripted-model", ((int)declaredCapabilities).ToString(CultureInfo.InvariantCulture), "0.5",
                maxTokens.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(30).TotalMilliseconds.ToString(CultureInfo.InvariantCulture),
                "1.00", "2.00", "USD", "");
            return Hash(canonical);
        }

        string classifierDepHash = ComputeDeploymentHash("classifier", 1000);
        string correlatorDepHash = ComputeDeploymentHash("correlator", 2000);
        string dbExpertDepHash = ComputeDeploymentHash("database-expert", 4000);

        string assignmentCanonical = string.Join('|', tenant, "bootstrap", "1", deploymentClassifierId,
            deploymentCorrelatorId, "database-expert=" + deploymentDatabaseExpertId);
        string assignmentHash = Hash(assignmentCanonical);

        PinnedModelCandidate CreateCandidate(string slot, ModelDeploymentVersionId depId, string logicalName, string depHash, int maxTokens) => new()
        {
            Tenant = tenant,
            AssignmentReleaseId = releaseId,
            AssignmentConfigurationHash = assignmentHash,
            Slot = slot,
            DeploymentVersionId = depId,
            DeploymentLogicalName = logicalName,
            DeploymentVersion = 1,
            DeploymentConfigurationHash = depHash,
            ProviderConnectionRevisionId = providerRevisionId,
            ProviderConnectionRevision = 1,
            ProviderConfigurationHash = providerHash,
            AdapterKind = adapterKind,
            ProviderModel = "scripted-model",
            Capabilities = declaredCapabilities,
            Temperature = 0.5f,
            MaxOutputTokens = maxTokens,
            Timeout = TimeSpan.FromSeconds(30),
            InputCostPerMillionTokens = 1.00m,
            OutputCostPerMillionTokens = 2.00m,
            Currency = "USD",
            ProviderParameters = FrozenDictionary<string, string>.Empty
        };

        var classifier = CreateCandidate("classifier", deploymentClassifierId, "classifier", classifierDepHash, 1000);
        var correlator = CreateCandidate("correlator", deploymentCorrelatorId, "correlator", correlatorDepHash, 2000);
        var dbExpert = CreateCandidate("database-expert", deploymentDatabaseExpertId, "database-expert", dbExpertDepHash, 4000);

        return new PinnedModelAssignment
        {
            ReleaseId = releaseId,
            Name = "bootstrap",
            Version = 1,
            ConfigurationHash = assignmentHash,
            Classifier = classifier,
            Correlator = correlator,
            Agents = new Dictionary<string, PinnedModelCandidate>(StringComparer.Ordinal)
            {
                { "database-expert", dbExpert }
            }.ToFrozenDictionary(StringComparer.Ordinal)
        };
    }
}
