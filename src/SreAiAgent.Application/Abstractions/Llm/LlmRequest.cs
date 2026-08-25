namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>One structured model call (ARCHITECTURE.md 6.1).</summary>
/// <typeparam name="T">The expected response type.</typeparam>
public sealed record LlmRequest<T>
    where T : class
{
    public required string SystemPrompt { get; init; }

    public required string UserPrompt { get; init; }

    /// <summary>
    /// Names the JSON schema the response must satisfy. The gateway validates
    /// against it before returning, so a caller never handles unvalidated model
    /// output.
    /// </summary>
    public required string ResponseSchemaName { get; init; }

    public required ModelProfile ModelProfile { get; init; }

    /// <summary>Tools the model may request. Empty for a pure reasoning call.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    /// <summary>
    /// Per-call timeout. Must be shorter than the agent timeout, which must be
    /// shorter than the workflow deadline (ARCHITECTURE.md 11.3).
    /// </summary>
    public required TimeSpan Timeout { get; init; }

    public required LlmCallMetadata Metadata { get; init; }
}
