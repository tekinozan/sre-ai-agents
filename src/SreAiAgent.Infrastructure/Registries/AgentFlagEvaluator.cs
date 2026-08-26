using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Evaluates agent availability from the pinned flag release
/// (ARCHITECTURE.md 24.3).
/// </summary>
/// <remarks>
/// This decides participation, never permission. Section 24.4 keeps the two
/// apart so a flag flip cannot become a privilege escalation: an agent that this
/// evaluator marks Active still executes under the risk ceiling from the policy
/// release, and the tool gateway still refuses anything above it.
///
/// Every path returns a reason. "The database expert did not run" is a question
/// somebody asks during a post-incident review, and the answer has to be
/// recoverable from the audit trail rather than reconstructed from logs.
/// </remarks>
internal sealed class AgentFlagEvaluator : IAgentFlagEvaluator
{
    private readonly FrozenDictionary<string, AgentFlag> _flags;
    private readonly AgentAvailabilityState _defaultState;
    private readonly TimeProvider _timeProvider;

    public AgentFlagEvaluator(
        IOptions<RegistryOptions> options,
        YamlArtifactLoader loader,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;

        string root = Path.Combine(options.Value.ConfigurationRoot, "flags");
        string flagSetVersion = ResolveFlagSetVersion(loader, root, options.Value.FlagRelease);

        FlagSetDocument flagSet = LoadFlagSet(loader, root, flagSetVersion);

        // An agent with no flag entry is disabled, not enabled. A new agent must
        // be switched on deliberately; defaulting to available would mean adding
        // a definition file is enough to put an untested expert in front of a
        // production incident.
        _defaultState = ParseState(flagSet.Defaults?.State) ?? AgentAvailabilityState.Disabled;

        Dictionary<string, AgentFlag> flags = new(StringComparer.Ordinal);
        foreach (AgentFlagDocument document in flagSet.Agents ?? [])
        {
            if (string.IsNullOrWhiteSpace(document.Name) || string.IsNullOrWhiteSpace(document.Version))
            {
                throw new ArtifactLoadException(root, "An agent flag requires 'name' and 'version'.");
            }

            flags[$"{document.Name}:{document.Version}"] = new AgentFlag(
                State: ParseState(document.Availability?.State) ?? AgentAvailabilityState.Disabled,
                Environments: (document.Availability?.Environments ?? []).ToFrozenDictionary(StringComparer.Ordinal),
                AllowedIncidentTypes: (document.Routing?.AllowedIncidentTypes ?? []).ToFrozenSet(StringComparer.Ordinal),
                RolloutMode: document.Rollout?.Mode ?? "all",
                RolloutTenants: (document.Rollout?.Tenants ?? []).ToFrozenSet(StringComparer.Ordinal));
        }

        _flags = flags.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<AgentAvailability> EvaluateAsync(
        AgentDefinition agent,
        IncidentSnapshot incident,
        IReadOnlyList<string> incidentTypes,
        ConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (!_flags.TryGetValue(agent.Reference, out AgentFlag? flag))
        {
            return Result(agent, _defaultState, "No flag entry; the default applies.", now);
        }

        // A kill switch is checked before anything else and overrides the pinned
        // snapshot. Section 24.5 makes it the one documented exception to
        // immutability, because it exists for the case where an agent is actively
        // causing harm and waiting for in-flight work to drain is unacceptable.
        if (flag.State is AgentAvailabilityState.KillSwitched)
        {
            return Result(agent, AgentAvailabilityState.KillSwitched, "Kill switch is engaged.", now);
        }

        if (flag.State is AgentAvailabilityState.Disabled)
        {
            return Result(agent, AgentAvailabilityState.Disabled, "Disabled by flag.", now);
        }

        if (flag.Environments.TryGetValue(incident.Environment, out bool enabled) && !enabled)
        {
            return Result(
                agent,
                AgentAvailabilityState.Disabled,
                $"Not enabled for environment '{incident.Environment}'.",
                now);
        }

        if (flag.Environments.Count > 0 && !flag.Environments.ContainsKey(incident.Environment))
        {
            return Result(
                agent,
                AgentAvailabilityState.Disabled,
                $"Environment '{incident.Environment}' is not listed on the flag.",
                now);
        }

        if (flag.AllowedIncidentTypes.Count > 0 && !IsTypeAllowed(flag, incidentTypes))
        {
            return Result(
                agent,
                AgentAvailabilityState.Disabled,
                $"Routing flag allows [{string.Join(", ", flag.AllowedIncidentTypes)}] but the incident "
                + $"was classified as [{string.Join(", ", incidentTypes)}].",
                now);
        }

        if (string.Equals(flag.RolloutMode, "tenant_allowlist", StringComparison.Ordinal)
            && !flag.RolloutTenants.Contains(incident.Tenant))
        {
            return Result(
                agent,
                AgentAvailabilityState.Disabled,
                $"Tenant '{incident.Tenant}' is not on the rollout allowlist.",
                now);
        }

        return Result(agent, flag.State, "Enabled by flag.", now);
    }

    /// <summary>
    /// True when any classified type is on the flag's allowlist. An unclassified
    /// incident passes: routing already widens the agent selection when
    /// classification is weak, and refusing every agent here would leave such an
    /// incident with no investigation at all.
    /// </summary>
    private static bool IsTypeAllowed(AgentFlag flag, IReadOnlyList<string> incidentTypes)
    {
        if (incidentTypes.Count == 0)
        {
            return true;
        }

        foreach (string type in incidentTypes)
        {
            if (flag.AllowedIncidentTypes.Contains(type))
            {
                return true;
            }
        }

        return false;
    }

    private static ValueTask<AgentAvailability> Result(
        AgentDefinition agent,
        AgentAvailabilityState state,
        string reason,
        DateTimeOffset now)
        => ValueTask.FromResult(new AgentAvailability(agent.Name, agent.Version, state, reason, now));

    private static AgentAvailabilityState? ParseState(string? value) => value switch
    {
        "disabled" => AgentAvailabilityState.Disabled,
        "shadow" => AgentAvailabilityState.Shadow,
        "canary" => AgentAvailabilityState.Canary,
        "read_only" => AgentAvailabilityState.ReadOnly,
        "active" => AgentAvailabilityState.Active,
        "kill_switched" => AgentAvailabilityState.KillSwitched,
        null => null,
        // An unrecognised state is treated as disabled rather than guessed at. A
        // typo in a flag file must not accidentally enable an agent.
        _ => AgentAvailabilityState.Disabled,
    };

    private static string ResolveFlagSetVersion(YamlArtifactLoader loader, string root, string releaseName)
    {
        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(Path.Combine(root, "releases")))
        {
            LoadedArtifact<FlagReleaseDocument> artifact = loader.Load<FlagReleaseDocument>(path);
            if (string.Equals(artifact.Value.Name, releaseName, StringComparison.Ordinal))
            {
                return artifact.Value.FlagSet
                    ?? throw new ArtifactLoadException(path, "A flag release requires 'flag_set'.");
            }
        }

        throw new ArtifactLoadException(root, $"Flag release '{releaseName}' was not found.");
    }

    private static FlagSetDocument LoadFlagSet(YamlArtifactLoader loader, string root, string version)
    {
        string path = Path.Combine(root, $"agent-flags.{version}.yaml");
        return loader.Load<FlagSetDocument>(path).Value;
    }

    private sealed record AgentFlag(
        AgentAvailabilityState State,
        FrozenDictionary<string, bool> Environments,
        FrozenSet<string> AllowedIncidentTypes,
        string RolloutMode,
        FrozenSet<string> RolloutTenants);
}
