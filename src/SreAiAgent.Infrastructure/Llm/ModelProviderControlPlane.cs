using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Persistence.Entities;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.Infrastructure.Llm;

internal sealed class ModelProviderControlPlane(
    ModelProviderRepository providers,
    ProviderRevisionVerifier verifier,
    ICredentialResolver credentials,
    ProviderEndpointPolicy endpointPolicy,
    IOptions<LlmOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider) : IModelProviderControlPlane
{
    private readonly LlmOptions _options = options.Value;

    public Task<IReadOnlyList<ProviderConnection>> ListConnectionsAsync(
        string tenant,
        CancellationToken cancellationToken)
        => providers.ListConnectionsAsync(tenant, cancellationToken);

    public async Task<Result<ProviderConnection>> CreateConnectionAsync(
        string tenant,
        string displayName,
        string actor,
        CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            ProviderConnection connection = ProviderConnection.Create(tenant, displayName, now);
            providers.AddConnection(connection);
            providers.AddAudit(tenant, actor, "create", "provider_connection", connection.Id.Value, "none", now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ProviderConnection>.Success(connection);
        }
        catch (DomainException exception)
        {
            return Result<ProviderConnection>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ProviderConnection>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ProviderConnection>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }

    public Task<IReadOnlyList<ProviderConnectionRevision>> ListRevisionsAsync(
        string tenant,
        ProviderConnectionId connectionId,
        CancellationToken cancellationToken)
        => providers.ListRevisionsAsync(tenant, connectionId, cancellationToken);

    public async Task<Result<ProviderConnectionRevision>> CreateRevisionAsync(
        string tenant,
        ProviderConnectionId connectionId,
        CreateProviderRevisionCommand command,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProviderConnection? connection = await providers
            .FindConnectionAsync(tenant, connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is null)
        {
            return NotFound<ProviderConnectionRevision>(ApplicationErrorCodes.ProviderConnectionNotFound, "No such provider connection.");
        }

        if (environment.IsProduction() && command.AdapterKind == ProviderAdapterKind.Scripted)
        {
            return Result<ProviderConnectionRevision>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelCapabilityMismatch,
                "Scripted model providers are not permitted in production."));
        }

        try
        {
            if (!command.CredentialRef.StartsWith(ExternalReferenceCredentialResolver.EnvironmentPrefix, StringComparison.Ordinal)
                && !command.CredentialRef.StartsWith(ExternalReferenceCredentialResolver.FilePrefix, StringComparison.Ordinal))
            {
                return Result<ProviderConnectionRevision>.Failure(Error.Permanent(
                    DomainErrorCodes.ModelConfigurationInvalid,
                    "A credential reference must use env:// or file://."));
            }

            Result<CredentialValue> credentialResult = await credentials
                .ResolveAsync(command.CredentialRef, cancellationToken)
                .ConfigureAwait(false);
            if (credentialResult.IsFailure)
            {
                return Result<ProviderConnectionRevision>.Failure(credentialResult.Error);
            }

            endpointPolicy.Validate(command.AdapterKind, command.Endpoint);
            ProviderCapabilityDescriptor descriptor = ProviderClientCatalog.Descriptor(command.AdapterKind);
            if ((descriptor.Capabilities & command.DeclaredCapabilities) != command.DeclaredCapabilities)
            {
                return Result<ProviderConnectionRevision>.Failure(Error.Permanent(
                    ApplicationErrorCodes.ModelCapabilityMismatch,
                    "The provider revision declares capabilities its adapter does not support."));
            }

            int revisionNumber = await providers
                .NextRevisionAsync(tenant, connectionId, cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            ProviderConnectionRevision revision = ProviderConnectionRevision.Create(
                connectionId,
                tenant,
                revisionNumber,
                command.AdapterKind,
                command.Endpoint,
                command.Region,
                command.ApiVersion,
                command.Organization,
                command.CredentialRef,
                command.DeclaredCapabilities,
                now,
                actor);
            providers.AddRevision(revision);
            await providers.SetActiveRevisionAsync(tenant, connectionId, revision.Id, cancellationToken).ConfigureAwait(false);
            providers.AddAudit(tenant, actor, "create", "provider_revision", revision.Id.Value, revision.ConfigurationHash, now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ProviderConnectionRevision>.Success(revision);
        }
        catch (DomainException exception)
        {
            return Result<ProviderConnectionRevision>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ProviderConnectionRevision>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ProviderConnectionRevision>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }

    public async Task<Result<ProviderRevisionVerification>> VerifyRevisionAsync(
        string tenant,
        ProviderConnectionId connectionId,
        int revision,
        string actor,
        CancellationToken cancellationToken)
    {
        ProviderConnectionRevision? providerRevision = await providers
            .FindRevisionAsync(tenant, connectionId, revision, cancellationToken)
            .ConfigureAwait(false);
        if (providerRevision is null)
        {
            return NotFound<ProviderRevisionVerification>(ApplicationErrorCodes.ProviderRevisionNotFound, "No such provider revision.");
        }

        Result<ProviderVerificationProbe> probe = await verifier
            .VerifyAsync(providerRevision, cancellationToken)
            .ConfigureAwait(false);
        if (probe.IsFailure)
        {
            return Result<ProviderRevisionVerification>.Failure(probe.Error);
        }

        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            ProviderRevisionVerification verification = new(
                providerRevision.Id,
                probe.Value.Capabilities,
                probe.Value.RequestId,
                now,
                actor);
            providers.AddVerification(tenant, verification);
            providers.AddAudit(tenant, actor, "verify", "provider_revision", providerRevision.Id.Value,
                providerRevision.ConfigurationHash, now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ProviderRevisionVerification>.Success(verification);
        }
        catch (DomainException exception)
        {
            return Result<ProviderRevisionVerification>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ProviderRevisionVerification>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ProviderRevisionVerification>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }

    public Task<IReadOnlyList<ModelDeploymentVersion>> ListDeploymentVersionsAsync(
        string tenant,
        CancellationToken cancellationToken)
        => providers.ListDeploymentVersionsAsync(tenant, cancellationToken);

    public async Task<Result<ModelDeploymentVersion>> CreateDeploymentVersionAsync(
        string tenant,
        CreateModelDeploymentVersionCommand command,
        string actor,
        CancellationToken cancellationToken)
        => await CreateDeploymentAsync(tenant, command, actor, expectedLogicalName: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<Result<ModelDeploymentVersion>> CreateNextDeploymentVersionAsync(
        string tenant,
        ModelDeploymentVersionId priorVersionId,
        CreateModelDeploymentVersionCommand command,
        string actor,
        CancellationToken cancellationToken)
    {
        ModelDeploymentVersion? prior = await providers.FindDeploymentAsync(tenant, priorVersionId, cancellationToken)
            .ConfigureAwait(false);
        return prior is null
            ? NotFound<ModelDeploymentVersion>(ApplicationErrorCodes.ModelDeploymentNotFound, "No such model deployment version.")
            : await CreateDeploymentAsync(tenant, command, actor, prior.LogicalName, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ModelAssignmentRelease>> ListAssignmentReleasesAsync(
        string tenant,
        CancellationToken cancellationToken)
        => providers.ListAssignmentsAsync(tenant, cancellationToken);

    public async Task<Result<ModelAssignmentRelease>> CreateAssignmentReleaseAsync(
        string tenant,
        CreateModelAssignmentReleaseCommand command,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        HashSet<ModelDeploymentVersionId> ids =
        [
            command.ClassifierDeploymentVersionId,
            command.CorrelatorDeploymentVersionId,
            .. command.AgentDeploymentVersionIds.Values,
        ];
        foreach (ModelDeploymentVersionId id in ids)
        {
            if (await providers.FindDeploymentAsync(tenant, id, cancellationToken).ConfigureAwait(false) is null)
            {
                return NotFound<ModelAssignmentRelease>(
                    ApplicationErrorCodes.ModelDeploymentNotFound,
                    "An assignment slot references a model deployment outside the tenant or one that does not exist.");
            }
        }

        try
        {
            int version = await providers.NextAssignmentVersionAsync(tenant, command.Name, cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            ModelAssignmentRelease release = ModelAssignmentRelease.Create(
                tenant,
                command.Name,
                version,
                command.ClassifierDeploymentVersionId,
                command.CorrelatorDeploymentVersionId,
                command.AgentDeploymentVersionIds,
                now,
                actor);
            providers.AddAssignment(release);
            providers.AddAudit(tenant, actor, "create", "model_assignment_release", release.Id.Value,
                release.ConfigurationHash, now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ModelAssignmentRelease>.Success(release);
        }
        catch (DomainException exception)
        {
            return Result<ModelAssignmentRelease>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }

    public async Task<Result<ModelAssignmentEvaluationHandoff>> RequestEvaluationAsync(
        string tenant,
        ModelAssignmentReleaseId releaseId,
        string dataset,
        string baseline,
        string actor,
        CancellationToken cancellationToken)
    {
        try
        {
            ModelAssignmentRelease? release = await providers.FindAssignmentAsync(tenant, releaseId, cancellationToken)
                .ConfigureAwait(false);
            if (release is null)
            {
                return NotFound<ModelAssignmentEvaluationHandoff>(ApplicationErrorCodes.ModelAssignmentNotFound, "No such model assignment release.");
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            ModelAssignmentEvaluationId evaluationId = ModelAssignmentEvaluationId.New();
            providers.AddEvaluation(new ModelAssignmentEvaluationEntity
            {
                Id = evaluationId,
                Tenant = tenant,
                AssignmentReleaseId = releaseId,
                Dataset = Required(dataset, "dataset"),
                Baseline = Required(baseline, "baseline"),
                Status = "requested",
                Passed = false,
                RequestedAt = now,
                RequestedBy = actor,
            });
            providers.AddAudit(tenant, actor, "evaluate", "model_assignment_release", release.Id.Value,
                release.ConfigurationHash, now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ModelAssignmentEvaluationHandoff>.Success(new ModelAssignmentEvaluationHandoff(
                evaluationId,
                releaseId,
                dataset,
                baseline,
                "requested",
                now));
        }
        catch (DomainException exception)
        {
            return Result<ModelAssignmentEvaluationHandoff>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ModelAssignmentEvaluationHandoff>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ModelAssignmentEvaluationHandoff>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }
    public async Task<Result<ModelAssignmentEvaluationHandoff>> CompleteEvaluationAsync(
        string tenant,
        ModelAssignmentEvaluationId evaluationId,
        bool passed,
        string artifactHash,
        string actor,
        CancellationToken cancellationToken)
    {
        try
        {
            ModelAssignmentEvaluationEntity? evaluation = await providers.FindEvaluationAsync(tenant, evaluationId, cancellationToken)
                .ConfigureAwait(false);
            if (evaluation is null)
            {
                return NotFound<ModelAssignmentEvaluationHandoff>(DomainErrorCodes.ModelConfigurationInvalid, "No such model assignment evaluation.");
            }

            if (!string.Equals(evaluation.Status, "requested", StringComparison.Ordinal))
            {
                return Result<ModelAssignmentEvaluationHandoff>.Failure(Error.Permanent(
                    ApplicationErrorCodes.ModelResourceConflict,
                    "The evaluation is already in a terminal state."));
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            evaluation.Status = passed ? "passed" : "failed";
            evaluation.Passed = passed;
            evaluation.ArtifactHash = Required(artifactHash, "artifactHash");
            evaluation.CompletedAt = now;

            providers.UpdateEvaluation(evaluation);
            providers.AddAudit(tenant, actor, "complete_evaluation", "model_assignment_release", evaluation.AssignmentReleaseId.Value,
                evaluation.ArtifactHash, now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result<ModelAssignmentEvaluationHandoff>.Success(new ModelAssignmentEvaluationHandoff(
                evaluation.Id,
                evaluation.AssignmentReleaseId,
                evaluation.Dataset,
                evaluation.Baseline,
                evaluation.Status,
                evaluation.RequestedAt));
        }
        catch (DomainException exception)
        {
            return Result<ModelAssignmentEvaluationHandoff>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ModelAssignmentEvaluationHandoff>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ModelAssignmentEvaluationHandoff>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }


    public async Task<Result<ModelAssignmentRelease>> ActivateAsync(
        string tenant,
        ModelAssignmentReleaseId releaseId,
        string actor,
        CancellationToken cancellationToken)
    {
        try
        {
            ModelAssignmentRelease? release = await providers.FindAssignmentAsync(tenant, releaseId, cancellationToken)
                .ConfigureAwait(false);
            PinnedModelAssignment? snapshot = await providers.BuildSnapshotAsync(tenant, releaseId, cancellationToken)
                .ConfigureAwait(false);
            if (release is null || snapshot is null)
            {
                return NotFound<ModelAssignmentRelease>(ApplicationErrorCodes.ModelAssignmentNotFound, "No such model assignment release.");
            }

            if (_options.RequireEvaluationForActivation
                && !await providers.HasPassedEvaluationAsync(tenant, releaseId, cancellationToken).ConfigureAwait(false))
            {
                return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                    ApplicationErrorCodes.ModelAssignmentEvaluationRequired,
                    "A passing immutable evaluation result is required before activation."));
            }

            IEnumerable<PinnedModelCandidate> candidates =
                new[] { snapshot.Classifier, snapshot.Correlator }.Concat(snapshot.Agents.Values);
            foreach (PinnedModelCandidate candidate in candidates)
            {
                if (environment.IsProduction() && candidate.AdapterKind == ProviderAdapterKind.Scripted)
                {
                    return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                        ApplicationErrorCodes.ModelCapabilityMismatch,
                        "Scripted model assignments cannot be activated in production."));
                }

                ProviderCapabilityDescriptor descriptor = ProviderClientCatalog.Descriptor(candidate.AdapterKind);
                if ((descriptor.Capabilities & candidate.Capabilities) != candidate.Capabilities)
                {
                    return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                        ApplicationErrorCodes.ModelCapabilityMismatch,
                        $"Assignment slot '{candidate.Slot}' requires unsupported provider capabilities."));
                }

                ProviderConnectionRevision? revision = await providers.FindRevisionAsync(
                    tenant,
                    candidate.ProviderConnectionRevisionId,
                    cancellationToken).ConfigureAwait(false);
                if (revision is null || !string.Equals(revision.ConfigurationHash, candidate.ProviderConfigurationHash, StringComparison.Ordinal))
                {
                    return NotFound<ModelAssignmentRelease>(ApplicationErrorCodes.ProviderRevisionNotFound,
                        "A pinned provider revision is missing or its immutable hash does not match.");
                }

                if ((revision.DeclaredCapabilities & candidate.Capabilities) != candidate.Capabilities)
                {
                    return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                        ApplicationErrorCodes.ModelCapabilityMismatch,
                        $"Assignment slot '{candidate.Slot}' requires capabilities its provider revision does not declare."));
                }

                ProviderRevisionVerification? verified = await providers.LatestVerificationAsync(
                    tenant,
                    candidate.ProviderConnectionRevisionId,
                    cancellationToken).ConfigureAwait(false);
                if (verified is null || (verified.VerifiedCapabilities & candidate.Capabilities) != candidate.Capabilities)
                {
                    return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                        ApplicationErrorCodes.ModelCapabilityMismatch,
                        $"Assignment slot '{candidate.Slot}' has not been verified for its required capabilities."));
                }

                endpointPolicy.Validate(revision.AdapterKind, revision.Endpoint);
                if (revision.AdapterKind != ProviderAdapterKind.Scripted)
                {
                    Result<CredentialValue> credential = await credentials
                        .ResolveAsync(revision.CredentialRef, cancellationToken)
                        .ConfigureAwait(false);
                    if (credential.IsFailure)
                    {
                        return Result<ModelAssignmentRelease>.Failure(credential.Error);
                    }
                }
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            await providers.ActivateAsync(tenant, releaseId, now, actor, cancellationToken).ConfigureAwait(false);
            providers.AddAudit(tenant, actor, "activate", "model_assignment_release", release.Id.Value,
                release.ConfigurationHash, now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ModelAssignmentRelease>.Success(release);
        }
        catch (DomainException exception)
        {
            return Result<ModelAssignmentRelease>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ModelAssignmentRelease>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }
    private async Task<Result<ModelDeploymentVersion>> CreateDeploymentAsync(
        string tenant,
        CreateModelDeploymentVersionCommand command,
        string actor,
        string? expectedLogicalName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProviderConnectionRevision? revision = await providers.FindRevisionAsync(
            tenant,
            command.ProviderConnectionRevisionId,
            cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            return NotFound<ModelDeploymentVersion>(ApplicationErrorCodes.ProviderRevisionNotFound, "No such provider revision.");
        }

        if (expectedLogicalName is not null && !string.Equals(expectedLogicalName, command.LogicalName, StringComparison.Ordinal))
        {
            return Result<ModelDeploymentVersion>.Failure(Error.Permanent(
                DomainErrorCodes.ModelConfigurationInvalid,
                "A new deployment version must keep the prior logical name."));
        }

        if (command.ProviderParameters.Keys.Any(static key => !string.Equals(key, "seed", StringComparison.Ordinal)))
        {
            return Result<ModelDeploymentVersion>.Failure(Error.Permanent(
                DomainErrorCodes.ModelConfigurationInvalid,
                "Only the allowlisted provider parameter 'seed' is supported."));
        }

        if (command.ProviderParameters.TryGetValue("seed", out string? seedString))
        {
            if (!int.TryParse(seedString, CultureInfo.InvariantCulture, out _))
            {
                return Result<ModelDeploymentVersion>.Failure(Error.Permanent(
                    DomainErrorCodes.ModelConfigurationInvalid,
                    "The 'seed' parameter must be an integer."));
            }

            if ((command.RequiredCapabilities & ModelCapability.Seed) == ModelCapability.None)
            {
                return Result<ModelDeploymentVersion>.Failure(Error.Permanent(
                    DomainErrorCodes.ModelConfigurationInvalid,
                    "A deployment with a seed parameter must require the Seed capability."));
            }
        }

        try
        {
            int version = await providers.NextDeploymentVersionAsync(tenant, command.LogicalName, cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            ModelDeploymentVersion deployment = ModelDeploymentVersion.Create(
                tenant,
                command.LogicalName,
                version,
                command.ProviderConnectionRevisionId,
                command.ProviderModel,
                command.RequiredCapabilities,
                command.Temperature,
                command.MaxOutputTokens,

                command.Timeout,
                command.InputCostPerMillionTokens,
                command.OutputCostPerMillionTokens,
                command.Currency,
                command.ProviderParameters,
                now,
                actor);
            providers.AddDeployment(deployment);
            providers.AddAudit(tenant, actor, "create", "model_deployment_version", deployment.Id.Value,
                deployment.ConfigurationHash, now);
            await providers.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ModelDeploymentVersion>.Success(deployment);
        }
        catch (DomainException exception)
        {
            return Result<ModelDeploymentVersion>.Failure(Error.Permanent(exception.Code, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Result<ModelDeploymentVersion>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ModelDeploymentVersion>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelResourceConflict,
                "A model control-plane resource with that identity already exists."));
        }
    }

    private static Result<T> NotFound<T>(ErrorCode code, string message)
        => Result<T>.Failure(Error.Permanent(code, message));

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new DomainException(DomainErrorCodes.ModelConfigurationInvalid, $"'{name}' is required.")
            : value;
}
