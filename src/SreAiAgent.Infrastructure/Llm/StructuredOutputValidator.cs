using System.Collections.Frozen;
using System.Text.Json;
using Json.Schema;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>
/// Validates model output against a JSON Schema before anything downstream sees
/// it (ARCHITECTURE.md 6.1 and 18).
/// </summary>
/// <remarks>
/// Validation happens even when the provider claims to enforce structured
/// output. Provider-side enforcement varies by model and by version, degrades
/// under truncation, and is not something the platform can prove after the fact.
/// A schema check here costs microseconds and is the difference between rejecting
/// a malformed finding and persisting an evidence-free claim as if it were a
/// verified observation.
/// </remarks>
internal sealed class StructuredOutputValidator
{
    private readonly FrozenDictionary<string, JsonSchema> _schemas;

    public StructuredOutputValidator()
    {
        _schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal)
        {
            [AgentFindingSchemaName] = JsonSchema.FromText(AgentFindingSchema),
            [ClassificationSchemaName] = JsonSchema.FromText(ClassificationSchema),
            [CorrelationSchemaName] = JsonSchema.FromText(CorrelationSchema),
            [RecommendationSchemaName] = JsonSchema.FromText(RecommendationSchema),
        }.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public const string AgentFindingSchemaName = "AgentFinding";
    public const string ClassificationSchemaName = "IncidentClassification";
    public const string CorrelationSchemaName = "CorrelationResult";
    public const string RecommendationSchemaName = "RecommendationSet";

    /// <summary>Every schema name the platform can validate, for startup checks.</summary>
    public IReadOnlyCollection<string> KnownSchemas => _schemas.Keys;

    /// <summary>Validates a document, returning the first failure path if invalid.</summary>
    public Result Validate(string schemaName, JsonElement document)
    {
        if (!_schemas.TryGetValue(schemaName, out JsonSchema? schema))
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                $"No JSON Schema named '{schemaName}' is registered."));
        }

        EvaluationResults results = schema.Evaluate(
            document,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        if (results.IsValid)
        {
            return Result.Success();
        }

        string detail = DescribeFirstFailure(results);

        // Retryable: a model that ignored its schema once may not the next time,
        // because sampling is not deterministic. It is the only model failure the
        // catalogue treats as worth a second attempt.
        return Result.Failure(Error.Transient(
            ApplicationErrorCodes.LlmStructuredOutputInvalid,
            $"Model output failed schema '{schemaName}': {detail}"));
    }

    private static string DescribeFirstFailure(EvaluationResults results)
    {
        if (results.Details is null)
        {
            return "document did not satisfy the schema";
        }

        foreach (EvaluationResults detail in results.Details)
        {
            if (detail.IsValid || detail.Errors is null or { Count: 0 })
            {
                continue;
            }

            foreach (KeyValuePair<string, string> error in detail.Errors)
            {
                return $"{detail.InstanceLocation} {error.Value}";
            }
        }

        return "document did not satisfy the schema";
    }

    // Schemas are embedded rather than loaded from disk. They are the contract
    // between this code and the deserializer immediately below it: a schema that
    // could drift from the type it validates would let a model return a shape
    // that passes validation and then fails to bind.

    private const string AgentFindingSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "observations", "hypotheses", "confidence"],
          "properties": {
            "summary": { "type": "string", "minLength": 1, "maxLength": 4000 },
            "observations": {
              "type": "array", "maxItems": 50,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["statement", "evidence_ids"],
                "properties": {
                  "statement": { "type": "string", "minLength": 1, "maxLength": 2000 },
                  "evidence_ids": { "type": "array", "maxItems": 50, "items": { "type": "string", "format": "uuid" } }
                }
              }
            },
            "hypotheses": {
              "type": "array", "maxItems": 20,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["statement", "supporting_evidence_ids", "contradicting_evidence_ids", "confidence"],
                "properties": {
                  "statement": { "type": "string", "minLength": 1, "maxLength": 2000 },
                  "supporting_evidence_ids": { "type": "array", "maxItems": 50, "items": { "type": "string", "format": "uuid" } },
                  "contradicting_evidence_ids": { "type": "array", "maxItems": 50, "items": { "type": "string", "format": "uuid" } },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
                }
              }
            },
            "evidence_gaps": { "type": "array", "maxItems": 20, "items": { "type": "string", "maxLength": 1000 } },
            "recommended_next_checks": { "type": "array", "maxItems": 20, "items": { "type": "string", "maxLength": 1000 } },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
          }
        }
        """;

    private const string ClassificationSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["incident_types", "confidence", "rationale"],
          "properties": {
            "incident_types": { "type": "array", "minItems": 1, "maxItems": 8, "items": { "type": "string", "maxLength": 64 } },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "rationale": { "type": "string", "minLength": 1, "maxLength": 1000 }
          }
        }
        """;

    private const string CorrelationSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "confidence", "key_observations", "ranked_hypotheses"],
          "properties": {
            "summary": { "type": "string", "minLength": 1, "maxLength": 8000 },
            "category": { "type": ["string", "null"], "maxLength": 128 },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "key_observations": {
              "type": "array", "maxItems": 50,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["statement", "evidence_ids"],
                "properties": {
                  "statement": { "type": "string", "minLength": 1, "maxLength": 2000 },
                  "evidence_ids": { "type": "array", "maxItems": 50, "items": { "type": "string", "format": "uuid" } }
                }
              }
            },
            "ranked_hypotheses": {
              "type": "array", "maxItems": 20,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["statement", "supporting_evidence_ids", "contradicting_evidence_ids", "confidence"],
                "properties": {
                  "statement": { "type": "string", "minLength": 1, "maxLength": 2000 },
                  "supporting_evidence_ids": { "type": "array", "maxItems": 50, "items": { "type": "string", "format": "uuid" } },
                  "contradicting_evidence_ids": { "type": "array", "maxItems": 50, "items": { "type": "string", "format": "uuid" } },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
                }
              }
            },
            "evidence_gaps": { "type": "array", "maxItems": 20, "items": { "type": "string", "maxLength": 1000 } }
          }
        }
        """;

    private const string RecommendationSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["recommendations"],
          "properties": {
            "recommendations": {
              "type": "array", "maxItems": 20,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["rank", "title", "rationale", "risk", "confidence"],
                "properties": {
                  "rank": { "type": "integer", "minimum": 1, "maximum": 20 },
                  "title": { "type": "string", "minLength": 1, "maxLength": 512 },
                  "rationale": { "type": "string", "minLength": 1, "maxLength": 4000 },
                  "risk": { "type": "string", "enum": ["read_only", "low", "medium", "high", "destructive"] },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
                  "evidence_ids": { "type": "array", "maxItems": 50, "items": { "type": "string", "format": "uuid" } }
                }
              }
            }
          }
        }
        """;
}
