using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Loads prompt artifacts once and resolves them through pinned releases
/// (ARCHITECTURE.md 8.1).
/// </summary>
/// <remarks>
/// Shared policy fragments are composed into each prompt at load time rather than
/// at render time. Composition is deterministic and identical for every render,
/// so doing it per call would repeat the same string work on the hottest path in
/// an investigation. It also means the content hash covers the fully composed
/// prompt, which is what an audit actually wants: knowing the expert template
/// version is not enough if the shared security policy changed underneath it.
///
/// Everything is a frozen dictionary. Lookups happen for every agent on every
/// incident, and a frozen dictionary is optimised for exactly this read-only,
/// build-once pattern.
/// </remarks>
internal sealed class PromptRegistry : IPromptRegistry
{
    private const string SharedFragmentPrefix = "shared.";

    private readonly FrozenDictionary<string, PromptDefinition> _prompts;
    private readonly FrozenDictionary<string, PromptRelease> _releases;

    public PromptRegistry(IOptions<RegistryOptions> options, YamlArtifactLoader loader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loader);

        RegistryOptions settings = options.Value;
        string promptRoot = settings.PromptRoot;

        Dictionary<string, string> fragments = LoadSharedFragments(loader, promptRoot);
        Dictionary<string, PromptDefinition> prompts = LoadPrompts(loader, promptRoot, fragments);
        Dictionary<string, PromptRelease> releases = LoadReleases(loader, promptRoot);

        _prompts = prompts.ToFrozenDictionary(StringComparer.Ordinal);
        _releases = releases.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<PromptDefinition> All => _prompts.Values;

    /// <inheritdoc />
    public ValueTask<Result<PromptDefinition>> ResolveAsync(
        string releaseName,
        string promptName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_releases.TryGetValue(releaseName, out PromptRelease? release))
        {
            return ValueTask.FromResult(Result<PromptDefinition>.Failure(Error.Permanent(
                ApplicationErrorCodes.PromptNotFound,
                $"Prompt release '{releaseName}' is not loaded.")));
        }

        if (!release.Prompts.TryGetValue(promptName, out string? version))
        {
            return ValueTask.FromResult(Result<PromptDefinition>.Failure(Error.Permanent(
                ApplicationErrorCodes.PromptNotFound,
                $"Release '{releaseName}' does not pin a version for prompt '{promptName}'.")));
        }

