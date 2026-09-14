using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Llm;

public sealed record CreateProviderRevisionCommand(
    ProviderAdapterKind AdapterKind,
    string Endpoint,
    string? Region,
    string? ApiVersion,
    string? Organization,
    string CredentialRef,
    ModelCapability DeclaredCapabilities);

public sealed record CreateModelDeploymentVersionCommand(
    string LogicalName,
    ProviderConnectionRevisionId ProviderConnectionRevisionId,
    string ProviderModel,
    ModelCapability RequiredCapabilities,
    float Temperature,
    int MaxOutputTokens,
    TimeSpan Timeout,
    decimal InputCostPerMillionTokens,
    decimal OutputCostPerMillionTokens,
    string Currency,
    IReadOnlyDictionary<string, string> ProviderParameters);

public sealed record CreateModelAssignmentReleaseCommand(
    string Name,
    ModelDeploymentVersionId ClassifierDeploymentVersionId,
    ModelDeploymentVersionId CorrelatorDeploymentVersionId,
    IReadOnlyDictionary<string, ModelDeploymentVersionId> AgentDeploymentVersionIds);

public sealed record ProviderRevisionVerification(
    ProviderConnectionRevisionId ProviderConnectionRevisionId,
    ModelCapability VerifiedCapabilities,
    string RequestId,
    DateTimeOffset VerifiedAt,
    string VerifiedBy);

public sealed record ModelAssignmentEvaluationHandoff(
    ModelAssignmentEvaluationId EvaluationId,
    ModelAssignmentReleaseId ReleaseId,
    string Dataset,
    string Baseline,
    string Status,
    DateTimeOffset RequestedAt);

public interface IModelProviderControlPlane
{
    Task<IReadOnlyList<ProviderConnection>> ListConnectionsAsync(TenantScope scope, CancellationToken cancellationToken);
    Task<Result<ProviderConnection>> CreateConnectionAsync(TenantScope scope, string displayName, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderConnectionRevision>> ListRevisionsAsync(TenantScope scope, ProviderConnectionId connectionId, CancellationToken cancellationToken);
    Task<Result<ProviderConnectionRevision>> CreateRevisionAsync(TenantScope scope, ProviderConnectionId connectionId, CreateProviderRevisionCommand command, string actor, CancellationToken cancellationToken);
    Task<Result<ProviderRevisionVerification>> VerifyRevisionAsync(TenantScope scope, ProviderConnectionId connectionId, int revision, string actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelDeploymentVersion>> ListDeploymentVersionsAsync(TenantScope scope, CancellationToken cancellationToken);
    Task<Result<ModelDeploymentVersion>> CreateDeploymentVersionAsync(TenantScope scope, CreateModelDeploymentVersionCommand command, string actor, CancellationToken cancellationToken);
    Task<Result<ModelDeploymentVersion>> CreateNextDeploymentVersionAsync(TenantScope scope, ModelDeploymentVersionId priorVersionId, CreateModelDeploymentVersionCommand command, string actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelAssignmentRelease>> ListAssignmentReleasesAsync(TenantScope scope, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentRelease>> CreateAssignmentReleaseAsync(TenantScope scope, CreateModelAssignmentReleaseCommand command, string actor, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentEvaluationHandoff>> RequestEvaluationAsync(TenantScope scope, ModelAssignmentReleaseId releaseId, string dataset, string baseline, string actor, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentEvaluationHandoff>> CompleteEvaluationAsync(TenantScope scope, ModelAssignmentEvaluationId evaluationId, bool passed, string artifactHash, string actor, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentRelease>> ActivateAsync(TenantScope scope, ModelAssignmentReleaseId releaseId, string actor, CancellationToken cancellationToken);
}
