using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Tests.Findings;

/// <summary>
/// Covers the confidence bounds and evidence-reference rules of
/// ARCHITECTURE.md 5.2, the two invariants that stop a model's unsupported
/// assertion from reaching an operator as fact.
/// </summary>
public sealed class EvidenceRuleTests
{
    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(42)]
    public void ConfidenceOutsideTheUnitIntervalIsRejected(double raw)
    {
        DomainException exception = Should.Throw<DomainException>(() => Confidence.From((decimal)raw));

        exception.Code.ShouldBe(DomainErrorCodes.ConfidenceOutOfRange);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void ConfidenceInsideTheUnitIntervalIsAccepted(double raw)
        => Confidence.From((decimal)raw).Value.ShouldBe((decimal)raw);

    [Fact]
    public void ClampingIsAvailableForUntrustedModelOutput()
    {
        Confidence.Clamp(1.8m).ShouldBe(Confidence.Certain);
        Confidence.Clamp(-3m).ShouldBe(Confidence.None);
    }

    [Fact]
    public void VerifiedObservationRequiresAtLeastOneEvidenceReference()
    {
        DomainException exception = Should.Throw<DomainException>(
            () => Observation.Verified("the pool was exhausted", []));

        exception.Code.ShouldBe(DomainErrorCodes.EvidenceReferenceRequired);
    }

    [Fact]
    public void UncitedModelOutputIsDowngradedRatherThanTrusted()
    {
        Observation observation = Observation.FromModelOutput("the pool was exhausted", []);

        observation.IsVerified.ShouldBeFalse();
        observation.EvidenceIds.ShouldBeEmpty();
    }

    [Fact]
    public void CitedModelOutputIsVerified()
    {
        EvidenceId evidenceId = EvidenceId.New();

        Observation observation = Observation.FromModelOutput("the pool was exhausted", [evidenceId]);

        observation.IsVerified.ShouldBeTrue();
        observation.EvidenceIds.ShouldHaveSingleItem().ShouldBe(evidenceId);
    }

    [Fact]
    public void HypothesisWithNoSupportingEvidenceIsSpeculative()
    {
        Hypothesis hypothesis = new(
            "connection pool exhaustion",
            SupportingEvidenceIds: [],
            ContradictingEvidenceIds: [EvidenceId.New()],
            Confidence.From(0.2m));

        hypothesis.IsSpeculative.ShouldBeTrue();
    }

    [Fact]
    public void CitedEvidenceIsDeduplicatedAcrossObservationsAndHypotheses()
    {
        EvidenceId shared = EvidenceId.New();
        EvidenceId contradicting = EvidenceId.New();

        AgentFinding finding = new(
            "summary",
            Observations: [Observation.Verified("saturated", [shared]), Observation.Unverified("maybe gc")],
            Hypotheses:
            [
                new Hypothesis("pool exhaustion", [shared], [contradicting], Confidence.From(0.7m)),
            ],
            EvidenceGaps: ["no trace backend configured"],
            RecommendedNextChecks: ["inspect slow query log"],
            Confidence.From(0.6m));

        finding.CitedEvidenceIds().ShouldBe([shared, contradicting], ignoreOrder: true);
        finding.UnverifiedObservationCount().ShouldBe(1);
    }
}
