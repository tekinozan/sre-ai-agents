using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>
/// The structured result an agent must produce (ARCHITECTURE.md 7.3). This is
/// the only shape an agent can return: free-form prose is not an accepted
/// output, and chain-of-thought is never captured, stored or exposed.
/// </summary>
/// <param name="Summary">Operator-facing summary of what the agent concluded.</param>
/// <param name="Observations">Facts, each either evidence-backed or explicitly unverified.</param>
/// <param name="Hypotheses">Candidate explanations with supporting and contradicting evidence.</param>
/// <param name="EvidenceGaps">
/// What the agent could not check. Stating the gap is more useful than guessing
/// past it, and the evaluation harness scores whether gaps were declared.
/// </param>
/// <param name="RecommendedNextChecks">Concrete follow-up investigations.</param>
/// <param name="Confidence">Overall confidence, constrained to [0, 1].</param>
public sealed record AgentFinding(
    string Summary,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<string> EvidenceGaps,
    IReadOnlyList<string> RecommendedNextChecks,
    Confidence Confidence)
{
    /// <summary>A finding that reached no conclusion, used when an agent fails.</summary>
    public static AgentFinding Empty(string summary)
        => new(summary, [], [], [], [], Confidence.None);

    /// <summary>Every distinct evidence id cited anywhere in the finding.</summary>
    /// <remarks>
    /// Materialized rather than lazily enumerated: callers iterate it more than
    /// once (persistence, correlation, the citation-accuracy evaluation), and a
    /// deferred LINQ chain would re-walk every observation each time.
    /// </remarks>
    public IReadOnlyList<EvidenceId> CitedEvidenceIds()
    {
        HashSet<EvidenceId> cited = [];

        foreach (Observation observation in Observations)
        {
            foreach (EvidenceId evidenceId in observation.EvidenceIds)
            {
                cited.Add(evidenceId);
            }
        }

        foreach (Hypothesis hypothesis in Hypotheses)
        {
            foreach (EvidenceId evidenceId in hypothesis.SupportingEvidenceIds)
            {
                cited.Add(evidenceId);
            }

            foreach (EvidenceId evidenceId in hypothesis.ContradictingEvidenceIds)
            {
                cited.Add(evidenceId);
            }
        }

        return cited.Count == 0 ? [] : [.. cited];
    }

    /// <summary>
    /// Observations the platform could not substantiate. Surfaced to operators
    /// and tracked as the unsupported-claim rate in the release gate
    /// (ARCHITECTURE.md 18).
    /// </summary>
    public int UnverifiedObservationCount()
    {
        int count = 0;
        foreach (Observation observation in Observations)
        {
            if (!observation.IsVerified)
            {
                count++;
            }
        }

        return count;
    }
}
