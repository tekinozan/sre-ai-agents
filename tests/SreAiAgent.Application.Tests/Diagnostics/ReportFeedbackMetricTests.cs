using System.Diagnostics.Metrics;
using SreAiAgent.Application.Diagnostics;

namespace SreAiAgent.Application.Tests.Diagnostics;

public sealed class ReportFeedbackMetricTests
{
    [Fact]
    public void AVerdictIsCountedByVerdictAndOutcomeOnly()
    {
        using TestMeterFactory factory = new();
        SreMetrics metrics = new(factory);
        List<(long Value, Dictionary<string, object?> Tags)> recorded = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "sre_report_feedback_total" && factory.Owns(instrument.Meter))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            recorded.Add((value, tags.ToArray().ToDictionary(static tag => tag.Key, static tag => tag.Value, StringComparer.Ordinal))));
        listener.Start();

        metrics.ReportFeedback("acme", "Incorrect", "Partial");

        recorded.ShouldHaveSingleItem();
        recorded[0].Value.ShouldBe(1);

        // No incident id, actor or text: the series stays bounded and the
        // free text stays in the table.
        recorded[0].Tags.Keys.Order(StringComparer.Ordinal).ShouldBe(["outcome", "tenant", "verdict"]);
        recorded[0].Tags["verdict"].ShouldBe("Incorrect");
        recorded[0].Tags["outcome"].ShouldBe("Partial");
    }
}
