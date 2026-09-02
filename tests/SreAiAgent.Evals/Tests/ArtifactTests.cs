namespace SreAiAgent.Evals.Harness;

public sealed class ArtifactTests
{
    [Theory]
    [InlineData("{\"raw_prompt\":\"incident evidence\"}")]
    [InlineData("{\"raw_tool_payload\":{}}")]
    [InlineData("{\"raw_provider_response\":\"ok\"}")]
    [InlineData("{\"authorization\":\"Bearer abcdefghijklmnopqrstuvwxyz\"}")]
    [InlineData("{\"message\":\"sk-abcdefghijklmnop\"}")]
    public void Artifact_writer_rejects_raw_or_credential_material(string content)
    {
        Should.Throw<InvalidDataException>(() => ArtifactWriter.EnsureSafe(content, []));
    }

    [Fact]
    public void Artifact_writer_rejects_dataset_secret_canary()
    {
        Should.Throw<InvalidDataException>(() => ArtifactWriter.EnsureSafe("{\"error\":\"EVAL_SECRET_CANARY\"}", ["EVAL_SECRET_CANARY"]));
    }

    [Fact]
    public void Safe_aggregate_artifact_contains_no_raw_surface()
    {
        CandidateSnapshot candidate = EvalTestData.Snapshot();
        string serialized = System.Text.Json.JsonSerializer.Serialize(candidate, EvalJsonContext.Default.CandidateSnapshot);

        Should.NotThrow(() => ArtifactWriter.EnsureSafe(serialized, []));
        serialized.ShouldNotContain("raw_prompt", Case.Insensitive);
        serialized.ShouldNotContain("credential_environment_variable", Case.Insensitive);
    }
}
