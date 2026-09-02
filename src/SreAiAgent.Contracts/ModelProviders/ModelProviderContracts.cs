using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.Contracts.ModelProviders;

/// <summary>
/// Body of <c>POST /v1/model-providers</c>. A connection is created empty; a
/// caller then attaches revisions.
/// </summary>
/// <param name="DisplayName">
/// Operator-visible label, unique per tenant, e.g. <c>"acme-openai"</c>. The
/// value is not routed on: the runtime resolves a provider by revision id, not
/// by name.
/// </param>
public sealed record CreateModelProviderRequest(string DisplayName);

/// <summary>A provider connection, as returned by the management API.</summary>
public sealed record ModelProviderConnectionResponse
{
    public required ProviderConnectionId ConnectionId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// The revision the runtime resolves for this connection today, or null when
    /// no revision has been created yet. Only set by <c>CreateRevisionAsync</c>
    /// and the release activation path; never editable in isolation.
    /// </summary>
    public ProviderConnectionRevisionId? ActiveRevisionId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>
/// Body of <c>POST /v1/model-providers/{connectionId}/revisions</c>.
/// </summary>
/// <remarks>
/// There is deliberately <b>no</b> <c>api_key</c> field. Credentials never
/// travel over the API and never land in the database; the caller pins one by
/// external reference (<c>env://VARIABLE</c> or <c>file://relative/path</c>) and
/// the runtime resolves it at request time. A raw key posted here is refused
/// with <c>MODEL_CONFIGURATION_INVALID</c>.
/// </remarks>
public sealed record CreateProviderRevisionRequest
{
    /// <summary>
    /// One of <c>scripted</c>, <c>openai</c>, <c>openai_compatible</c>,
    /// <c>azure_openai</c> or <c>anthropic</c> (case-insensitive).
    /// </summary>
    public required string AdapterKind { get; init; }

    public required string Endpoint { get; init; }

    public string? Region { get; init; }

    public string? ApiVersion { get; init; }

    public string? Organization { get; init; }

    /// <summary>External reference to the secret; never the secret itself.</summary>
    public required string CredentialRef { get; init; }

    /// <summary>
    /// Capabilities the operator declares for this revision, subset of the
    /// adapter's descriptor. Snake-case tokens, e.g.
    /// <c>["structured_output", "system_message"]</c>.
    /// </summary>
    public IReadOnlyList<string>? DeclaredCapabilities { get; init; }
}

/// <summary>An immutable provider revision, as returned by the management API.</summary>
public sealed record ProviderConnectionRevisionResponse
{
    public required ProviderConnectionRevisionId RevisionId { get; init; }

    public required ProviderConnectionId ConnectionId { get; init; }

    public required int Revision { get; init; }

    public required string AdapterKind { get; init; }

    public required string Endpoint { get; init; }

    public string? Region { get; init; }

    public string? ApiVersion { get; init; }

    public string? Organization { get; init; }

    /// <summary>Reference form only; never a raw secret.</summary>
    public required string CredentialRef { get; init; }

    public required IReadOnlyList<string> DeclaredCapabilities { get; init; }

    public required string ConfigurationHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

/// <summary>
/// Result of <c>POST /v1/model-providers/{connectionId}/revisions/{revision}/verify</c>.
/// </summary>
public sealed record ProviderRevisionVerificationResponse
{
    public required ProviderConnectionRevisionId RevisionId { get; init; }

    public required IReadOnlyList<string> VerifiedCapabilities { get; init; }

    public required string RequestId { get; init; }

    public required DateTimeOffset VerifiedAt { get; init; }

    public required string VerifiedBy { get; init; }
}

/// <summary>Body of <c>POST /v1/model-deployments</c> and its next-version sibling.</summary>
public sealed record CreateModelDeploymentRequest
{
    public required string LogicalName { get; init; }

    public required ProviderConnectionRevisionId ProviderConnectionRevisionId { get; init; }

    public required string ProviderModel { get; init; }

    public IReadOnlyList<string>? RequiredCapabilities { get; init; }

    public required float Temperature { get; init; }

    public required int MaxOutputTokens { get; init; }

    /// <summary>Per-call deadline, in seconds. Must be positive.</summary>
    public required int TimeoutSeconds { get; init; }

    public required decimal InputCostPerMillionTokens { get; init; }

    public required decimal OutputCostPerMillionTokens { get; init; }

    public required string Currency { get; init; }

    public IReadOnlyDictionary<string, string>? ProviderParameters { get; init; }
}

/// <summary>An immutable model deployment version.</summary>
public sealed record ModelDeploymentVersionResponse
{
    public required ModelDeploymentVersionId DeploymentVersionId { get; init; }

    public required string LogicalName { get; init; }

    public required int Version { get; init; }

    public required ProviderConnectionRevisionId ProviderConnectionRevisionId { get; init; }

    public required string ProviderModel { get; init; }

    public required IReadOnlyList<string> RequiredCapabilities { get; init; }

    public required float Temperature { get; init; }

    public required int MaxOutputTokens { get; init; }

    public required int TimeoutSeconds { get; init; }

    public required decimal InputCostPerMillionTokens { get; init; }

    public required decimal OutputCostPerMillionTokens { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyDictionary<string, string> ProviderParameters { get; init; }

    public required string ConfigurationHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

/// <summary>Body of <c>POST /v1/model-assignment-releases</c>.</summary>
public sealed record CreateModelAssignmentReleaseRequest
{
    public required string Name { get; init; }

    public required ModelDeploymentVersionId ClassifierDeploymentVersionId { get; init; }

    public required ModelDeploymentVersionId CorrelatorDeploymentVersionId { get; init; }

    /// <summary>Agent name → deployment version id.</summary>
    public required IReadOnlyDictionary<string, ModelDeploymentVersionId> AgentDeploymentVersionIds { get; init; }
}

/// <summary>An immutable model assignment release.</summary>
public sealed record ModelAssignmentReleaseResponse
{
    public required ModelAssignmentReleaseId ReleaseId { get; init; }

    public required string Name { get; init; }

    public required int Version { get; init; }

    public required ModelDeploymentVersionId ClassifierDeploymentVersionId { get; init; }

    public required ModelDeploymentVersionId CorrelatorDeploymentVersionId { get; init; }

    public required IReadOnlyDictionary<string, ModelDeploymentVersionId> AgentDeploymentVersionIds { get; init; }

    public required string ConfigurationHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

/// <summary>Body of <c>POST /v1/model-assignment-releases/{id}/evaluate</c>.</summary>
public sealed record RequestEvaluationRequest(string Dataset, string Baseline);

/// <summary>
/// Body of <c>POST /v1/model-assignment-releases/{id}/evaluations/{evaluationId}</c>:
/// the evaluation CI job hands its verdict back to the control plane.
/// </summary>
public sealed record CompleteEvaluationRequest
{
    public required bool Passed { get; init; }

    public required string ArtifactHash { get; init; }
}

/// <summary>An evaluation handoff record, as returned by request/complete.</summary>
public sealed record ModelAssignmentEvaluationResponse
{
    public required ModelAssignmentEvaluationId EvaluationId { get; init; }

    public required ModelAssignmentReleaseId ReleaseId { get; init; }

    public required string Dataset { get; init; }

    public required string Baseline { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
}
