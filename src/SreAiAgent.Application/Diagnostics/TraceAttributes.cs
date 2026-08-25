using System.Collections.Frozen;
using System.Diagnostics;

namespace SreAiAgent.Application.Diagnostics;

/// <summary>
/// The only attributes permitted on a span, and the only way to set one.
/// </summary>
/// <remarks>
/// ARCHITECTURE.md 16.1 lists exactly which metadata may appear on a span and
/// states that prompts, tokens, credentials and raw tool results may not. That is
/// a rule nobody can hold in their head across a hundred call sites, so it is
/// enforced instead of documented: <c>Activity.SetTag</c> is on the banned-API
/// list, and <see cref="SetSafeTag"/> is the only route to a tag. A key outside
/// the allowlist is dropped rather than written.
///
/// Dropping instead of throwing is deliberate. A telemetry mistake must not take
/// down an investigation, and the rejection is still visible: it increments
/// <c>sre_trace_attribute_rejected_total</c>, which is exactly the kind of signal
/// that should page a platform owner rather than an on-call SRE.
/// </remarks>
public static class TraceAttributes
{
    public const string IncidentId = "incident.id";
    public const string IncidentStatus = "incident.status";
    public const string IncidentSeverity = "incident.severity";
    public const string Tenant = "tenant";
    public const string Environment = "deployment.environment";
    public const string Service = "service.under.investigation";

    public const string AgentName = "agent.name";
    public const string AgentVersion = "agent.version";
    public const string AgentIsShadow = "agent.shadow";

    public const string PromptName = "prompt.name";
    public const string PromptVersion = "prompt.version";

    /// <summary>SHA-256 of the rendered prompt. Identifies it without revealing it.</summary>
    public const string PromptHash = "prompt.hash";

    public const string ModelProvider = "model.provider";
    public const string ModelName = "model.name";

    public const string ToolName = "tool.name";
    public const string ToolVersion = "tool.version";
    public const string ToolRisk = "tool.risk";

    public const string ResultStatus = "result.status";
    public const string ErrorCode = "error.code";

    public const string ConfigurationSnapshotId = "config.snapshot.id";
    public const string CorrelationId = "correlation.id";
    public const string WorkflowAttempt = "workflow.attempt";
    public const string MessagingTopic = "messaging.destination.name";
    public const string MessagingEventType = "messaging.event.type";

    /// <summary>
    /// Counts, not contents. Knowing that a call consumed 4 tools and returned 12
    /// evidence rows is useful; the tool output itself is not span material.
    /// </summary>
    public const string ToolCallCount = "tool.call.count";

    public const string EvidenceCount = "evidence.count";

    private static readonly FrozenSet<string> Allowed = new[]
    {
        IncidentId, IncidentStatus, IncidentSeverity, Tenant, Environment, Service,
        AgentName, AgentVersion, AgentIsShadow,
        PromptName, PromptVersion, PromptHash,
        ModelProvider, ModelName,
        ToolName, ToolVersion, ToolRisk,
        ResultStatus, ErrorCode,
        ConfigurationSnapshotId, CorrelationId, WorkflowAttempt,
        MessagingTopic, MessagingEventType,
        ToolCallCount, EvidenceCount,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every permitted key, for the security tests to assert against.</summary>
    public static IReadOnlyCollection<string> AllowedKeys => Allowed;

    /// <summary>True when <paramref name="key"/> may appear on a span.</summary>
    public static bool IsAllowed(string key) => Allowed.Contains(key);

    /// <summary>
    /// Sets a tag if the key is allowed, otherwise drops it.
    /// </summary>
    /// <remarks>
    /// The null check on <paramref name="activity"/> is what makes this cheap
    /// when nothing is listening: with no exporter attached the activity is null,
    /// and neither the allowlist lookup nor the value boxing happens.
    /// </remarks>
    public static Activity? SetSafeTag(this Activity? activity, string key, object? value)
    {
        if (activity is null)
        {
            return activity;
        }

        if (!Allowed.Contains(key))
        {
            Interlocked.Increment(ref _rejectedTagCount);
            return activity;
        }

#pragma warning disable RS0030 // This is the single sanctioned call site.
        return activity.SetTag(key, value);
#pragma warning restore RS0030
    }

    /// <summary>
    /// How many tags have been rejected in this process. Published as
    /// <c>sre_trace_attribute_rejected_total</c>; a non-zero value means code was
    /// written that tried to put something on a span it should not have.
    /// </summary>
    public static long RejectedTagCount => Interlocked.Read(ref _rejectedTagCount);

    private static long _rejectedTagCount;
}
