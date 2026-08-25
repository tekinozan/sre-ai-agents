using System.Text.Json;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Tools;

/// <summary>
/// A typed tool invocation as requested by an agent (ARCHITECTURE.md 6.2).
/// </summary>
/// <param name="ToolName">Registry name, for example <c>prometheus.query_range</c>.</param>
/// <param name="ToolVersion">Pinned version from the tool release.</param>
/// <param name="Arguments">
/// Raw arguments, validated against the tool's JSON Schema before anything else
/// happens. Untrusted until then: this is model output.
/// </param>
/// <param name="IncidentId">The incident this call belongs to.</param>
/// <param name="AgentName">The requesting agent, for audit and rate limiting.</param>
/// <param name="AgentRunId">The specific run, so evidence links back to it.</param>
/// <param name="RequestId">
/// Idempotency key for this execution. A retried activity reuses it, so a
/// duplicate request resolves to the existing result rather than a second call.
/// </param>
public sealed record ToolExecutionRequest(
    string ToolName,
    string ToolVersion,
    JsonElement Arguments,
    IncidentId IncidentId,
    string AgentName,
    AgentRunId? AgentRunId,
    Guid RequestId);
