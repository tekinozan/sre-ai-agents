using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace SreAiAgent.Evals.Harness;

internal sealed class EvalRunner
{
    internal const string WriteBaselineEnvironmentVariable = "SRE_EVAL_WRITE_BASELINE";

    private readonly LoadedDataset _dataset;
    private readonly CandidateDefinition _definition;
    private readonly EvalConfig _config;
    private readonly EvalBaseline _baseline;

    public EvalRunner(LoadedDataset dataset, CandidateDefinition definition, EvalConfig config, EvalBaseline baseline)
    {
        _dataset = dataset;
        _definition = definition;
        _config = config;
        _baseline = baseline;
    }

    public static EvalRunner LoadScripted()
    {
        LoadedDataset dataset = DatasetLoader.Load(Path.Combine(RepositoryPaths.EvalProject, "Datasets", "incidents", "v1"));
        CandidateDefinition candidate = CandidateFreezer.LoadDefinition(Path.Combine(RepositoryPaths.EvalProject, "Candidates", "scripted-2026.08.31.1.json"));
        EvalConfig config = CandidateFreezer.LoadConfig(Path.Combine(RepositoryPaths.EvalProject, "eval-config.v1.json"));
        EvalBaseline baseline = LoadOrSynthesizeScriptedBaseline(dataset, candidate, config);
        return new EvalRunner(dataset, candidate, config, baseline);
    }

    public async Task<EvalRunResult> RunScriptedSmokeAsync(string artifactRoot, CancellationToken cancellationToken)
        => await RunAsync(
            _dataset.Cases.Where(static item => item.Tags.Contains("scripted-smoke", StringComparer.Ordinal)).ToList(),
            artifactRoot,
            cancellationToken).ConfigureAwait(false);

    public async Task<EvalRunResult> RunLiveMatrixAsync(string artifactRoot, CancellationToken cancellationToken)
    {
        CandidateFreezer.RequireLivePrerequisites(_definition, Environment.GetEnvironmentVariable);
        return await RunAsync(
            _dataset.Cases.Where(static item => item.Tags.Contains("live-matrix", StringComparer.Ordinal)).ToList(),
            artifactRoot,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EvalRunResult> RunAsync(IReadOnlyList<EvalCase> cases, string artifactRoot, CancellationToken cancellationToken)
    {
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("The selected eval matrix contains no cases.");
        }

        CandidateSnapshot candidate = CandidateFreezer.Freeze(_definition, _dataset, _config);
        bool writeBaseline = string.Equals(Environment.GetEnvironmentVariable(WriteBaselineEnvironmentVariable), "1", StringComparison.Ordinal);

        // Baseline compatibility gates promotion. When regenerating the baseline
        // the outgoing baseline is by definition the wrong identity, so skip the
        // compatibility check for that one authorised path.
        if (!writeBaseline)
        {
            BaselineGate.EnsureCompatible(_baseline, candidate);
        }

        int concurrency = Math.Max(1, _config.Concurrency);
        TimeSpan perCaseTimeout = _config.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_config.TimeoutSeconds)
            : Timeout.InfiniteTimeSpan;
        int warmup = Math.Max(0, _config.WarmupRuns);

        // Ordered upfront so parallel execution does not change what gets run;
        // ordering of the aggregated attempts is enforced when we materialise them.
        IReadOnlyList<EvalCase> orderedCases = cases.OrderBy(static item => item.CaseId, StringComparer.Ordinal).ToList();

        ConcurrentDictionary<string, List<EvalAttempt>> perCase = new(StringComparer.Ordinal);
        ParallelOptions parallelOptions = new()
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = concurrency,
        };

        await Parallel.ForEachAsync(orderedCases, parallelOptions, async (evalCase, caseToken) =>
        {
            List<EvalAttempt> local = new(_config.Repeats);

            // Warmup attempts touch the same path but are excluded from the aggregate.
            // Runs remain sequential inside one case so ordering and repeat
            // numbering stay stable per (case_id, repeat).
            for (int warmupIndex = 0; warmupIndex < warmup; warmupIndex++)
            {
                await RunSingleAttemptAsync(candidate, evalCase, perCaseTimeout, caseToken).ConfigureAwait(false);
            }

            for (int repeat = 1; repeat <= _config.Repeats; repeat++)
            {
                (EvalOutput output, IReadOnlyList<ToolInvocation> tools) = await RunSingleAttemptAsync(candidate, evalCase, perCaseTimeout, caseToken).ConfigureAwait(false);
                EvalScore score = EvalScorer.Score(evalCase, output, tools);
                local.Add(new EvalAttempt(evalCase.CaseId, repeat, score, output, tools));
            }

            perCase[evalCase.CaseId] = local;
        }).ConfigureAwait(false);

