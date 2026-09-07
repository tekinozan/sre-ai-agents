using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Alerts;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers tenant binding rules at alert ingestion (ARCHITECTURE.md 10.1).
/// </summary>
public sealed class AlertTenantBindingTests
{
    private static CreateIncidentRequest CreateRequest(IReadOnlyDictionary<string, string>? labels = null)
        => new()
        {
            Source = "alertmanager",
            ExternalId = "ext-1",
            Fingerprint = "fp-1",
            Title = "High Latency",
            Description = "Latency above threshold",
            Service = "checkout",
            Environment = "production",
            Severity = IncidentSeverity.Critical,
            DetectedAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
            Labels = labels,
            Annotations = new Dictionary<string, string>(StringComparer.Ordinal) { ["summary"] = "High Latency" },
            IdentityLabelKeys = ["alertname"],
            StartInvestigation = true,
        };

    [Fact]
    public void PayloadWithNoTenantLabelGetsBoundToCallerTenant()
    {
        CreateIncidentRequest request = CreateRequest(labels: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alertname"] = "HighLatency",
            ["service"] = "checkout",
        });

        Result<CreateIncidentRequest> result = AlertNormalizer.BindTenant(request, "acme");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Labels.ShouldNotBeNull();
        result.Value.Labels["tenant"].ShouldBe("acme");
        result.Value.Labels["alertname"].ShouldBe("HighLatency");
        result.Value.Labels["service"].ShouldBe("checkout");
    }

    [Fact]
    public void PayloadWithNullLabelsGetsBoundToCallerTenant()
    {
        CreateIncidentRequest request = CreateRequest(labels: null);

        Result<CreateIncidentRequest> result = AlertNormalizer.BindTenant(request, "acme");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Labels.ShouldNotBeNull();
        result.Value.Labels["tenant"].ShouldBe("acme");
    }

    [Fact]
    public void PayloadLabelledForDifferentTenantFailsWithForbidden()
    {
        CreateIncidentRequest request = CreateRequest(labels: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant"] = "globex",
            ["alertname"] = "HighLatency",
        });

        Result<CreateIncidentRequest> result = AlertNormalizer.BindTenant(request, "acme");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.Forbidden.ToString());
        ErrorCatalog.Describe(result.Error.Code).HttpStatusCode.ShouldBe(403);
    }

    [Fact]
    public void PayloadLabelledForSameTenantSucceedsAndPreservesLabels()
    {
        CreateIncidentRequest request = CreateRequest(labels: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant"] = "acme",
            ["alertname"] = "HighLatency",
            ["region"] = "us-east-1",
        });

        Result<CreateIncidentRequest> result = AlertNormalizer.BindTenant(request, "acme");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Labels.ShouldNotBeNull();
        result.Value.Labels["tenant"].ShouldBe("acme");
        result.Value.Labels["alertname"].ShouldBe("HighLatency");
        result.Value.Labels["region"].ShouldBe("us-east-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BindingWithEmptyTenantFailsWithForbidden(string emptyTenant)
    {
        CreateIncidentRequest request = CreateRequest(labels: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alertname"] = "HighLatency",
        });

        Result<CreateIncidentRequest> result = AlertNormalizer.BindTenant(request, emptyTenant);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.Forbidden.ToString());
        ErrorCatalog.Describe(result.Error.Code).HttpStatusCode.ShouldBe(403);
    }
}
