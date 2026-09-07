using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Application.Workflows;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Audit;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;

namespace SreAiAgent.Evals.Harness;

internal sealed class ProductionEvalRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly CandidateSnapshot _candidate;
    private readonly string? _credentialEnvironmentVariable;
    private readonly EvalAuditSink _audit;

    private ProductionEvalRuntime(ServiceProvider provider, CandidateSnapshot candidate, string? credentialEnvironmentVariable, EvalAuditSink audit)
    {
        _provider = provider;
        _candidate = candidate;
        _credentialEnvironmentVariable = credentialEnvironmentVariable;
        _audit = audit;
    }

    public static ProductionEvalRuntime Create(CandidateSnapshot candidate, DateTimeOffset fixedUtc, string? credentialEnvironmentVariable = null)
    {
        EvalAuditSink audit = new();
        ServiceCollection services = new();
        services.AddSingleton<TimeProvider>(new DeterministicTimeProvider(fixedUtc));
        services.AddSingleton<IAuditSink>(audit);
        services.AddSingleton<IOptions<RegistryOptions>>(Options.Create(new RegistryOptions
        {
            ConfigurationRoot = Path.Combine(RepositoryPaths.Root, "config"),
            PromptRoot = Path.Combine(RepositoryPaths.Root, "prompts"),
            AgentRelease = candidate.AgentRelease.Id,
            PromptRelease = candidate.PromptRelease.Id,
            ModelRelease = "models-2026.08.25.1",
            ToolRelease = candidate.ToolRelease.Id,
            PolicyRelease = candidate.PolicyRelease.Id,
            FlagRelease = "flags-2026.08.25.1",
        }));
        services.AddSingleton<IOptions<LlmOptions>>(Options.Create(new LlmOptions
        {
            Provider = candidate.ModelDeployment.Provider,
            ModelNameByProfile = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["analytical"] = candidate.ModelDeployment.Model,
                ["fast"] = candidate.ModelDeployment.Model,
            },
            MaxRetries = 0,
            LogRenderedPrompts = false,
        }));
        services.AddSreMetrics();
        services.AddSreRegistries();

        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<SreAiAgent.Infrastructure.Llm.Providers.IProviderRevisionReader, StubProviderRevisionReader>();

        // AddSreLlm registers IModelProviderControlPlane -> ModelProviderControlPlane,
        // which needs the EF database. The eval harness has no database and never
        // exercises the control plane, so we shadow that registration with a stub
        // that fails loudly if anything ever tries to use it. Registered BEFORE
        // AddSreLlm because that helper uses TryAddScoped and does not overwrite.
        services.AddScoped<IModelProviderControlPlane, StubModelProviderControlPlane>();
        services.AddSreLlm();

        services.AddSingleton<InMemoryInvestigationStores>();

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        return new ProductionEvalRuntime(provider, candidate, credentialEnvironmentVariable, audit);
    }

    public async Task<(EvalOutput Output, IReadOnlyList<ToolInvocation> Tools)> RunAsync(EvalCase evalCase, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _provider.CreateScope();

        IAgentRegistry agents = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
        IPromptRegistry prompts = scope.ServiceProvider.GetRequiredService<IPromptRegistry>();
        IModelProfileRegistry models = scope.ServiceProvider.GetRequiredService<IModelProfileRegistry>();
        IReadOnlyToolRegistry tools = scope.ServiceProvider.GetRequiredService<IReadOnlyToolRegistry>();
        IPromptRenderer renderer = scope.ServiceProvider.GetRequiredService<IPromptRenderer>();
        SreMetrics metrics = scope.ServiceProvider.GetRequiredService<SreMetrics>();
        TimeProvider timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        ILlmGateway gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();

        InMemoryInvestigationStores stores = scope.ServiceProvider.GetRequiredService<InMemoryInvestigationStores>();

        AgentDefinition definition = (await agents.ResolveAsync(_candidate.AgentRelease.Id, evalCase.Agent, cancellationToken).ConfigureAwait(false)).Value;

        FixtureToolGateway fixtures = new(evalCase.Fixtures, definition.AllowedTools, tools);

        AgentRuntimeOptions runtimeOptions = new();
        IIncidentRouter router = new IncidentRouter(
            agents,
            scope.ServiceProvider.GetRequiredService<IAgentFlagEvaluator>(),
            new TestToolProbe(tools, definition.AllowedTools),
            Options.Create(runtimeOptions));

        AgentExecutor executor = new(renderer, gateway, fixtures, metrics, timeProvider, NullLogger<AgentExecutor>.Instance);

        InvestigationActivities activities = new(
            stores, stores, stores, stores, router, executor, agents, prompts, tools, gateway, renderer, metrics, timeProvider, NullLogger<InvestigationActivities>.Instance);

        IncidentId incidentId = new(DeterministicGuid(evalCase.CaseId));
        Incident incident = new Incident(
            incidentId,
            new NewIncident(
                evalCase.Incident.Tenant,
                evalCase.Incident.Source,
                "eval-external-id",
                "eval-fingerprint",
                evalCase.Incident.Title,
                evalCase.Incident.Description,
                evalCase.Incident.Service,
                evalCase.Incident.Environment,
                Enum.Parse<IncidentSeverity>(evalCase.Incident.Severity, ignoreCase: true),
                evalCase.FixedUtc,
                evalCase.Incident.Labels
            ),
            evalCase.FixedUtc);

        stores.Add(incident);

        (PinnedModelAssignment assignment, ProviderConnectionRevision pinnedRevision) = await PinnedCandidateFactory.AssignmentAsync(
            _candidate,
            _credentialEnvironmentVariable,
            [evalCase.Agent],
            models,
            agents,
            timeProvider,
            cancellationToken);

        // The chat client resolver validates the candidate against the revision
        // the reader returns. Handing it the exact instance the factory built
        // guarantees hash, revision number, adapter kind and tenant match, so
        // the scripted client is actually resolved instead of hitting CONFIG_INVALID.
        StubProviderRevisionReader revisionReader = (StubProviderRevisionReader)scope.ServiceProvider.GetRequiredService<SreAiAgent.Infrastructure.Llm.Providers.IProviderRevisionReader>();
        revisionReader.Revision = pinnedRevision;

        ConfigurationSnapshotId configId = new(DeterministicGuid("config-" + evalCase.CaseId));
        stores.Add(new ConfigurationSnapshot(
            configId,
            _candidate.AgentRelease.Id,
            _candidate.PromptRelease.Id,
            assignment,
            _candidate.ToolRelease.Id,
            _candidate.PolicyRelease.Id,
            new Dictionary<string, string>().AsReadOnly(),
            evalCase.FixedUtc
        ));

        var incidentSnapshotRes = await activities.LoadIncidentAsync(incidentId, cancellationToken);
        if (incidentSnapshotRes.IsFailure)
            return FailCase(evalCase, "LOAD_FAILED", incidentSnapshotRes.Error.Message, fixtures);
        IncidentSnapshot incidentSnapshot = incidentSnapshotRes.Value;

        var configRes = await activities.ResolveConfigurationAsync(configId, cancellationToken);
        if (configRes.IsFailure)
            return FailCase(evalCase, "CONFIG_FAILED", configRes.Error.Message, fixtures);

        var classifyRes = await activities.ClassifyIncidentAsync(new ClassifyIncidentInput(
            incidentSnapshot,
            configId,
            "eval-" + evalCase.CaseId,
            evalCase.FixedUtc.AddSeconds(60)), cancellationToken);

        if (classifyRes.IsFailure)
            return FailCase(evalCase, "CLASSIFY_FAILED", classifyRes.Error.Message, fixtures);
        IncidentClassification classification = classifyRes.Value;

        var selectRes = await activities.SelectAgentsAsync(new SelectAgentsInput(
            incidentSnapshot,
            classification,
            configId,
            "eval-" + evalCase.CaseId), cancellationToken);

        if (selectRes.IsFailure)
            return FailCase(evalCase, "SELECT_FAILED", selectRes.Error.Message, fixtures);
        AgentSelection selection = selectRes.Value;

        if (!selection.Agents.Any(s => s.AgentName == evalCase.Agent))
        {
            return FailCase(evalCase, "AGENT_NOT_SELECTED", $"Case agent {evalCase.Agent} was not selected by router.", fixtures);
        }

        var runResult = await activities.RunAgentAsync(new RunAgentInput(
            incidentSnapshot,
            selection.Agents.First(s => s.AgentName == evalCase.Agent),
            classification,
            configId,
            "eval-" + evalCase.CaseId,
            evalCase.FixedUtc.AddSeconds(60)
        ), cancellationToken);

        if (runResult.Status != AgentRunStatus.Succeeded)
        {
            return FailCase(evalCase, runResult.ErrorCode?.Value, runResult.ErrorMessage, fixtures, runResult.Usage);
        }

        var correlateRes = await activities.CorrelateFindingsAsync(new CorrelateFindingsInput(
            incidentSnapshot,
            new[] { runResult },
            configId,
            "eval-" + evalCase.CaseId,
            evalCase.FixedUtc.AddSeconds(60)
        ), cancellationToken);

        if (correlateRes.IsFailure)
        {
            return FailCase(evalCase, "CORRELATE_FAILED", correlateRes.Error.Message, fixtures, runResult.Usage);
        }

        CorrelationResult correlation = correlateRes.Value;

        AgentRunUsage totalUsage = new AgentRunUsage(
            _audit.LlmCalls.Count,
            _audit.ToolCalls.Count,
            _audit.LlmCalls.Sum(l => l.InputTokens),
            _audit.LlmCalls.Sum(l => l.OutputTokens),
            _audit.LlmCalls.Sum(l => l.EstimatedCost)
        );

        List<Guid> citations = correlation.KeyObservations.SelectMany(item => item.EvidenceIds)
            .Concat(correlation.RankedHypotheses.SelectMany(item => item.SupportingEvidenceIds))
            .Select(value => value.Value)
            .Distinct()
            .ToList();

        EvalOutput output = new EvalOutput(
            classification.IncidentTypes,
            correlation.Category,
            correlation.Summary,
            correlation.RankedHypotheses.Select(item => item.Statement).ToList(),
            correlation.EvidenceGaps,
            runResult.Finding?.RecommendedNextChecks ?? [],
            citations,
            correlation.Confidence.Value,
            runResult.Status,
            totalUsage,
            (timeProvider.GetUtcNow() - evalCase.FixedUtc).TotalMilliseconds,
            null,
            null);

        return (output, fixtures.Invocations);
    }

    private (EvalOutput Output, IReadOnlyList<ToolInvocation> Tools) FailCase(EvalCase evalCase, string? errorCode, string? errorMessage, FixtureToolGateway fixtures, AgentRunUsage? partialUsage = null)
    {
        AgentRunUsage usage = partialUsage ?? new AgentRunUsage(
            _audit.LlmCalls.Count,
            _audit.ToolCalls.Count,
            _audit.LlmCalls.Sum(l => l.InputTokens),
            _audit.LlmCalls.Sum(l => l.OutputTokens),
            _audit.LlmCalls.Sum(l => l.EstimatedCost)
        );

        return (new EvalOutput(
            [],
            null,
            string.Empty,
            [],
            [],
            [],
            [],
            0m,
            AgentRunStatus.Failed,
            usage,
            0,
            errorCode,
            errorMessage), fixtures.Invocations);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private static Guid DeterministicGuid(string material)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16));
    }
}

