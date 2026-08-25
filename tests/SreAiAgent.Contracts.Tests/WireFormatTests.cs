using System.Text.Json;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Tests;

/// <summary>
/// Locks down the wire format. These are contract tests in the literal sense:
/// a change that breaks one of them breaks an external consumer, whether that is
/// an alert source, a Kafka subscriber or a dashboard.
/// </summary>
public sealed class WireFormatTests
{
    [Fact]
    public void SerializationUsesTheGeneratedContextRatherThanReflection()
    {
        // If the resolver were not wired up, serialization would silently fall
        // back to reflection and the banned-API rule would be enforcing nothing.
        SreAiAgentJson.Options.TypeInfoResolver.ShouldNotBeNull();
        SreAiAgentJson.Options.IsReadOnly.ShouldBeTrue();
        SreAiAgentJsonContext.Default.IncidentResponse.ShouldNotBeNull();
    }

    [Fact]
    public void PropertyNamesAreSnakeCase()
    {
        IncidentAcceptedResponse response = new(IncidentId.New(), IncidentStatus.Open, IsDuplicate: false);

        string json = JsonSerializer.Serialize(response, SreAiAgentJsonContext.Default.IncidentAcceptedResponse);

        json.ShouldContain("incident_id");
        json.ShouldContain("is_duplicate");
        json.ShouldNotContain("incidentId");
    }

    [Fact]
    public void EnumsAreWrittenAsNamesNotOrdinals()
    {
        IncidentAcceptedResponse response = new(IncidentId.New(), IncidentStatus.Investigating, IsDuplicate: true);

        string json = JsonSerializer.Serialize(response, SreAiAgentJsonContext.Default.IncidentAcceptedResponse);

        json.ShouldContain("\"Investigating\"");
    }

    [Fact]
    public void StronglyTypedIdsAreIndistinguishableFromGuidsOnTheWire()
    {
        IncidentId incidentId = IncidentId.New();
        IncidentAcceptedResponse response = new(incidentId, IncidentStatus.Open, IsDuplicate: false);

        string json = JsonSerializer.Serialize(response, SreAiAgentJsonContext.Default.IncidentAcceptedResponse);

        json.ShouldContain(incidentId.Value.ToString("D", CultureInfo.InvariantCulture));

        IncidentAcceptedResponse? roundTripped =
            JsonSerializer.Deserialize(json, SreAiAgentJsonContext.Default.IncidentAcceptedResponse);
        roundTripped.ShouldNotBeNull();
        roundTripped.IncidentId.ShouldBe(incidentId);
    }

    [Fact]
    public void NullMembersAreOmittedRatherThanWrittenAsNull()
    {
        IncidentTimelineEntryResponse entry = new(
            IncidentEventId.New(),
            "sre.incident.created",
            "Incident opened",
            DateTimeOffset.UnixEpoch,
            Actor: null);

        string json = JsonSerializer.Serialize(
            (IReadOnlyList<IncidentTimelineEntryResponse>)[entry],
            SreAiAgentJsonContext.Default.IReadOnlyListIncidentTimelineEntryResponse);

        json.ShouldNotContain("actor");
    }

    [Fact]
    public void EventEnvelopeRoundTripsWithItsPayload()
    {
        EventEnvelope<IncidentCompletedPayload> envelope = new(
            EventId: Guid.CreateVersion7(),
            EventType: "sre.incident.completed",
            EventVersion: 1,
            OccurredAt: new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
            CorrelationId: "corr-1",
            CausationId: null,
            Payload: new IncidentCompletedPayload(
                IncidentId.New(),
                IncidentStatus.Resolved,
                InvestigationOutcome.Complete,
                ReportVersion: 1,
                ErrorCode: null));

        string json = JsonSerializer.Serialize(
            envelope, SreAiAgentJsonContext.Default.EventEnvelopeIncidentCompletedPayload);
        EventEnvelope<IncidentCompletedPayload>? roundTripped =
            JsonSerializer.Deserialize(json, SreAiAgentJsonContext.Default.EventEnvelopeIncidentCompletedPayload);

        roundTripped.ShouldNotBeNull();
        roundTripped.EventId.ShouldBe(envelope.EventId);
        roundTripped.Payload.IncidentId.ShouldBe(envelope.Payload.IncidentId);
        roundTripped.Payload.Outcome.ShouldBe(InvestigationOutcome.Complete);
        json.ShouldContain("event_id");
        json.ShouldContain("correlation_id");
    }

    [Fact]
    public void AnUnknownProviderFieldDoesNotBreakIngestion()
    {
        // Alert sources add fields without warning; rejecting them would take
        // ingestion down for a cosmetic upstream change.
        const string Json = """
            {
              "source": "alertmanager",
              "external_id": "alert-1",
              "title": "High latency",
              "service": "checkout",
              "environment": "production",
              "severity": "High",
              "brand_new_provider_field": {"nested": true}
            }
            """;

        CreateIncidentRequest? request =
            JsonSerializer.Deserialize(Json, SreAiAgentJsonContext.Default.CreateIncidentRequest);

        request.ShouldNotBeNull();
        request.Service.ShouldBe("checkout");
        request.Severity.ShouldBe(IncidentSeverity.High);
    }

    [Fact]
    public void MalformedJsonIsRejectedRatherThanRepaired()
    {
        const string TrailingComma = """{"source":"alertmanager","external_id":"a",}""";

        Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<CreateIncidentRequest>(TrailingComma, SreAiAgentJson.Options));
    }

    [Fact]
    public void EveryPublishedTopicIsVersioned()
        => KafkaTopics.All.ShouldAllBe(static topic => topic.EndsWith(".v1", StringComparison.Ordinal));
}
