using System.Text.Json;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Serialization;

namespace SreAiAgent.Contracts.Tests;

/// <summary>
/// Locks what an omitted member deserializes to.
/// </summary>
/// <remarks>
/// System.Text.Json does not run property initializers when it materializes a
/// type with required members, so a default written next to a property is not
/// the value an absent member arrives with. That is easy to write, compiles, and
/// produced an incident whose investigation silently never started.
///
/// These tests exist so the optional members of the request contract stay
/// nullable. If someone reintroduces an initializer, the value here goes from
/// null to that default and the test fails, which is the moment to notice rather
/// than in production.
/// </remarks>
public sealed class AbsentMemberTests
{
    private const string MinimalRequest = """
        {
          "source": "alertmanager",
          "external_id": "alert-1",
          "title": "Checkout latency above threshold",
          "service": "checkout",
          "environment": "production",
          "severity": "High"
        }
        """;

    [Fact]
    public void OmittedOptionalMembersDeserializeToNullRatherThanADeclaredDefault()
    {
        CreateIncidentRequest request = JsonSerializer.Deserialize(
            MinimalRequest, SreAiAgentJsonContext.Default.CreateIncidentRequest)!;

        request.Labels.ShouldBeNull();
        request.Annotations.ShouldBeNull();
        request.IdentityLabelKeys.ShouldBeNull();
        request.StartInvestigation.ShouldBeNull();
        request.Fingerprint.ShouldBeNull();
        request.DetectedAt.ShouldBeNull();
    }

    [Fact]
    public void RequiredMembersStillArrive()
    {
        CreateIncidentRequest request = JsonSerializer.Deserialize(
            MinimalRequest, SreAiAgentJsonContext.Default.CreateIncidentRequest)!;

        request.Source.ShouldBe("alertmanager");
        request.ExternalId.ShouldBe("alert-1");
        request.Service.ShouldBe("checkout");
    }

    [Fact]
    public void AMissingRequiredMemberIsRejected()
    {
        const string WithoutService = """
            {"source":"alertmanager","external_id":"a","title":"t","environment":"production","severity":"High"}
            """;

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(
            WithoutService, SreAiAgentJsonContext.Default.CreateIncidentRequest));
    }
}
