using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.Infrastructure.Persistence.Entities;

public sealed class ProviderConnectionEntity
{
    public ProviderConnectionId Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ProviderConnectionRevisionId? ActiveRevisionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class ProviderConnectionRevisionEntity
{
    public ProviderConnectionRevisionId Id { get; set; }
    public ProviderConnectionId ProviderConnectionId { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public int Revision { get; set; }
    public ProviderAdapterKind AdapterKind { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? ApiVersion { get; set; }
    public string? Organization { get; set; }
    public string CredentialRef { get; set; } = string.Empty;
    public ModelCapability DeclaredCapabilities { get; set; }
    public string ConfigurationHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class ProviderRevisionVerificationEntity
{
    public Guid Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public ProviderConnectionRevisionId ProviderConnectionRevisionId { get; set; }
    public ModelCapability VerifiedCapabilities { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset VerifiedAt { get; set; }
    public string VerifiedBy { get; set; } = string.Empty;
}

public sealed class ModelDeploymentVersionEntity
{
    public ModelDeploymentVersionId Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string LogicalName { get; set; } = string.Empty;
    public int Version { get; set; }
    public ProviderConnectionRevisionId ProviderConnectionRevisionId { get; set; }
    public string ProviderModel { get; set; } = string.Empty;
    public ModelCapability RequiredCapabilities { get; set; }
    public float Temperature { get; set; }
    public int MaxOutputTokens { get; set; }
    public int TimeoutMilliseconds { get; set; }
    public decimal InputCostPerMillionTokens { get; set; }
    public decimal OutputCostPerMillionTokens { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ProviderParametersJson { get; set; } = "{}";
    public string ConfigurationHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class ModelAssignmentReleaseEntity
{
    public ModelAssignmentReleaseId Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public ModelDeploymentVersionId ClassifierDeploymentVersionId { get; set; }
    public ModelDeploymentVersionId CorrelatorDeploymentVersionId { get; set; }
    public string ConfigurationHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class ModelAssignmentAgentSlotEntity
{
    public ModelAssignmentReleaseId AssignmentReleaseId { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public ModelDeploymentVersionId DeploymentVersionId { get; set; }
}

public sealed class TenantActiveModelAssignmentEntity
{
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public ModelAssignmentReleaseId AssignmentReleaseId { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public string ActivatedBy { get; set; } = string.Empty;
}

public sealed class ModelAssignmentEvaluationEntity
{
    public ModelAssignmentEvaluationId Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public ModelAssignmentReleaseId AssignmentReleaseId { get; set; }
    public string Dataset { get; set; } = string.Empty;
    public string Baseline { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? ArtifactHash { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; set; }
}

// ModelControlPlaneAuditEntity is unchanged: it holds no alternate key anyone
// references and no foreign key into a widened principal key (D40's F1-2
// scope is the (Tenant, ...) key chain in ModelProviderConfigurations.cs),
// so it stays tenant-only.

public sealed class ModelControlPlaneAuditEntity
{
    public Guid Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public string ConfigurationHash { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>A tenant's model-cost budget (gemini-budgets-notifications.md B).</summary>
public sealed class TenantBudgetEntity
{
    public string Tenant { get; set; } = string.Empty;
    public decimal LimitAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}
