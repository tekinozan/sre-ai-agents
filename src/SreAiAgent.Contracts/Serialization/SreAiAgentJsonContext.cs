using System.Text.Json.Serialization;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Contracts.Approvals;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Streaming;

namespace SreAiAgent.Contracts.Serialization;

/// <summary>
/// Compile-time serialization metadata for every wire contract
/// (docs/DECISIONS.md D10).
/// </summary>
/// <remarks>
/// Reflection-based serialization is banned platform-wide, and the banned-API
/// list enforces it. The reasons are concrete: the reflection path allocates and
/// walks type metadata on every call, on a hot path that runs for each alert,
/// each outbox dispatch and each Kafka message; and it is the mechanism through
/// which an unexpected polymorphic payload can be materialized. Generated
/// metadata removes both.
///
/// Wire property names are snake_case, matching the JSON in ARCHITECTURE.md 8.2
/// and 24.5 and the conventions of the alert sources being consumed.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(CanonicalAlert))]
[JsonSerializable(typeof(CreateIncidentRequest))]
[JsonSerializable(typeof(IncidentAcceptedResponse))]
[JsonSerializable(typeof(IncidentResponse))]
[JsonSerializable(typeof(IncidentActionRequest))]
[JsonSerializable(typeof(IReadOnlyList<IncidentTimelineEntryResponse>))]
[JsonSerializable(typeof(IReadOnlyList<EvidenceResponse>))]
[JsonSerializable(typeof(RootCauseReportResponse))]
[JsonSerializable(typeof(ApprovalResponse))]
[JsonSerializable(typeof(IReadOnlyList<ApprovalResponse>))]
[JsonSerializable(typeof(ApproveRequest))]
[JsonSerializable(typeof(RejectRequest))]
[JsonSerializable(typeof(EditApprovalRequest))]
[JsonSerializable(typeof(InvestigationStreamEvent))]
[JsonSerializable(typeof(EventEnvelope<CanonicalAlert>))]
[JsonSerializable(typeof(EventEnvelope<IncidentCreatedPayload>))]
[JsonSerializable(typeof(EventEnvelope<InvestigationRequestedPayload>))]
[JsonSerializable(typeof(EventEnvelope<IncidentUpdatedPayload>))]
[JsonSerializable(typeof(EventEnvelope<IncidentCompletedPayload>))]
[JsonSerializable(typeof(EventEnvelope<RemediationRequestedPayload>))]
[JsonSerializable(typeof(EventEnvelope<RemediationCompletedPayload>))]
[JsonSerializable(typeof(EventEnvelope<DeadLetterPayload>))]
public sealed partial class SreAiAgentJsonContext : JsonSerializerContext;
