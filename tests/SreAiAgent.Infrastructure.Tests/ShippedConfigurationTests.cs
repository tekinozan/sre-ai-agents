using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Llm;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Runs the real startup path against the artifacts that ship in this
/// repository, covering the six checks of ARCHITECTURE.md 7.1.
/// </summary>
/// <remarks>
/// This is the test that would have caught a typo in a released agent artifact
/// before a deploy. Everything here is exercised through the same host startup
/// the API and the workers use, so a check that passes here passes in production
/// for the same reason.
/// </remarks>
public sealed class ShippedConfigurationTests : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = BuildHost();

        // StartAsync runs RegistryValidator. If any cross-reference in the
        // shipped artifacts is broken, this throws and every test fails with the
        // list of problems, which is exactly what a deploy would do.
        await _host.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
    }

    [Fact]
    public void ShippedConfigurationPassesStartupValidation()
    {
        // Reaching this point means InitializeAsync completed, so validation
        // passed. Asserting the registries are populated guards against a silent
        // pass caused by an empty release.
        _host.Services.GetRequiredService<IAgentRegistry>().All.ShouldNotBeEmpty();
        _host.Services.GetRequiredService<IReadOnlyToolRegistry>().All.ShouldNotBeEmpty();
        _host.Services.GetRequiredService<IPromptRegistry>().All.ShouldNotBeEmpty();
        _host.Services.GetRequiredService<IModelProfileRegistry>().All.ShouldNotBeEmpty();
    }

    [Fact]
    public void EveryShippedToolIsReadOnly()
    {
        // ARCHITECTURE.md 20: V1 contains read-only tools only. The registry
        // refuses to load anything else, so this asserts the release as well as
        // the guard.
        IReadOnlyToolRegistry tools = _host.Services.GetRequiredService<IReadOnlyToolRegistry>();

        tools.All.ShouldAllBe(static tool => tool.Risk == RiskLevel.ReadOnly);
    }

    [Fact]
    public void NoWriteToolRegistryIsResolvable()
    {
        // Section 15.2 separates the registries physically. V1 registers no write
        // implementation, so a component that tried to take one could not even be
        // constructed.
        _host.Services.GetService<IWriteToolRegistry>().ShouldBeNull();
    }

    [Fact]
    public async Task SharedPolicyFragmentsAreComposedIntoExpertPrompts()
    {
        IPromptRegistry prompts = _host.Services.GetRequiredService<IPromptRegistry>();

        Result<PromptDefinition> prompt = await prompts.ResolveAsync(
            "sre-prompts-2026.08.25.1",
            "database_expert.investigate",
            TestContext.Current.CancellationToken);

        prompt.IsSuccess.ShouldBeTrue();

        // A prompt shipping with a literal {{shared.security-policy}} in its text
        // would silently lose the entire untrusted-data policy.
        prompt.Value.SystemTemplate.ShouldNotContain("{{shared.");
        prompt.Value.SystemTemplate.ShouldContain("<untrusted_data>");
        prompt.Value.SystemTemplate.ShouldContain("evidence id");
    }

    [Fact]
    public async Task PromptHashCoversTheComposedTextNotJustTheFile()
    {
        IPromptRegistry prompts = _host.Services.GetRequiredService<IPromptRegistry>();

        Result<PromptDefinition> database = await prompts.ResolveAsync(
            "sre-prompts-2026.08.25.1", "database_expert.investigate", TestContext.Current.CancellationToken);
        Result<PromptDefinition> application = await prompts.ResolveAsync(
            "sre-prompts-2026.08.25.1", "app_expert.investigate", TestContext.Current.CancellationToken);

        database.Value.ContentHash.ShouldNotBeNullOrWhiteSpace();
        database.Value.ContentHash.Length.ShouldBe(64);
        database.Value.ContentHash.ShouldNotBe(application.Value.ContentHash);
    }

    [Fact]
    public async Task TheReleaseVersionWinsOverWhateverAnAgentAskedFor()
    {
        IModelProfileRegistry models = _host.Services.GetRequiredService<IModelProfileRegistry>();

        // The agent artifact says analytical:v1 and the release also pins v1, but
        // resolution must go through the release: asking for a version the
        // release does not pin has to yield the pinned one, or the configuration
        // snapshot guarantees nothing.
        Result<ModelProfile> resolved = await models.ResolveAsync(
            "models-2026.08.25.1", "analytical:v99", TestContext.Current.CancellationToken);

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task AnUnpinnedPromptCannotBeResolved()
    {
        IPromptRegistry prompts = _host.Services.GetRequiredService<IPromptRegistry>();

        Result<PromptDefinition> missing = await prompts.ResolveAsync(
            "sre-prompts-2026.08.25.1", "nonexistent.prompt", TestContext.Current.CancellationToken);

        missing.IsFailure.ShouldBeTrue();
        missing.Error.Code.Value.ShouldBe("PROMPT_NOT_FOUND");
        missing.Error.IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public async Task ToolSchemasKeepNumericConstraintsNumeric()
    {
        IReadOnlyToolRegistry tools = _host.Services.GetRequiredService<IReadOnlyToolRegistry>();

        ToolDescriptor? descriptor = await tools.FindAsync(
            "prometheus.query_range", "v1", TestContext.Current.CancellationToken);

        descriptor.ShouldNotBeNull();

        // YAML scalars arrive as strings. A `maximum: 360` left as "360" is not
        // applied by any JSON Schema implementation, so the bound would silently
        // stop existing.
        JsonElement window = descriptor.Definition.ParameterSchema
            .GetProperty("properties")
            .GetProperty("window_minutes");

        window.GetProperty("maximum").ValueKind.ShouldBe(JsonValueKind.Number);
        window.GetProperty("maximum").GetInt32().ShouldBe(360);
        window.GetProperty("type").GetString().ShouldBe("integer");
    }

    [Fact]
    public void EveryProviderExpressionIsScopedByTheSelectorPlaceholder()
    {
        IReadOnlyToolRegistry tools = _host.Services.GetRequiredService<IReadOnlyToolRegistry>();

        ToolDescriptor[] expressive = [.. tools.All.Where(static tool => tool.ProviderExpressions.Count > 0)];

        // Startup validation having passed proves nothing if nothing ships an
        // expression, and the validator skips tools that declare none.
        expressive.ShouldNotBeEmpty();

        foreach (ToolDescriptor tool in expressive)
        {
            foreach ((string selector, string expression) in tool.ProviderExpressions)
            {
                // Without the placeholder the query carries no label matcher, so
                // it answers for every service in every environment at once.
                // Nothing downstream catches that: by the time the expression is
                // built, the service allowlist check has already passed, and the
                // result comes back looking like an ordinary answer.
                expression.ShouldContain(
                    "{{selector}}",
                    customMessage: $"{tool.Name} / {selector} builds a query with no label matcher.");
            }
        }
    }

    [Fact]
    public void TimeoutHierarchyHoldsInTheShippedPolicy()
    {
        IPolicyRegistry policies = _host.Services.GetRequiredService<IPolicyRegistry>();
        PolicyRelease policy = policies.All.ShouldHaveSingleItem();

        // ARCHITECTURE.md 11.3: http < tool < agent < workflow.
        policy.Timeouts.HttpClient.ShouldBeLessThan(policy.Timeouts.Tool);
        policy.Timeouts.Tool.ShouldBeLessThan(policy.Timeouts.Agent);
        policy.Timeouts.Agent.ShouldBeLessThan(policy.Timeouts.Workflow);
        policy.RiskCeiling.ShouldBe(RiskLevel.ReadOnly);
    }

    [Fact]
    public void EveryReleasedAgentIsDeclaredInTheFlagRelease()
    {
        // Not one of the six checks in 7.1, and the only one here that a broken
        // release does not announce. The flag file's defaults are
        // state: disabled, so an agent released without a flag entry loads,
        // passes startup validation and then never participates in anything.
        IReadOnlyDictionary<string, string> declared =
            _host.Services.GetRequiredService<IAgentFlagEvaluator>().DeclaredStates();

        string[] undeclared = [.. _host.Services.GetRequiredService<IAgentRegistry>().All
            .Where(agent => !declared.ContainsKey($"{agent.Name}:{agent.Version}"))
            .Select(static agent => agent.Reference)];

        undeclared.ShouldBeEmpty(
            "An agent missing from the flag release inherits the file's defaults, which "
            + "disable it in every environment. Nothing else in the platform reports this.");
    }

    [Fact]
    public async Task TheScriptedProviderCanExerciseEveryReleasedAgent()
    {
        ScriptedChatClient client = new(TimeProvider.System);
        List<string> unreachable = [];

        foreach (AgentDefinition agent in _host.Services.GetRequiredService<IAgentRegistry>().All)
        {
            // The scripted provider chooses what to request from the tool names
            // it finds in the prompt, which is what a real model does too. An
            // agent whose entire allowlist is unknown to it asks for nothing,
            // gathers no evidence and returns no finding — and because that is a
            // valid, empty turn, nothing anywhere reports a problem.
            ChatResponse response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, $"available_tools: {string.Join(", ", agent.AllowedTools)}")],
                cancellationToken: TestContext.Current.CancellationToken);

            AgentTurn? turn = JsonSerializer.Deserialize(response.Text, LlmJsonContext.Default.AgentTurn);

            if (turn?.ToolRequests is not { Count: > 0 })
            {
                unreachable.Add(agent.Reference);
            }
        }

        unreachable.ShouldBeEmpty(
            "V1 ships one IChatClient and it is scripted (docs/DECISIONS.md D09), so an "
            + "agent it does not recognise spends its budget and produces nothing. Teach "
            + "ScriptedChatClient about at least one tool the agent is allowed.");
    }

    private static IHost BuildHost()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Registries:ConfigurationRoot"] = RepositoryPaths.Config,
            ["Registries:PromptRoot"] = RepositoryPaths.Prompts,
            ["Registries:AgentRelease"] = "agents-2026.08.25.1",
            ["Registries:PromptRelease"] = "sre-prompts-2026.08.25.1",
            ["Registries:ModelRelease"] = "models-2026.08.25.1",
            ["Registries:ToolRelease"] = "tools-2026.08.25.1",
            ["Registries:PolicyRelease"] = "policies-2026.08.25.1",
            ["Registries:FlagRelease"] = "flags-2026.08.25.1",
            ["Llm:Provider"] = "scripted",
            ["Llm:ModelNameByProfile:analytical"] = "test-analytical-model",
            ["Llm:ModelNameByProfile:fast"] = "test-fast-model",
        });

        builder.Services.AddOptions<Configuration.RegistryOptions>()
            .BindConfiguration(Configuration.RegistryOptions.SectionName)
            .ValidateDataAnnotations();
        builder.Services.AddOptions<Configuration.LlmOptions>()
            .BindConfiguration(Configuration.LlmOptions.SectionName)
            .ValidateDataAnnotations();

        builder.Services.AddLogging();
        builder.Services.AddSreRegistries();

        return builder.Build();
    }
}
