using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Findings;

namespace SreAiAgent.Evals.Harness;

internal static class EvalTestData
{
    public static LoadedDataset Dataset()
        => DatasetLoader.Load(Path.Combine(RepositoryPaths.EvalProject, "Datasets", "incidents", "v1"));

    public static EvalCase DatabaseCase()
        => Dataset().Cases.Single(static item => item.CaseId == "database-pool-timeout-v1");

    public static EvalConfig Config()
        => CandidateFreezer.LoadConfig(Path.Combine(RepositoryPaths.EvalProject, "eval-config.v1.json"));

    public static CandidateDefinition Candidate()
        => CandidateFreezer.LoadDefinition(Path.Combine(RepositoryPaths.EvalProject, "Candidates", "scripted-2026.08.31.1.json"));

    public static CandidateSnapshot Snapshot()
        => CandidateFreezer.Freeze(Candidate(), Dataset(), Config());

    public static EvalOutput PassingOutput(EvalCase evalCase)
        => new(
            ["database"],
            "database.connection-pool",
            "Latency rose alongside connection pool contention.",
            ["The connection pool is undersized for current load."],
            ["Slow query evidence is missing."],
            ["Compare pool utilisation against the configured maximum."],
            evalCase.RequiredEvidenceIds,
            0.58m,
            AgentRunStatus.Succeeded,
            new AgentRunUsage(3, 2, 1000, 300, 0.01m),
            10,
            null,
            null);

    public static IReadOnlyList<ToolInvocation> PassingTools(EvalCase evalCase)
        => evalCase.Fixtures.Take(2).Select(static fixture => new ToolInvocation(
            fixture.Tool,
            fixture.Version,
            CanonicalJson.Canonicalize(fixture.Arguments),
            true,
            true,
            true,
            fixture.EvidenceId)).ToList();
}
