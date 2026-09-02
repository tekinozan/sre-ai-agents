using System.Text.Json;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Llm;

public sealed record PinnedModelCandidate
{
    public required string Tenant { get; init; }
    public required ModelAssignmentReleaseId AssignmentReleaseId { get; init; }
    public required string AssignmentConfigurationHash { get; init; }
    public required string Slot { get; init; }
    public required ModelDeploymentVersionId DeploymentVersionId { get; init; }
    public required string DeploymentLogicalName { get; init; }
    public required int DeploymentVersion { get; init; }
    public required string DeploymentConfigurationHash { get; init; }
    public required ProviderConnectionRevisionId ProviderConnectionRevisionId { get; init; }
    public required int ProviderConnectionRevision { get; init; }
    public required string ProviderConfigurationHash { get; init; }
    public required ProviderAdapterKind AdapterKind { get; init; }
    public required string ProviderModel { get; init; }
    public required ModelCapability Capabilities { get; init; }
    public required float Temperature { get; init; }
    public required int MaxOutputTokens { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required decimal InputCostPerMillionTokens { get; init; }
    public required decimal OutputCostPerMillionTokens { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyDictionary<string, string> ProviderParameters { get; init; }

    public decimal EstimateCost(long inputTokens, long outputTokens)
        => ((inputTokens * InputCostPerMillionTokens) + (outputTokens * OutputCostPerMillionTokens)) / 1_000_000m;
}

public sealed record PinnedModelAssignment
{
    public required ModelAssignmentReleaseId ReleaseId { get; init; }
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required string ConfigurationHash { get; init; }
    public required PinnedModelCandidate Classifier { get; init; }
    public required PinnedModelCandidate Correlator { get; init; }
    public required IReadOnlyDictionary<string, PinnedModelCandidate> Agents { get; init; }

    public Result<PinnedModelCandidate> ResolveSlot(string slot)
    {
        if (string.Equals(slot, "classifier", StringComparison.Ordinal))
        {
            return Result<PinnedModelCandidate>.Success(Classifier);
        }

        if (string.Equals(slot, "correlator", StringComparison.Ordinal))
        {
            return Result<PinnedModelCandidate>.Success(Correlator);
        }

        return Agents.TryGetValue(slot, out PinnedModelCandidate? candidate)
            ? Result<PinnedModelCandidate>.Success(candidate)
            : Result<PinnedModelCandidate>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelAssignmentSlotMissing,
                $"The pinned model assignment has no slot for '{slot}'."));
    }
}

public sealed record ProviderCapabilityDescriptor(
    ProviderAdapterKind AdapterKind,
    ModelCapability Capabilities);

public sealed record ProviderChatRequest(
    string SystemPrompt,
    string UserPrompt,
    string ProviderModel,
    JsonElement JsonSchema,
    float Temperature,
    int MaxOutputTokens,
    int? Seed,
    IReadOnlyDictionary<string, string> ProviderParameters);

public sealed record ProviderChatResponse(
    string Text,
    string ObservedProvider,
    string ObservedModel,
    long InputTokens,
    long OutputTokens,
    string RequestId,
    string FinishReason);

public interface IProviderChatClient
{
    ProviderCapabilityDescriptor CapabilityDescriptor { get; }

    Task<Result<ProviderChatResponse>> GetResponseAsync(
        ProviderChatRequest request,
        CancellationToken cancellationToken);
}

public interface IChatClientResolver
{
    Task<Result<IProviderChatClient>> ResolveAsync(
        PinnedModelCandidate candidate,
        CancellationToken cancellationToken);
}

public sealed class CredentialValue
{
    public CredentialValue(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Credential value cannot be empty.", nameof(value))
            : value;
    }

    public string Value { get; }

    public override string ToString() => "[REDACTED]";
}

public interface ICredentialResolver
{
    ValueTask<Result<CredentialValue>> ResolveAsync(
        string credentialReference,
        CancellationToken cancellationToken);
}
