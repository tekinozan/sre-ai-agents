using System.Collections.Frozen;
using System.Text;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Workflows;

// The model-backed halves of the activities: classification, correlation and
// recommendation. Split out so the orchestration in the main file reads as a
// sequence of steps rather than as prompt plumbing.

public sealed partial class InvestigationActivities
{
    private const int MaxEvidenceInCorrelation = 40;

    /// <summary>Consults the classifier when deterministic rules found nothing.</summary>
    private async Task<Result<IncidentClassification>> ClassifyWithModelAsync(
        ClassifyIncidentInput input,
        CancellationToken cancellationToken)
    {
        Result<ResolvedPrompt> prompt = await ResolvePromptAsync(
            input.ConfigurationSnapshotId, "classifier.classify", "fast", cancellationToken)
            .ConfigureAwait(false);

        if (prompt.IsFailure)
        {
            return Result<IncidentClassification>.Failure(prompt.Error);
        }

        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            ["incident_title"] = input.Incident.Title,
            ["incident_service"] = input.Incident.Service,
            ["incident_environment"] = input.Incident.Environment,
            ["incident_description"] = input.Incident.Description ?? "(none supplied)",
            ["incident_labels"] = FormatPairs(input.Incident.Labels),
            ["known_incident_types"] = string.Join(", ", KnownIncidentTypes),
            ["deterministic_partial_match"] = "(none)",
        };

        Result<LlmResult<ClassificationPayload>> response = await CallModelAsync<ClassificationPayload>(
            prompt.Value,
            variables,
            "IncidentClassification",
            input.Incident.IncidentId,
            input.Incident.Tenant,
            input.CorrelationId,
            "classifier",
            input.Deadline,
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IncidentClassification>.Failure(response.Error);
        }

        ClassificationPayload payload = response.Value.Value;

        // A model can name a type that routes to nobody. Unknown types are dropped
        // rather than trusted, and an empty result degrades to the caller's
        // fallback instead of producing a classification no agent can match.
        List<string> known = [.. payload.IncidentTypes
            .Select(static type => type.ToLowerInvariant())
            .Where(static type => KnownIncidentTypes.Contains(type))
            .Distinct(StringComparer.Ordinal)];

        if (known.Count == 0)
        {
            return Result<IncidentClassification>.Failure(Error.Permanent(
                ApplicationErrorCodes.LlmStructuredOutputInvalid,
                "The classifier returned no recognised incident type."));
        }

