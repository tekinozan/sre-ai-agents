using SreAiAgent.Domain.Remediation;

namespace SreAiAgent.Application.Abstractions.Tools;

/// <summary>
/// The authority under which a tool call runs (ARCHITECTURE.md 6.2 and 15.1).
/// </summary>
/// <remarks>
/// Passed explicitly rather than read from an ambient accessor. A tool executes
/// inside a workflow activity on a thread pool thread with no HTTP context, so an
/// ambient identity would either be absent or, worse, belong to whoever happened
/// to be there last. Explicit context also makes an over-broad execution visible
/// at the call site.
/// </remarks>
/// <param name="Tenant">Owning tenant. Scopes both policy and rate limits.</param>
/// <param name="Environment">Target environment, for example <c>production</c>.</param>
/// <param name="ServiceAllowlist">
/// Services this execution may touch. Empty means none: an allowlist that
/// defaults to permitting everything is not an allowlist.
/// </param>
/// <param name="Actor">Authenticated principal, or the platform for automated work.</param>
/// <param name="RiskCeiling">
/// The highest risk permitted. The gateway refuses anything above it regardless
/// of what the model asked for or what an approval said.
/// </param>
/// <param name="Deadline">Absolute UTC instant after which the call must not start.</param>
public sealed record ToolExecutionContext(
    string Tenant,
    string Environment,
    IReadOnlySet<string> ServiceAllowlist,
    string Actor,
    RiskLevel RiskCeiling,
    DateTimeOffset Deadline);
