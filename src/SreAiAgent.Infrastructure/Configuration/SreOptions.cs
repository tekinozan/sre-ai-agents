using System.ComponentModel.DataAnnotations;
using Confluent.Kafka;

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
public sealed class KafkaOptions : IValidatableObject
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

    /// <summary>Wire protocol. Plaintext is for the local compose stack only.</summary>
    public SecurityProtocol SecurityProtocol { get; init; } = SecurityProtocol.Plaintext;

    public SaslMechanism? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }

    /// <summary>Filesystem path to a PEM CA bundle mounted into the workload.</summary>
    public string? SslCaLocation { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SecurityProtocol is not (SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl))
        {
            yield break;
        }

        if (SaslMechanism is null)
        {
            yield return new ValidationResult(
                "Kafka:SaslMechanism is required when the security protocol uses SASL.",
                [nameof(SaslMechanism)]);
        }

        if (string.IsNullOrWhiteSpace(SaslUsername))
        {
            yield return new ValidationResult(
                "Kafka:SaslUsername is required when the security protocol uses SASL.",
                [nameof(SaslUsername)]);
        }

        if (string.IsNullOrWhiteSpace(SaslPassword))
        {
            yield return new ValidationResult(
                "Kafka:SaslPassword is required when the security protocol uses SASL.",
                [nameof(SaslPassword)]);
        }
    }
}

/// <summary>Language model gateway behaviour.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Root containing mounted secret files addressed by <c>file://</c> references.</summary>
    [Required(AllowEmptyStrings = false)]
    public string CredentialFileRoot { get; init; } = "/var/run/secrets/sre-ai";

    /// <summary>Production allowlist for OpenAI-compatible custom endpoint hosts.</summary>
    public IReadOnlyList<string> AllowedCustomEndpointHosts { get; init; } = [];

    /// <summary>Activation always requires a recorded passing evaluation.</summary>
    public bool RequireEvaluationForActivation { get; init; } = true;

    /// <summary>Legacy artifact placeholder resolution; runtime selection uses pinned assignments.</summary>
    public IReadOnlyDictionary<string, string> ModelNameByProfile { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Retained for shipped profile validation; it is never used to select a runtime provider.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; init; } = "scripted";

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

    public BootstrapOptions? Bootstrap { get; init; }

    public sealed class BootstrapOptions
    {
        [Required(AllowEmptyStrings = false)] public string Tenant { get; init; } = string.Empty;
        [Required(AllowEmptyStrings = false)] public string ConnectionDisplayName { get; init; } = "default";
        [Required] public SreAiAgent.Domain.ModelProviders.ProviderAdapterKind AdapterKind { get; init; } = SreAiAgent.Domain.ModelProviders.ProviderAdapterKind.Scripted;
        [Required(AllowEmptyStrings = false)] public string Endpoint { get; init; } = string.Empty;
        public string? Region { get; init; }
        public string? ApiVersion { get; init; }
        public string? Organization { get; init; }
        [Required(AllowEmptyStrings = false)] public string CredentialRef { get; init; } = string.Empty;
        [Required(AllowEmptyStrings = false)] public string AssignmentName { get; init; } = "bootstrap";
        [Required(AllowEmptyStrings = false)] public string ClassifierProfile { get; init; } = "fast";
        [Required(AllowEmptyStrings = false)] public string CorrelatorProfile { get; init; } = "analytical";
    }
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
    /// Replaced by the per-port selectors below. Nullable so that a configuration
    /// still setting it fails startup instead of silently getting the defaults.
    /// </summary>
    /// <remarks>
    /// One switch could not express the state this platform is actually in, where
    /// some ports have a real adapter and others do not. Left as a plain bool it
    /// would be indistinguishable from unset, and a deployment that meant
    /// <c>false</c> would quietly investigate fabricated data — the exact outcome
    /// the original guard existed to prevent.
    /// </remarks>
    public bool? UseFakeProviders { get; init; }

    /// <summary>Metrics backend: <c>fake</c> or <c>prometheus</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string MetricsProvider { get; init; } = ObservabilityProviders.Fake;

    /// <summary>Log backend: <c>fake</c> or <c>loki</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string LogProvider { get; init; } = ObservabilityProviders.Fake;

    /// <summary>Kubernetes backend: <c>fake</c> or <c>incluster</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string KubernetesProvider { get; init; } = ObservabilityProviders.Fake;

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
    /// Pod and event budget. A namespace during an incident can hold hundreds of
    /// both, and the ones that matter are the ones that changed most recently.
    /// </summary>
    [Range(1, 1000)]
    public int MaxKubernetesItems { get; init; } = 100;

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

/// <summary>The accepted values of the per-port observability selectors.</summary>
/// <remarks>
/// Named constants rather than an enum because these are configuration strings an
/// operator types into a ConfigMap, and a binding failure on an enum reports the
/// property rather than the value that was wrong.
/// </remarks>
public static class ObservabilityProviders
{
    public const string Fake = "fake";

    public const string Prometheus = "prometheus";

    public const string Loki = "loki";

    /// <summary>Reads pods and events from the API server of the cluster it runs in.</summary>
    public const string InCluster = "incluster";
}
