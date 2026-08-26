using System.Collections.Frozen;
using System.Text;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// The mutable state of one agent execution: what it has collected, what it was
/// refused, and how that becomes the next prompt.
/// </summary>
/// <remarks>
/// Separate from <see cref="AgentExecutor"/> so the loop reads as a sequence of
/// decisions rather than as bookkeeping. It is not thread-safe and does not need
/// to be: one session belongs to one agent execution, and the fan-out gives each
/// agent its own.
/// </remarks>
public sealed class AgentSession
{
    private readonly AgentContext _context;
    private readonly AgentRunId _runId;
    private readonly List<EvidenceId> _evidenceIds = [];
    private readonly List<ToolOutcome> _toolOutcomes = [];
    private readonly HashSet<EvidenceId> _collected = [];

    public AgentSession(AgentContext context, AgentRunId runId)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _runId = runId;

        // Prior evidence counts as citable. An agent that reasons over what an
        // earlier expert already gathered must be able to cite it, otherwise the
        // second expert's observations all arrive unverified.
        foreach (EvidenceRecord existing in context.ExistingEvidence)
        {
            _collected.Add(existing.Id);
        }

        ServiceAllowlist = new[] { context.Incident.Service }.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Services this agent may query, derived from the incident.</summary>
    public IReadOnlySet<string> ServiceAllowlist { get; }

    /// <summary>Records a tool result and its evidence, or the reason it was refused.</summary>
    public void RecordToolResult(string toolName, ToolExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess && result.EvidenceId is { } evidenceId)
        {
            _evidenceIds.Add(evidenceId);
            _collected.Add(evidenceId);
            _toolOutcomes.Add(new ToolOutcome(toolName, true, result.Summary ?? string.Empty, evidenceId));
            return;
        }

