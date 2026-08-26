using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Agents;

/// <summary>
/// Turns untrusted model output into domain types (ARCHITECTURE.md 5.2, 7.3).
/// </summary>
/// <remarks>
/// This is the boundary where a model's claims meet the platform's invariants,
/// and it is the single most important translation in the system.
///
/// Two rules are enforced here and nowhere else:
///
/// An evidence id the model cited must actually exist. A model can produce a
/// well-formed UUID that was never returned by any tool, and a schema cannot tell
/// the difference. Citations are checked against the evidence this run genuinely
/// collected, and an observation whose citations do not survive that check is
/// downgraded to unverified rather than dropped, so an operator still sees the
/// claim and the evaluation suite can score it as unsupported.
///
/// Confidence is clamped rather than rejected. A model returning 1.7 has told us
/// something useful about its certainty even though it broke the contract, and
/// failing the whole finding over one out-of-range number would discard genuine
/// evidence-backed observations alongside it.
/// </remarks>
public static class AgentFindingBinder
{
    /// <summary>
    /// Binds a model finding, keeping only citations that reference real evidence.
    /// </summary>
    /// <param name="summary">Model summary.</param>
    /// <param name="observations">Raw observations, statement plus cited ids.</param>
    /// <param name="hypotheses">Raw hypotheses.</param>
    /// <param name="evidenceGaps">What the model said it could not check.</param>
    /// <param name="recommendedNextChecks">Suggested follow-ups.</param>
    /// <param name="confidence">Raw confidence, clamped into range.</param>
    /// <param name="collectedEvidence">Evidence this run actually produced.</param>
    public static AgentFinding Bind(
        string summary,
        IReadOnlyList<RawObservation> observations,
        IReadOnlyList<RawHypothesis> hypotheses,
        IReadOnlyList<string> evidenceGaps,
        IReadOnlyList<string> recommendedNextChecks,
        decimal confidence,
        IReadOnlySet<EvidenceId> collectedEvidence)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(hypotheses);
        ArgumentNullException.ThrowIfNull(collectedEvidence);

        List<Observation> bound = new(observations.Count);
        foreach (RawObservation observation in observations)
        {
            if (string.IsNullOrWhiteSpace(observation.Statement))
            {
                continue;
            }

            IReadOnlyList<EvidenceId> real = KeepReal(observation.EvidenceIds, collectedEvidence);
            bound.Add(Observation.FromModelOutput(observation.Statement, real));
        }

        List<Hypothesis> boundHypotheses = new(hypotheses.Count);
        foreach (RawHypothesis hypothesis in hypotheses)
        {
            if (string.IsNullOrWhiteSpace(hypothesis.Statement))
            {
                continue;
            }

            boundHypotheses.Add(new Hypothesis(
                hypothesis.Statement,
                KeepReal(hypothesis.SupportingEvidenceIds, collectedEvidence),
                KeepReal(hypothesis.ContradictingEvidenceIds, collectedEvidence),
                Confidence.Clamp(hypothesis.Confidence)));
        }

        return new AgentFinding(
            string.IsNullOrWhiteSpace(summary) ? "The agent returned no summary." : summary,
            bound,
            boundHypotheses,
            evidenceGaps ?? [],
            recommendedNextChecks ?? [],
            Confidence.Clamp(confidence));
    }

    /// <summary>
    /// Parses cited ids and keeps only those the run actually collected.
    /// </summary>
    /// <remarks>
    /// An unparseable or unknown id is dropped silently rather than failing the
    /// finding. The consequence is visible where it matters: an observation that
    /// loses all its citations becomes unverified, which the report shows and the
    /// citation-accuracy metric counts.
    /// </remarks>
    private static List<EvidenceId> KeepReal(
        IReadOnlyList<string> cited,
        IReadOnlySet<EvidenceId> collected)
    {
        if (cited is null or { Count: 0 })
        {
            return [];
        }


        List<EvidenceId> real = new(cited.Count);
        foreach (string candidate in cited)
        {
            if (Guid.TryParse(candidate, CultureInfo.InvariantCulture, out Guid parsed))
            {
                EvidenceId evidenceId = EvidenceId.From(parsed);
                if (collected.Contains(evidenceId))
                {
                    real.Add(evidenceId);
                }
            }
        }

        return real;
    }
}

/// <summary>An observation exactly as a model returned it.</summary>
/// <param name="Statement">The claim.</param>
/// <param name="EvidenceIds">Cited ids as strings, not yet parsed or verified.</param>
public sealed record RawObservation(string Statement, IReadOnlyList<string> EvidenceIds);

/// <summary>A hypothesis exactly as a model returned it.</summary>
/// <param name="Statement">The proposed explanation.</param>
/// <param name="SupportingEvidenceIds">Cited supporting ids, unverified.</param>
/// <param name="ContradictingEvidenceIds">Cited contradicting ids, unverified.</param>
/// <param name="Confidence">Raw confidence, possibly out of range.</param>
public sealed record RawHypothesis(
    string Statement,
    IReadOnlyList<string> SupportingEvidenceIds,
    IReadOnlyList<string> ContradictingEvidenceIds,
    decimal Confidence);