        return Result<IncidentClassification>.Success(new IncidentClassification(
            known,
            ClassificationMethod.LlmClassifier,
            Confidence.Clamp(payload.Confidence),
            payload.Rationale));
    }

    /// <summary>Merges expert findings into one explanation.</summary>
    private async Task<Result<CorrelationResult>> CorrelateWithModelAsync(
        CorrelateFindingsInput input,
        IReadOnlyList<AgentRunResult> contributing,
        CancellationToken cancellationToken)
    {
        Result<ResolvedPrompt> prompt = await ResolvePromptAsync(
            input.ConfigurationSnapshotId, "correlator.correlate", "analytical", cancellationToken)
            .ConfigureAwait(false);

        if (prompt.IsFailure)
        {
            return Result<CorrelationResult>.Failure(prompt.Error);
        }

        IReadOnlyList<EvidenceRecord> evidence = await _store
            .GetUsableEvidenceAsync(input.Incident.IncidentId, MaxEvidenceInCorrelation, cancellationToken)
            .ConfigureAwait(false);

        HashSet<EvidenceId> collected = [.. evidence.Select(static item => item.Id)];
        foreach (AgentRunResult run in contributing)
        {
            foreach (EvidenceId evidenceId in run.EvidenceIds)
            {
                collected.Add(evidenceId);
            }
        }

        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            ["incident_title"] = input.Incident.Title,
            ["incident_service"] = input.Incident.Service,
            ["incident_environment"] = input.Incident.Environment,
            ["incident_detected_at"] = input.Incident.DetectedAt.ToString("O", CultureInfo.InvariantCulture),
            ["incident_description"] = input.Incident.Description ?? "(none supplied)",
            ["agent_findings"] = FormatFindings(contributing),
            ["failed_agents"] = FormatFailedAgents(input.AgentRuns),
            ["evidence_summary"] = FormatEvidence(evidence),
        };

        Result<LlmResult<CorrelationPayload>> response = await CallModelAsync<CorrelationPayload>(
            prompt.Value,
            variables,
            "CorrelationResult",
            input.Incident.IncidentId,
            input.Incident.Tenant,
            input.CorrelationId,
            "correlator",
            input.Deadline,
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<CorrelationResult>.Failure(response.Error);
        }

        CorrelationPayload payload = response.Value.Value;

        // Correlated observations go through the same citation check as an
        // expert's. A summariser can invent an id just as easily as an
        // investigator can.
        AgentFinding bound = AgentFindingBinder.Bind(
            payload.Summary,
            [.. payload.KeyObservations.Select(static observation =>
                new RawObservation(observation.Statement, observation.EvidenceIds))],
            [.. payload.RankedHypotheses.Select(static hypothesis => new RawHypothesis(
                hypothesis.Statement,
                hypothesis.SupportingEvidenceIds,
                hypothesis.ContradictingEvidenceIds,
                hypothesis.Confidence))],
            payload.EvidenceGaps,
            [],
            payload.Confidence,
            collected);

        return Result<CorrelationResult>.Success(new CorrelationResult(
            bound.Summary,
            payload.Category,
            bound.Confidence,
            bound.Observations,
            bound.Hypotheses,
            bound.EvidenceGaps));
    }

    /// <summary>Turns the explanation into ranked next steps.</summary>
    private async Task<Result<RecommendationSet>> RecommendWithModelAsync(
        BuildRecommendationsInput input,
        CancellationToken cancellationToken)
    {
        Result<ResolvedPrompt> prompt = await ResolvePromptAsync(
            input.ConfigurationSnapshotId, "correlator.recommend", "analytical", cancellationToken)
            .ConfigureAwait(false);

        if (prompt.IsFailure)
        {
            return Result<RecommendationSet>.Failure(prompt.Error);
        }

        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            ["incident_title"] = input.Incident.Title,
            ["incident_service"] = input.Incident.Service,
            ["incident_environment"] = input.Incident.Environment,
            ["correlation_summary"] = input.Correlation.Summary,
            ["ranked_hypotheses"] = FormatHypotheses(input.Correlation.RankedHypotheses),
            ["evidence_gaps"] = string.Join("\n", input.Correlation.EvidenceGaps.Select(static gap => "- " + gap)),
            ["evidence_summary"] = "(see the correlated explanation)",
        };

        Result<LlmResult<RecommendationSetPayload>> response = await CallModelAsync<RecommendationSetPayload>(
            prompt.Value,
            variables,
            "RecommendationSet",
            input.Incident.IncidentId,
            input.Incident.Tenant,
            input.CorrelationId,
            "correlator",
            input.Deadline,
            cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<RecommendationSet>.Failure(response.Error);
        }

        List<ProposedRecommendation> proposed = [];
        int rank = 1;

        foreach (RecommendationPayload item in response.Value.Value.Recommendations.OrderBy(static r => r.Rank))
        {
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            proposed.Add(new ProposedRecommendation(
                rank++,
                item.Title,
                item.Rationale,
                ParseRisk(item.Risk),
                Confidence.Clamp(item.Confidence),
                []));
        }

        return Result<RecommendationSet>.Success(new RecommendationSet(proposed));
    }

    /// <summary>
    /// Parses a risk level from model output, defaulting upward on anything
    /// unrecognised.
    /// </summary>
    /// <remarks>
    /// An unknown risk becomes Destructive, not ReadOnly. Guessing low on a value
    /// nobody recognises is how an action gets executed that a human would have
    /// refused; guessing high only means an extra approval.
    /// </remarks>
    private static RiskLevel ParseRisk(string? value) => value switch
    {
        "read_only" => RiskLevel.ReadOnly,
        "low" => RiskLevel.Low,
        "medium" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        _ => RiskLevel.Destructive,
    };

    private static readonly FrozenSet<string> KnownIncidentTypes =
        new[]
        {
            "database", "latency", "timeout", "connection-pool", "error-rate", "availability",
            "deployment", "kubernetes", "resource-exhaustion", "queue", "cache",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static string FormatPairs(IReadOnlyDictionary<string, string> pairs)
        => pairs.Count == 0
            ? "(none)"
            : string.Join("\n", pairs.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"- {pair.Key}: {pair.Value}"));

    private static string FormatFindings(IReadOnlyList<AgentRunResult> runs)
    {
        StringBuilder builder = new();

        foreach (AgentRunResult run in runs)
        {
            builder.Append("## ").Append(run.AgentName).AppendLine();
            builder.AppendLine(run.Finding!.Summary);

            foreach (Observation observation in run.Finding.Observations)
            {
                builder.Append("- observation").Append(observation.IsVerified ? " (verified)" : " (UNVERIFIED)")
                    .Append(": ").AppendLine(observation.Statement);

                if (observation.EvidenceIds.Count > 0)
                {
                    builder.Append("  evidence_id: ")
                        .AppendLine(string.Join(", ", observation.EvidenceIds.Select(static id => id.ToString())));
                }
            }

            foreach (Hypothesis hypothesis in run.Finding.Hypotheses)
            {
                builder.Append("- hypothesis (confidence ")
                    .Append(hypothesis.Confidence.ToString())
                    .Append("): ").AppendLine(hypothesis.Statement);

                if (hypothesis.ContradictingEvidenceIds.Count > 0)
                {
                    builder.Append("  contradicted by evidence_id: ")
                        .AppendLine(string.Join(
                            ", ", hypothesis.ContradictingEvidenceIds.Select(static id => id.ToString())));
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Names the agents that did not complete.
    /// </summary>
    /// <remarks>
    /// Passed to the correlator on purpose. An operator reading "the database was
    /// not checked" will check it; an operator reading a confident report that
    /// silently omits the database will not.
    /// </remarks>
    private static string FormatFailedAgents(IReadOnlyList<AgentRunResult> runs)
    {
        List<AgentRunResult> failed =
            [.. runs.Where(static run => run.Finding is null || run.Status is not AgentRunStatus.Succeeded)];

        return failed.Count == 0
            ? "(none)"
            : string.Join("\n", failed.Select(static run =>
                $"- {run.AgentName}: {run.Status} ({run.ErrorMessage ?? "no detail"})"));
    }

    private static string FormatEvidence(IReadOnlyList<EvidenceRecord> evidence)
    {
        if (evidence.Count == 0)
        {
            return "(no evidence collected)";
        }

        StringBuilder builder = new();
        foreach (EvidenceRecord record in evidence)
        {
            builder.Append("evidence_id: ").AppendLine(record.Id.ToString());
            builder.Append("  ").Append(record.ToolName).Append(": ").AppendLine(record.Summary);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatHypotheses(IReadOnlyList<Hypothesis> hypotheses)
        => hypotheses.Count == 0
            ? "(none)"
            : string.Join("\n", hypotheses.Select(static hypothesis =>
                $"- {hypothesis.Statement} (confidence {hypothesis.Confidence})"));
}

/// <summary>A prompt and the model profile it runs under, both resolved through a release.</summary>
internal sealed record ResolvedPrompt(PromptDefinition Prompt, ModelProfile Profile);