        // A denial is fed back as an outcome rather than swallowed. The agent
        // should reason about what it could not see, and the report should say so.
        _toolOutcomes.Add(new ToolOutcome(
            toolName,
            false,
            result.Error?.Message ?? "The tool did not return a result.",
            null));
    }

    /// <summary>Records that a tool was not attempted because the budget was spent.</summary>
    public void RecordToolRefusal(string toolName, string reason)
        => _toolOutcomes.Add(new ToolOutcome(toolName, false, reason, null));

    /// <summary>
    /// Builds the prompt variables for the next turn.
    /// </summary>
    /// <remarks>
    /// Evidence is rendered with its id inline as <c>evidence_id: {guid}</c>,
    /// which is what makes citation possible at all: a model cannot cite an
    /// identifier it was never shown. The list is capped because an investigation
    /// can accumulate more evidence than fits in a context window, and the newest
    /// is kept because it is the evidence this agent just asked for.
    /// </remarks>
    public IReadOnlyDictionary<string, string> BuildVariables(int maxEvidence)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            ["incident_title"] = _context.Incident.Title,
            ["incident_service"] = _context.Incident.Service,
            ["incident_environment"] = _context.Incident.Environment,
            ["incident_severity"] = _context.Incident.Severity.ToString(),
            ["incident_detected_at"] = _context.Incident.DetectedAt.ToString("O", CultureInfo.InvariantCulture),
            ["incident_description"] = _context.Incident.Description ?? "(none supplied)",
            ["incident_labels"] = FormatLabels(_context.Incident.Labels),
            ["available_tools"] = FormatTools(_context.AvailableTools),
            ["existing_evidence"] = FormatEvidence(maxEvidence),
            ["iteration_note"] = FormatToolOutcomes(),
        };

        return variables;
    }

    /// <summary>Audit metadata for one model call.</summary>
    public LlmCallMetadata MetadataFor(RenderedPrompt prompt, AgentRunId runId, int attempt)
        => new()
        {
            IncidentId = _context.Incident.IncidentId,
            AgentRunId = runId,
            AgentName = _context.Definition.Name,
            PromptName = _context.Prompt.Name,
            PromptVersion = _context.Prompt.Version,
            PromptHash = prompt.Hash,
            Tenant = _context.Tenant,
            CorrelationId = _context.CorrelationId,
            Attempt = attempt,
        };

    /// <summary>
    /// A stable idempotency key for a tool request.
    /// </summary>
    /// <remarks>
    /// Derived from the run, the tool and the arguments rather than random, so a
    /// retried workflow activity produces the same key and the gateway resolves
    /// to the existing result instead of calling the backend a second time.
    /// </remarks>
    public Guid RequestIdFor(AgentToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string material = string.Concat(
            _runId.ToString(), "|", request.ToolName, "|", request.Arguments.GetRawText());

        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Binds raw model output into a domain finding.</summary>
    public AgentFinding BindFinding(AgentFindingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return AgentFindingBinder.Bind(
            payload.Summary,
            [.. payload.Observations.Select(static observation =>
                new RawObservation(observation.Statement, observation.EvidenceIds))],
            [.. payload.Hypotheses.Select(static hypothesis => new RawHypothesis(
                hypothesis.Statement,
                hypothesis.SupportingEvidenceIds,
                hypothesis.ContradictingEvidenceIds,
                hypothesis.Confidence))],
            payload.EvidenceGaps,
            payload.RecommendedNextChecks,
            payload.Confidence,
            _collected.ToFrozenSet());
    }

    public AgentRunResult Succeeded(AgentFinding finding) => Build(AgentRunStatus.Succeeded, finding, null, null);

    public AgentRunResult Cancelled()
        => Build(AgentRunStatus.Cancelled, null, Errors.ApplicationErrorCodes.WorkflowCancelled,
            "The investigation was cancelled.");

    public AgentRunResult Failed(ErrorCode code, string message)
        => Build(AgentRunStatus.Failed, null, code, message);

    /// <summary>
    /// Ends the run on an exhausted budget, keeping whatever was gathered.
    /// </summary>
    /// <remarks>
    /// A partial result rather than a failure (ARCHITECTURE.md 11.3). The evidence
    /// this agent collected is still cited by the correlation step even though the
    /// agent itself never reached a conclusion.
    /// </remarks>
    public AgentRunResult BudgetExceeded(string reason)
        => Build(AgentRunStatus.BudgetExceeded, null, Errors.ApplicationErrorCodes.BudgetExceeded, reason);

    private AgentRunResult Build(
        AgentRunStatus status,
        AgentFinding? finding,
        ErrorCode? code,
        string? message)
        => new(
            _context.Definition.Name,
            _context.Definition.Version,
            _runId,
            status,
            _context.IsShadow,
            finding,
            _evidenceIds,
            code,
            message);

    private static string FormatLabels(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count == 0)
        {
            return "(none)";
        }

        StringBuilder builder = new();
        foreach ((string key, string value) in labels.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("- ").Append(key).Append(": ").AppendLine(value);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatTools(IReadOnlyList<ToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return "(none available)";
        }

        StringBuilder builder = new();
        foreach (ToolDefinition tool in tools)
        {
            builder.Append("- ").Append(tool.Name).Append(" (").Append(tool.Version).Append("): ")
                .AppendLine(tool.Description);
            builder.Append("  arguments schema: ").AppendLine(tool.ParameterSchema.GetRawText());
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatEvidence(int maxEvidence)
    {
        if (_toolOutcomes.Count == 0 && _context.ExistingEvidence.Count == 0)
        {
            return "(no evidence collected yet)";
        }

        StringBuilder builder = new();

        foreach (EvidenceRecord existing in _context.ExistingEvidence.Take(maxEvidence))
        {
            builder.Append("evidence_id: ").AppendLine(existing.Id.ToString());
            builder.Append("  from ").Append(existing.ToolName).Append(": ").AppendLine(existing.Summary);
        }

        foreach (ToolOutcome outcome in _toolOutcomes.Where(static item => item.EvidenceId is not null))
        {
            builder.Append("evidence_id: ").AppendLine(outcome.EvidenceId!.Value.ToString());
            builder.Append("  from ").Append(outcome.ToolName).Append(": ").AppendLine(outcome.Detail);
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatToolOutcomes()
    {
        List<ToolOutcome> refused = [.. _toolOutcomes.Where(static outcome => !outcome.Succeeded)];
        if (refused.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new("Tools that did not return data:");
        builder.AppendLine();
        foreach (ToolOutcome outcome in refused)
        {
            builder.Append("- ").Append(outcome.ToolName).Append(": ").AppendLine(outcome.Detail);
        }

        builder.AppendLine(
            "Report these as evidence gaps rather than assuming what they would have shown.");

        return builder.ToString();
    }

    private sealed record ToolOutcome(string ToolName, bool Succeeded, string Detail, EvidenceId? EvidenceId);
}
