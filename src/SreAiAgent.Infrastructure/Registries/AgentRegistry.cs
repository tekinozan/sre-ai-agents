using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Loads agent definitions and resolves them through pinned releases
/// (ARCHITECTURE.md 7.1).
/// </summary>
internal sealed class AgentRegistry : IAgentRegistry
{
    private readonly FrozenDictionary<string, AgentDefinition> _agents;
    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _releases;
    private readonly FrozenDictionary<string, IReadOnlyList<AgentDefinition>> _releaseAgents;

    public AgentRegistry(IOptions<RegistryOptions> options, YamlArtifactLoader loader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loader);

        string root = Path.Combine(options.Value.ConfigurationRoot, "agents");

        Dictionary<string, AgentDefinition> agents = new(StringComparer.Ordinal);
        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(root))
        {
            AgentDefinition definition = ReadDefinition(loader.Load<AgentDocument>(path));
            if (!agents.TryAdd(definition.Reference, definition))
            {
                throw new ArtifactLoadException(path, $"Duplicate agent '{definition.Reference}'.");
            }
        }

        Dictionary<string, FrozenDictionary<string, string>> releases = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<AgentDefinition>> releaseAgents = new(StringComparer.Ordinal);

        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(Path.Combine(root, "releases")))
        {
            LoadedArtifact<AgentReleaseDocument> artifact = loader.Load<AgentReleaseDocument>(path);
            AgentReleaseDocument document = artifact.Value;

            if (string.IsNullOrWhiteSpace(document.Name) || document.Agents is null or { Count: 0 })
            {
                throw new ArtifactLoadException(path, "An agent release requires 'name' and a non-empty 'agents' map.");
            }

            releases[document.Name] = document.Agents.ToFrozenDictionary(StringComparer.Ordinal);

            // Materialized once per release rather than projected on every
            // routing call. Routing enumerates every candidate agent for every
            // incident, and rebuilding this list each time would allocate on the
            // hottest path of the workflow.
            List<AgentDefinition> pinned = new(document.Agents.Count);
            foreach ((string name, string version) in document.Agents)
            {
                if (!agents.TryGetValue($"{name}:{version}", out AgentDefinition? definition))
                {
                    throw new ArtifactLoadException(
                        path, $"Release pins agent '{name}:{version}' which was not found on disk.");
                }

                pinned.Add(definition);
            }

            releaseAgents[document.Name] = pinned;
        }

        _agents = agents.ToFrozenDictionary(StringComparer.Ordinal);
        _releases = releases.ToFrozenDictionary(StringComparer.Ordinal);
        _releaseAgents = releaseAgents.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentDefinition> All => _agents.Values;

    /// <inheritdoc />
    public ValueTask<Result<AgentDefinition>> ResolveAsync(
        string releaseName,
        string agentName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_releases.TryGetValue(releaseName, out FrozenDictionary<string, string>? release))
        {
            return ValueTask.FromResult(Result<AgentDefinition>.Failure(Error.Permanent(
                ApplicationErrorCodes.AgentNotFound,
                $"Agent release '{releaseName}' is not loaded.")));
        }

        if (!release.TryGetValue(agentName, out string? version))
        {
            return ValueTask.FromResult(Result<AgentDefinition>.Failure(Error.Permanent(
                ApplicationErrorCodes.AgentNotFound,
                $"Release '{releaseName}' does not pin a version for agent '{agentName}'.")));
        }

        return ValueTask.FromResult(_agents.TryGetValue($"{agentName}:{version}", out AgentDefinition? definition)
            ? Result<AgentDefinition>.Success(definition)
            : Result<AgentDefinition>.Failure(Error.Permanent(
                ApplicationErrorCodes.AgentNotFound,
                $"Agent '{agentName}:{version}' pinned by release '{releaseName}' was not found on disk.")));
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<AgentDefinition>>> GetReleaseAgentsAsync(
        string releaseName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_releaseAgents.TryGetValue(releaseName, out IReadOnlyList<AgentDefinition>? agents)
            ? Result<IReadOnlyList<AgentDefinition>>.Success(agents)
            : Result<IReadOnlyList<AgentDefinition>>.Failure(Error.Permanent(
                ApplicationErrorCodes.AgentNotFound,
                $"Agent release '{releaseName}' is not loaded.")));
    }

    private static AgentDefinition ReadDefinition(LoadedArtifact<AgentDocument> artifact)
    {
        AgentDocument document = artifact.Value;
        string path = artifact.Path;

        if (string.IsNullOrWhiteSpace(document.Name) || string.IsNullOrWhiteSpace(document.Version))
        {
            throw new ArtifactLoadException(path, "An agent requires 'name' and 'version'.");
        }

        if (string.IsNullOrWhiteSpace(document.PromptRef) || string.IsNullOrWhiteSpace(document.ModelProfile))
        {
            throw new ArtifactLoadException(path, "An agent requires 'prompt_ref' and 'model_profile'.");
        }

        if (document.Execution is null)
        {
            throw new ArtifactLoadException(
                path,
                "An agent requires an 'execution' block. Budgets are not optional: an agent without a "
                + "ceiling is a model-and-tool loop with no exit condition.");
        }

        AgentExecutionDocument execution = document.Execution;
        if (execution.MaxIterations <= 0
            || execution.TimeoutSeconds <= 0
            || execution.MaxToolCalls <= 0
            || execution.MaxLlmCalls <= 0)
        {
            throw new ArtifactLoadException(path, "Every execution budget must be positive.");
        }

        List<string> allowedTools = document.Tools?.Allow ?? [];
        if (allowedTools.Count == 0)
        {
            throw new ArtifactLoadException(
                path,
                "An agent must allow at least one tool. An agent with no tools can only assert, never verify.");
        }

        return new AgentDefinition
        {
            Name = document.Name,
            Version = document.Version,
            PromptRef = document.PromptRef,
            ModelProfileRef = document.ModelProfile,
            Capabilities = (document.Capabilities ?? []).ToFrozenSet(StringComparer.Ordinal),
            IncidentTypes = (document.IncidentTypes ?? []).ToFrozenSet(StringComparer.Ordinal),
            AllowedTools = allowedTools.ToFrozenSet(StringComparer.Ordinal),
            Budget = new AgentExecutionBudget(
                execution.MaxIterations,
                TimeSpan.FromSeconds(execution.TimeoutSeconds),
                execution.MaxToolCalls,
                execution.MaxLlmCalls,
                execution.MaxTotalTokens,
                execution.MaxEstimatedCost),
            OutputSchema = document.OutputSchema ?? "AgentFinding",
        };
    }
}
