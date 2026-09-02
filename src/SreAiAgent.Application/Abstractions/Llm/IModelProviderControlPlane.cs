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
    Task<IReadOnlyList<ProviderConnection>> ListConnectionsAsync(string tenant, CancellationToken cancellationToken);
    Task<Result<ProviderConnection>> CreateConnectionAsync(string tenant, string displayName, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderConnectionRevision>> ListRevisionsAsync(string tenant, ProviderConnectionId connectionId, CancellationToken cancellationToken);
    Task<Result<ProviderConnectionRevision>> CreateRevisionAsync(string tenant, ProviderConnectionId connectionId, CreateProviderRevisionCommand command, string actor, CancellationToken cancellationToken);
    Task<Result<ProviderRevisionVerification>> VerifyRevisionAsync(string tenant, ProviderConnectionId connectionId, int revision, string actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelDeploymentVersion>> ListDeploymentVersionsAsync(string tenant, CancellationToken cancellationToken);
    Task<Result<ModelDeploymentVersion>> CreateDeploymentVersionAsync(string tenant, CreateModelDeploymentVersionCommand command, string actor, CancellationToken cancellationToken);
    Task<Result<ModelDeploymentVersion>> CreateNextDeploymentVersionAsync(string tenant, ModelDeploymentVersionId priorVersionId, CreateModelDeploymentVersionCommand command, string actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelAssignmentRelease>> ListAssignmentReleasesAsync(string tenant, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentRelease>> CreateAssignmentReleaseAsync(string tenant, CreateModelAssignmentReleaseCommand command, string actor, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentEvaluationHandoff>> RequestEvaluationAsync(string tenant, ModelAssignmentReleaseId releaseId, string dataset, string baseline, string actor, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentEvaluationHandoff>> CompleteEvaluationAsync(string tenant, ModelAssignmentEvaluationId evaluationId, bool passed, string artifactHash, string actor, CancellationToken cancellationToken);
    Task<Result<ModelAssignmentRelease>> ActivateAsync(string tenant, ModelAssignmentReleaseId releaseId, string actor, CancellationToken cancellationToken);
}
