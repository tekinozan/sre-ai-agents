using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Workflows;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Tests.Workflows;

/// <summary>
/// Covers the deterministic routing signals of ARCHITECTURE.md 11.2.
/// </summary>
/// <remarks>
/// Each test here is a model call that does not happen. This classifier runs on
/// every incident, so the cases it handles are cost and latency the platform
/// never pays and answers it cannot get wrong.
/// </remarks>
public sealed class DeterministicClassifierTests
{
    private static IncidentSnapshot Incident(
        string title = "Checkout latency above SLO",
        string? description = null,
        string service = "checkout",
        params (string Key, string Value)[] labels)
        => new(
            IncidentId.New(),
            "tenant-a",
            "alertmanager",
            title,
            description,
            service,
            "production",
            IncidentSeverity.High,
            IncidentStatus.Open,
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
            labels.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    [Fact]
    public void AComponentLabelClassifiesWithHighConfidence()
    {
        IncidentClassification? result = DeterministicClassifier.TryClassify(
            Incident(labels: ("component", "database")));

        result.ShouldNotBeNull();
        result.IncidentTypes.ShouldContain("database");
        result.Method.ShouldBe(ClassificationMethod.Deterministic);
        result.Confidence.Value.ShouldBeGreaterThan(0.8m);
    }

    [Fact]
    public void TextAloneClassifiesWithLowerConfidenceThanALabel()
    {
        IncidentClassification? fromText = DeterministicClassifier.TryClassify(
            Incident(title: "Connection pool exhausted on checkout"));
        IncidentClassification? fromLabel = DeterministicClassifier.TryClassify(
            Incident(labels: ("component", "database")));

        fromText.ShouldNotBeNull();
        fromLabel.ShouldNotBeNull();

        // A label is an assertion by whoever configured the alert. Text is
        // inference, and the confidence has to say so, because routing widens the
        // agent selection below the low-confidence threshold instead of guessing.
        fromText.Confidence.Value.ShouldBeLessThan(fromLabel.Confidence.Value);
    }

    [Theory]
    [InlineData("Connection pool exhausted", "database")]
    [InlineData("Pod OOMKilled in checkout", "resource-exhaustion")]
    [InlineData("checkout pods in CrashLoopBackOff", "kubernetes")]
    [InlineData("Kafka consumer lag rising", "queue")]
    [InlineData("Redis evictions spiking", "cache")]
    [InlineData("5xx rate above threshold", "error-rate")]
    [InlineData("p99 latency above SLO", "latency")]
    [InlineData("Upstream timeout on orders", "timeout")]
    public void EachDocumentedSignalIsRecognised(string title, string expectedType)
    {
        IncidentClassification? result = DeterministicClassifier.TryClassify(Incident(title: title));

        result.ShouldNotBeNull();
        result.IncidentTypes.ShouldContain(expectedType);
    }

    [Fact]
    public void AnIncidentSpanningLayersGetsEveryMatchingType()
    {
        // Picking one would cost the investigation an expert. Latency with
        // database errors is genuinely both.
        IncidentClassification? result = DeterministicClassifier.TryClassify(
            Incident(title: "p99 latency above SLO", description: "connection pool exhausted"));

        result.ShouldNotBeNull();
        result.IncidentTypes.ShouldContain("latency");
        result.IncidentTypes.ShouldContain("database");
    }

    [Fact]
    public void NothingRecognisableReturnsNullSoTheModelCanBeConsulted()
    {
        // Returning a wrong guess here would be worse than admitting defeat: the
        // caller falls back to a classifier, and a fabricated deterministic answer
        // would prevent that.
        DeterministicClassifier.TryClassify(Incident(title: "Something odd happened", service: "widgets"))
            .ShouldBeNull();
    }

    [Fact]
    public void ClassificationIsStable()
    {
        IncidentSnapshot incident = Incident(title: "p99 latency above SLO", description: "connection pool exhausted");

        IncidentClassification? first = DeterministicClassifier.TryClassify(incident);
        IncidentClassification? second = DeterministicClassifier.TryClassify(incident);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        second.IncidentTypes.ShouldBe(first.IncidentTypes);
        second.Confidence.ShouldBe(first.Confidence);
    }
}
