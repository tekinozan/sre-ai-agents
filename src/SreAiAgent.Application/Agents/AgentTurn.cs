using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Application.Agents;

// The shapes a model is asked to produce.
//
// These live in Application rather than Infrastructure because ILlmGateway is
// generic over them: the port's contract is "give me this type back, validated",
// so the type is part of the contract. An Infrastructure-only copy would mean the
// application layer could not name what it asked for.
//
// They are separate from the domain types on purpose. Model output is untrusted
// input where confidence may be 1.7 and an evidence id may be invented; the
// domain types refuse to represent that, which is exactly why they cannot be the
// deserialization target. AgentFindingBinder does the translation.

/// <summary>An observation exactly as a model returned it.</summary>
public sealed record AgentObservationPayload
{
    [JsonPropertyName("statement")]
    public string Statement { get; init; } = string.Empty;

    [JsonPropertyName("evidence_ids")]
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
}

/// <summary>A hypothesis exactly as a model returned it.</summary>
public sealed record AgentHypothesisPayload
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

/// <summary>A finding exactly as a model returned it, before binding.</summary>
public sealed record AgentFindingPayload
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("observations")]
    public IReadOnlyList<AgentObservationPayload> Observations { get; init; } = [];

    [JsonPropertyName("hypotheses")]
    public IReadOnlyList<AgentHypothesisPayload> Hypotheses { get; init; } = [];

    [JsonPropertyName("evidence_gaps")]
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];

    [JsonPropertyName("recommended_next_checks")]
    public IReadOnlyList<string> RecommendedNextChecks { get; init; } = [];

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }
}

/// <summary>
/// A tool the model wants run.
/// </summary>
/// <remarks>
/// Never executed as written. The tool gateway validates the arguments against
/// the tool's schema, checks the agent's allowlist, the actor's scopes and the
/// risk ceiling, and only then dispatches. That is what makes an injected
/// instruction in a log line harmless: it can change what the model asks for and
/// cannot change what the gateway permits.
/// </remarks>
public sealed record AgentToolRequest
{
    [JsonPropertyName("tool")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("tool_version")]
    public string ToolVersion { get; init; } = "v1";

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

/// <summary>
/// One turn of the agent loop: either tool requests, or a final finding.
/// </summary>
/// <remarks>
/// Modelled as one response with two optional halves rather than two schemas, so
/// a model that returns both is handled explicitly instead of by whichever branch
/// happens to be checked first.
/// </remarks>
public sealed record AgentTurn
{
    [JsonPropertyName("tool_requests")]
    public IReadOnlyList<AgentToolRequest> ToolRequests { get; init; } = [];

    [JsonPropertyName("finding")]
    public AgentFindingPayload? Finding { get; init; }
}

/// <summary>The classification a model produces when deterministic routing cannot.</summary>
public sealed record ClassificationPayload
{
    [JsonPropertyName("incident_types")]
    public IReadOnlyList<string> IncidentTypes { get; init; } = [];

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }

    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = string.Empty;
}

/// <summary>The correlated explanation a model produces from expert findings.</summary>
public sealed record CorrelationPayload
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }

    [JsonPropertyName("key_observations")]
    public IReadOnlyList<AgentObservationPayload> KeyObservations { get; init; } = [];

    [JsonPropertyName("ranked_hypotheses")]
    public IReadOnlyList<AgentHypothesisPayload> RankedHypotheses { get; init; } = [];

    [JsonPropertyName("evidence_gaps")]
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];
}

/// <summary>One proposed recommendation.</summary>
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

/// <summary>The ranked recommendation set.</summary>
public sealed record RecommendationSetPayload
{
    [JsonPropertyName("recommendations")]
    public IReadOnlyList<RecommendationPayload> Recommendations { get; init; } = [];
}
