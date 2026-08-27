using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>
/// One execution of one agent within one investigation, persisted to
/// <c>agent_runs</c>. Carries the accounting that ARCHITECTURE.md 16.2 needs and
/// the exact release identifiers that produced the result, so a regression can
/// be attributed to a prompt, a model or a tool rather than guessed at.
/// </summary>
public sealed class AgentRun : Entity<AgentRunId>
{
    private const int MaximumReasonLength = 1024;

    private AgentRun()
    {
    }

    private AgentRun(
        AgentRunId id,
        IncidentId incidentId,
        AgentExecutionIdentity identity,
        bool isShadow,
        DateTimeOffset startedAt)
    {
        Id = id;
        IncidentId = incidentId;
        AgentName = Guard.AgainstNullOrWhiteSpace(identity.AgentName, DomainErrorCodes.IncidentFieldRequired);
        AgentVersion = Guard.AgainstNullOrWhiteSpace(identity.AgentVersion, DomainErrorCodes.IncidentFieldRequired);
        PromptName = identity.PromptName;
        PromptVersion = identity.PromptVersion;
        PromptHash = identity.PromptHash;
        ModelProvider = identity.ModelProvider;
        ModelName = identity.ModelName;
        ModelProfileVersion = identity.ModelProfileVersion;
        IsShadow = isShadow;
        Status = AgentRunStatus.Running;
        StartedAt = startedAt.ToUniversalTime();
    }

    public IncidentId IncidentId { get; private set; }

    public string AgentName { get; private set; } = null!;

    public string AgentVersion { get; private set; } = null!;

    public string PromptName { get; private set; } = null!;

    public string PromptVersion { get; private set; } = null!;

    /// <summary>SHA-256 of the rendered prompt. The prompt text itself is not stored.</summary>
    public string PromptHash { get; private set; } = null!;

    public string ModelProvider { get; private set; } = null!;

    public string ModelName { get; private set; } = null!;

    public string ModelProfileVersion { get; private set; } = null!;

    /// <summary>
    /// True when the run is a shadow evaluation (ARCHITECTURE.md 24.6). Shadow
    /// results reach the evaluation store only; they never influence the report
    /// an operator sees.
    /// </summary>
    public bool IsShadow { get; private set; }

    public AgentRunStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int LlmCallCount { get; private set; }

    public int ToolCallCount { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    /// <summary>Estimated spend in the platform's accounting currency.</summary>
    public decimal EstimatedCost { get; private set; }

    public ErrorCode? ErrorCode { get; private set; }

    /// <summary>Redacted failure explanation. Returned by the API.</summary>
    public string? ErrorReason { get; private set; }

    /// <summary>The finding this run produced, once it succeeded.</summary>
    public FindingId? FindingId { get; private set; }

    /// <summary>Wall-clock duration, available once the run has ended.</summary>
    public TimeSpan? Duration => CompletedAt - StartedAt;

    public static AgentRun Start(
        IncidentId incidentId,
        AgentExecutionIdentity identity,
        bool isShadow,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new AgentRun(AgentRunId.New(), incidentId, identity, isShadow, startedAt);
    }

    /// <summary>Accumulates usage as the run proceeds, for budget enforcement.</summary>
    public void RecordLlmCall(long inputTokens, long outputTokens, decimal estimatedCost)
    {
        LlmCallCount++;
        InputTokens += inputTokens;
        OutputTokens += outputTokens;
        EstimatedCost += estimatedCost;
    }

    /// <summary>Counts a tool execution, successful or not.</summary>
    public void RecordToolCall() => ToolCallCount++;

    /// <summary>
    /// Sets the call counts from a completed run's own accounting.
    /// </summary>
    /// <remarks>
    /// The executor enforces the budget against its own counters, so those are
    /// the authoritative totals. Replaying them one increment at a time here
    /// would be the same arithmetic done twice, and the two copies would
    /// eventually disagree about a run that ended early.
    /// </remarks>
    public void SetCallCounts(int llmCalls, int toolCalls)
    {
        LlmCallCount = llmCalls;
        ToolCallCount = toolCalls;
    }

    public void Complete(FindingId findingId, DateTimeOffset completedAt)
    {
        Status = AgentRunStatus.Succeeded;
        FindingId = findingId;
        CompletedAt = completedAt.ToUniversalTime();
    }

    /// <summary>
    /// Ends the run without a successful finding. Partial results are kept: a
    /// budget-exceeded run that gathered three useful observations is more
    /// valuable than nothing.
    /// </summary>
    public void Fail(AgentRunStatus status, ErrorCode errorCode, string reason, DateTimeOffset completedAt)
    {
        Status = status is AgentRunStatus.Succeeded or AgentRunStatus.Running
            ? AgentRunStatus.Failed
            : status;
        ErrorCode = errorCode;
        ErrorReason = Guard.AgainstTooLong(
            reason ?? string.Empty,
            MaximumReasonLength,
            DomainErrorCodes.IncidentFieldRequired);
        CompletedAt = completedAt.ToUniversalTime();
    }
}