        List<EvalAttempt> attempts = orderedCases
            .SelectMany(item => perCase[item.CaseId])
            .ToList();

        EvalAggregate aggregate = EvalAggregator.Aggregate(attempts);
        EvalBaseline effectiveBaseline = writeBaseline
            ? SynthesizeBaseline(candidate, aggregate, baselineIdSuffix: candidate.Dataset.Hash[..8])
            : _baseline;
        GateResult gate = BaselineGate.Evaluate(aggregate, effectiveBaseline, _config);

        // The immutable artifact directory is `runId`; two runs of the same
        // candidate on the same case set must not collide, so the run's start
        // timestamp participates in the id.
        string startTimestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        string idMaterial = candidate.CandidateHash + "|" + string.Join('|', orderedCases.Select(static item => item.CaseId)) + "|" + startTimestamp;
        string runId = "eval-" + startTimestamp + "-" + CanonicalJson.HashUtf8(idMaterial)[..16];

        RunManifest manifest = new("sre-eval-run/v1", runId, candidate, orderedCases.Min(static item => item.FixedUtc), effectiveBaseline.BaselineId);
        List<SafeAttemptArtifact> safeAttempts = attempts.Select(static attempt => new SafeAttemptArtifact(
            attempt.CaseId,
            attempt.Repeat,
            attempt.Score.Hard,
            attempt.Score.Quality,
            attempt.Output.Status.ToString(),
            attempt.Output.SafeErrorCode,
            attempt.Output.Usage,
            attempt.Output.LatencyMilliseconds,
            attempt.Tools.Select(static tool => tool.Tool).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())).ToList();
        SummaryArtifact summary = new("sre-eval-summary/v1", runId, candidate.CandidateHash, candidate.Dataset.Hash, aggregate, gate);
        ArtifactBundle bundle = new(manifest, safeAttempts, summary, ArtifactWriter.BuildJunit(runId, attempts, gate));
        IReadOnlyList<string> canaries = orderedCases.SelectMany(static item => item.Security.SecretCanaries).Distinct(StringComparer.Ordinal).ToList();
        string directory = ArtifactWriter.Write(artifactRoot, bundle, canaries);

        string? writtenBaselinePath = null;
        if (writeBaseline && gate.PromotionEligible)
        {
            writtenBaselinePath = WriteBaselineFile(effectiveBaseline, candidate);
        }

