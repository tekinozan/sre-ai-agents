using System.Text.Json;
using System.Text.Json.Serialization;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Findings;

namespace SreAiAgent.Evals.Harness;

internal sealed record EvalDatasetManifest
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("dataset_id")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("cases")]
    public required IReadOnlyList<string> Cases { get; init; }
}

internal sealed record EvalCase
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("case_id")]
    public required string CaseId { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyList<string> Tags { get; init; }

    [JsonPropertyName("incident")]
    public required EvalIncident Incident { get; init; }

    [JsonPropertyName("fixed_utc")]
    public required DateTimeOffset FixedUtc { get; init; }

    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("expected_routing_families")]
    public required IReadOnlyList<string> ExpectedRoutingFamilies { get; init; }

    [JsonPropertyName("expected_root_cause_families")]
    public required IReadOnlyList<string> ExpectedRootCauseFamilies { get; init; }

    [JsonPropertyName("acceptable_hypotheses")]
    public required IReadOnlyList<string> AcceptableHypotheses { get; init; }

    [JsonPropertyName("required_claims")]
    public required IReadOnlyList<string> RequiredClaims { get; init; }

    [JsonPropertyName("forbidden_claims")]
    public required IReadOnlyList<string> ForbiddenClaims { get; init; }

    [JsonPropertyName("required_evidence_ids")]
    public required IReadOnlyList<Guid> RequiredEvidenceIds { get; init; }

    [JsonPropertyName("security")]
    public required EvalSecurityExpectation Security { get; init; }

    [JsonPropertyName("budgets")]
    public required EvalBudget Budgets { get; init; }

    [JsonPropertyName("fixtures")]
    public required IReadOnlyList<ToolFixture> Fixtures { get; init; }

    [JsonPropertyName("rubric")]
    public required EvalRubric Rubric { get; init; }
}

internal sealed record EvalIncident
{
    [JsonPropertyName("tenant")]
    public required string Tenant { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("labels")]
    public required IReadOnlyDictionary<string, string> Labels { get; init; }
}

internal sealed record ToolFixture
{
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("arguments")]
    public required JsonElement Arguments { get; init; }

    [JsonPropertyName("evidence_id")]
    public required Guid EvidenceId { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("redacted_result")]
    public required JsonElement RedactedResult { get; init; }

    [JsonPropertyName("is_truncated")]
    public bool IsTruncated { get; init; }

    [JsonPropertyName("redacted_value_count")]
    public int RedactedValueCount { get; init; }
}

internal sealed record EvalSecurityExpectation
{
    [JsonPropertyName("prompt_injection_present")]
    public bool PromptInjectionPresent { get; init; }

    [JsonPropertyName("must_refuse")]
    public bool MustRefuse { get; init; }

    [JsonPropertyName("secret_canaries")]
    public required IReadOnlyList<string> SecretCanaries { get; init; }
}

internal sealed record EvalBudget
{
    [JsonPropertyName("max_llm_calls")]
    public int MaxLlmCalls { get; init; }

    [JsonPropertyName("max_tool_calls")]
    public int MaxToolCalls { get; init; }

    [JsonPropertyName("max_total_tokens")]
    public long MaxTotalTokens { get; init; }

    [JsonPropertyName("max_cost")]
    public decimal MaxCost { get; init; }

    [JsonPropertyName("max_latency_ms")]
    public double MaxLatencyMilliseconds { get; init; }
}

internal sealed record EvalRubric
{
    [JsonPropertyName("ambiguity_expected")]
    public bool AmbiguityExpected { get; init; }

    [JsonPropertyName("minimum_quality")]
    public decimal MinimumQuality { get; init; }
}

internal sealed record EvalConfig
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("scorer_version")]
    public required string ScorerVersion { get; init; }

    [JsonPropertyName("repeats")]
    public int Repeats { get; init; }

    [JsonPropertyName("concurrency")]
    public int Concurrency { get; init; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; }

    [JsonPropertyName("seed")]
    public int Seed { get; init; }

    [JsonPropertyName("warmup_runs")]
    public int WarmupRuns { get; init; }

    [JsonPropertyName("absolute_thresholds")]
    public required GateThresholds AbsoluteThresholds { get; init; }

    [JsonPropertyName("regression_limits")]
    public required RegressionLimits RegressionLimits { get; init; }

}

internal sealed record GateThresholds
{
    [JsonPropertyName("minimum_quality")]
    public decimal MinimumQuality { get; init; }

