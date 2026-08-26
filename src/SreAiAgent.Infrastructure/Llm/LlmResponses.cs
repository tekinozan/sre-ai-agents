using System.Text.Json.Serialization;

namespace SreAiAgent.Infrastructure.Llm;

// The shapes a model is asked to produce.
//
// These are separate from the domain types on purpose. Model output is untrusted
// input: confidence arrives as a bare decimal that may be 1.7, evidence ids
// arrive as strings that may not parse, and a required list may be absent. The
// domain types refuse to represent any of that, which is exactly why they cannot
// be the deserialization target. Binding happens here, and the translation into
// domain types is where the invalid values are rejected or downgraded.
//
// Property names are snake_case to match the JSON Schema the model was given.

/// <summary>Raw observation as returned by a model.</summary>
public sealed record ObservationPayload
{
    [JsonPropertyName("statement")]
    public string Statement { get; init; } = string.Empty;

    [JsonPropertyName("evidence_ids")]
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
}

/// <summary>Raw hypothesis as returned by a model.</summary>
public sealed record HypothesisPayload
{
    [JsonPropertyName("statement")]
    public string Statement { get; init; } = string.Empty;

    [JsonPropertyName("supporting_evidence_ids")]
    public IReadOnlyList<string> SupportingEvidenceIds { get; init; } = [];

    [JsonPropertyName("contradicting_evidence_ids")]
    public IReadOnlyList<string> ContradictingEvidenceIds { get; init; } = [];

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }
}

/// <summary>Raw agent finding, validated against the AgentFinding schema.</summary>
public sealed record AgentFindingPayload
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("observations")]
    public IReadOnlyList<ObservationPayload> Observations { get; init; } = [];

    [JsonPropertyName("hypotheses")]
    public IReadOnlyList<HypothesisPayload> Hypotheses { get; init; } = [];

    [JsonPropertyName("evidence_gaps")]
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];

    [JsonPropertyName("recommended_next_checks")]
    public IReadOnlyList<string> RecommendedNextChecks { get; init; } = [];

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }
}

/// <summary>Raw classification.</summary>
public sealed record ClassificationPayload
{
    [JsonPropertyName("incident_types")]
    public IReadOnlyList<string> IncidentTypes { get; init; } = [];

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }

    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = string.Empty;
}

/// <summary>Raw correlation result.</summary>
public sealed record CorrelationPayload
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }

    [JsonPropertyName("key_observations")]
    public IReadOnlyList<ObservationPayload> KeyObservations { get; init; } = [];

    [JsonPropertyName("ranked_hypotheses")]
    public IReadOnlyList<HypothesisPayload> RankedHypotheses { get; init; } = [];

    [JsonPropertyName("evidence_gaps")]
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];
}

/// <summary>Raw recommendation.</summary>
public sealed record RecommendationPayload
{
    [JsonPropertyName("rank")]
    public int Rank { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = string.Empty;

    [JsonPropertyName("risk")]
    public string Risk { get; init; } = "read_only";

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }

    [JsonPropertyName("evidence_ids")]
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
}

/// <summary>Raw recommendation set.</summary>
public sealed record RecommendationSetPayload
{
    [JsonPropertyName("recommendations")]
    public IReadOnlyList<RecommendationPayload> Recommendations { get; init; } = [];
}

/// <summary>
/// A tool the model wants run. Never executed as written: the tool gateway
/// validates the arguments against the tool's schema, checks the allowlist, the
/// scopes and the risk ceiling, and only then dispatches to an adapter.
/// </summary>
public sealed record ToolRequestPayload
{
    [JsonPropertyName("tool")]
    public string Tool { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public System.Text.Json.JsonElement Arguments { get; init; }
}

/// <summary>
/// One turn of an agent loop: either tool requests, or a final finding.
/// </summary>
/// <remarks>
/// Modelled as one response with two optional halves rather than two separate
/// calls, so a model that returns both is handled explicitly: tool requests win,
/// because a finding produced in the same breath as a request for evidence was
/// written before that evidence existed.
/// </remarks>
public sealed record AgentTurnPayload
{
    [JsonPropertyName("tool_requests")]
    public IReadOnlyList<ToolRequestPayload> ToolRequests { get; init; } = [];

    [JsonPropertyName("finding")]
    public AgentFindingPayload? Finding { get; init; }
}
