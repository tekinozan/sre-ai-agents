using System.Text.Json.Serialization;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>
/// A factual statement an agent makes about the incident (ARCHITECTURE.md 7.3).
/// </summary>
/// <remarks>
/// The rule from section 5.2 that "an observation without an evidence reference
/// is marked unverified or rejected" is enforced by construction: the only way
/// to build a verified observation is to supply at least one evidence id. A
/// model that invents a fact simply cannot produce a verified observation.
/// </remarks>
[JsonConverter(typeof(ObservationJsonConverter))]
public sealed record Observation
{
    private Observation(string statement, IReadOnlyList<EvidenceId> evidenceIds, bool isVerified)
    {
        Statement = statement;
        EvidenceIds = evidenceIds;
        IsVerified = isVerified;
    }

    /// <summary>What the agent claims to be true.</summary>
    public string Statement { get; }

    /// <summary>The evidence backing the claim. Empty only when unverified.</summary>
    public IReadOnlyList<EvidenceId> EvidenceIds { get; }

    /// <summary>True only when at least one piece of evidence supports the claim.</summary>
    public bool IsVerified { get; }

    /// <summary>Creates an evidence-backed observation.</summary>
    /// <exception cref="DomainException">No evidence was supplied.</exception>
    public static Observation Verified(string statement, IReadOnlyList<EvidenceId> evidenceIds)
    {
        Guard.AgainstNullOrWhiteSpace(statement, DomainErrorCodes.EvidenceReferenceRequired);
        ArgumentNullException.ThrowIfNull(evidenceIds);

        if (evidenceIds.Count == 0)
        {
            throw new DomainException(
                DomainErrorCodes.EvidenceReferenceRequired,
                "A verified observation must cite at least one piece of evidence.");
        }

        return new Observation(statement, evidenceIds, isVerified: true);
    }

    /// <summary>
    /// Creates an observation the platform could not substantiate. Kept rather
    /// than discarded so an operator can see what the agent believed and the
    /// evaluation harness can score unsupported claims.
    /// </summary>
    public static Observation Unverified(string statement)
    {
        Guard.AgainstNullOrWhiteSpace(statement, DomainErrorCodes.EvidenceReferenceRequired);
        return new Observation(statement, [], isVerified: false);
    }

    /// <summary>
    /// Builds an observation from untrusted model output, downgrading to
    /// unverified rather than throwing when the citations are missing.
    /// </summary>
    public static Observation FromModelOutput(string statement, IReadOnlyList<EvidenceId> evidenceIds)
        => evidenceIds is { Count: > 0 }
            ? Verified(statement, evidenceIds)
            : Unverified(statement);
}
