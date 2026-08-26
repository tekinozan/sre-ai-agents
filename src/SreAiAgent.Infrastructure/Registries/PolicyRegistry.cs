using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Loads policy releases (ARCHITECTURE.md 6.2, 15.1 and 15.2).
/// </summary>
/// <remarks>
/// The timeout hierarchy of section 11.3 is validated here rather than trusted.
/// An inverted pair, for example a tool timeout longer than the agent timeout,
/// means the inner operation can never complete on its own terms: every tool call
/// would be cut short by the agent deadline instead, turning a recoverable
/// per-tool timeout into a whole failed agent. That is a subtle, expensive bug to
/// find in production and a one-line check at startup.
/// </remarks>
internal sealed class PolicyRegistry : IPolicyRegistry
{
    private readonly FrozenDictionary<string, PolicyRelease> _releases;

    public PolicyRegistry(IOptions<RegistryOptions> options, YamlArtifactLoader loader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loader);

        string root = Path.Combine(options.Value.ConfigurationRoot, "policies");
        Dictionary<string, PolicyRelease> releases = new(StringComparer.Ordinal);

        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(root))
        {
            PolicyRelease release = ReadRelease(loader.Load<PolicyDocument>(path));
            releases[release.Name] = release;
        }

        _releases = releases.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<PolicyRelease> All => _releases.Values;

    /// <inheritdoc />
    public ValueTask<Result<PolicyRelease>> ResolveAsync(string releaseName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_releases.TryGetValue(releaseName, out PolicyRelease? release)
            ? Result<PolicyRelease>.Success(release)
            : Result<PolicyRelease>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                $"Policy release '{releaseName}' is not loaded.")));
    }

    private static PolicyRelease ReadRelease(LoadedArtifact<PolicyDocument> artifact)
    {
        PolicyDocument document = artifact.Value;
        string path = artifact.Path;

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new ArtifactLoadException(path, "A policy release requires 'name'.");
        }

        if (document.ArgumentPolicy is null
            || document.RateLimits is null
            || document.Timeouts is null
            || document.Budgets is null)
        {
            throw new ArtifactLoadException(
                path,
                "A policy release requires 'argument_policy', 'rate_limits', 'timeouts' and 'budgets'. "
                + "A missing block would default to zero, which either blocks everything or bounds nothing.");
        }

        RiskLevel ceiling = document.RiskCeiling switch
        {
            "read_only" => RiskLevel.ReadOnly,
            "low" => RiskLevel.Low,
            "medium" => RiskLevel.Medium,
            "high" => RiskLevel.High,
            "destructive" => RiskLevel.Destructive,
            _ => throw new ArtifactLoadException(
                path, $"Unknown risk_ceiling '{document.RiskCeiling}'."),
        };

        TimeoutDocument timeouts = document.Timeouts;
        ValidateTimeoutHierarchy(timeouts, path);

        Dictionary<string, IReadOnlySet<string>> roleScopes = new(StringComparer.Ordinal);
        foreach ((string role, List<string> scopes) in document.RoleScopes ?? [])
        {
            roleScopes[role] = scopes.ToFrozenSet(StringComparer.Ordinal);
        }

        RateLimitDocument limits = document.RateLimits;
        BudgetDocument budgets = document.Budgets;
        ArgumentPolicyDocument arguments = document.ArgumentPolicy;

        return new PolicyRelease(
            Name: document.Name,
            RiskCeiling: ceiling,
            RoleScopes: roleScopes.ToFrozenDictionary(StringComparer.Ordinal),
            MaxWindowMinutes: arguments.MaxWindowMinutes,
            MaxResultBytes: arguments.MaxResultBytes,
            MinStepSeconds: arguments.MinStepSeconds,
            RateLimits: new PolicyRateLimits(
                limits.PerTenantPerMinute, limits.PerIncidentPerMinute, limits.PerToolPerMinute),
            Timeouts: new PolicyTimeouts(
                TimeSpan.FromSeconds(timeouts.HttpClientSeconds),
                TimeSpan.FromSeconds(timeouts.ToolSeconds),
                TimeSpan.FromSeconds(timeouts.AgentSeconds),
                TimeSpan.FromSeconds(timeouts.WorkflowSeconds)),
            Budgets: new PolicyBudgets(
                budgets.MaxLlmCallsPerIncident,
                budgets.MaxToolCallsPerIncident,
                budgets.MaxTotalTokensPerIncident,
                budgets.MaxEstimatedCostPerIncident,
                TimeSpan.FromSeconds(budgets.MaxRuntimeSeconds)));
    }

    private static void ValidateTimeoutHierarchy(TimeoutDocument timeouts, string path)
    {
        if (timeouts.HttpClientSeconds <= 0
            || timeouts.ToolSeconds <= 0
            || timeouts.AgentSeconds <= 0
            || timeouts.WorkflowSeconds <= 0)
        {
            throw new ArtifactLoadException(path, "Every timeout must be positive.");
        }

        if (timeouts.HttpClientSeconds >= timeouts.ToolSeconds
            || timeouts.ToolSeconds >= timeouts.AgentSeconds
            || timeouts.AgentSeconds >= timeouts.WorkflowSeconds)
        {
            throw new ArtifactLoadException(
                path,
                "Timeouts must satisfy http_client < tool < agent < workflow (ARCHITECTURE.md 11.3). "
                + $"Got http={timeouts.HttpClientSeconds.ToString(CultureInfo.InvariantCulture)}s, "
                + $"tool={timeouts.ToolSeconds.ToString(CultureInfo.InvariantCulture)}s, "
                + $"agent={timeouts.AgentSeconds.ToString(CultureInfo.InvariantCulture)}s, "
                + $"workflow={timeouts.WorkflowSeconds.ToString(CultureInfo.InvariantCulture)}s. "
                + "An inverted pair means the inner operation is always cut short by the outer one, "
                + "so a recoverable per-tool timeout becomes a failed agent instead.");
        }
    }
}
