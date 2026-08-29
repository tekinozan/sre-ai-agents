using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Audit;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability;
using SreAiAgent.Infrastructure.Registries;
using SreAiAgent.Infrastructure.Security;
using SreAiAgent.Infrastructure.Tools;
using SreAiAgent.Infrastructure.Tools.Adapters;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers the tool gateway pipeline of ARCHITECTURE.md 6.2 and the tool security
/// rules of 15.2.
/// </summary>
/// <remarks>
/// These are the tests that matter most in this repository. Every check here is
/// one that stands between a model, which can be steered by injected log content,
/// and a production system.
/// </remarks>
public sealed class ToolGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);

    private ToolGateway BuildGateway(
        IReadOnlySet<string>? actorScopes = null,
        IReadOnlySet<string>? agentTools = null,
        PolicyRelease? policy = null)
    {
        RegistryOptions registryOptions = new()
        {
            ConfigurationRoot = RepositoryPaths.Config,
            PromptRoot = RepositoryPaths.Prompts,
            AgentRelease = "agents-2026.08.25.1",
            PromptRelease = "sre-prompts-2026.08.25.1",
            ModelRelease = "models-2026.08.25.1",
            ToolRelease = "tools-2026.08.25.1",
            PolicyRelease = "policies-2026.08.25.1",
            FlagRelease = "flags-2026.08.25.1",
        };

        IOptions<RegistryOptions> options = Options.Create(registryOptions);
        YamlArtifactLoader loader = new();
        ReadOnlyToolRegistry tools = new(options, loader);
        PolicyRegistry policies = new(options, loader);

        PolicyRelease resolved = policy ?? policies.All.First();

        ObservabilityOptions observability = new();
        IOptions<ObservabilityOptions> observabilityOptions = Options.Create(observability);

        SreMetrics metrics = new(new TestMeterFactory());

        List<IToolAdapter> adapters =
        [
            new PrometheusQueryRangeAdapter(new FakeMetricsProvider(_time), _time),
            new PrometheusQueryAdapter(new FakeMetricsProvider(_time), _time),
            new LogSearchAdapter(new FakeLogProvider(), _time),
            new KubernetesPodsAdapter(new FakeKubernetesObservability(_time), observabilityOptions),
            new DatabaseHealthAdapter(new FakeDatabaseObservability(), _time),
        ];

        RecordingResolver resolver = new(
            resolved,
            actorScopes ?? DefaultScopes,
            agentTools ?? DefaultAgentTools);

        Resolver = resolver;

        return new ToolGateway(
            tools,
            new ToolRateLimiter(_time),
            new RegexRedactor(),
            new ToolAdapterCatalog(adapters),
            resolver,
            new RecordingAudit(),
            metrics,
            _time,
            NullLogger<ToolGateway>.Instance);
    }

    private RecordingResolver Resolver { get; set; } = null!;

    /// <summary>
    /// Accepts audit rows and discards them.
    /// </summary>
    /// <remarks>
    /// The gateway stages audit rows through a unit of work these tests do not
    /// have. What they assert is the policy pipeline, and a sink that records
    /// nothing keeps that the subject.
    /// </remarks>
    private sealed class RecordingAudit : IAuditSink
    {
        public void RecordToolCall(ToolCallAudit call)
        {
        }

        public void RecordLlmCall(LlmCallAudit call)
        {
        }
    }

    private static readonly FrozenSet<string> DefaultScopes =
        new[] { "metrics:read", "logs:read", "kubernetes:read", "database:read" }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> DefaultAgentTools =
        new[] { "prometheus.query", "prometheus.query_range", "logs.search", "database.health" }
            .ToFrozenSet(StringComparer.Ordinal);

    private static ToolExecutionContext Context(
        RiskLevel ceiling = RiskLevel.ReadOnly,
        params string[] allowedServices)
        => new(
            Tenant: "tenant-a",
            Environment: "production",
            ServiceAllowlist: (allowedServices.Length == 0 ? ["checkout"] : allowedServices)
                .ToFrozenSet(StringComparer.Ordinal),
            Actor: "investigation-worker",
            RiskCeiling: ceiling,
            Deadline: Now.AddMinutes(10));

    private static ToolExecutionRequest Request(string tool, string argumentsJson, string agent = "database-expert")
        => new(
            ToolName: tool,
            ToolVersion: "v1",
            Arguments: JsonDocument.Parse(argumentsJson).RootElement.Clone(),
            IncidentId: IncidentId.New(),
            AgentName: agent,
            AgentRunId: AgentRunId.New(),
            RequestId: Guid.CreateVersion7());

    [Fact]
    public async Task AValidReadOnlyCallSucceedsAndProducesEvidence()
    {
        ToolGateway gateway = BuildGateway();

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("database.health", """{"service":"checkout","window_minutes":30}"""),
            Context(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.EvidenceId.ShouldNotBeNull();
        result.Summary.ShouldNotBeNull();
        result.Summary.ShouldContain("connection pool");
        Resolver.Recorded.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ATooWideWindowIsDeniedByArgumentPolicy()
    {
        ToolGateway gateway = BuildGateway();

        // 30 days is a valid integer and an indefensible question to ask a
        // metrics backend during an incident.
        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("prometheus.query_range",
                """{"metric":"http_requests_total","service":"checkout","window_minutes":43200}"""),
            Context(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        // The schema bound rejects it first, which is the cheaper check; either
        // way it never reaches the backend.
        result.Error!.Code.Value.ShouldBeOneOf("TOOL_ARGUMENTS_INVALID", "TOOL_POLICY_DENIED");
        Resolver.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task AServiceOutsideTheAllowlistIsDenied()
    {
        ToolGateway gateway = BuildGateway();

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("database.health", """{"service":"payments","window_minutes":30}"""),
            Context(RiskLevel.ReadOnly, "checkout"),
            TestContext.Current.CancellationToken);

        // The allowlist is the tenant isolation boundary. Without it a model could
        // name any service and read another team's telemetry.
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.Value.ShouldBe("TOOL_POLICY_DENIED");
        result.Error.IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public async Task AToolOutsideTheAgentAllowlistIsDenied()
    {
        // The database expert asking for Kubernetes data is exactly what a prompt
        // injection in a log line would try to arrange.
        ToolGateway gateway = BuildGateway(agentTools: DefaultAgentTools);

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("kubernetes.pods", """{"namespace":"shop","service":"checkout"}"""),
            Context(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.Value.ShouldBe("TOOL_POLICY_DENIED");
        result.Error.Message.ShouldContain("not allowed to use tool");
    }

    [Fact]
    public async Task AMissingScopeIsDenied()
    {
        ToolGateway gateway = BuildGateway(
            actorScopes: new[] { "metrics:read" }.ToFrozenSet(StringComparer.Ordinal));

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("database.health", """{"service":"checkout","window_minutes":30}"""),
            Context(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Message.ShouldContain("database:read");
    }

    [Fact]
    public async Task ArgumentsThatFailTheSchemaNeverReachAnAdapter()
    {
        ToolGateway gateway = BuildGateway();

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("database.health", """{"service":"checkout","window_minutes":"thirty","evil":true}"""),
            Context(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.Value.ShouldBe("TOOL_ARGUMENTS_INVALID");
        Resolver.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnknownToolIsRefused()
    {
        ToolGateway gateway = BuildGateway();

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("shell.execute", """{"command":"rm -rf /"}"""),
            Context(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.Value.ShouldBe("TOOL_NOT_FOUND");
    }

    [Fact]
    public async Task ACallThatCannotFinishBeforeTheDeadlineIsRefused()
    {
        ToolGateway gateway = BuildGateway();

        ToolExecutionContext expiring = Context() with { Deadline = Now.AddSeconds(2) };

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("database.health", """{"service":"checkout","window_minutes":30}"""),
            expiring,
            TestContext.Current.CancellationToken);

        // Starting work guaranteed to be abandoned costs the backend real load for
        // no possible benefit.
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.Value.ShouldBe("WORKFLOW_DEADLINE_EXCEEDED");
    }

    [Fact]
    public async Task EvidenceIsRedactedBeforeItIsStored()
    {
        ToolGateway gateway = BuildGateway();

        ToolExecutionResult result = await gateway.ExecuteAsync(
            Request("logs.search", """{"service":"checkout","window_minutes":30,"minimum_level":"error"}"""),
            Context(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        EvidenceRecord evidence = Resolver.Recorded.ShouldHaveSingleItem();
        evidence.RedactionStatus.ShouldBeOneOf(RedactionStatus.Clean, RedactionStatus.Redacted);
        evidence.IsUsableInPrompt.ShouldBeTrue();
        evidence.ContentHash.Length.ShouldBe(64);
    }

    [Fact]
    public async Task RateLimitsBoundOneInvestigation()
    {
        PolicyRelease strict = BuildStrictPolicy();
        ToolGateway gateway = BuildGateway(policy: strict);
        ToolExecutionContext context = Context();
        IncidentId incidentId = IncidentId.New();

        List<ToolExecutionResult> results = [];
        for (int i = 0; i < 4; i++)
        {
            ToolExecutionRequest request = Request(
                "database.health", """{"service":"checkout","window_minutes":30}""") with
            {
                IncidentId = incidentId,
            };

            results.Add(await gateway.ExecuteAsync(request, context, TestContext.Current.CancellationToken));
        }

        // One runaway investigation must not degrade a shared backend for every
        // other tenant, and the backend is frequently the one being investigated.
        results.Count(static result => result.IsSuccess).ShouldBe(2);
        results.Last().Error!.Code.Value.ShouldBe("TOOL_RATE_LIMITED");
        results.Last().Error!.IsRetryable.ShouldBeTrue();
    }

    private static PolicyRelease BuildStrictPolicy() => new(
        Name: "test-strict",
        RiskCeiling: RiskLevel.ReadOnly,
        RoleScopes: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
        MaxWindowMinutes: 360,
        MaxResultBytes: 524_288,
        MinStepSeconds: 15,
        RateLimits: new PolicyRateLimits(PerTenantPerMinute: 100, PerIncidentPerMinute: 2, PerToolPerMinute: 100),
        Timeouts: new PolicyTimeouts(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(600)),
        Budgets: new PolicyBudgets(24, 40, 400_000, 3m, TimeSpan.FromSeconds(600)));

    private sealed class RecordingResolver : IToolExecutionContextResolver
    {
        private readonly ToolAuthorization _authorization;

        public RecordingResolver(
            PolicyRelease policy,
            IReadOnlySet<string> scopes,
            IReadOnlySet<string> agentTools)
            => _authorization = new ToolAuthorization(policy, scopes, agentTools);

        public List<EvidenceRecord> Recorded { get; } = [];

        public Task<ToolAuthorization> ResolveAsync(
            ToolExecutionRequest request,
            ToolExecutionContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(_authorization);

        public Task RecordEvidenceAsync(EvidenceRecord evidence, CancellationToken cancellationToken)
        {
            Recorded.Add(evidence);
            return Task.CompletedTask;
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            Meter meter = new(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (Meter meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }

    /// <summary>A clock the tests control, so deadline and rate-limit windows are exact.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
