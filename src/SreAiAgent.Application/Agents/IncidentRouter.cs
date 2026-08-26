using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Chooses which experts investigate an incident (ARCHITECTURE.md 11.2, 24.3).
/// </summary>
/// <remarks>
/// Selection is capability first, availability second, and the two are kept
/// apart. Capability asks whether an agent could contribute; availability asks
/// whether it is allowed to. Merging them would mean a disabled agent looked
/// identical to an irrelevant one in the audit trail, and "why did the database
/// expert not run" is a question somebody asks in every post-incident review.
/// </remarks>
public sealed class IncidentRouter : IIncidentRouter
{
    private readonly IAgentRegistry _agents;
    private readonly IAgentFlagEvaluator _flags;
    private readonly IReadOnlyToolRegistryProbe _tools;
    private readonly AgentRuntimeOptions _options;

    public IncidentRouter(
        IAgentRegistry agents,
        IAgentFlagEvaluator flags,
        IReadOnlyToolRegistryProbe tools,
        IOptions<AgentRuntimeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _agents = agents;
        _flags = flags;
        _tools = tools;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<Result<AgentSelection>> SelectAsync(
        IncidentSnapshot incident,
        IncidentClassification classification,
        ConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(snapshot);

        Result<IReadOnlyList<AgentDefinition>> candidates = await _agents
            .GetReleaseAgentsAsync(snapshot.AgentRelease, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.IsFailure)
        {
            return Result<AgentSelection>.Failure(candidates.Error);
        }

        List<SelectedAgent> selected = [];
        List<ExcludedAgent> excluded = [];

        // A low-confidence classification widens the net rather than narrowing
        // it. Routing to three experts and having one report "nothing in my
        // layer" costs one agent run; routing to none leaves an incident with no
        // investigation at all.
        bool widen = classification.Confidence.Value < (decimal)_options.LowConfidenceThreshold;

        foreach (AgentDefinition agent in candidates.Value)
        {
            if (!widen && !MatchesIncidentType(agent, classification))
            {
                excluded.Add(new ExcludedAgent(
                    agent.Name,
                    agent.Version,
                    $"Handles [{string.Join(", ", agent.IncidentTypes)}] but the incident was classified as "
                    + $"[{string.Join(", ", classification.IncidentTypes)}]."));
                continue;
            }

            // A missing tool is a capability failure, not an availability one. An
            // agent whose only metrics tool was removed from the release cannot
            // verify anything, and an agent that can only assert is worse than no
            // agent: it produces confident, uncited claims.
            string? missingTool = FirstMissingTool(agent);
            if (missingTool is not null)
            {
                excluded.Add(new ExcludedAgent(
                    agent.Name, agent.Version, $"Required tool '{missingTool}' is not in the pinned tool release."));
                continue;
            }

            AgentAvailability availability = await _flags
                .EvaluateAsync(agent, incident, classification.IncidentTypes, snapshot, cancellationToken)
                .ConfigureAwait(false);

            if (availability.State is AgentAvailabilityState.KillSwitched)
            {
                excluded.Add(new ExcludedAgent(agent.Name, agent.Version, "Kill switch is engaged."));
                continue;
            }

            if (availability.IsShadow)
            {
                // Shadow agents are selected but flagged, so they run against the
                // same evidence while their output stays out of the report.
                selected.Add(new SelectedAgent(agent.Name, agent.Version, IsShadow: true));
                continue;
            }

            if (!availability.IsSelectable)
            {
                excluded.Add(new ExcludedAgent(
                    agent.Name, agent.Version, availability.Reason ?? "Not available."));
                continue;
            }

            selected.Add(new SelectedAgent(agent.Name, agent.Version, IsShadow: false));
        }

        // The cap applies to real agents only. A shadow run is an evaluation
        // concern and must not displace an expert the operator depends on.
        List<SelectedAgent> real = [.. selected.Where(static agent => !agent.IsShadow)];
        if (real.Count > _options.MaxAgentsPerInvestigation)
        {
            foreach (SelectedAgent dropped in real.Skip(_options.MaxAgentsPerInvestigation))
            {
                excluded.Add(new ExcludedAgent(
                    dropped.AgentName,
                    dropped.AgentVersion,
                    $"Exceeded the per-investigation limit of "
                    + $"{_options.MaxAgentsPerInvestigation.ToString(CultureInfo.InvariantCulture)} agents."));
            }

            selected =
            [
                .. real.Take(_options.MaxAgentsPerInvestigation),
                .. selected.Where(static agent => agent.IsShadow),
            ];
        }

        return Result<AgentSelection>.Success(new AgentSelection(selected, excluded));
    }

    private static bool MatchesIncidentType(AgentDefinition agent, IncidentClassification classification)
    {
        foreach (string type in classification.IncidentTypes)
        {
            if (agent.IncidentTypes.Contains(type))
            {
                return true;
            }
        }

        return false;
    }

    private string? FirstMissingTool(AgentDefinition agent)
    {
        foreach (string tool in agent.AllowedTools)
        {
            if (!_tools.Contains(tool))
            {
                return tool;
            }
        }

        return null;
    }
}

/// <summary>
/// Whether a tool name exists in the pinned release.
/// </summary>
/// <remarks>
/// A narrow port rather than the full registry, because routing only ever asks
/// this one question. Depending on the whole registry here would let a future
/// change execute a tool from inside the router, which is not where tool
/// execution belongs.
/// </remarks>
public interface IReadOnlyToolRegistryProbe
{
    /// <summary>True when a tool with this name is in the pinned release.</summary>
    bool Contains(string toolName);
}

/// <summary>Agent runtime limits that are not per-agent.</summary>
public sealed class AgentRuntimeOptions
{
    public const string SectionName = "AgentRuntime";

    /// <summary>
    /// How many agents run concurrently in one fan-out. Bounded rather than
    /// unlimited: each agent holds a model connection and a database connection,
    /// and an unbounded fan-out turns a wide incident into a self-inflicted
    /// outage of the backends being investigated.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 4;

    public int MinAgentsPerInvestigation { get; init; } = 2;

    /// <summary>ARCHITECTURE.md 11.2 suggests two to five experts per investigation.</summary>
    public int MaxAgentsPerInvestigation { get; init; } = 5;

    /// <summary>Below this classification confidence, routing widens the selection.</summary>
    public double LowConfidenceThreshold { get; init; } = 0.5;
}
