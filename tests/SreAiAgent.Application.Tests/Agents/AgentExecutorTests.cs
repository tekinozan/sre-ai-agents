using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Tests.Agents;

/// <summary>
/// Covers how a model call's failure becomes the agent run's status.
/// </summary>
public sealed class AgentExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly ILlmGateway _llm = Substitute.For<ILlmGateway>();
    private readonly AgentExecutor _executor;

    public AgentExecutorTests()
    {
        IPromptRenderer renderer = Substitute.For<IPromptRenderer>();
        renderer.Render(Arg.Any<PromptDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(Result<RenderedPrompt>.Success(new RenderedPrompt("system", "user", "hash", 10)));

        _executor = new AgentExecutor(
            renderer,
            _llm,
            Substitute.For<IToolGateway>(),
            new SreMetrics(new TestMeterFactory()),
            TimeProvider.System,
            NullLogger<AgentExecutor>.Instance);
    }

    [Fact]
    public async Task ASpentTenantBudgetEndsTheRunAsBudgetExceeded()
    {
        // Reported as Failed, the investigation came out Partial: indistinguishable
        // from a provider outage, when the operator had in fact capped spending.
        Returns(Error.Permanent(ApplicationErrorCodes.BudgetExceeded, "The tenant's monthly model budget is spent."));

        AgentRunResult result = await _executor.ExecuteAsync(Context(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(AgentRunStatus.BudgetExceeded);
        result.ErrorCode.ShouldBe(ApplicationErrorCodes.BudgetExceeded);
    }

    [Fact]
    public async Task AProviderFailureStillFailsTheRun()
    {
        Returns(Error.Transient(ApplicationErrorCodes.LlmProviderError, "The provider is unavailable."));

        AgentRunResult result = await _executor.ExecuteAsync(Context(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ErrorCode.ShouldBe(ApplicationErrorCodes.LlmProviderError);
    }

    private void Returns(Error error)
        => _llm.GenerateStructuredAsync(Arg.Any<LlmRequest<AgentTurn>>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmResult<AgentTurn>>.Failure(error));

    private static AgentContext Context() => new()
    {
        Incident = new IncidentSnapshot(
            IncidentId.New(), "acme", "alertmanager", "Checkout latency above threshold", null,
            "checkout", "production", IncidentSeverity.High, IncidentStatus.Investigating, Now,
            new Dictionary<string, string>()),
        Definition = new AgentDefinition
        {
            Name = "metrics",
            Version = "1.0.0",
            PromptRef = "metrics:1.0.0",
            ModelProfileRef = "agent",
            Capabilities = new HashSet<string>(StringComparer.Ordinal),
            IncidentTypes = new HashSet<string>(StringComparer.Ordinal),
            AllowedTools = new HashSet<string>(StringComparer.Ordinal),
            Budget = new AgentExecutionBudget(5, TimeSpan.FromMinutes(1), 5, 5, 100_000, 10m),
            OutputSchema = "agent-finding",
        },
        Prompt = new PromptDefinition(
            "metrics", "1.0.0", "system", "user",
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 10_000, "hash"),
        ModelCandidate = new PinnedModelCandidate
        {
            Tenant = "acme",
            AssignmentReleaseId = ModelAssignmentReleaseId.New(),
            AssignmentConfigurationHash = "hash1",
            Slot = "agent",
            DeploymentVersionId = ModelDeploymentVersionId.New(),
            DeploymentLogicalName = "deploy",
            DeploymentVersion = 1,
            DeploymentConfigurationHash = "hash2",
            ProviderConnectionRevisionId = ProviderConnectionRevisionId.New(),
            ProviderConnectionRevision = 1,
            ProviderConfigurationHash = "hash3",
            AdapterKind = ProviderAdapterKind.OpenAi,
            ProviderModel = "model",
            Capabilities = ModelCapability.None,
            Temperature = 0,
            MaxOutputTokens = 100,
            Timeout = TimeSpan.FromSeconds(30),
            InputCostPerMillionTokens = 0,
            OutputCostPerMillionTokens = 0,
            Currency = "USD",
            ProviderParameters = new Dictionary<string, string>(),
        },
        AvailableTools = [],
        ExistingEvidence = [],
        Deadline = TimeProvider.System.GetUtcNow().AddMinutes(5),
        CorrelationId = "corr",
        Tenant = "acme",
        Environment = "production",
        RiskCeiling = RiskLevel.ReadOnly,
        IsShadow = false,
        ConfigurationSnapshotId = ConfigurationSnapshotId.New(),
    };
}
