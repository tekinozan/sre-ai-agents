using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Audit;

/// <summary>
/// Records what the platform asked of the outside world
/// (ARCHITECTURE.md 6.2 stage 13, 9.2).
/// </summary>
/// <remarks>
/// Staged into the caller's unit of work rather than written directly, so an
/// audit row commits with the investigation that produced it. A trail written in
/// its own transaction survives a rollback and then describes work the platform
/// decided had not happened, which is worse than no trail: it is a trail that
/// lies.
///
/// Internal on purpose. Auditing is something the gateways do on the way past;
/// nothing above infrastructure should be able to write a row claiming a tool
/// ran.
/// </remarks>
internal interface IAuditSink
{
    /// <summary>Records one tool execution attempt, successful or not.</summary>
    void RecordToolCall(ToolCallAudit call);

    /// <summary>Records one model call, successful or not.</summary>
    void RecordLlmCall(LlmCallAudit call);
}

/// <summary>One tool execution, as it will be audited.</summary>
/// <param name="ToolCallId">Identity shared with the evidence the call produced.</param>
/// <param name="IncidentId">The incident under investigation.</param>
/// <param name="AgentRunId">The run that asked, when there was one.</param>
/// <param name="ToolName">Tool requested.</param>
/// <param name="ToolVersion">Version requested.</param>
/// <param name="AgentName">Agent that asked.</param>
/// <param name="Outcome">succeeded, denied or failed.</param>
/// <param name="ErrorCode">Stable code when it did not succeed.</param>
/// <param name="ArgumentsJson">The arguments asked for.</param>
/// <param name="Duration">How long the attempt took.</param>
/// <param name="ExecutedAt">When it happened, UTC.</param>
internal sealed record ToolCallAudit(
    ToolCallId ToolCallId,
    IncidentId IncidentId,
    AgentRunId? AgentRunId,
    string ToolName,
    string ToolVersion,
    string AgentName,
    string Outcome,
    ErrorCode? ErrorCode,
    string ArgumentsJson,
    TimeSpan Duration,
    DateTimeOffset ExecutedAt);

/// <summary>One model call, as it will be audited.</summary>
/// <param name="LlmCallId">Identity of the call.</param>
/// <param name="IncidentId">The incident under investigation.</param>
/// <param name="AgentRunId">The run that called, when there was one.</param>
/// <param name="AgentName">Agent that called.</param>
/// <param name="PromptName">Prompt used.</param>
/// <param name="PromptVersion">Pinned prompt version.</param>
/// <param name="PromptHash">Hash of the rendered prompt, not the prompt itself.</param>
/// <param name="ModelProvider">Provider that served it.</param>
/// <param name="ModelName">Concrete model.</param>
/// <param name="Outcome">succeeded or failed.</param>
/// <param name="ErrorCode">Stable code when it failed.</param>
/// <param name="InputTokens">Prompt tokens.</param>
/// <param name="OutputTokens">Completion tokens.</param>
/// <param name="EstimatedCost">Estimated cost in the profile's currency.</param>
/// <param name="Duration">How long the call took.</param>
/// <param name="CalledAt">When it happened, UTC.</param>
internal sealed record LlmCallAudit(
    LlmCallId LlmCallId,
    IncidentId IncidentId,
    AgentRunId? AgentRunId,
    string AgentName,
    string PromptName,
    string PromptVersion,
    string PromptHash,
    string ModelProvider,
    string ModelName,
    string Outcome,
    ErrorCode? ErrorCode,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    TimeSpan Duration,
    DateTimeOffset CalledAt);
