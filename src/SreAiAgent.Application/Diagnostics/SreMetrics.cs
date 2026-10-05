using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SreAiAgent.Application.Diagnostics;

/// <summary>
/// The instruments listed in ARCHITECTURE.md 16.2, created once.
/// </summary>
/// <remarks>
/// Instruments are fields rather than being resolved per call. Creating one is a
/// dictionary lookup plus a lock inside the meter, and these are recorded on the
/// hottest paths in the system; the difference is small per call and unmissable
/// across an investigation that makes hundreds of tool and model calls.
///
/// Registered through <see cref="IMeterFactory"/> so tests can collect from an
/// isolated meter instead of racing against every other test in the process.
/// </remarks>
public sealed class SreMetrics
{
    /// <summary>The meter name to register with the metrics provider.</summary>
    public const string MeterName = "SreAiAgent";

    private readonly Counter<long> _incidentStarted;
    private readonly Counter<long> _incidentCompleted;
    private readonly Counter<long> _incidentFailed;
    private readonly Histogram<double> _incidentDuration;
    private readonly Counter<long> _workflowResume;
    private readonly Counter<long> _workflowTimeout;
    private readonly Counter<long> _agentRun;
    private readonly Histogram<double> _agentRunDuration;
    private readonly Counter<long> _llmRequests;
    private readonly Counter<long> _llmInputTokens;
    private readonly Counter<long> _llmOutputTokens;
    private readonly Counter<double> _llmEstimatedCost;
    private readonly Counter<long> _toolRequests;
    private readonly Counter<long> _toolPolicyDenied;
    private readonly Histogram<double> _toolDuration;
    private readonly Counter<long> _promptRenderFailure;
    private readonly Counter<long> _reportFeedback;

    public SreMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter meter = meterFactory.Create(MeterName, SreActivitySources.Version);

        _incidentStarted = meter.CreateCounter<long>(
            "sre_incident_started_total", description: "Investigations started.");
        _incidentCompleted = meter.CreateCounter<long>(
            "sre_incident_completed_total", description: "Investigations that produced a report.");
        _incidentFailed = meter.CreateCounter<long>(
            "sre_incident_failed_total", description: "Investigations that ended without a report.");
        _incidentDuration = meter.CreateHistogram<double>(
            "sre_incident_duration_seconds", unit: "s", description: "End-to-end investigation duration.");

        _workflowResume = meter.CreateCounter<long>(
            "sre_workflow_resume_total", description: "Workflows resumed after a worker restart.");
        _workflowTimeout = meter.CreateCounter<long>(
            "sre_workflow_timeout_total", description: "Workflows stopped by the deadline.");

        _agentRun = meter.CreateCounter<long>(
            "sre_agent_run_total", description: "Agent executions by agent and outcome.");
        _agentRunDuration = meter.CreateHistogram<double>(
            "sre_agent_run_duration_seconds", unit: "s", description: "Agent execution duration.");

        _llmRequests = meter.CreateCounter<long>(
            "sre_llm_requests_total", description: "Model calls by provider, model and outcome.");
        _llmInputTokens = meter.CreateCounter<long>(
            "sre_llm_input_tokens_total", description: "Prompt tokens consumed.");
        _llmOutputTokens = meter.CreateCounter<long>(
            "sre_llm_output_tokens_total", description: "Completion tokens produced.");
        _llmEstimatedCost = meter.CreateCounter<double>(
            "sre_llm_estimated_cost_total", description: "Estimated model spend.");

        _toolRequests = meter.CreateCounter<long>(
            "sre_tool_requests_total", description: "Tool executions by tool and outcome.");
        _toolPolicyDenied = meter.CreateCounter<long>(
            "sre_tool_policy_denied_total", description: "Tool executions refused by policy.");
        _toolDuration = meter.CreateHistogram<double>(
            "sre_tool_duration_seconds", unit: "s", description: "Tool execution duration.");

        _promptRenderFailure = meter.CreateCounter<long>(
            "sre_prompt_render_failure_total", description: "Prompt renders rejected by the renderer.");

        _reportFeedback = meter.CreateCounter<long>(
            "sre_report_feedback_total", description: "Verdicts people gave on root cause reports.");

