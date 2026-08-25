using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Resolves agent definitions through a pinned release.
/// </summary>
/// <remarks>
/// Backed by a frozen dictionary built once at startup, so lookups are constant
/// time and allocation-free. Routing consults this for every candidate agent on
/// every incident, which makes it hot enough that a per-call dictionary rebuild
/// or a database round trip would show up in investigation latency.
/// </remarks>
public interface IAgentRegistry
{
    ValueTask<Result<AgentDefinition>> ResolveAsync(
        string releaseName,
        string agentName,
        CancellationToken cancellationToken);

    /// <summary>Every agent pinned by a release, for routing and validation.</summary>
    ValueTask<Result<IReadOnlyList<AgentDefinition>>> GetReleaseAgentsAsync(
        string releaseName,
        CancellationToken cancellationToken);

    /// <summary>Every loaded definition, for startup validation.</summary>
    IReadOnlyCollection<AgentDefinition> All { get; }
}
