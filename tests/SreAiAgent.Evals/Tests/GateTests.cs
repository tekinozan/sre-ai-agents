using System.Text.Json;

namespace SreAiAgent.Evals.Harness;

public sealed class GateTests
{
    [Fact]
    public void Baseline_requires_dataset_scorer_and_repeat_compatibility()
    {
        CandidateSnapshot candidate = EvalTestData.Snapshot();
        EvalBaseline compatible = Baseline(candidate, Aggregate());
        BaselineGate.EnsureCompatible(compatible, candidate);

        EvalBaseline wrongDataset = compatible with { DatasetHash = new string('f', 64) };
        Should.Throw<InvalidDataException>(() => BaselineGate.EnsureCompatible(wrongDataset, candidate));
    }

    [Fact]
    public void Absolute_and_regression_threshold_edges_are_inclusive()
    {
        EvalAggregate aggregate = Aggregate();
        EvalConfig config = EvalTestData.Config() with
        {
            AbsoluteThresholds = new GateThresholds
            {
                MinimumQuality = aggregate.Quality.Median,
                MaximumP95LatencyMilliseconds = aggregate.LatencyMilliseconds.P95,
                MaximumP95Tokens = aggregate.TotalTokens.P95,
                MaximumP95Cost = aggregate.Cost.P95,
                MaximumErrorRate = aggregate.ErrorRate,
            },
            RegressionLimits = new RegressionLimits
            {
                QualityDrop = 0,
                LatencyRatio = 1,
                TokenRatio = 1,
                CostRatio = 1,
                ErrorRateIncrease = 0,
            },
        };

        BaselineGate.Evaluate(aggregate, Baseline(EvalTestData.Snapshot(), aggregate), config).PromotionEligible.ShouldBeTrue();
    }

    [Fact]
    public void Value_beyond_absolute_edge_is_not_promotion_eligible()
    {
        EvalAggregate aggregate = Aggregate() with { LatencyMilliseconds = new MetricDistribution(10, 10.01) };
        EvalConfig config = EvalTestData.Config() with
        {
            AbsoluteThresholds = EvalTestData.Config().AbsoluteThresholds with { MaximumP95LatencyMilliseconds = 10 },
        };

        GateResult result = BaselineGate.Evaluate(aggregate, Baseline(EvalTestData.Snapshot(), Aggregate()), config);
        result.PromotionEligible.ShouldBeFalse();
        result.Failures.ShouldContain("absolute_latency");
    }

    private static EvalBaseline Baseline(CandidateSnapshot candidate, EvalAggregate aggregate)
        => new()
        {
            SchemaVersion = "sre-eval-baseline/v1",
            BaselineId = "unit-baseline-v1",
            CandidateHash = new string('a', 64),
            DatasetHash = candidate.Dataset.Hash,
            ScorerHash = candidate.Scorer.Hash,
            Repeats = candidate.RunSettings.Repeats,
            Aggregate = aggregate,
        };

    private static EvalAggregate Aggregate()
        => new(
            2,
            0,
            0,
            new MetricDistribution(10, 10),
            new MetricDistribution(1000, 1000),
            new MetricDistribution(300, 300),
            new MetricDistribution(1300, 1300),
            new DecimalMetricDistribution(0.01m, 0.01m),
            new DecimalMetricDistribution(1m, 1m),
            new Dictionary<string, DecimalMetricDistribution>(StringComparer.Ordinal)
            {
                ["diagnosis_correctness"] = new(1m, 1m),
                ["evidence_grounding"] = new(1m, 1m),
                ["uncertainty_calibration"] = new(1m, 1m),
                ["actionability"] = new(1m, 1m),
            });
}
