using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Streaming;

/// <summary>
/// Event names published on <c>GET /v1/incidents/{id}/events</c>
/// (ARCHITECTURE.md 12.1).
/// </summary>
public static class InvestigationStreamEventTypes
{
    public const string IncidentStatus = "incident.status";
    public const string AgentStarted = "agent.started";
    public const string AgentCompleted = "agent.completed";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string CorrelationCompleted = "correlation.completed";
    public const string ApprovalRequired = "approval.required";
    public const string IncidentCompleted = "incident.completed";
}

/// <summary>
/// One server-sent event describing investigation progress.
/// </summary>
/// <remarks>
/// The stream reports what the platform is doing, never what a model is
/// thinking. There is no field here that could carry a prompt, a token count, a
/// raw tool result or a reasoning trace, which is a structural guarantee rather
/// than a review checklist item: the type simply has nowhere to put them.
/// </remarks>
/// <param name="EventType">One of <see cref="InvestigationStreamEventTypes"/>.</param>
/// <param name="IncidentId">The incident being streamed.</param>
/// <param name="OccurredAt">When the step happened, in UTC.</param>
/// <param name="Sequence">Monotonic per stream, so a reconnecting client can resume.</param>
/// <param name="Status">Current lifecycle status, when the event carries one.</param>
/// <param name="AgentName">Agent involved, for agent and tool events.</param>
/// <param name="ToolName">Tool involved, for tool events.</param>
/// <param name="ResultStatus">Coarse outcome, for example <c>succeeded</c> or <c>denied</c>.</param>
/// <param name="Message">Short operator-facing text, already redacted.</param>
public sealed record InvestigationStreamEvent(
    string EventType,
    IncidentId IncidentId,
    DateTimeOffset OccurredAt,
    long Sequence,
    IncidentStatus? Status = null,
    string? AgentName = null,
    string? ToolName = null,
    string? ResultStatus = null,
    string? Message = null);
