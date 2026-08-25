using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>
/// A candidate explanation with the evidence for and against it
/// (ARCHITECTURE.md 7.3).
/// </summary>
/// <remarks>
/// Contradicting evidence is a first-class field rather than an afterthought.
/// An investigation that only ever collects confirmation is how a plausible
/// wrong answer reaches an operator with high confidence.
/// </remarks>
/// <param name="Statement">The proposed explanation.</param>
/// <param name="SupportingEvidenceIds">Evidence consistent with the hypothesis.</param>
/// <param name="ContradictingEvidenceIds">Evidence that argues against it.</param>
/// <param name="Confidence">Constrained to [0, 1] by the type.</param>
public sealed record Hypothesis(
    string Statement,
    IReadOnlyList<EvidenceId> SupportingEvidenceIds,
    IReadOnlyList<EvidenceId> ContradictingEvidenceIds,
    Confidence Confidence)
{
    /// <summary>
    /// True when nothing supports the hypothesis. Such a hypothesis may be
    /// reported as an open question but must not drive a recommendation.
    /// </summary>
    public bool IsSpeculative => SupportingEvidenceIds.Count == 0;
}