        return new EvalRunResult(candidate, attempts, aggregate, gate, runId, directory, writtenBaselinePath);
    }

    private async Task<(EvalOutput Output, IReadOnlyList<ToolInvocation> Tools)> RunSingleAttemptAsync(
        CandidateSnapshot candidate,
        EvalCase evalCase,
        TimeSpan perCaseTimeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource caseCts = perCaseTimeout == Timeout.InfiniteTimeSpan
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource(perCaseTimeout);
        using CancellationTokenSource linked = perCaseTimeout == Timeout.InfiniteTimeSpan
            ? caseCts
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, caseCts.Token);

        await using ProductionEvalRuntime runtime = ProductionEvalRuntime.Create(candidate, evalCase.FixedUtc, _definition.CredentialEnvironmentVariable);
        return await runtime.RunAsync(evalCase, linked.Token).ConfigureAwait(false);
    }

    internal static EvalBaseline SynthesizeBaseline(CandidateSnapshot candidate, EvalAggregate aggregate, string baselineIdSuffix)
    {
        string safeCandidateSlug = SanitizeSlug(candidate.CandidateId);
        string baselineId = string.Equals(candidate.ModelDeployment.Provider, "scripted", StringComparison.Ordinal)
            ? "scripted-smoke-" + DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + "-" + baselineIdSuffix
            : "baseline-" + safeCandidateSlug + "-" + candidate.CandidateHash[..8];

        return new EvalBaseline
        {
            SchemaVersion = "sre-eval-baseline/v1",
            BaselineId = baselineId,
            CandidateHash = candidate.CandidateHash,
            DatasetHash = candidate.Dataset.Hash,
            ScorerHash = candidate.Scorer.Hash,
            Repeats = candidate.RunSettings.Repeats,
            Aggregate = aggregate,
        };
    }

    internal static string WriteBaselineFile(EvalBaseline baseline, CandidateSnapshot candidate, string? directory = null)
    {
        string baselinesDir = Path.GetFullPath(directory ?? Path.Combine(RepositoryPaths.EvalProject, "Baselines"));
        Directory.CreateDirectory(baselinesDir);
        string fileName = ComputeBaselineFileName(candidate, baseline.BaselineId);
        string fullPath = Path.GetFullPath(Path.Combine(baselinesDir, fileName));

        string baselinesDirWithSep = baselinesDir.EndsWith(Path.DirectorySeparatorChar)
            ? baselinesDir
            : baselinesDir + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(baselinesDirWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Resolved baseline path '{fullPath}' traverses outside the baselines directory '{baselinesDir}'.");
        }

        if (File.Exists(fullPath))
        {
            throw new InvalidOperationException($"The baseline file '{fullPath}' already exists. Baselines are immutable and cannot be overwritten.");
        }

        string json = JsonSerializer.Serialize(baseline, EvalJsonContext.Default.EvalBaseline);
        using FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream, System.Text.Encoding.UTF8);
        writer.Write(json);
        return fullPath;
    }

    internal static string ComputeBaselineFileName(CandidateSnapshot candidate, string baselineId)
    {
        if (string.Equals(candidate.ModelDeployment.Provider, "scripted", StringComparison.Ordinal))
        {
            string date = DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
            return $"scripted-smoke-{date}.json";
        }

        string safeId = SanitizeSlug(baselineId);
        return $"{safeId}.json";
    }

    internal static string SanitizeSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "candidate";
        }

        System.Text.StringBuilder builder = new(input.Length);
        foreach (char c in input)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('-');
            }
        }

        string result = builder.ToString().Trim('-', '.');
        while (result.Contains("..", StringComparison.Ordinal))
        {
            result = result.Replace("..", ".", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(result) ? "candidate" : result;
    }

    private static EvalBaseline LoadOrSynthesizeScriptedBaseline(LoadedDataset dataset, CandidateDefinition candidate, EvalConfig config)
    {
        string baselinesDir = Path.Combine(RepositoryPaths.EvalProject, "Baselines");
        if (Directory.Exists(baselinesDir))
        {
            string? latest = Directory.EnumerateFiles(baselinesDir, "scripted-smoke-*.json")
                .OrderByDescending(static path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latest is not null)
            {
                return JsonSerializer.Deserialize(File.ReadAllText(latest), EvalJsonContext.Default.EvalBaseline)
                    ?? throw new InvalidDataException($"Scripted eval baseline '{latest}' deserialized to null.");
            }
        }

        return SynthesizePlaceholderBaseline(candidate, dataset, config);
    }

    public static EvalBaseline SynthesizePlaceholderBaseline(CandidateDefinition candidate, LoadedDataset dataset, EvalConfig config)
    {
        CandidateSnapshot snapshot = CandidateFreezer.Freeze(candidate, dataset, config);
        return new EvalBaseline
        {
            SchemaVersion = "sre-eval-baseline/v1",
            BaselineId = string.Equals(candidate.ModelDeployment.Provider, "scripted", StringComparison.Ordinal)
                ? "scripted-smoke-uninitialised"
                : $"baseline-{candidate.CandidateId}-uninitialised",
            CandidateHash = snapshot.CandidateHash,
            DatasetHash = snapshot.Dataset.Hash,
            ScorerHash = snapshot.Scorer.Hash,
            Repeats = snapshot.RunSettings.Repeats,
            Aggregate = new EvalAggregate(
                0,
                0,
                0m,
                new MetricDistribution(0, 0),
                new MetricDistribution(0, 0),
                new MetricDistribution(0, 0),
                new MetricDistribution(0, 0),
                new DecimalMetricDistribution(0m, 0m),
                new DecimalMetricDistribution(0m, 0m),
                new Dictionary<string, DecimalMetricDistribution>(StringComparer.Ordinal)
                {
                    ["diagnosis_correctness"] = new(0m, 0m),
                    ["evidence_grounding"] = new(0m, 0m),
                    ["uncertainty_calibration"] = new(0m, 0m),
                    ["actionability"] = new(0m, 0m),
                }),
        };
    }
}
