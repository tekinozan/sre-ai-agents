using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Tools;

/// <summary>
/// The deterministic half of the security design (ARCHITECTURE.md 6.2, 15.1 and
/// 15.2), loaded from a pinned policy release.
/// </summary>
/// <param name="Name">Release name.</param>
/// <param name="RiskCeiling">Highest risk any execution may reach.</param>
/// <param name="RoleScopes">Role to the authorization scopes it grants.</param>
/// <param name="MaxWindowMinutes">
/// Largest observability window allowed. A schema says a value is well-formed;
/// this says it is reasonable. Thirty days is a valid integer and still an
/// indefensible question to ask a metrics backend mid-incident.
/// </param>
/// <param name="MaxResultBytes">Result-size cap applied before redaction.</param>
/// <param name="MinStepSeconds">Finest metric resolution allowed.</param>
/// <param name="RateLimits">Per-tenant, per-incident and per-tool ceilings.</param>
/// <param name="Timeouts">The timeout hierarchy of section 11.3.</param>
/// <param name="Budgets">Per-incident budget ceilings.</param>
public sealed record PolicyRelease(
    string Name,
    RiskLevel RiskCeiling,
    IReadOnlyDictionary<string, IReadOnlySet<string>> RoleScopes,
    int MaxWindowMinutes,
    int MaxResultBytes,
    int MinStepSeconds,
    PolicyRateLimits RateLimits,
    PolicyTimeouts Timeouts,
    PolicyBudgets Budgets);

/// <summary>
/// Ceilings that stop one runaway investigation exhausting a shared backend for
/// every other tenant (ARCHITECTURE.md 14).
/// </summary>
public sealed record PolicyRateLimits(
    int PerTenantPerMinute,
    int PerIncidentPerMinute,
    int PerToolPerMinute);

/// <summary>
/// The nested timeouts of ARCHITECTURE.md 11.3: http &lt; tool &lt; agent &lt;
/// workflow. Validated at startup, because an inverted pair means the inner
/// operation can never complete and every call becomes a timeout.
/// </summary>
public sealed record PolicyTimeouts(
    TimeSpan HttpClient,
    TimeSpan Tool,
    TimeSpan Agent,
    TimeSpan Workflow);

/// <summary>Per-incident budget ceilings from ARCHITECTURE.md 11.3.</summary>
public sealed record PolicyBudgets(
    int MaxLlmCalls,
    int MaxToolCalls,
    long MaxTotalTokens,
    decimal MaxEstimatedCost,
    TimeSpan MaxRuntime);

/// <summary>Resolves policy releases.</summary>
public interface IPolicyRegistry
{
    ValueTask<Result<PolicyRelease>> ResolveAsync(string releaseName, CancellationToken cancellationToken);

    IReadOnlyCollection<PolicyRelease> All { get; }
}
