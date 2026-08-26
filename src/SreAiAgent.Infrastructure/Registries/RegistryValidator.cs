using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Cross-checks every registry against the pinned releases before the host
/// starts serving (ARCHITECTURE.md 7.1).
/// </summary>
/// <remarks>
/// The six checks in section 7.1 are each a failure that would otherwise surface
/// mid-incident: an agent pointing at a prompt version that was never released, a
/// model profile that cannot do structured output, a tool that is allowed but not
/// registered, a budget of zero. Every one is cheap to detect at boot and
/// expensive to diagnose at three in the morning.
///
/// Every problem is collected before throwing. Failing on the first one turns
/// fixing a bad release into a sequence of deploys, each revealing the next
/// error; reporting all of them makes it one edit.
/// </remarks>
internal sealed partial class RegistryValidator : IHostedService
{
    private readonly RegistryOptions _options;
    private readonly IPromptRegistry _prompts;
    private readonly IModelProfileRegistry _models;
    private readonly IAgentRegistry _agents;
    private readonly IReadOnlyToolRegistry _tools;
    private readonly IPolicyRegistry _policies;
    private readonly ILogger<RegistryValidator> _logger;

    public RegistryValidator(
        IOptions<RegistryOptions> options,
        IPromptRegistry prompts,
        IModelProfileRegistry models,
        IAgentRegistry agents,
        IReadOnlyToolRegistry tools,
        IPolicyRegistry policies,
        ILogger<RegistryValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _prompts = prompts;
        _models = models;
        _agents = agents;
        _tools = tools;
        _policies = policies;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        List<string> problems = [];

        Result<IReadOnlyList<AgentDefinition>> agents =
            await _agents.GetReleaseAgentsAsync(_options.AgentRelease, cancellationToken).ConfigureAwait(false);

        if (agents.IsFailure)
        {
            problems.Add(agents.Error.Message);
        }
        else
        {
            foreach (AgentDefinition agent in agents.Value)
            {
                await ValidateAgentAsync(agent, problems, cancellationToken).ConfigureAwait(false);
            }
        }

        Result<PolicyRelease> policy =
            await _policies.ResolveAsync(_options.PolicyRelease, cancellationToken).ConfigureAwait(false);

        if (policy.IsFailure)
        {
            problems.Add(policy.Error.Message);
        }
        else if (agents.IsSuccess)
        {
            ValidateBudgetsAgainstPolicy(agents.Value, policy.Value, problems);
        }

        if (problems.Count > 0)
        {
            throw new ConfigurationValidationException(problems);
        }

        LogValidated(
            _logger,
            agents.IsSuccess ? agents.Value.Count : 0,
            _tools.All.Count,
            _prompts.All.Count,
            _models.All.Count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ValidateAgentAsync(
        AgentDefinition agent,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        // Check 1: the prompt reference resolves through the pinned release.
        (string promptName, _) = SplitReference(agent.PromptRef);
        Result<PromptDefinition> prompt =
            await _prompts.ResolveAsync(_options.PromptRelease, promptName, cancellationToken).ConfigureAwait(false);

        if (prompt.IsFailure)
        {
            problems.Add($"Agent '{agent.Reference}': {prompt.Error.Message}");
        }

        // Check 2: the model profile resolves, and can do what the agent needs.
        Result<ModelProfile> model = await _models
            .ResolveAsync(_options.ModelRelease, agent.ModelProfileRef, cancellationToken)
            .ConfigureAwait(false);

        if (model.IsFailure)
        {
            problems.Add($"Agent '{agent.Reference}': {model.Error.Message}");
        }
        else
        {
            if (!model.Value.SupportsStructuredOutput)
            {
                problems.Add(
                    $"Agent '{agent.Reference}' requires structured output but model profile "
                    + $"'{model.Value.Reference}' does not support it.");
            }

            if (agent.AllowedTools.Count > 0 && !model.Value.SupportsToolCalling)
            {
                problems.Add(
                    $"Agent '{agent.Reference}' allows tools but model profile "
                    + $"'{model.Value.Reference}' does not support tool calling.");
            }
        }

        // Check 3: every allowed tool exists in the pinned tool release.
        foreach (string toolName in agent.AllowedTools)
        {
            if (!_tools.All.Any(descriptor => string.Equals(descriptor.Name, toolName, StringComparison.Ordinal)))
            {
                problems.Add(
                    $"Agent '{agent.Reference}' allows tool '{toolName}', which is not in tool release "
                    + $"'{_options.ToolRelease}'.");
            }
        }

        // Check 4: the declared output schema is one the platform can validate.
        if (!string.Equals(agent.OutputSchema, "AgentFinding", StringComparison.Ordinal))
        {
            problems.Add(
                $"Agent '{agent.Reference}' declares output schema '{agent.OutputSchema}', but only "
                + "'AgentFinding' is implemented.");
        }

        // Check 5: budgets are meaningful.
        AgentExecutionBudget budget = agent.Budget;
        if (budget.MaxIterations <= 0 || budget.MaxLlmCalls <= 0 || budget.MaxToolCalls <= 0)
        {
            problems.Add($"Agent '{agent.Reference}' has a non-positive execution budget.");
        }

        if (budget.MaxLlmCalls > budget.MaxIterations * 2)
        {
            problems.Add(
                $"Agent '{agent.Reference}' allows {budget.MaxLlmCalls.ToString(CultureInfo.InvariantCulture)} "
                + $"model calls across {budget.MaxIterations.ToString(CultureInfo.InvariantCulture)} iterations. "
                + "The call budget should bound the iteration budget, not exceed it, or it stops being a limit.");
        }
    }

    private static void ValidateBudgetsAgainstPolicy(
        IReadOnlyList<AgentDefinition> agents,
        PolicyRelease policy,
        List<string> problems)
    {
        // Check 6: an individual agent cannot be allowed more than the whole
        // incident is. A per-agent budget above the per-incident ceiling is not
        // an error the runtime would catch; it would simply never be reachable,
        // which makes the artifact misleading to whoever reads it next.
        foreach (AgentDefinition agent in agents)
        {
            if (agent.Budget.Timeout >= policy.Timeouts.Workflow)
            {
                problems.Add(
                    $"Agent '{agent.Reference}' has a timeout of {agent.Budget.Timeout} which is not shorter "
                    + $"than the workflow deadline of {policy.Timeouts.Workflow} (ARCHITECTURE.md 11.3).");
            }

            if (agent.Budget.MaxLlmCalls > policy.Budgets.MaxLlmCalls)
            {
                problems.Add(
                    $"Agent '{agent.Reference}' allows more model calls than the per-incident budget of "
                    + $"{policy.Budgets.MaxLlmCalls.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (agent.Budget.MaxEstimatedCost > policy.Budgets.MaxEstimatedCost)
            {
                problems.Add(
                    $"Agent '{agent.Reference}' allows more spend than the per-incident budget of "
                    + $"{policy.Budgets.MaxEstimatedCost.ToString(CultureInfo.InvariantCulture)}.");
            }
        }
    }

    private static (string Name, string? Version) SplitReference(string reference)
    {
        int separator = reference.IndexOf(':', StringComparison.Ordinal);
        return separator < 0
            ? (reference, null)
            : (reference[..separator], reference[(separator + 1)..]);
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Configuration validated: {AgentCount} agents, {ToolCount} tools, {PromptCount} prompts, {ModelCount} model profiles.")]
    private static partial void LogValidated(
        ILogger logger,
        int agentCount,
        int toolCount,
        int promptCount,
        int modelCount);
}

/// <summary>
/// Raised when the pinned configuration is internally inconsistent. Fatal: a
/// process that cannot trust its own release set must not accept work.
/// </summary>
public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IReadOnlyList<string> problems)
        : base(BuildMessage(problems))
        => Problems = problems;

    /// <summary>Every problem found, so one edit can fix them all.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string BuildMessage(IReadOnlyList<string> problems)
        => $"Configuration validation failed with {problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s):"
           + System.Environment.NewLine
           + string.Join(System.Environment.NewLine, problems.Select(static problem => "  - " + problem));
}
