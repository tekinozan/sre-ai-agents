using System.Text.Json;
using System.Text.Json.Serialization;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Workflow.Temporal;

/// <summary>
/// Compile-time serialization metadata for everything that crosses the Temporal
/// boundary (docs/DECISIONS.md D10).
/// </summary>
/// <remarks>
/// Temporal serializes every activity argument and result into workflow history.
/// Its default converter uses reflection, which the platform bans, so it is
/// pointed at this generated context instead.
///
/// Listing the root types is enough: the generator walks into everything they
/// reference. Anything genuinely missing fails loudly on the first call rather
/// than silently degrading, which is the behaviour worth having for a payload
/// that becomes part of an immutable history.
///
/// Property names are snake_case and enums are strings. History is read by
/// people during an incident, and an integer enum in a payload is a lookup
/// nobody wants to do at three in the morning.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(InvestigationWorkflowInput))]
[JsonSerializable(typeof(ApprovalDecisionSignal))]
[JsonSerializable(typeof(InvestigationStatus))]
[JsonSerializable(typeof(IncidentId))]
[JsonSerializable(typeof(ConfigurationSnapshotId))]
[JsonSerializable(typeof(ClassifyIncidentInput))]
[JsonSerializable(typeof(SelectAgentsInput))]
[JsonSerializable(typeof(RunAgentInput))]
[JsonSerializable(typeof(CorrelateFindingsInput))]
[JsonSerializable(typeof(BuildRecommendationsInput))]
[JsonSerializable(typeof(DecideApprovalInput))]
[JsonSerializable(typeof(PersistReportInput))]
[JsonSerializable(typeof(CompleteInvestigationInput))]
[JsonSerializable(typeof(FailInvestigationInput))]
// The success type of every Result is listed in its own right.
//
// Listing the roots is enough for ordinary records, because the generator walks
// their property graphs. It is not enough here: Result carries a custom
// converter, so the generator stops at the boundary and never sees T. The gap
// does not surface until an activity actually returns that shape, which is what
// happened on the first Temporal run, where ResolveConfiguration failed with
// NoMetadataForType after the workflow had already started.
[JsonSerializable(typeof(IncidentSnapshot))]
[JsonSerializable(typeof(ResolvedConfiguration))]
[JsonSerializable(typeof(IncidentClassification))]
[JsonSerializable(typeof(AgentSelection))]
[JsonSerializable(typeof(CorrelationResult))]
[JsonSerializable(typeof(RecommendationSet))]
[JsonSerializable(typeof(ApprovalDecision))]
[JsonSerializable(typeof(PersistedReport))]
[JsonSerializable(typeof(ConfigurationSnapshot))]
[JsonSerializable(typeof(Result<IncidentSnapshot>))]
[JsonSerializable(typeof(Result<ResolvedConfiguration>))]
[JsonSerializable(typeof(Result<IncidentClassification>))]
[JsonSerializable(typeof(Result<AgentSelection>))]
[JsonSerializable(typeof(Result<CorrelationResult>))]
[JsonSerializable(typeof(Result<RecommendationSet>))]
[JsonSerializable(typeof(Result<ApprovalDecision>))]
[JsonSerializable(typeof(Result<PersistedReport>))]
[JsonSerializable(typeof(AgentRunResult))]
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(Error))]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;

/// <summary>The options Temporal's payload converter is built from.</summary>
internal static class WorkflowPayloadJson
{
    /// <summary>Shared, read-only options backed by the generated context.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(WorkflowJsonContext.Default.Options)
        {
            // History is written by one release and replayed by the next. A
            // member added in the meantime must not stop the older payload from
            // being read, so unknown members are ignored rather than rejected.
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64,
        };

        options.MakeReadOnly();
        return options;
    }
}
