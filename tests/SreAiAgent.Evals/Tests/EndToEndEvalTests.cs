using System.Text.Json;

namespace SreAiAgent.Evals.Harness;

public sealed class EndToEndEvalTests
{
    [Fact]
    [Trait("Category", "ScriptedEval")]
    public async Task Full_scripted_case_uses_production_pipeline_and_writes_safe_gate_artifacts()
    {
        string artifactRoot = Path.Combine(RepositoryPaths.Root, "artifacts", "evals");
        EvalRunResult result = await EvalRunner.LoadScripted().RunScriptedSmokeAsync(artifactRoot, TestContext.Current.CancellationToken);

        result.Attempts.Count.ShouldBe(16);
        result.Attempts.ShouldAllBe(static attempt => attempt.Score.Hard.Passed);
        result.Gate.PromotionEligible.ShouldBeTrue();
        File.Exists(Path.Combine(result.ArtifactDirectory, "manifest.json")).ShouldBeTrue();
        File.Exists(Path.Combine(result.ArtifactDirectory, "attempts.jsonl")).ShouldBeTrue();
        File.Exists(Path.Combine(result.ArtifactDirectory, "summary.json")).ShouldBeTrue();
        File.Exists(Path.Combine(result.ArtifactDirectory, "results.junit.xml")).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "LiveEval")]
    public async Task Explicit_live_matrix_requires_exact_candidate_and_credentials()
    {
        string candidatePath = Environment.GetEnvironmentVariable("SRE_EVAL_CANDIDATE")
            ?? throw new InvalidOperationException("Requested live eval is missing SRE_EVAL_CANDIDATE; live runs fail rather than skip.");
        string baselinePath = Environment.GetEnvironmentVariable("SRE_EVAL_BASELINE")
            ?? throw new InvalidOperationException("Requested live eval is missing SRE_EVAL_BASELINE; live runs fail rather than skip.");
        LoadedDataset dataset = EvalTestData.Dataset();
        CandidateDefinition candidate = CandidateFreezer.LoadDefinition(candidatePath);
        CandidateFreezer.RequireLivePrerequisites(candidate, Environment.GetEnvironmentVariable);
        EvalConfig config = EvalTestData.Config();
        EvalBaseline baseline = JsonSerializer.Deserialize(File.ReadAllText(baselinePath), EvalJsonContext.Default.EvalBaseline)
            ?? throw new InvalidDataException("Live baseline deserialized to null.");
        EvalRunner runner = new(dataset, candidate, config, baseline);

        EvalRunResult result = await runner.RunLiveMatrixAsync(
            Path.Combine(RepositoryPaths.Root, "artifacts", "evals"),
            TestContext.Current.CancellationToken);

        result.Gate.PromotionEligible.ShouldBeTrue();
    }
}