    [JsonPropertyName("maximum_p95_latency_ms")]
    public double MaximumP95LatencyMilliseconds { get; init; }

    [JsonPropertyName("maximum_p95_tokens")]
    public double MaximumP95Tokens { get; init; }

    [JsonPropertyName("maximum_p95_cost")]
    public decimal MaximumP95Cost { get; init; }

    [JsonPropertyName("maximum_error_rate")]
    public decimal MaximumErrorRate { get; init; }
}

internal sealed record RegressionLimits
{
    [JsonPropertyName("quality_drop")]
    public decimal QualityDrop { get; init; }

    [JsonPropertyName("latency_ratio")]
    public decimal LatencyRatio { get; init; }

    [JsonPropertyName("token_ratio")]
    public decimal TokenRatio { get; init; }

    [JsonPropertyName("cost_ratio")]
    public decimal CostRatio { get; init; }

    [JsonPropertyName("error_rate_increase")]
    public decimal ErrorRateIncrease { get; init; }
}

internal sealed record CandidateDefinition
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("candidate_id")]
    public required string CandidateId { get; init; }

    [JsonPropertyName("tenant")]
    public required string Tenant { get; init; }

    [JsonPropertyName("provider_connection_revision")]
    public required ProviderConnectionRevisionIdentity ProviderConnectionRevision { get; init; }

    [JsonPropertyName("model_deployment")]
    public required ModelDeploymentIdentity ModelDeployment { get; init; }

    [JsonPropertyName("assignment_release")]
    public required VersionedIdentity AssignmentRelease { get; init; }

    [JsonPropertyName("releases")]
    public required ReleaseIdentity Releases { get; init; }

    [JsonPropertyName("generation")]
    public required GenerationIdentity Generation { get; init; }

    [JsonPropertyName("credential_environment_variable")]
    public string? CredentialEnvironmentVariable { get; init; }
}

internal record VersionedIdentity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("hash")]
    public required string Hash { get; init; }
}
internal sealed record ProviderConnectionRevisionIdentity : VersionedIdentity
{
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }

    [JsonPropertyName("api_version")]
    public string? ApiVersion { get; init; }

    [JsonPropertyName("organization")]
    public string? Organization { get; init; }
}


internal sealed record ModelDeploymentIdentity : VersionedIdentity
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }
}

internal sealed record ReleaseIdentity
{
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("policy")]
    public required string Policy { get; init; }
}

internal sealed record GenerationIdentity
{
    [JsonPropertyName("temperature")]
    public decimal Temperature { get; init; }

    [JsonPropertyName("maximum_output_tokens")]
    public int MaximumOutputTokens { get; init; }

    [JsonPropertyName("seed")]
    public int Seed { get; init; }
}

internal sealed record CandidateSnapshot
{
    public required string CandidateId { get; init; }
    public required string Tenant { get; init; }
    public required ProviderConnectionRevisionIdentity ProviderConnectionRevision { get; init; }
    public required ModelDeploymentIdentity ModelDeployment { get; init; }
    public required VersionedIdentity AssignmentRelease { get; init; }
    public required VersionedIdentity AgentRelease { get; init; }
    public required VersionedIdentity PromptRelease { get; init; }
    public required VersionedIdentity ModelRelease { get; init; }
    public required VersionedIdentity ToolRelease { get; init; }
    public required VersionedIdentity PolicyRelease { get; init; }
    public required GenerationIdentity Generation { get; init; }
    public required VersionedIdentity Dataset { get; init; }
    public required VersionedIdentity Scorer { get; init; }
    public required EvalRunSettings RunSettings { get; init; }
    public required string CandidateHash { get; init; }
}

internal sealed record EvalRunSettings(int Repeats, int Concurrency, int TimeoutSeconds, int Seed, int WarmupRuns);
internal sealed record EvalOutput(
    IReadOnlyList<string> RoutingFamilies,
    string? RootCauseFamily,
    string Summary,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> EvidenceGaps,
    IReadOnlyList<string> NextChecks,
    IReadOnlyList<Guid> CitationIds,
    decimal Confidence,
    AgentRunStatus Status,
    AgentRunUsage Usage,
    double LatencyMilliseconds,
    string? SafeErrorCode,
    string? SafeErrorMessage);

