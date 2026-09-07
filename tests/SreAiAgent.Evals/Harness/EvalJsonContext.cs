using System.Text.Json.Serialization;

namespace SreAiAgent.Evals.Harness;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EvalDatasetManifest))]
[JsonSerializable(typeof(EvalCase))]
[JsonSerializable(typeof(EvalConfig))]
[JsonSerializable(typeof(CandidateDefinition))]
[JsonSerializable(typeof(CandidateSnapshot))]
[JsonSerializable(typeof(ProviderConnectionRevisionIdentity))]
[JsonSerializable(typeof(ModelDeploymentIdentity))]
[JsonSerializable(typeof(EvalBaseline))]
[JsonSerializable(typeof(RunManifest))]
[JsonSerializable(typeof(SafeAttemptArtifact))]
[JsonSerializable(typeof(SummaryArtifact))]
[JsonSerializable(typeof(EvalAggregate))]
[JsonSerializable(typeof(ArtifactBundle))]
internal sealed partial class EvalJsonContext : JsonSerializerContext;
