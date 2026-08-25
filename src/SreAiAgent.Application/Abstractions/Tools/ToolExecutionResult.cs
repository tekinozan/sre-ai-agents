using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Tools;

/// <summary>
/// The outcome of a tool execution, successful or refused.
/// </summary>
/// <remarks>
/// A refusal is a normal result rather than an exception. A policy denial is
/// information the agent should reason about and the report should record, and
/// throwing would push it into a catch block where that context is usually lost.
/// </remarks>
public sealed record ToolExecutionResult
{
    public required bool IsSuccess { get; init; }

    /// <summary>Set on success. Evidence is created by the gateway, never by an agent.</summary>
    public EvidenceId? EvidenceId { get; init; }

    /// <summary>
    /// Redacted, size-capped content the agent may reason over. Never the raw
    /// backend response: raw content has not been through redaction, and an agent
    /// is not permitted to see it.
    /// </summary>
    public string? ContentJson { get; init; }

    /// <summary>Short description of what the tool returned, safe for a prompt.</summary>
    public string? Summary { get; init; }

    /// <summary>Set on failure or refusal.</summary>
    public Error? Error { get; init; }

    /// <summary>The audited execution record, present whether or not it succeeded.</summary>
    public required ToolCallId ToolCallId { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>True when the result-size cap dropped part of the payload.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>How many values redaction replaced before the content was stored.</summary>
    public int RedactedValueCount { get; init; }

    public static ToolExecutionResult Success(
        ToolCallId toolCallId,
        EvidenceId evidenceId,
        string summary,
        string contentJson,
        TimeSpan duration,
        bool isTruncated,
        int redactedValueCount)
        => new()
        {
            IsSuccess = true,
            ToolCallId = toolCallId,
            EvidenceId = evidenceId,
            Summary = summary,
            ContentJson = contentJson,
            Duration = duration,
            IsTruncated = isTruncated,
            RedactedValueCount = redactedValueCount,
        };

    public static ToolExecutionResult Failure(ToolCallId toolCallId, Error error, TimeSpan duration)
        => new()
        {
            IsSuccess = false,
            ToolCallId = toolCallId,
            Error = error,
            Duration = duration,
        };
}
