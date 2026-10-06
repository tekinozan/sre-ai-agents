using System.Text.Json;
using System.Text.RegularExpressions;

namespace SreAiAgent.Evals.Harness;

internal static partial class CandidateFreezer
{
    public static CandidateDefinition LoadDefinition(string path)
    {
        CandidateDefinition candidate = JsonSerializer.Deserialize(File.ReadAllText(path), EvalJsonContext.Default.CandidateDefinition)
            ?? throw new InvalidDataException($"Candidate '{path}' deserialized to null.");
        Validate(candidate);
        return candidate;
    }

    public static EvalConfig LoadConfig(string path)
        => JsonSerializer.Deserialize(File.ReadAllText(path), EvalJsonContext.Default.EvalConfig)
            ?? throw new InvalidDataException($"Eval config '{path}' deserialized to null.");

    public static CandidateSnapshot Freeze(CandidateDefinition candidate, LoadedDataset dataset, EvalConfig config)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(config);

        VersionedIdentity agent = Release(candidate.Releases.Agent, "agent", HashAgentRelease(candidate.Releases.Agent));
        VersionedIdentity prompt = Release(candidate.Releases.Prompt, "prompt", HashPromptRelease(candidate.Releases.Prompt));
        VersionedIdentity model = Release(candidate.Releases.Model, "model", HashModelRelease(candidate.Releases.Model));
        VersionedIdentity tool = Release(candidate.Releases.Tool, "tool", HashToolRelease(candidate.Releases.Tool));
        VersionedIdentity policy = Release(candidate.Releases.Policy, "policy", HashPolicyRelease(candidate.Releases.Policy));
        VersionedIdentity datasetIdentity = new() { Id = dataset.Manifest.DatasetId, Version = dataset.Manifest.Version, Hash = dataset.Hash };

