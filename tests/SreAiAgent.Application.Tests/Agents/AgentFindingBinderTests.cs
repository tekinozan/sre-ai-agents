using System.Collections.Frozen;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Tests.Agents;

/// <summary>
/// Covers the boundary where untrusted model output meets the domain invariants
/// of ARCHITECTURE.md 5.2 and 7.3.
/// </summary>
public sealed class AgentFindingBinderTests
{
    private readonly EvidenceId _real = EvidenceId.New();
    private readonly EvidenceId _alsoReal = EvidenceId.New();

    private FrozenSet<EvidenceId> Collected => new[] { _real, _alsoReal }.ToFrozenSet();

    [Fact]
    public void ACitationThatMatchesRealEvidenceProducesAVerifiedObservation()
    {
        AgentFinding finding = AgentFindingBinder.Bind(
            "summary",
            [new RawObservation("the pool was exhausted", [_real.ToString()])],
            [],
            [],
            [],
            0.6m,
            Collected);

        Observation observation = finding.Observations.ShouldHaveSingleItem();
        observation.IsVerified.ShouldBeTrue();
        observation.EvidenceIds.ShouldHaveSingleItem().ShouldBe(_real);
    }

    [Fact]
    public void AFabricatedEvidenceIdIsRejectedAndTheClaimDowngraded()
    {
        // A model can emit a well-formed UUID that no tool ever returned, and a
        // JSON Schema cannot tell the difference. This is the check that catches
        // it, and it is the difference between a report that cites evidence and a
        // report that appears to.
        string fabricated = Guid.CreateVersion7().ToString();

        AgentFinding finding = AgentFindingBinder.Bind(
            "summary",
            [new RawObservation("the pool was exhausted", [fabricated])],
            [],
            [],
            [],
            0.9m,
            Collected);

        Observation observation = finding.Observations.ShouldHaveSingleItem();
        observation.IsVerified.ShouldBeFalse();
        observation.EvidenceIds.ShouldBeEmpty();
        finding.UnverifiedObservationCount().ShouldBe(1);
    }

    [Fact]
    public void TheClaimSurvivesEvenWhenItsCitationsDoNot()
    {
        // Dropped rather than downgraded, the operator would never see that the
        // agent believed something it could not support, and the eval suite could
        // not score it as an unsupported claim.
        AgentFinding finding = AgentFindingBinder.Bind(
            "summary",
            [new RawObservation("the database is on fire", ["not-a-uuid"])],
            [],
            [],
            [],
            0.5m,
            Collected);

        finding.Observations.ShouldHaveSingleItem().Statement.ShouldBe("the database is on fire");
    }

    [Fact]
    public void MixedCitationsKeepOnlyTheRealOnes()
    {
        AgentFinding finding = AgentFindingBinder.Bind(
            "summary",
            [new RawObservation("saturated", [_real.ToString(), Guid.CreateVersion7().ToString()])],
            [],
            [],
            [],
            0.6m,
            Collected);

        Observation observation = finding.Observations.ShouldHaveSingleItem();
        observation.IsVerified.ShouldBeTrue();
        observation.EvidenceIds.ShouldHaveSingleItem().ShouldBe(_real);
    }

    [Theory]
    [InlineData(1.7)]
    [InlineData(-0.4)]
    [InlineData(42)]
    public void OutOfRangeConfidenceIsClampedRatherThanFailingTheFinding(double raw)
    {
        // Failing the whole finding over one bad number would discard the
        // evidence-backed observations alongside it.
        AgentFinding finding = AgentFindingBinder.Bind(
            "summary",
            [new RawObservation("saturated", [_real.ToString()])],
            [],
            [],
            [],
            (decimal)raw,
            Collected);

        finding.Confidence.Value.ShouldBeInRange(0m, 1m);
        finding.Observations.ShouldHaveSingleItem().IsVerified.ShouldBeTrue();
    }

    [Fact]
    public void ContradictingEvidenceIsBoundToo()
    {
        AgentFinding finding = AgentFindingBinder.Bind(
            "summary",
            [],
            [new RawHypothesis("pool exhaustion", [_real.ToString()], [_alsoReal.ToString()], 0.7m)],
            [],
            [],
            0.7m,
            Collected);

        Hypothesis hypothesis = finding.Hypotheses.ShouldHaveSingleItem();
        hypothesis.SupportingEvidenceIds.ShouldHaveSingleItem().ShouldBe(_real);
        hypothesis.ContradictingEvidenceIds.ShouldHaveSingleItem().ShouldBe(_alsoReal);
        hypothesis.IsSpeculative.ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyStatementIsDroppedEntirely()
    {
        AgentFinding finding = AgentFindingBinder.Bind(
            "summary",
            [new RawObservation("   ", [_real.ToString()]), new RawObservation("real claim", [_real.ToString()])],
            [],
            [],
            [],
            0.6m,
            Collected);

        finding.Observations.ShouldHaveSingleItem().Statement.ShouldBe("real claim");
    }

    [Fact]
    public void AMissingSummaryIsReplacedRatherThanLeftEmpty()
    {
        AgentFinding finding = AgentFindingBinder.Bind("", [], [], [], [], 0m, Collected);

        finding.Summary.ShouldNotBeNullOrWhiteSpace();
    }
}