internal sealed class TestToolProbe : IReadOnlyToolRegistryProbe
{
    private readonly IReadOnlyToolRegistry _registry;
    private readonly HashSet<string> _allowed;

    public TestToolProbe(IReadOnlyToolRegistry registry, IEnumerable<string> allowed)
    {
        _registry = registry;
        _allowed = [.. allowed];
    }

    public bool Contains(string toolName) => _allowed.Contains(toolName) && _registry.All.Any(t => t.Name == toolName);
}

internal sealed class EvalAuditSink : IAuditSink
{
    public List<ToolCallAudit> ToolCalls { get; } = [];
    public List<LlmCallAudit> LlmCalls { get; } = [];
    public void RecordToolCall(ToolCallAudit call) => ToolCalls.Add(call);
    public void RecordLlmCall(LlmCallAudit call) => LlmCalls.Add(call);
}

internal sealed class DeterministicTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;
    private long _timestamp;

    public DeterministicTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override long TimestampFrequency => 1_000_000;
    public override long GetTimestamp() => Interlocked.Add(ref _timestamp, 10_000);
}

internal sealed class StubProviderRevisionReader : SreAiAgent.Infrastructure.Llm.Providers.IProviderRevisionReader
{
    public SreAiAgent.Domain.ModelProviders.ProviderConnectionRevision? Revision { get; set; }

