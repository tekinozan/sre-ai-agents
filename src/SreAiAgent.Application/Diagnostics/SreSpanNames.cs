namespace SreAiAgent.Application.Diagnostics;

/// <summary>
/// Span names from ARCHITECTURE.md 16.1, as constants.
/// </summary>
/// <remarks>
/// Constants rather than interpolated strings. A span name is a dimension in
/// every tracing backend, so building one from an incident id or an agent name
/// produces unbounded cardinality and, in most backends, a bill. Variable parts
/// belong in attributes, which is what <see cref="TraceAttributes"/> governs.
/// </remarks>
public static class SreSpanNames
{
    /// <summary>The root span of an investigation.</summary>
    public const string Investigation = "incident.investigation";

    /// <summary>
    /// The span the durable engine opens around an investigation: Temporal's
    /// tracing interceptor names it after the workflow type, not this
    /// repository's convention. It plays the role of <see cref="Investigation"/>
    /// on that engine, so the sampler has to recognise it too.
    /// </summary>
    public const string DurableInvestigation = "RunWorkflow:SreInvestigation";

    public const string Classify = "classify";
    public const string Route = "route";
    public const string AgentExecute = "agent.execute";
    public const string PromptRender = "prompt.render";
    public const string LlmCall = "llm.call";
    public const string ToolExecute = "tool.execute";
    public const string Correlate = "correlate";
    public const string ApprovalWait = "approval.wait";
    public const string RemediationExecute = "remediation.execute";

    public const string OutboxDispatch = "outbox.dispatch";
    public const string MessageConsume = "message.consume";
    public const string AlertIngest = "alert.ingest";
}
