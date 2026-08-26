using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers the JSONB columns of ARCHITECTURE.md 9.1.
/// </summary>
/// <remarks>
/// A round trip is the only honest test of a serializer. Serialization alone
/// always looks correct, because writing a property never needs a constructor;
/// it is the read back that discovers a type the serializer cannot rebuild. And
/// these particular types are exactly the ones at risk: the domain deliberately
/// hides their constructors so an invariant cannot be bypassed, which is also
/// what stops a serializer from bypassing it accidentally.
///
/// The read path here is not hypothetical. Correlation loads every prior finding
/// through this converter, so anything lost in transit is lost from the report an
/// operator reads.
/// </remarks>
public sealed class JsonbRoundTripTests
{
    private static readonly EvidenceId FirstEvidence = EvidenceId.New();
    private static readonly EvidenceId SecondEvidence = EvidenceId.New();

    [Fact]
    public void ConfidenceSurvivesTheRoundTrip()
    {
        AgentFinding finding = new(
            "Connection pool exhaustion on the primary.",
            [],
            [],
            [],
            [],
            Confidence.From(0.82m));

        AgentFinding restored = JsonbConverters.DeserializeFinding(JsonbConverters.SerializeFinding(finding));

        restored.Confidence.Value.ShouldBe(0.82m);
    }

    [Fact]
    public void VerifiedObservationsKeepTheirEvidence()
    {
        IReadOnlyList<Observation> observations =
        [
            Observation.Verified("Active connections reached the configured maximum.", [FirstEvidence]),
            Observation.Unverified("The spike may have started earlier than the retention window."),
        ];

        IReadOnlyList<Observation> restored =
            JsonbConverters.DeserializeObservations(JsonbConverters.SerializeObservations(observations));

        restored.Count.ShouldBe(2);
        restored[0].Statement.ShouldBe("Active connections reached the configured maximum.");
        restored[0].IsVerified.ShouldBeTrue();
        restored[0].EvidenceIds.ShouldBe([FirstEvidence]);
        restored[1].IsVerified.ShouldBeFalse();
        restored[1].EvidenceIds.ShouldBeEmpty();
    }

    [Fact]
    public void HypothesesKeepBothSidesOfTheirEvidence()
    {
        IReadOnlyList<Hypothesis> hypotheses =
        [
            new Hypothesis(
                "A slow query held connections open.",
                [FirstEvidence],
                [SecondEvidence],
                Confidence.From(0.4m)),
        ];

        IReadOnlyList<Hypothesis> restored =
            JsonbConverters.DeserializeHypotheses(JsonbConverters.SerializeHypotheses(hypotheses));

        restored.Count.ShouldBe(1);
        restored[0].SupportingEvidenceIds.ShouldBe([FirstEvidence]);
        restored[0].ContradictingEvidenceIds.ShouldBe([SecondEvidence]);
        restored[0].Confidence.Value.ShouldBe(0.4m);
    }

    [Fact]
    public void AnObservationCannotBeRestoredAsVerifiedWithoutEvidence()
    {
        // A hand-edited row, or one written before the invariant existed. The
        // converter must downgrade rather than reconstruct a state the domain
        // refuses to build, because every consumer downstream trusts IsVerified
        // to mean an evidence id exists.
        const string Tampered = """[{"statement":"The database was the cause.","evidence_ids":[],"is_verified":true}]""";

        IReadOnlyList<Observation> restored = JsonbConverters.DeserializeObservations(Tampered);

        restored.Count.ShouldBe(1);
        restored[0].IsVerified.ShouldBeFalse();
    }
}