    public Task<SreAiAgent.Domain.ModelProviders.ProviderConnectionRevision?> FindRevisionAsync(
        string tenant,
        SreAiAgent.Domain.ModelProviders.ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Revision);
    }
}

internal sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

internal sealed class StubModelProviderControlPlane : IModelProviderControlPlane
{
    // Every method is unreachable in the eval harness: the runtime pins its
    // provider revision explicitly through StubProviderRevisionReader and never
    // asks the control plane to list, create, verify, evaluate or activate. If
    // any activity ever starts calling into it, we want the test to name the
    // exact operation rather than fail with a mystery NullReferenceException.
    private static Task<T> Reject<T>([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => Task.FromException<T>(new NotSupportedException(
            $"The eval harness does not implement IModelProviderControlPlane.{member}."));

    public Task<IReadOnlyList<ProviderConnection>> ListConnectionsAsync(string tenant, CancellationToken cancellationToken) => Reject<IReadOnlyList<ProviderConnection>>();
    public Task<Result<ProviderConnection>> CreateConnectionAsync(string tenant, string displayName, string actor, CancellationToken cancellationToken) => Reject<Result<ProviderConnection>>();
    public Task<IReadOnlyList<ProviderConnectionRevision>> ListRevisionsAsync(string tenant, ProviderConnectionId connectionId, CancellationToken cancellationToken) => Reject<IReadOnlyList<ProviderConnectionRevision>>();
    public Task<Result<ProviderConnectionRevision>> CreateRevisionAsync(string tenant, ProviderConnectionId connectionId, CreateProviderRevisionCommand command, string actor, CancellationToken cancellationToken) => Reject<Result<ProviderConnectionRevision>>();
    public Task<Result<ProviderRevisionVerification>> VerifyRevisionAsync(string tenant, ProviderConnectionId connectionId, int revision, string actor, CancellationToken cancellationToken) => Reject<Result<ProviderRevisionVerification>>();
    public Task<IReadOnlyList<ModelDeploymentVersion>> ListDeploymentVersionsAsync(string tenant, CancellationToken cancellationToken) => Reject<IReadOnlyList<ModelDeploymentVersion>>();
    public Task<Result<ModelDeploymentVersion>> CreateDeploymentVersionAsync(string tenant, CreateModelDeploymentVersionCommand command, string actor, CancellationToken cancellationToken) => Reject<Result<ModelDeploymentVersion>>();
    public Task<Result<ModelDeploymentVersion>> CreateNextDeploymentVersionAsync(string tenant, ModelDeploymentVersionId priorVersionId, CreateModelDeploymentVersionCommand command, string actor, CancellationToken cancellationToken) => Reject<Result<ModelDeploymentVersion>>();
    public Task<IReadOnlyList<ModelAssignmentRelease>> ListAssignmentReleasesAsync(string tenant, CancellationToken cancellationToken) => Reject<IReadOnlyList<ModelAssignmentRelease>>();
    public Task<Result<ModelAssignmentRelease>> CreateAssignmentReleaseAsync(string tenant, CreateModelAssignmentReleaseCommand command, string actor, CancellationToken cancellationToken) => Reject<Result<ModelAssignmentRelease>>();
    public Task<Result<ModelAssignmentEvaluationHandoff>> RequestEvaluationAsync(string tenant, ModelAssignmentReleaseId releaseId, string dataset, string baseline, string actor, CancellationToken cancellationToken) => Reject<Result<ModelAssignmentEvaluationHandoff>>();
    public Task<Result<ModelAssignmentEvaluationHandoff>> CompleteEvaluationAsync(string tenant, ModelAssignmentEvaluationId evaluationId, bool passed, string artifactHash, string actor, CancellationToken cancellationToken) => Reject<Result<ModelAssignmentEvaluationHandoff>>();
    public Task<Result<ModelAssignmentRelease>> ActivateAsync(string tenant, ModelAssignmentReleaseId releaseId, string actor, CancellationToken cancellationToken) => Reject<Result<ModelAssignmentRelease>>();
}
