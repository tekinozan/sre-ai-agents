using System.Diagnostics;

namespace SreAiAgent.Application.Diagnostics;

/// <summary>
/// The activity sources the platform emits from (ARCHITECTURE.md 16.1).
/// </summary>
/// <remarks>
/// These live in the application layer on purpose. <see cref="ActivitySource"/>
/// and <see cref="System.Diagnostics.Metrics.Meter"/> are BCL primitives, not
/// OpenTelemetry types; the SDK, the exporters, the sampler and the resource
/// attributes are wired up in Infrastructure and the hosts. Section 1.2 forbids
/// OpenTelemetry implementation details here, and nothing in this file is one.
/// The practical benefit is that instrumentation sits next to the logic it
/// describes instead of being bolted on from outside.
///
/// One source per module rather than one for everything, so an operator can
/// enable tracing for tool execution alone without also collecting every
/// persistence span.
/// </remarks>
public static class SreActivitySources
{
    /// <summary>Version stamped on every source, so a trace records which build produced it.</summary>
    public static readonly string Version =
        typeof(SreActivitySources).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>Root investigation spans.</summary>
    public static readonly ActivitySource Investigation = new("SreAiAgent.Investigation", Version);

    /// <summary>Classification, routing and agent execution.</summary>
    public static readonly ActivitySource Agent = new("SreAiAgent.Agent", Version);

    /// <summary>Prompt rendering and model calls.</summary>
    public static readonly ActivitySource Llm = new("SreAiAgent.Llm", Version);

    /// <summary>Tool gateway executions, including refusals.</summary>
    public static readonly ActivitySource Tool = new("SreAiAgent.Tool", Version);

    /// <summary>Outbox dispatch and Kafka produce/consume.</summary>
    public static readonly ActivitySource Messaging = new("SreAiAgent.Messaging", Version);

    /// <summary>Repository and unit-of-work operations.</summary>
    public static readonly ActivitySource Persistence = new("SreAiAgent.Persistence", Version);

    /// <summary>Every source name, for registering them with the tracer provider.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "SreAiAgent.Investigation",
        "SreAiAgent.Agent",
        "SreAiAgent.Llm",
        "SreAiAgent.Tool",
        "SreAiAgent.Messaging",
        "SreAiAgent.Persistence",
    ];
}
