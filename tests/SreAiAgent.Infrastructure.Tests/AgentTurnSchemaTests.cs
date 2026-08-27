using System.Text.Json;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Llm;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers the schema an agent turn is validated against.
/// </summary>
/// <remarks>
/// The agent loop was validating each turn against the agent definition's
/// output_schema, which describes the finding rather than the envelope carrying
/// it. Every turn was rejected: the envelope's members are additional properties
/// the finding schema forbids, and the finding's required members sit one level
/// down. Nothing threw and nothing failed to compile; agents simply never
/// produced anything, and the platform reported a partial result with no cause.
///
/// These tests pin the envelope to the type the loop actually sends, so the two
/// cannot drift apart again.
/// </remarks>
public sealed class AgentTurnSchemaTests
{
    private readonly StructuredOutputValidator _validator = new();

    private static JsonElement Serialize(AgentTurn turn)
        => JsonSerializer.SerializeToElement(turn, LlmJsonContext.Default.AgentTurn);

    [Fact]
    public void ATurnRequestingToolsValidates()
    {
        AgentTurn turn = new()
        {
            ToolRequests =
            [
                new AgentToolRequest
                {
                    ToolName = "prometheus.query_range",
                    Arguments = JsonDocument
                        .Parse("""{"metric":"db_connections","service":"checkout","window_minutes":30}""")
                        .RootElement.Clone(),
                },
            ],
        };

        _validator.Validate(AgentTurn.SchemaName, Serialize(turn)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ATurnCarryingAFindingValidates()
    {
        AgentTurn turn = new()
        {
            Finding = new AgentFindingPayload
            {
                Summary = "Connection pool exhaustion on the primary.",
                Observations =
                [
                    new AgentObservationPayload
                    {
                        Statement = "Active connections reached the configured maximum.",
                        EvidenceIds = [EvidenceId.New().Value.ToString("D", CultureInfo.InvariantCulture)],
                    },
                ],
                Hypotheses = [],
                Confidence = 0.72m,
            },
        };

        _validator.Validate(AgentTurn.SchemaName, Serialize(turn)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AnEmptyTurnValidates()
    {
        // An agent that neither asks for a tool nor concludes anything is a
        // wasted iteration, not a protocol violation. The budget stops the loop;
        // the schema does not need to.
        _validator.Validate(AgentTurn.SchemaName, Serialize(new AgentTurn())).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AToolRequestNamingAnUnknownMemberIsRejected()
    {
        // The wire name is "tool", not "tool_name". A schema that disagreed with
        // the type would reject every real turn while its own fixtures passed.
        const string Turn = """
            {"tool_requests":[{"tool_name":"prometheus.query","arguments":{}}]}
            """;

        _validator.Validate(AgentTurn.SchemaName, JsonDocument.Parse(Turn).RootElement)
            .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ABareFindingDoesNotValidateAsATurn()
    {
        // The regression itself: the finding shape is not the turn shape, and
        // the validator has to say so rather than accepting either.
        const string BareFinding = """
            {"summary":"Something","observations":[],"hypotheses":[],"confidence":0.5}
            """;

        _validator.Validate(AgentTurn.SchemaName, JsonDocument.Parse(BareFinding).RootElement)
            .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AFindingWithAnOutOfRangeConfidenceIsRejected()
    {
        const string Turn = """
            {"finding":{"summary":"s","observations":[],"hypotheses":[],"confidence":1.7}}
            """;

        _validator.Validate(AgentTurn.SchemaName, JsonDocument.Parse(Turn).RootElement)
            .IsFailure.ShouldBeTrue();
    }
}
