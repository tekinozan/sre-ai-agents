using System.Security.Claims;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.ModelProviders;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Endpoints;

/// <summary>
/// The tenant model-provider management surface of ARCHITECTURE.md 12.1.
/// </summary>
/// <remarks>
/// Every route delegates to <see cref="IModelProviderControlPlane"/> and
/// passes the caller's <see cref="TenantScope"/> (D40, F1-5), derived from
/// validated claims via <c>principal.TenantScope()</c>. The scope is never
/// taken from a route, body or header — a scope a caller could choose is not
/// an isolation boundary, and the same guarantee that keeps one customer's
/// incidents away from another's must hold for their model configuration,
/// down to which product and environment it applies to.
/// </remarks>
public static class ModelProviderEndpoints
{
    private const int DefaultReadLimit = 100;
    private const int EvaluationSummaryLimit = 256 * 1024;

    /// <summary>Maps the model-provider management endpoints.</summary>
    public static IEndpointRouteBuilder MapModelProviderEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder providers = builder.MapGroup("/v1").WithTags("Model providers");

        providers.MapGet("/model-providers", ListConnectionsAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Lists the tenant's provider connections.");

        providers.MapPost("/model-providers", CreateConnectionAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Creates a provider connection.");

        providers.MapGet("/model-providers/{connectionId:guid}/revisions", ListRevisionsAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Lists the immutable revisions of a provider connection.");

        providers.MapPost("/model-providers/{connectionId:guid}/revisions", CreateRevisionAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Creates a new immutable revision on a provider connection.");

        providers.MapPost("/model-providers/{connectionId:guid}/revisions/{revision:int}/verify", VerifyRevisionAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Verifies a provider revision against the live endpoint.");

        providers.MapGet("/model-deployments", ListDeploymentsAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Lists deployment versions; optionally filtered by logical name.");

        providers.MapPost("/model-deployments", CreateDeploymentAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Creates the first version of a model deployment.");

        providers.MapPost("/model-deployments/{deploymentVersionId:guid}/versions", CreateNextDeploymentAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Creates the next version of an existing model deployment.");

        providers.MapGet("/model-assignment-releases", ListAssignmentReleasesAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Lists the tenant's model assignment releases.");

        providers.MapPost("/model-assignment-releases", CreateAssignmentReleaseAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Creates an immutable model assignment release.");

        providers.MapPost("/model-assignment-releases/{releaseId:guid}/evaluate", RequestEvaluationAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Requests an evaluation for an assignment release.");

        providers.MapPost("/model-assignment-releases/{releaseId:guid}/evaluations/{evaluationId:guid}", CompleteEvaluationAsync)
            .RequireAuthorization(SrePolicies.CompleteModelEvaluations)
            .Accepts<object>("application/json")
            .WithSummary("Completes an evaluation with the harness's summary.json, sent unchanged as the body.");

        providers.MapPost("/model-assignment-releases/{releaseId:guid}/activate", ActivateAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Activates an assignment release for the tenant.");

        return builder;
    }

    private static async Task<IResult> ListConnectionsAsync(
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderConnection> connections = await controlPlane
            .ListConnectionsAsync(principal.TenantScope(), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ModelProviderConnectionResponse> response = connections
            .Take(DefaultReadLimit)
            .Select(ToResponse)
            .ToArray();
        return Results.Json(response);
    }

    private static async Task<IResult> CreateConnectionAsync(
        CreateModelProviderRequest request,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<ProviderConnection> result = await controlPlane
            .CreateConnectionAsync(principal.TenantScope(), request.DisplayName, principal.Actor(), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value), statusCode: StatusCodes.Status201Created)
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> ListRevisionsAsync(
        Guid connectionId,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderConnectionRevision> revisions = await controlPlane
            .ListRevisionsAsync(principal.TenantScope(), ProviderConnectionId.From(connectionId), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ProviderConnectionRevisionResponse> response = revisions
            .Take(DefaultReadLimit)
            .Select(ToResponse)
            .ToArray();
        return Results.Json(response);
    }

    private static async Task<IResult> CreateRevisionAsync(
        Guid connectionId,
        CreateProviderRevisionRequest request,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseAdapterKind(request.AdapterKind, out ProviderAdapterKind adapterKind))
        {
            return InvalidAdapterKind(request.AdapterKind);
        }

        if (!TryParseCapabilities(request.DeclaredCapabilities, out ModelCapability declared, out string? invalidCapability))
        {
            return InvalidCapability(invalidCapability);
        }

        CreateProviderRevisionCommand command = new(
            adapterKind,
            request.Endpoint,
            request.Region,
            request.ApiVersion,
            request.Organization,
            request.CredentialRef,
            declared);

        Result<ProviderConnectionRevision> result = await controlPlane
            .CreateRevisionAsync(
                principal.TenantScope(),
                ProviderConnectionId.From(connectionId),
                command,
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value), statusCode: StatusCodes.Status201Created)
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> VerifyRevisionAsync(
        Guid connectionId,
        int revision,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        Result<ProviderRevisionVerification> result = await controlPlane
            .VerifyRevisionAsync(
                principal.TenantScope(),
                ProviderConnectionId.From(connectionId),
                revision,
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value))
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> ListDeploymentsAsync(
        string? logicalName,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelDeploymentVersion> deployments = await controlPlane
            .ListDeploymentVersionsAsync(principal.TenantScope(), cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<ModelDeploymentVersion> filtered = string.IsNullOrWhiteSpace(logicalName)
            ? deployments
            : deployments.Where(item => string.Equals(item.LogicalName, logicalName, StringComparison.Ordinal));

        IReadOnlyList<ModelDeploymentVersionResponse> response = filtered
            .Take(DefaultReadLimit)
            .Select(ToResponse)
            .ToArray();
        return Results.Json(response);
    }

    private static async Task<IResult> CreateDeploymentAsync(
        CreateModelDeploymentRequest request,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<CreateModelDeploymentVersionCommand> command = BuildDeploymentCommand(request);
        if (command.IsFailure)
        {
            return ResultExtensions.Problem(command.Error);
        }

        Result<ModelDeploymentVersion> result = await controlPlane
            .CreateDeploymentVersionAsync(principal.TenantScope(), command.Value, principal.Actor(), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value), statusCode: StatusCodes.Status201Created)
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> CreateNextDeploymentAsync(
        Guid deploymentVersionId,
        CreateModelDeploymentRequest request,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<CreateModelDeploymentVersionCommand> command = BuildDeploymentCommand(request);
        if (command.IsFailure)
        {
            return ResultExtensions.Problem(command.Error);
        }

        Result<ModelDeploymentVersion> result = await controlPlane
            .CreateNextDeploymentVersionAsync(
                principal.TenantScope(),
                ModelDeploymentVersionId.From(deploymentVersionId),
                command.Value,
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value), statusCode: StatusCodes.Status201Created)
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> ListAssignmentReleasesAsync(
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelAssignmentRelease> releases = await controlPlane
            .ListAssignmentReleasesAsync(principal.TenantScope(), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ModelAssignmentReleaseResponse> response = releases
            .Take(DefaultReadLimit)
            .Select(ToResponse)
            .ToArray();
        return Results.Json(response);
    }

    private static async Task<IResult> CreateAssignmentReleaseAsync(
        CreateModelAssignmentReleaseRequest request,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CreateModelAssignmentReleaseCommand command = new(
            request.Name,
            request.ClassifierDeploymentVersionId,
            request.CorrelatorDeploymentVersionId,
            request.AgentDeploymentVersionIds);

        Result<ModelAssignmentRelease> result = await controlPlane
            .CreateAssignmentReleaseAsync(principal.TenantScope(), command, principal.Actor(), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value), statusCode: StatusCodes.Status201Created)
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> RequestEvaluationAsync(
        Guid releaseId,
        RequestEvaluationRequest request,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<ModelAssignmentEvaluationHandoff> result = await controlPlane
            .RequestEvaluationAsync(
                principal.TenantScope(),
                ModelAssignmentReleaseId.From(releaseId),
                request.Dataset,
                request.Baseline,
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value))
            : ResultExtensions.Problem(result.Error);
    }

    /// <remarks>
    /// The body is read as bytes, not bound to a type: the artifact hash is the
    /// SHA-256 of exactly what the harness wrote, so the CI job can compare it
    /// with <c>sha256sum summary.json</c> (D50).
    /// </remarks>
    private static async Task<IResult> CompleteEvaluationAsync(
        Guid releaseId,
        Guid evaluationId,
        HttpRequest request,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > EvaluationSummaryLimit)
        {
            return ResultExtensions.Problem(Error.Permanent(
                ApplicationErrorCodes.ModelEvaluationSummaryInvalid, "The body must be the harness's summary.json."));
        }

        using MemoryStream body = new();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > EvaluationSummaryLimit)
            {
                return ResultExtensions.Problem(Error.Permanent(
                    ApplicationErrorCodes.ModelEvaluationSummaryInvalid, "The body must be the harness's summary.json."));
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        Result<ModelAssignmentEvaluationHandoff> result = await controlPlane
            .CompleteEvaluationAsync(
                principal.TenantScope(),
                ModelAssignmentReleaseId.From(releaseId),
                ModelAssignmentEvaluationId.From(evaluationId),
                body.GetBuffer().AsMemory(0, (int)body.Length),
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value))
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> ActivateAsync(
        Guid releaseId,
        ClaimsPrincipal principal,
        IModelProviderControlPlane controlPlane,
        CancellationToken cancellationToken)
    {
        Result<ModelAssignmentRelease> result = await controlPlane
            .ActivateAsync(
                principal.TenantScope(),
                ModelAssignmentReleaseId.From(releaseId),
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value))
            : ResultExtensions.Problem(result.Error);
    }

    // ---- Mapping ---------------------------------------------------------

    private static ModelProviderConnectionResponse ToResponse(ProviderConnection connection) => new()
    {
        ConnectionId = connection.Id,
        DisplayName = connection.DisplayName,
        ActiveRevisionId = connection.ActiveRevisionId,
        CreatedAt = connection.CreatedAt,
        ArchivedAt = connection.ArchivedAt,
    };

    private static ProviderConnectionRevisionResponse ToResponse(ProviderConnectionRevision revision) => new()
    {
        RevisionId = revision.Id,
        ConnectionId = revision.ProviderConnectionId,
        Revision = revision.Revision,
        AdapterKind = FormatAdapterKind(revision.AdapterKind),
        Endpoint = revision.Endpoint,
        Region = revision.Region,
        ApiVersion = revision.ApiVersion,
        Organization = revision.Organization,
        CredentialRef = revision.CredentialRef,
        DeclaredCapabilities = FormatCapabilities(revision.DeclaredCapabilities),
        ConfigurationHash = revision.ConfigurationHash,
        CreatedAt = revision.CreatedAt,
        CreatedBy = revision.CreatedBy,
    };

    private static ProviderRevisionVerificationResponse ToResponse(ProviderRevisionVerification verification) => new()
    {
        RevisionId = verification.ProviderConnectionRevisionId,
        VerifiedCapabilities = FormatCapabilities(verification.VerifiedCapabilities),
        RequestId = verification.RequestId,
        VerifiedAt = verification.VerifiedAt,
        VerifiedBy = verification.VerifiedBy,
    };

    private static ModelDeploymentVersionResponse ToResponse(ModelDeploymentVersion deployment) => new()
    {
        DeploymentVersionId = deployment.Id,
        LogicalName = deployment.LogicalName,
        Version = deployment.Version,
        ProviderConnectionRevisionId = deployment.ProviderConnectionRevisionId,
        ProviderModel = deployment.ProviderModel,
        RequiredCapabilities = FormatCapabilities(deployment.RequiredCapabilities),
        Temperature = deployment.Temperature,
        MaxOutputTokens = deployment.MaxOutputTokens,
        TimeoutSeconds = checked((int)deployment.Timeout.TotalSeconds),
        InputCostPerMillionTokens = deployment.InputCostPerMillionTokens,
        OutputCostPerMillionTokens = deployment.OutputCostPerMillionTokens,
        Currency = deployment.Currency,
        ProviderParameters = deployment.ProviderParameters,
        ConfigurationHash = deployment.ConfigurationHash,
        CreatedAt = deployment.CreatedAt,
        CreatedBy = deployment.CreatedBy,
    };

    private static ModelAssignmentReleaseResponse ToResponse(ModelAssignmentRelease release) => new()
    {
        ReleaseId = release.Id,
        Name = release.Name,
        Version = release.Version,
        ClassifierDeploymentVersionId = release.ClassifierDeploymentVersionId,
        CorrelatorDeploymentVersionId = release.CorrelatorDeploymentVersionId,
        AgentDeploymentVersionIds = release.AgentDeploymentVersionIds,
        ConfigurationHash = release.ConfigurationHash,
        CreatedAt = release.CreatedAt,
        CreatedBy = release.CreatedBy,
    };

    private static ModelAssignmentEvaluationResponse ToResponse(ModelAssignmentEvaluationHandoff handoff) => new()
    {
        EvaluationId = handoff.EvaluationId,
        ReleaseId = handoff.ReleaseId,
        Dataset = handoff.Dataset,
        Baseline = handoff.Baseline,
        Status = handoff.Status,
        RequestedAt = handoff.RequestedAt,
        RequestedBy = handoff.RequestedBy,
        ArtifactHash = handoff.ArtifactHash,
        CompletedAt = handoff.CompletedAt,
        CompletedBy = handoff.CompletedBy,
    };

    // ---- Parsing ---------------------------------------------------------

    private static Result<CreateModelDeploymentVersionCommand> BuildDeploymentCommand(CreateModelDeploymentRequest request)
    {
        if (!TryParseCapabilities(request.RequiredCapabilities, out ModelCapability required, out string? invalidCapability))
        {
            return Result<CreateModelDeploymentVersionCommand>.Failure(Error.Permanent(
                DomainErrorCodes.ModelConfigurationInvalid,
                $"'{invalidCapability}' is not a supported model capability."));
        }

        if (request.TimeoutSeconds <= 0)
        {
            return Result<CreateModelDeploymentVersionCommand>.Failure(Error.Permanent(
                DomainErrorCodes.ModelConfigurationInvalid,
                "'timeout_seconds' must be positive."));
        }

        return Result<CreateModelDeploymentVersionCommand>.Success(new CreateModelDeploymentVersionCommand(
            request.LogicalName,
            request.ProviderConnectionRevisionId,
            request.ProviderModel,
            required,
            request.Temperature,
            request.MaxOutputTokens,
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            request.InputCostPerMillionTokens,
            request.OutputCostPerMillionTokens,
            request.Currency,
            request.ProviderParameters ?? new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    private static bool TryParseAdapterKind(string? value, out ProviderAdapterKind adapterKind)
    {
        adapterKind = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Snake-case tokens on the wire, PascalCase names on the enum. Accept
        // both so `"openai"` and `"OpenAi"` route to the same adapter, matching
        // the plan's verification example.
        string canonical = value.Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(canonical, ignoreCase: true, out adapterKind)
            && Enum.IsDefined(adapterKind);
    }

    private static string FormatAdapterKind(ProviderAdapterKind kind) => kind switch
    {
        ProviderAdapterKind.Scripted => "scripted",
        ProviderAdapterKind.OpenAi => "openai",
        ProviderAdapterKind.OpenAiCompatible => "openai_compatible",
        ProviderAdapterKind.AzureOpenAi => "azure_openai",
        ProviderAdapterKind.Anthropic => "anthropic",
        ProviderAdapterKind.Gemini => "gemini",
        _ => throw new InvalidOperationException("The provider adapter kind is not registered."),
    };

    private static bool TryParseCapabilities(
        IReadOnlyList<string>? tokens,
        out ModelCapability capability,
        out string? invalidToken)
    {
        capability = ModelCapability.None;
        invalidToken = null;
        if (tokens is null || tokens.Count == 0)
        {
            return true;
        }

        foreach (string token in tokens)
        {
            if (!TryParseCapability(token, out ModelCapability parsed))
            {
                invalidToken = token;
                return false;
            }

            capability |= parsed;
        }

        return true;
    }

    private static bool TryParseCapability(string token, out ModelCapability capability)
    {
        capability = ModelCapability.None;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "none":
                capability = ModelCapability.None;
                return true;
            case "structured_output":
                capability = ModelCapability.StructuredOutput;
                return true;
            case "system_message":
                capability = ModelCapability.SystemMessage;
                return true;
            case "seed":
                capability = ModelCapability.Seed;
                return true;
            case "tool_calling":
                capability = ModelCapability.ToolCalling;
                return true;
            case "parallel_tool_calls":
                capability = ModelCapability.ParallelToolCalls;
                return true;
            default:
                return false;
        }
    }

    private static IReadOnlyList<string> FormatCapabilities(ModelCapability capability)
    {
        if (capability == ModelCapability.None)
        {
            return Array.Empty<string>();
        }

        List<string> tokens = new(5);
        if (capability.HasFlag(ModelCapability.StructuredOutput))
        {
            tokens.Add("structured_output");
        }

        if (capability.HasFlag(ModelCapability.SystemMessage))
        {
            tokens.Add("system_message");
        }

        if (capability.HasFlag(ModelCapability.Seed))
        {
            tokens.Add("seed");
        }

        if (capability.HasFlag(ModelCapability.ToolCalling))
        {
            tokens.Add("tool_calling");
        }

        if (capability.HasFlag(ModelCapability.ParallelToolCalls))
        {
            tokens.Add("parallel_tool_calls");
        }

        return tokens;
    }

    private static IResult InvalidAdapterKind(string? received)
        => ResultExtensions.Problem(Error.Permanent(
            DomainErrorCodes.ModelConfigurationInvalid,
            string.IsNullOrWhiteSpace(received)
                ? "'adapter_kind' is required."
                : $"'{received}' is not a supported adapter_kind."));

    private static IResult InvalidCapability(string? received)
        => ResultExtensions.Problem(Error.Permanent(
            DomainErrorCodes.ModelConfigurationInvalid,
            $"'{received}' is not a supported model capability."));
}
