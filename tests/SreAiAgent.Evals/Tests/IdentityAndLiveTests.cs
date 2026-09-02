namespace SreAiAgent.Evals.Harness;

public sealed class IdentityAndLiveTests
{
    [Fact]
    public void Candidate_snapshot_is_stable_and_hashes_every_release_and_run_setting()
    {
        CandidateSnapshot first = EvalTestData.Snapshot();
        CandidateSnapshot second = EvalTestData.Snapshot();

        first.ShouldBe(second);
        first.CandidateHash.Length.ShouldBe(64);
        new[]
        {
            first.AgentRelease.Hash,
            first.PromptRelease.Hash,
            first.ModelRelease.Hash,
            first.ToolRelease.Hash,
            first.PolicyRelease.Hash,
            first.Dataset.Hash,
            first.Scorer.Hash,
        }.ShouldAllBe(static hash => hash.Length == 64);
        first.RunSettings.Repeats.ShouldBe(2);
    }


    [Fact]
    public void Requested_live_candidate_missing_secret_fails_instead_of_skipping()
    {
        CandidateDefinition scripted = EvalTestData.Candidate();
        CandidateDefinition live = scripted with
        {
            ModelDeployment = scripted.ModelDeployment with { Provider = "openai", Model = "gpt-live-pinned" },
            CredentialEnvironmentVariable = "SRE_EVAL_OPENAI_KEY",
        };

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            CandidateFreezer.RequireLivePrerequisites(live, static _ => null));

        error.Message.ShouldContain("missing credential");
    }
}