        return ValueTask.FromResult(_prompts.TryGetValue(Key(promptName, version), out PromptDefinition? definition)
            ? Result<PromptDefinition>.Success(definition)
            : Result<PromptDefinition>.Failure(Error.Permanent(
                ApplicationErrorCodes.PromptNotFound,
                $"Prompt '{promptName}:{version}' pinned by release '{releaseName}' was not found on disk.")));
    }

    /// <inheritdoc />
    public ValueTask<Result<PromptRelease>> GetReleaseAsync(string releaseName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_releases.TryGetValue(releaseName, out PromptRelease? release)
            ? Result<PromptRelease>.Success(release)
            : Result<PromptRelease>.Failure(Error.Permanent(
                ApplicationErrorCodes.PromptNotFound,
                $"Prompt release '{releaseName}' is not loaded.")));
    }

    private static string Key(string name, string version) => $"{name}:{version}";

    private static Dictionary<string, string> LoadSharedFragments(YamlArtifactLoader loader, string promptRoot)
    {
        Dictionary<string, string> fragments = new(StringComparer.Ordinal);

        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(Path.Combine(promptRoot, "shared")))
        {
            LoadedArtifact<PromptDocument> artifact = loader.Load<PromptDocument>(path);
            PromptDocument document = artifact.Value;

            if (string.IsNullOrWhiteSpace(document.Name) || string.IsNullOrWhiteSpace(document.Content))
            {
                throw new ArtifactLoadException(path, "A shared fragment requires 'name' and 'content'.");
            }

            fragments[document.Name] = document.Content.TrimEnd();
        }

        return fragments;
    }

    private static Dictionary<string, PromptDefinition> LoadPrompts(
        YamlArtifactLoader loader,
        string promptRoot,
        Dictionary<string, string> fragments)
    {
        Dictionary<string, PromptDefinition> prompts = new(StringComparer.Ordinal);

        foreach (string directory in Directory.EnumerateDirectories(promptRoot))
        {
            string folder = Path.GetFileName(directory);
            if (folder is "shared" or "releases")
            {
                continue;
            }

            foreach (string path in YamlArtifactLoader.EnumerateArtifacts(directory))
            {
                LoadedArtifact<PromptDocument> artifact = loader.Load<PromptDocument>(path);
                PromptDocument document = artifact.Value;

                if (string.IsNullOrWhiteSpace(document.Name) || string.IsNullOrWhiteSpace(document.Version))
                {
                    throw new ArtifactLoadException(path, "A prompt requires 'name' and 'version'.");
                }

                if (string.IsNullOrWhiteSpace(document.System) || string.IsNullOrWhiteSpace(document.User))
                {
                    throw new ArtifactLoadException(path, "A prompt requires both 'system' and 'user'.");
                }

                if (document.MaxRenderedCharacters <= 0)
                {
                    throw new ArtifactLoadException(
                        path,
                        "'max_rendered_characters' must be positive: an unbounded prompt is a cost incident waiting to happen.");
                }

                string system = ComposeFragments(document.System, fragments, path);
                string user = ComposeFragments(document.User, fragments, path);

                PromptDefinition definition = new(
                    Name: document.Name,
                    Version: document.Version,
                    SystemTemplate: system,
                    UserTemplate: user,
                    RequiredVariables: (document.RequiredVariables ?? []).ToFrozenSet(StringComparer.Ordinal),
                    OptionalVariables: (document.OptionalVariables ?? []).ToFrozenSet(StringComparer.Ordinal),
                    MaxRenderedCharacters: document.MaxRenderedCharacters,
                    ContentHash: HashComposed(system, user));

                if (!prompts.TryAdd(definition.Reference, definition))
                {
                    throw new ArtifactLoadException(path, $"Duplicate prompt '{definition.Reference}'.");
                }
            }
        }

        return prompts;
    }

    private static Dictionary<string, PromptRelease> LoadReleases(YamlArtifactLoader loader, string promptRoot)
    {
        Dictionary<string, PromptRelease> releases = new(StringComparer.Ordinal);

        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(Path.Combine(promptRoot, "releases")))
        {
            LoadedArtifact<PromptReleaseDocument> artifact = loader.Load<PromptReleaseDocument>(path);
            PromptReleaseDocument document = artifact.Value;

            if (string.IsNullOrWhiteSpace(document.Name) || document.Prompts is null or { Count: 0 })
            {
                throw new ArtifactLoadException(path, "A prompt release requires 'name' and a non-empty 'prompts' map.");
            }

            releases[document.Name] = new PromptRelease(
                document.Name,
                document.Prompts.ToFrozenDictionary(StringComparer.Ordinal),
                document.CreatedAt);
        }

        return releases;
    }

    /// <summary>
    /// Substitutes <c>{{shared.x}}</c> placeholders with the named fragment. An
    /// unknown fragment is fatal rather than left in place: a prompt shipping to
    /// production with a literal <c>{{shared.security-policy}}</c> in its text
    /// would silently lose the whole policy.
    /// </summary>
    private static string ComposeFragments(string template, Dictionary<string, string> fragments, string path)
    {
        if (!template.Contains("{{" + SharedFragmentPrefix, StringComparison.Ordinal))
        {
            return template;
        }

        StringBuilder builder = new(template);
        foreach ((string name, string content) in fragments)
        {
            builder.Replace("{{" + name + "}}", content);
        }

        string composed = builder.ToString();

        int remaining = composed.IndexOf("{{" + SharedFragmentPrefix, StringComparison.Ordinal);
        if (remaining >= 0)
        {
            int end = composed.IndexOf("}}", remaining, StringComparison.Ordinal);
            string reference = end > remaining
                ? composed[(remaining + 2)..end]
                : SharedFragmentPrefix + "unknown";
            throw new ArtifactLoadException(path, $"Unknown shared fragment '{reference}'.");
        }

        return composed;
    }

    private static string HashComposed(string system, string user)
    {
        // Hashing the composed text, not the file, so a change to a shared policy
        // fragment changes every prompt that includes it. An audit that only
        // recorded the expert template version would miss that entirely.
        byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(system, "", user));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
