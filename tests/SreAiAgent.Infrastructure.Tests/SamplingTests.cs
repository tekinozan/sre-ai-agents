using System.Diagnostics;
using OpenTelemetry.Trace;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Infrastructure.Observability.Telemetry;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers the sampling policy of ARCHITECTURE.md 16.1.
/// </summary>
/// <remarks>
/// The case that matters is an investigation under a parent that was not
/// sampled. That is the normal case, not an edge one: an investigation always
/// starts inside the Kafka consumer span that triggered it, which continues the
/// trace of the HTTP request that accepted the alert, and at a low ingress ratio
/// most of those requests are dropped.
///
/// The first implementation wrapped this sampler in a ParentBasedSampler, which
/// only consults its inner sampler for root spans. Nothing failed and nothing
/// logged; investigations simply never appeared in the tracing backend, which is
/// indistinguishable from the platform being idle.
/// </remarks>
public sealed class SamplingTests
{
    /// <summary>A ratio of zero, so anything sampled here was sampled deliberately.</summary>
    private readonly InvestigationAwareSampler _sampler = new(0d);

    private static SamplingParameters Parameters(string name, ActivityTraceFlags parentFlags)
        => new(
            new ActivityContext(
                ActivityTraceId.CreateRandom(),
                ActivitySpanId.CreateRandom(),
                parentFlags),
            ActivityTraceId.CreateRandom(),
            name,
            ActivityKind.Internal);

    [Fact]
    public void AnInvestigationIsSampledEvenWhenItsParentWasNot()
    {
        SamplingResult result = _sampler.ShouldSample(
            Parameters(SreSpanNames.Investigation, ActivityTraceFlags.None));

        result.Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void AnInvestigationIsSampledWhenItIsARoot()
    {
        SamplingResult result = _sampler.ShouldSample(
            new SamplingParameters(
                default,
                ActivityTraceId.CreateRandom(),
                SreSpanNames.Investigation,
                ActivityKind.Internal));

        result.Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void SpansUnderASampledParentAreKept()
    {
        // Everything inside an investigation follows it, so a sampled
        // investigation keeps its agent, model and tool spans rather than a
        // disconnected subset of them.
        SamplingResult result = _sampler.ShouldSample(
            Parameters(SreSpanNames.AgentExecute, ActivityTraceFlags.Recorded));

        result.Decision.ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void OtherSpansUnderAnUnsampledParentAreDropped()
    {
        SamplingResult result = _sampler.ShouldSample(
            Parameters(SreSpanNames.ToolExecute, ActivityTraceFlags.None));

        result.Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Fact]
    public void OtherRootSpansFollowTheConfiguredRatio()
    {
        // At a ratio of zero an ordinary root is dropped. This is the ingress
        // behaviour the investigation exemption exists to override.
        SamplingResult result = _sampler.ShouldSample(
            new SamplingParameters(
                default,
                ActivityTraceId.CreateRandom(),
                "GET /health/live",
                ActivityKind.Server));

        result.Decision.ShouldBe(SamplingDecision.Drop);
    }
}