internal sealed record ToolInvocation(string Tool, string Version, string CanonicalArguments, bool Allowed, bool ReadOnly, bool FixtureMatched, Guid? ReturnedEvidenceId);
internal sealed record HardScore(bool SchemaAndCompletion, bool EvidenceAndCitations, bool Claims, bool Security, bool Tools, bool RoutingAndRootCause, bool Budgets, bool MinimumQuality)
{
    public bool Passed => SchemaAndCompletion && EvidenceAndCitations && Claims && Security && Tools && RoutingAndRootCause && Budgets && MinimumQuality;
}

internal sealed record QualityScore(decimal DiagnosisCorrectness, decimal EvidenceGrounding, decimal UncertaintyCalibration, decimal Actionability)
{
    public decimal Overall => (DiagnosisCorrectness + EvidenceGrounding + UncertaintyCalibration + Actionability) / 4m;
}

internal sealed record EvalScore(HardScore Hard, QualityScore Quality, IReadOnlyList<string> Failures);
internal sealed record EvalAttempt(string CaseId, int Repeat, EvalScore Score, EvalOutput Output, IReadOnlyList<ToolInvocation> Tools);

internal sealed record MetricDistribution(double Median, double P95);
internal sealed record DecimalMetricDistribution(decimal Median, decimal P95);
internal sealed record EvalAggregate(
    int AttemptCount,
    int HardFailureCount,
    decimal ErrorRate,
    MetricDistribution LatencyMilliseconds,
    MetricDistribution InputTokens,
    MetricDistribution OutputTokens,
    MetricDistribution TotalTokens,
    DecimalMetricDistribution Cost,
    DecimalMetricDistribution Quality,
    IReadOnlyDictionary<string, DecimalMetricDistribution> QualityDimensions);

internal sealed record EvalBaseline
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }
    [JsonPropertyName("baseline_id")]
    public required string BaselineId { get; init; }
    [JsonPropertyName("candidate_hash")]
    public required string CandidateHash { get; init; }
    [JsonPropertyName("dataset_hash")]
    public required string DatasetHash { get; init; }
    [JsonPropertyName("scorer_hash")]
    public required string ScorerHash { get; init; }
    [JsonPropertyName("repeats")]
    public int Repeats { get; init; }
    [JsonPropertyName("aggregate")]
    public required EvalAggregate Aggregate { get; init; }
}

internal sealed record GateResult(bool PromotionEligible, IReadOnlyList<string> Failures);
internal sealed record RunManifest(string SchemaVersion, string RunId, CandidateSnapshot Candidate, DateTimeOffset FixedUtc, string BaselineId);
internal sealed record SafeAttemptArtifact(string CaseId, int Repeat, HardScore Hard, QualityScore Quality, string Status, string? ErrorCode, AgentRunUsage Usage, double LatencyMilliseconds, IReadOnlyList<string> Tools);
/// <summary>
/// What the control plane is handed to complete an evaluation
/// (incidents-feedback-evals.md E-1).
/// </summary>
/// <remarks>
/// v2 names what was evaluated: the platform refuses a summary whose release,
/// deployment or provider revision is not the release it is asked to promote,
/// and a run against a synthesized bootstrap baseline says so instead of
/// passing for a regression comparison.
/// </remarks>
internal sealed record SummaryArtifact(
    string SchemaVersion,
    string RunId,
    string CandidateHash,
    string DatasetHash,
    SummaryIdentity AssignmentRelease,
    SummaryIdentity ModelDeployment,
    SummaryIdentity ProviderConnectionRevision,
    SummaryIdentity Dataset,
    SummaryBaseline Baseline,
    EvalAggregate Aggregate,
    GateResult Gate);
internal sealed record SummaryIdentity(string Id, string Version, string Hash)
{
    public static SummaryIdentity From(VersionedIdentity identity) => new(identity.Id, identity.Version, identity.Hash);
}
internal sealed record SummaryBaseline(string Id, bool Bootstrap);
internal sealed record ArtifactBundle(RunManifest Manifest, IReadOnlyList<SafeAttemptArtifact> Attempts, SummaryArtifact Summary, string JunitXml);
internal sealed record LoadedDataset(EvalDatasetManifest Manifest, IReadOnlyList<EvalCase> Cases, string Hash, string Directory);
internal sealed record EvalRunResult(CandidateSnapshot Candidate, IReadOnlyList<EvalAttempt> Attempts, EvalAggregate Aggregate, GateResult Gate, string RunId, string ArtifactDirectory, string? WrittenBaselinePath = null);
