using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Audit;

/// <summary>Stages audit rows in the caller's unit of work.</summary>
internal sealed class AuditStore(SreDbContext context) : IAuditSink
{
    /// <inheritdoc />
    public void RecordToolCall(ToolCallAudit call)
    {
        ArgumentNullException.ThrowIfNull(call);

        context.ToolCalls.Add(new ToolCallEntity
        {
            // The gateway's own call id, so an audit row and the evidence it
            // produced can be joined without a second lookup.
            Id = call.ToolCallId.Value,
            IncidentId = call.IncidentId.Value,
            AgentRunId = call.AgentRunId?.Value,
            ToolName = call.ToolName,
            ToolVersion = call.ToolVersion,
            AgentName = call.AgentName,
            Outcome = call.Outcome,
            ErrorCode = call.ErrorCode?.Value,
            ArgumentsJson = call.ArgumentsJson,
            DurationMilliseconds = (int)call.Duration.TotalMilliseconds,
            ExecutedAt = call.ExecutedAt,
        });
    }

    /// <inheritdoc />
    public void RecordLlmCall(LlmCallAudit call)
    {
        ArgumentNullException.ThrowIfNull(call);

        context.LlmCalls.Add(new LlmCallEntity
        {
            Id = call.LlmCallId.Value,
            IncidentId = call.IncidentId.Value,
            AgentRunId = call.AgentRunId?.Value,
            AgentName = call.AgentName,
            PromptName = call.PromptName,
            PromptVersion = call.PromptVersion,
            PromptHash = call.PromptHash,
            ModelProvider = call.ModelProvider,
            ModelName = call.ModelName,
            Outcome = call.Outcome,
            ErrorCode = call.ErrorCode?.Value,
            InputTokens = call.InputTokens,
            OutputTokens = call.OutputTokens,
            EstimatedCost = call.EstimatedCost,
            DurationMilliseconds = (int)call.Duration.TotalMilliseconds,
            CalledAt = call.CalledAt,
        });
    }
}
