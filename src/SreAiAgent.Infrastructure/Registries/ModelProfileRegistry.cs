using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Loads model profiles and resolves them through pinned releases
/// (ARCHITECTURE.md 8.2).
/// </summary>
/// <remarks>
/// The artifact's <c>model</c> field is a placeholder; the concrete deployment
/// name comes from <see cref="LlmOptions.ModelNameByProfile"/>. That split is
/// what lets the same reviewed artifact run in staging and production without a
/// per-environment copy that will eventually diverge.
/// </remarks>
internal sealed class ModelProfileRegistry : IModelProfileRegistry
{
    private const string EnvironmentPlaceholder = "configured-by-environment";

    private readonly FrozenDictionary<string, ModelProfile> _profiles;
    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _releases;

    public ModelProfileRegistry(
        IOptions<RegistryOptions> registryOptions,
        IOptions<LlmOptions> llmOptions,
        YamlArtifactLoader loader)
    {
        ArgumentNullException.ThrowIfNull(registryOptions);
        ArgumentNullException.ThrowIfNull(llmOptions);
        ArgumentNullException.ThrowIfNull(loader);

        string root = Path.Combine(registryOptions.Value.ConfigurationRoot, "models");
        IReadOnlyDictionary<string, string> modelNames = llmOptions.Value.ModelNameByProfile;

        Dictionary<string, ModelProfile> profiles = new(StringComparer.Ordinal);
        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(root))
        {
            LoadedArtifact<ModelProfileDocument> artifact = loader.Load<ModelProfileDocument>(path);
            ModelProfileDocument document = artifact.Value;

            if (string.IsNullOrWhiteSpace(document.Name)
                || string.IsNullOrWhiteSpace(document.Version)
                || string.IsNullOrWhiteSpace(document.Provider))
            {
                throw new ArtifactLoadException(path, "A model profile requires 'name', 'version' and 'provider'.");
            }

            if (document.TimeoutSeconds <= 0 || document.MaxOutputTokens <= 0)
            {
                throw new ArtifactLoadException(
                    path, "'timeout_seconds' and 'max_output_tokens' must be positive.");
            }

            string modelName = ResolveModelName(document, modelNames, path);

            ModelProfile profile = new()
            {
                Name = document.Name,
                Version = document.Version,
                Provider = document.Provider,
                ModelName = modelName,
                Temperature = document.Temperature,
                Timeout = TimeSpan.FromSeconds(document.TimeoutSeconds),
                MaxOutputTokens = document.MaxOutputTokens,
                SupportsStructuredOutput = document.StructuredOutput,
                SupportsToolCalling = document.ToolCalling,
                SupportsParallelToolCalls = document.ParallelToolCalls,
                InputCostPerMillionTokens = document.InputCostPerMillionTokens,
                OutputCostPerMillionTokens = document.OutputCostPerMillionTokens,
            };

            if (!profiles.TryAdd(profile.Reference, profile))
            {
                throw new ArtifactLoadException(path, $"Duplicate model profile '{profile.Reference}'.");
            }
        }

        Dictionary<string, FrozenDictionary<string, string>> releases = new(StringComparer.Ordinal);
        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(Path.Combine(root, "releases")))
        {
            LoadedArtifact<ModelReleaseDocument> artifact = loader.Load<ModelReleaseDocument>(path);
            ModelReleaseDocument document = artifact.Value;

            if (string.IsNullOrWhiteSpace(document.Name) || document.Profiles is null or { Count: 0 })
            {
                throw new ArtifactLoadException(path, "A model release requires 'name' and a non-empty 'profiles' map.");
            }

            releases[document.Name] = document.Profiles.ToFrozenDictionary(StringComparer.Ordinal);
        }

        _profiles = profiles.ToFrozenDictionary(StringComparer.Ordinal);
        _releases = releases.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ModelProfile> All => _profiles.Values;

    /// <inheritdoc />
    /// <remarks>
    /// Accepts either a bare name or a <c>name:version</c> reference. Agent
    /// artifacts write the latter; the release pins the former.
    /// </remarks>
    public ValueTask<Result<ModelProfile>> ResolveAsync(
        string releaseName,
        string profileReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_releases.TryGetValue(releaseName, out FrozenDictionary<string, string>? release))
        {
            return ValueTask.FromResult(Result<ModelProfile>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelProfileNotFound,
                $"Model release '{releaseName}' is not loaded.")));
        }

        int separator = profileReference.IndexOf(':', StringComparison.Ordinal);
        string name = separator < 0 ? profileReference : profileReference[..separator];

        if (!release.TryGetValue(name, out string? pinnedVersion))
        {
            return ValueTask.FromResult(Result<ModelProfile>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelProfileNotFound,
                $"Release '{releaseName}' does not pin a version for model profile '{name}'.")));
        }

        // The release wins over whatever the agent artifact wrote. A pinned
        // release is the whole point: an agent asking for analytical:v2 while the
        // release pins v1 must get v1, otherwise the snapshot means nothing.
        return ValueTask.FromResult(_profiles.TryGetValue($"{name}:{pinnedVersion}", out ModelProfile? profile)
            ? Result<ModelProfile>.Success(profile)
            : Result<ModelProfile>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelProfileNotFound,
                $"Model profile '{name}:{pinnedVersion}' pinned by release '{releaseName}' was not found on disk.")));
    }

    private static string ResolveModelName(
        ModelProfileDocument document,
        IReadOnlyDictionary<string, string> modelNames,
        string path)
    {
        if (!string.Equals(document.Model, EnvironmentPlaceholder, StringComparison.Ordinal))
        {
            return document.Model ?? throw new ArtifactLoadException(path, "A model profile requires 'model'.");
        }

        if (modelNames.TryGetValue(document.Name!, out string? configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        throw new ArtifactLoadException(
            path,
            $"Profile '{document.Name}' defers its model to configuration, but " +
            $"Llm:ModelNameByProfile:{document.Name} is not set. Fail at startup rather than at the first incident.");
    }
}
