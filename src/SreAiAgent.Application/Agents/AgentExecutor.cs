using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Runs one expert agent: reason, request tools, reason again, report.
/// </summary>
/// <remarks>
/// The loop never throws. An agent is one participant in a fan-out, and its
/// timeout, exhausted budget or invalid output must not take down the other four;
/// every failure path returns an <see cref="AgentRunResult"/> the workflow can
/// merge and the report can describe.
///
/// The model never touches a tool. It emits a typed request, the executor hands
/// that request to the tool gateway, and the gateway decides. That indirection is
/// what makes an injected instruction in a log line harmless: it can change what
/// the model asks for and cannot change what the gateway permits.
/// </remarks>
public sealed partial class AgentExecutor : IAgentExecutor
{
    private const int MaxEvidenceInPrompt = 20;

    private readonly IPromptRenderer _renderer;
    private readonly ILlmGateway _llm;
    private readonly IToolGateway _tools;
    private readonly SreMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentExecutor> _logger;

    public AgentExecutor(
        IPromptRenderer renderer,
        ILlmGateway llm,
        IToolGateway tools,
        SreMetrics metrics,
        TimeProvider timeProvider,
        ILogger<AgentExecutor> logger)
    {
        _renderer = renderer;
        _llm = llm;
        _tools = tools;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> ExecuteAsync(AgentContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        AgentRunId runId = AgentRunId.New();
        long started = _timeProvider.GetTimestamp();

        using Activity? activity = SreActivitySources.Agent.StartActivity(
            SreSpanNames.AgentExecute, ActivityKind.Internal);

        activity
            .SetSafeTag(TraceAttributes.AgentName, context.Definition.Name)
            .SetSafeTag(TraceAttributes.AgentVersion, context.Definition.Version)
            .SetSafeTag(TraceAttributes.AgentIsShadow, context.IsShadow)
            .SetSafeTag(TraceAttributes.IncidentId, context.Incident.IncidentId.ToString())
            .SetSafeTag(TraceAttributes.Tenant, context.Tenant);

        AgentBudgetTracker budget = new(context.Definition.Budget, context.Deadline, _timeProvider);
        AgentSession session = new(context, runId);

        try
        {
            AgentRunResult result = await RunLoopAsync(context, session, budget, runId, cancellationToken)
                .ConfigureAwait(false);

            _metrics.AgentRunCompleted(
                context.Definition.Name,
                context.Definition.Version,
                result.Status.ToString(),
                context.IsShadow,
                _timeProvider.GetElapsedTime(started));

            activity.SetSafeTag(TraceAttributes.ResultStatus, result.Status.ToString());
            activity.SetSafeTag(TraceAttributes.EvidenceCount, result.EvidenceIds.Count);
            activity.SetSafeTag(TraceAttributes.ToolCallCount, budget.ToolCallCount);

            // Attached at the one point every successful path passes through, so a
            // new terminal branch cannot be added that forgets to report usage.
            return result with { Usage = budget.Snapshot() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation of the whole investigation, not of this agent. Reported
            // rather than rethrown so the fan-out records which agents had already
            // finished.
            return session.Cancelled() with { Usage = budget.Snapshot() };
        }
        catch (Exception exception)
        {
            // The last line of defence for the fan-out. Whatever went wrong in one
            // agent, the other four keep their results.
            LogUnexpectedFailure(_logger, context.Definition.Name, exception);
            activity?.SetStatus(ActivityStatusCode.Error);

            return session.Failed(
                ApplicationErrorCodes.LlmProviderError,
                "The agent failed unexpectedly.") with
            { Usage = budget.Snapshot() };
        }
    }

    private async Task<AgentRunResult> RunLoopAsync(
        AgentContext context,
        AgentSession session,
        AgentBudgetTracker budget,
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            BudgetVerdict iteration = budget.TryBeginIteration();
            if (!iteration.IsAllowed)
            {
                // Whatever was gathered is kept. An agent that made three useful
                // observations before running out of iterations is worth more than
                // nothing (ARCHITECTURE.md 11.3).
                return session.BudgetExceeded(iteration.Reason ?? "Budget exhausted.");
            }

            BudgetVerdict modelCall = budget.TryBeginLlmCall();
            if (!modelCall.IsAllowed)
            {
                return session.BudgetExceeded(modelCall.Reason ?? "Budget exhausted.");
            }

            Result<RenderedPrompt> prompt = RenderPrompt(context, session);
            if (prompt.IsFailure)
            {
                return session.Failed(prompt.Error.Code, prompt.Error.Message);
            }

            LlmRequest<AgentTurn> request = new()
            {
                SystemPrompt = prompt.Value.System,
                UserPrompt = prompt.Value.User,
                // The turn envelope, not the definition's output schema: the
                // latter describes the finding that arrives inside it.
                ResponseSchemaName = AgentTurn.SchemaName,
                Candidate = context.ModelCandidate,
                Tools = context.AvailableTools,
                Timeout = Min(context.ModelCandidate.Timeout, budget.RemainingTime()),
                Metadata = session.MetadataFor(prompt.Value, runId, budget.LlmCallCount),
            };

            Result<LlmResult<AgentTurn>> response = await _llm
                .GenerateStructuredAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                // The tenant's spending limit is a budget like this agent's own,
                // not a provider fault. Reported as a failure it made a run the
                // operator had capped look like a degraded provider (Partial).
                return response.Error.Code == ApplicationErrorCodes.BudgetExceeded
                    ? session.BudgetExceeded(response.Error.Message)
                    : session.Failed(response.Error.Code, response.Error.Message);
            }

            budget.RecordUsage(
                response.Value.InputTokens, response.Value.OutputTokens, response.Value.EstimatedCost);

            AgentTurn turn = response.Value.Value;

            // Tool requests win over a finding returned in the same breath. A
            // finding written alongside a request for evidence was written before
            // that evidence existed.
            if (turn.ToolRequests.Count > 0)
            {
                await ExecuteToolsAsync(context, session, budget, turn, runId, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (turn.Finding is not null)
            {
                return session.Succeeded(session.BindFinding(turn.Finding));
            }

            // Neither tools nor a finding: the model has nothing further to add.
            return session.Succeeded(session.BindFinding(EmptyFinding()));
        }
    }

    private async Task ExecuteToolsAsync(
        AgentContext context,
        AgentSession session,
        AgentBudgetTracker budget,
        AgentTurn turn,
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        foreach (AgentToolRequest requested in turn.ToolRequests)
        {
            BudgetVerdict toolCall = budget.TryBeginToolCall();
            if (!toolCall.IsAllowed)
            {
                session.RecordToolRefusal(requested.ToolName, toolCall.Reason ?? "Tool budget exhausted.");
                return;
            }

            ToolExecutionRequest request = new(
                ToolName: requested.ToolName,
                ToolVersion: requested.ToolVersion,
                Arguments: requested.Arguments,
                IncidentId: context.Incident.IncidentId,
                AgentName: context.Definition.Name,
                AgentRunId: runId,
                // Derived rather than random, so a retried activity reuses the same
                // key and the gateway resolves to the existing result instead of
                // calling the backend twice.
                RequestId: session.RequestIdFor(requested));

            ToolExecutionContext executionContext = new(
                Tenant: context.Tenant,
                Environment: context.Environment,
                ServiceAllowlist: session.ServiceAllowlist,
                Actor: $"agent:{context.Definition.Name}",
                RiskCeiling: context.RiskCeiling,
                Deadline: context.Deadline);

            ToolExecutionResult result = await _tools
                .ExecuteAsync(request, executionContext, cancellationToken)
                .ConfigureAwait(false);

            // A refusal is fed back to the model as an observation rather than
            // ending the run. "That tool was denied" is information the agent
            // should reason about and the report should record.
            session.RecordToolResult(requested.ToolName, result);
        }
    }

    private Result<RenderedPrompt> RenderPrompt(AgentContext context, AgentSession session)
        => _renderer.Render(context.Prompt, session.BuildVariables(MaxEvidenceInPrompt));

    private static AgentFindingPayload EmptyFinding()
        => new() { Summary = "The agent produced no finding." };

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Agent {AgentName} failed unexpectedly.")]
    private static partial void LogUnexpectedFailure(ILogger logger, string agentName, Exception exception);
}