        // Hashed in canonical form, not as serialized. The eval context writes
        // indented JSON, whose line breaks are Environment.NewLine: the same
        // configuration hashed differently on Windows and Linux, so a baseline
        // recorded on one never matched a run on the other.
        using JsonDocument configDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(config, EvalJsonContext.Default.EvalConfig));
        VersionedIdentity scorer = new()
        {
            Id = config.ScorerVersion,
            Version = config.SchemaVersion,
            Hash = CanonicalJson.Hash(configDocument.RootElement),
        };

        CandidateSnapshot unhashed = new()
        {
            CandidateId = candidate.CandidateId,
            Tenant = candidate.Tenant,
            ProviderConnectionRevision = candidate.ProviderConnectionRevision,
            ModelDeployment = candidate.ModelDeployment,
            AssignmentRelease = candidate.AssignmentRelease,
            AgentRelease = agent,
            PromptRelease = prompt,
            ModelRelease = model,
            ToolRelease = tool,
            PolicyRelease = policy,
            Generation = candidate.Generation,
            Dataset = datasetIdentity,
            Scorer = scorer,
            RunSettings = new EvalRunSettings(config.Repeats, config.Concurrency, config.TimeoutSeconds, config.Seed, config.WarmupRuns),
            CandidateHash = string.Empty,
        };

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(unhashed, EvalJsonContext.Default.CandidateSnapshot));
        string hash = CanonicalJson.Hash(document.RootElement);
        return unhashed with { CandidateHash = hash };
    }

    public static string RequireLivePrerequisites(CandidateDefinition candidate, Func<string, string?> environment)
    {
        if (string.Equals(candidate.ModelDeployment.Provider, "scripted", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A requested live eval cannot use the scripted provider.");
        }

        if (string.IsNullOrWhiteSpace(candidate.CredentialEnvironmentVariable) || !IsSafeEnvironmentVariableName(candidate.CredentialEnvironmentVariable))
        {
            throw new InvalidOperationException("The live candidate must pin a safe, valid credential environment variable name.");
        }

        string? credential = environment(candidate.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException($"Requested live eval is missing credential '{candidate.CredentialEnvironmentVariable}'.");
        }

        return credential;
    }

    private static void Validate(CandidateDefinition candidate)
    {
        if (candidate.SchemaVersion != "sre-eval-candidate/v1")
        {
            throw new InvalidDataException($"Unsupported candidate schema '{candidate.SchemaVersion}'.");
        }

        string[] identities =
        [
            candidate.CandidateId,
            candidate.Tenant,
            candidate.ProviderConnectionRevision.Id,
            candidate.ProviderConnectionRevision.Version,
            candidate.ProviderConnectionRevision.Endpoint,
            candidate.ModelDeployment.Id,
            candidate.ModelDeployment.Version,
            candidate.AssignmentRelease.Id,
            candidate.AssignmentRelease.Version,
            candidate.Releases.Agent,
            candidate.Releases.Prompt,
            candidate.Releases.Model,
            candidate.Releases.Tool,
            candidate.Releases.Policy,
        ];

        if (identities.Any(static value => string.IsNullOrWhiteSpace(value) || value.Contains("latest", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Candidate identities must be exact and cannot use latest or mutable aliases.");
        }

        // On Unix a rooted path such as "/v1/chat" parses as an absolute file
        // URI, so TryCreate alone accepts it there and rejects it on Windows. An
        // endpoint is absolute only when it names its scheme itself.
        string endpoint = candidate.ProviderConnectionRevision.Endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            || (uri.IsFile && !endpoint.StartsWith(Uri.UriSchemeFile + ":", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Candidate endpoint '{candidate.ProviderConnectionRevision.Endpoint}' must be a valid absolute URI.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Candidate endpoint '{candidate.ProviderConnectionRevision.Endpoint}' must use HTTPS.");
        }

        if (candidate.CredentialEnvironmentVariable is not null && !IsSafeEnvironmentVariableName(candidate.CredentialEnvironmentVariable))
        {
            throw new InvalidDataException($"Credential environment variable name '{candidate.CredentialEnvironmentVariable}' is invalid. It must be a 1-128 character alphanumeric identifier allowing underscores.");
        }
        if (string.Equals(candidate.ModelDeployment.Provider, "azure_openai", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(candidate.ProviderConnectionRevision.ApiVersion))
        {
            throw new InvalidDataException("Azure OpenAI candidates must pin an api_version in provider_connection_revision.");
        }
        foreach (string hash in new[] { candidate.ProviderConnectionRevision.Hash, candidate.ModelDeployment.Hash, candidate.AssignmentRelease.Hash })
        {
            if (!Sha256().IsMatch(hash))
            {
                throw new InvalidDataException("Candidate revision hashes must be lowercase SHA-256 values.");
            }
        }
    }
    internal static bool IsSafeEnvironmentVariableName(string name)
        => name.Length is > 0 and <= 128
           && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');


    private static VersionedIdentity Release(string release, string family, string hash)
        => new() { Id = release, Version = family + "/v1", Hash = hash };

    private static string HashAgentRelease(string release)
        => HashReleaseFiles(Path.Combine(RepositoryPaths.Root, "config", "agents"), release, "*.yaml");

    private static string HashModelRelease(string release)
        => HashReleaseFiles(Path.Combine(RepositoryPaths.Root, "config", "models"), release, "*.yaml");

    private static string HashToolRelease(string release)
        => HashReleaseFiles(Path.Combine(RepositoryPaths.Root, "config", "tools"), release, "*.yaml");

    private static string HashPromptRelease(string release)
        => HashReleaseFiles(Path.Combine(RepositoryPaths.Root, "prompts"), release, "*.yaml");

    private static string HashPolicyRelease(string release)
    {
        string path = Path.Combine(RepositoryPaths.Root, "config", "policies", release + ".yaml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Pinned policy release not found.", path);
        }

        return CanonicalJson.HashFiles([path]);
    }

    private static string HashReleaseFiles(string root, string release, string pattern)
    {
        string releasePath = Path.Combine(root, "releases", release + ".yaml");
        if (!File.Exists(releasePath))
        {
            throw new FileNotFoundException("Pinned release not found.", releasePath);
        }

        IEnumerable<string> definitions = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}releases{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        return CanonicalJson.HashFiles(definitions.Append(releasePath));
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}
