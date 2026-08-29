using System.ComponentModel.DataAnnotations;

namespace SreAiAgent.Infrastructure.Configuration;

// Options for every external dependency (ARCHITECTURE.md 13).
//
// All of these are bound with ValidateDataAnnotations and ValidateOnStart. A
// process that is misconfigured must fail at boot, loudly, rather than at three
// in the morning when the first incident tries to reach a backend whose URL was
// never set. A pod that will not start is a deployment failure someone notices;
// a pod that starts and then cannot investigate is an outage.
//
// Secrets are deliberately absent from these types where possible. Where a
// secret is unavoidable it is a reference to be resolved, never the value, so an
// IOptions instance dumped into a log or an exception cannot leak one.

/// <summary>PostgreSQL connection and pooling.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Connection string. Supplied through a Kubernetes secret reference or a
    /// managed secret store in production; a local `.env` is for development only.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Command timeout. Kept well under the tool timeout.</summary>
    [Range(1, 120)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Apply EF Core migrations at startup. Off in production.</summary>
    public bool MigrateOnStartup { get; init; }

    /// <summary>
    /// How long a resolved incident blocks a new one for the same fingerprint.
    /// Too short and a flapping alert opens an incident per flap; too long and a
    /// genuine recurrence is silently folded into a closed investigation.
    /// </summary>
    [Range(1, 1440)]
    public int IncidentDeduplicationWindowMinutes { get; init; } = 60;
}

/// <summary>Kafka connection and consumer behaviour.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ConsumerGroup { get; init; } = "sre-ai-agent";

    /// <summary>
    /// Always false. The offset is committed by hand only after the inbox row and
    /// the effect have been written, which is what makes at-least-once delivery
    /// safe (ARCHITECTURE.md 10.2). Auto-commit would acknowledge messages the
    /// platform has not finished with.
    /// </summary>
    public bool EnableAutoCommit { get; } // deliberately get-only and false

    /// <summary>Maximum messages held in the bounded channel between consume and process.</summary>
    [Range(1, 10000)]
    public int MaxInFlightMessages { get; init; } = 100;

    /// <summary>How long a poisoned message is retried before it is dead-lettered.</summary>
    [Range(1, 20)]
    public int MaxDeliveryAttempts { get; init; } = 5;
}

/// <summary>Language model gateway behaviour.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// Which adapter backs <c>IChatClient</c>. V1 ships only <c>scripted</c>; a
    /// real provider is a one-line registration and a package reference.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; init; } = "scripted";

    /// <summary>
    /// Concrete model name per environment, keyed by the profile's `model` field
    /// placeholder. Keeps one profile artifact usable in staging and production.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelNameByProfile { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Retries for a transient provider failure. Bounded, with jitter.</summary>
    [Range(0, 5)]
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Whether a rendered prompt may be logged. Defaults to false and should stay
    /// false outside a local investigation of the platform itself: a rendered
    /// prompt contains the retrieved evidence and is the richest source of
    /// sensitive data in the system (ARCHITECTURE.md 8.1).
    /// </summary>
    public bool LogRenderedPrompts { get; init; }
}

/// <summary>Where configuration artifacts are loaded from, and which releases are pinned.</summary>
public sealed class RegistryOptions
{
    public const string SectionName = "Registries";

    /// <summary>Root of the `config` directory.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConfigurationRoot { get; init; } = "config";

    /// <summary>Root of the `prompts` directory.</summary>
    [Required(AllowEmptyStrings = false)]
    public string PromptRoot { get; init; } = "prompts";

    [Required(AllowEmptyStrings = false)]
    public string AgentRelease { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string PromptRelease { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ModelRelease { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ToolRelease { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string PolicyRelease { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string FlagRelease { get; init; } = string.Empty;
}

/// <summary>Authentication, authorization and request limits.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>OIDC authority. Required outside development.</summary>
    public string? Authority { get; init; }

    public string? Audience { get; init; }

    /// <summary>
    /// Bypasses JWT validation for local development. Guarded at startup: a host
    /// running in Production refuses to start with this enabled, because a
    /// development convenience left on in production is an open API.
    /// </summary>
    public bool AllowDevelopmentAuthentication { get; init; }

    /// <summary>Maximum accepted request body, in bytes.</summary>
    [Range(1024, 10 * 1024 * 1024)]
    public int MaxRequestBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Requests per minute per authenticated principal.</summary>
    [Range(1, 100000)]
    public int RateLimitPerMinute { get; init; } = 600;
}

/// <summary>Observability backend endpoints.</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// Use in-memory fake adapters instead of real backends. Local development
    /// does not reach production observability endpoints (ARCHITECTURE.md 19.3).
    /// </summary>
    public bool UseFakeProviders { get; init; }

    public string? PrometheusBaseAddress { get; init; }

    public string? LogBackendBaseAddress { get; init; }

    public string? TraceBackendBaseAddress { get; init; }

    /// <summary>Kubernetes namespaces this deployment may inspect, keyed by cluster.</summary>
    public IReadOnlyDictionary<string, string> KubernetesNamespaceByCluster { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Per-request timeout for a backend call. Must stay below the tool timeout
    /// in the tool artifact, which is itself below the agent timeout
    /// (ARCHITECTURE.md 11.3).
    /// </summary>
    [Range(1, 60)]
    public int RequestTimeoutSeconds { get; init; } = 8;

    /// <summary>Floor on metric resolution, matching the tool schema's minimum.</summary>
    [Range(1, 3600)]
    public int MinStepSeconds { get; init; } = 15;

    /// <summary>
    /// Point budget per series. A legal step and a legal window still multiply,
    /// and Prometheus refuses a range query above eleven thousand points.
    /// </summary>
    [Range(1, 11000)]
    public int MaxPointsPerSeries { get; init; } = 720;

    /// <summary>
    /// Series budget. A query that fans out across every pod would crowd the
    /// evidence that matters out of the prompt.
    /// </summary>
    [Range(1, 500)]
    public int MaxSeries { get; init; } = 20;

    /// <summary>
    /// Entry budget for a log search, applied on top of the tool schema's own
    /// limit. The count of matches is reported separately, so trimming the page
    /// does not hide how much there was.
    /// </summary>
    [Range(1, 1000)]
    public int MaxLogEntries { get; init; } = 200;
}

/// <summary>OpenTelemetry export configuration.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    [Required(AllowEmptyStrings = false)]
    public string ServiceName { get; init; } = "sre-ai-agent";

    public string? OtlpEndpoint { get; init; }

    /// <summary>
    /// Sampling ratio for HTTP ingress. Investigations are always sampled
    /// regardless: they are low-volume and high-value, and a trace of the one
    /// incident someone is asking about is worth more than a uniform percentage.
    /// </summary>
    [Range(0, 1)]
    public double HttpSamplingRatio { get; init; } = 0.1;

    /// <summary>Expose the Prometheus scrape endpoint at <c>/metrics</c>.</summary>
    public bool EnablePrometheusEndpoint { get; init; } = true;
}

/// <summary>Transactional outbox dispatcher behaviour.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Whether this host drains the outbox. Safe to enable on several replicas.</summary>
    public bool Enabled { get; init; } = true;

    [Range(1, 1000)]
    public int BatchSize { get; init; } = 100;

    /// <summary>Idle poll interval. A write also signals the dispatcher directly.</summary>
    [Range(50, 60000)]
    public int PollIntervalMilliseconds { get; init; } = 500;

    /// <summary>Attempts before a message is dead-lettered rather than retried forever.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; init; } = 8;
}