        meter.CreateObservableCounter(
            "sre_trace_attribute_rejected_total",
            static () => TraceAttributes.RejectedTagCount,
            description: "Span attributes dropped for not being on the allowlist.");
    }

    // Recording helpers below build their dimensions with TagList, a struct that
    // holds up to eight tags inline. The obvious alternative, a
    // KeyValuePair<string, object?>[], allocates on every single recording, and
    // these run once per tool call, per model call and per agent run.

    public void IncidentStarted(string tenant, string environment)
        => _incidentStarted.Add(1, Tags(tenant, environment));

    public void IncidentCompleted(string tenant, string environment, string outcome, TimeSpan duration)
    {
        TagList tags = Tags(tenant, environment);
        tags.Add("outcome", outcome);
        _incidentCompleted.Add(1, tags);
        _incidentDuration.Record(duration.TotalSeconds, tags);
    }

    /// <remarks>
    /// Counted on every submission, a revision included: the series answers how
    /// often people judge reports, and the feedback table answers what they
    /// currently think.
    /// </remarks>
    public void ReportFeedback(string tenant, string verdict, string outcome)
        => _reportFeedback.Add(1, new TagList
        {
            { TraceAttributes.Tenant, tenant },
            { "verdict", verdict },
            { "outcome", outcome },
        });

    public void IncidentFailed(string tenant, string environment, string errorCode)
    {
        TagList tags = Tags(tenant, environment);
        tags.Add(TraceAttributes.ErrorCode, errorCode);
        _incidentFailed.Add(1, tags);
    }

    public void WorkflowResumed(string tenant) => _workflowResume.Add(1, new TagList { { TraceAttributes.Tenant, tenant } });

    public void WorkflowTimedOut(string tenant) => _workflowTimeout.Add(1, new TagList { { TraceAttributes.Tenant, tenant } });

    public void AgentRunCompleted(string agentName, string agentVersion, string status, bool isShadow, TimeSpan duration)
    {
        TagList tags = new()
        {
            { TraceAttributes.AgentName, agentName },
            { TraceAttributes.AgentVersion, agentVersion },
            { TraceAttributes.ResultStatus, status },
            { TraceAttributes.AgentIsShadow, isShadow },
        };
        _agentRun.Add(1, tags);
        _agentRunDuration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>
    /// Dimensions the LLM instruments by the pinned deployment as well as the
    /// provider kind and outcome. Tenant is deliberately excluded — the metric
    /// backend has no upper bound on tenant count, and a bounded cardinality
    /// per instrument is what keeps the store affordable.
    /// </summary>
    public void LlmCall(
        string providerKind,
        string deploymentLogicalName,
        int deploymentVersion,
        string status,
        long inputTokens,
        long outputTokens,
        decimal estimatedCost)
    {
        TagList tags = new()
        {
            { TraceAttributes.ModelProvider, providerKind },
            { TraceAttributes.ModelName, deploymentLogicalName },
            { TraceAttributes.ModelDeploymentVersion, deploymentVersion },
            { TraceAttributes.ResultStatus, status },
        };
        _llmRequests.Add(1, tags);
        _llmInputTokens.Add(inputTokens, tags);
        _llmOutputTokens.Add(outputTokens, tags);
        _llmEstimatedCost.Add((double)estimatedCost, tags);
    }

    public void ToolCall(string toolName, string toolVersion, string status, TimeSpan duration)
    {
        TagList tags = new()
        {
            { TraceAttributes.ToolName, toolName },
            { TraceAttributes.ToolVersion, toolVersion },
            { TraceAttributes.ResultStatus, status },
        };
        _toolRequests.Add(1, tags);
        _toolDuration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>
    /// Counted separately from a failed call. A policy denial is a security
    /// signal: a sustained rise means an agent is repeatedly asking for something
    /// it is not allowed to have, which is worth an alert even though nothing is
    /// broken.
    /// </summary>
    public void ToolPolicyDenied(string toolName, string reason)
        => _toolPolicyDenied.Add(1, new TagList
        {
            { TraceAttributes.ToolName, toolName },
            { "denial.reason", reason },
        });

    public void PromptRenderFailed(string promptName, string reason)
        => _promptRenderFailure.Add(1, new TagList
        {
            { TraceAttributes.PromptName, promptName },
            { TraceAttributes.ErrorCode, reason },
        });

    private static TagList Tags(string tenant, string environment) => new()
    {
        { TraceAttributes.Tenant, tenant },
        { TraceAttributes.Environment, environment },
    };
}
