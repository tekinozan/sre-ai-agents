namespace SreAiAgent.Evals.Harness;

internal static class EvalAggregator
{
    public static EvalAggregate Aggregate(IReadOnlyList<EvalAttempt> attempts)
    {
        if (attempts.Count == 0)
        {
            throw new ArgumentException("At least one eval attempt is required.", nameof(attempts));
        }

        return new EvalAggregate(
            attempts.Count,
            attempts.Count(static item => !item.Score.Hard.Passed),
            decimal.Divide(attempts.Count(static item => item.Output.SafeErrorCode is not null), attempts.Count),
            Distribution(attempts.Select(static item => item.Output.LatencyMilliseconds)),
            Distribution(attempts.Select(static item => (double)item.Output.Usage.InputTokens)),
            Distribution(attempts.Select(static item => (double)item.Output.Usage.OutputTokens)),
            Distribution(attempts.Select(static item => (double)(item.Output.Usage.InputTokens + item.Output.Usage.OutputTokens))),
            Distribution(attempts.Select(static item => item.Output.Usage.EstimatedCost)),
            Distribution(attempts.Select(static item => item.Score.Quality.Overall)),
            new Dictionary<string, DecimalMetricDistribution>(StringComparer.Ordinal)
            {
                ["diagnosis_correctness"] = Distribution(attempts.Select(static item => item.Score.Quality.DiagnosisCorrectness)),
                ["evidence_grounding"] = Distribution(attempts.Select(static item => item.Score.Quality.EvidenceGrounding)),
                ["uncertainty_calibration"] = Distribution(attempts.Select(static item => item.Score.Quality.UncertaintyCalibration)),
                ["actionability"] = Distribution(attempts.Select(static item => item.Score.Quality.Actionability)),
            });
    }

    internal static MetricDistribution Distribution(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return new MetricDistribution(Median(sorted), Percentile95(sorted));
    }

    internal static DecimalMetricDistribution Distribution(IEnumerable<decimal> values)
    {
        decimal[] sorted = values.Order().ToArray();
        return new DecimalMetricDistribution(Median(sorted), Percentile95(sorted));
    }

    private static double Median(double[] values)
        => values.Length % 2 == 1 ? values[values.Length / 2] : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2d;

    private static decimal Median(decimal[] values)
        => values.Length % 2 == 1 ? values[values.Length / 2] : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2m;

    private static double Percentile95(double[] values) => values[Math.Max(0, checked((int)Math.Ceiling(values.Length * 0.95d)) - 1)];
    private static decimal Percentile95(decimal[] values) => values[Math.Max(0, checked((int)Math.Ceiling(values.Length * 0.95d)) - 1)];
}

internal static class BaselineGate
{
    public static void EnsureCompatible(EvalBaseline baseline, CandidateSnapshot candidate)
    {
        if (baseline.SchemaVersion != "sre-eval-baseline/v1")
        {
            throw new InvalidDataException($"Unsupported baseline schema '{baseline.SchemaVersion}'.");
        }

        if (!string.Equals(baseline.DatasetHash, candidate.Dataset.Hash, StringComparison.Ordinal)
            || !string.Equals(baseline.ScorerHash, candidate.Scorer.Hash, StringComparison.Ordinal)
            || baseline.Repeats != candidate.RunSettings.Repeats)
        {
            throw new InvalidDataException("Baseline dataset/scorer/repeat identity is incompatible with the frozen candidate.");
        }

        if (baseline.CandidateHash.Length != 64)
        {
            throw new InvalidDataException("Baseline candidate hash is not a SHA-256 identity.");
        }
    }

    public static GateResult Evaluate(EvalAggregate aggregate, EvalBaseline baseline, EvalConfig config)
    {
        List<string> failures = [];
        Add(failures, aggregate.HardFailureCount == 0, "hard_gate");
        Add(failures, aggregate.Quality.Median >= config.AbsoluteThresholds.MinimumQuality, "absolute_quality");
        Add(failures, aggregate.LatencyMilliseconds.P95 <= config.AbsoluteThresholds.MaximumP95LatencyMilliseconds, "absolute_latency");
        Add(failures, aggregate.TotalTokens.P95 <= config.AbsoluteThresholds.MaximumP95Tokens, "absolute_tokens");
        Add(failures, aggregate.Cost.P95 <= config.AbsoluteThresholds.MaximumP95Cost, "absolute_cost");
        Add(failures, aggregate.ErrorRate <= config.AbsoluteThresholds.MaximumErrorRate, "absolute_error_rate");

        Add(failures, aggregate.Quality.Median >= baseline.Aggregate.Quality.Median - config.RegressionLimits.QualityDrop, "regression_quality");
        Add(failures, aggregate.LatencyMilliseconds.P95 <= baseline.Aggregate.LatencyMilliseconds.P95 * (double)config.RegressionLimits.LatencyRatio, "regression_latency");
        Add(failures, aggregate.TotalTokens.P95 <= baseline.Aggregate.TotalTokens.P95 * (double)config.RegressionLimits.TokenRatio, "regression_tokens");
        Add(failures, aggregate.Cost.P95 <= baseline.Aggregate.Cost.P95 * config.RegressionLimits.CostRatio, "regression_cost");
        Add(failures, aggregate.ErrorRate <= baseline.Aggregate.ErrorRate + config.RegressionLimits.ErrorRateIncrease, "regression_error_rate");
        return new GateResult(failures.Count == 0, failures);
    }

    private static void Add(List<string> failures, bool passed, string name)
    {
        if (!passed)
        {
            failures.Add(name);
        }
    }
}
