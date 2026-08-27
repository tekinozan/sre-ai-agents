using OpenTelemetry.Trace;
using SreAiAgent.Application.Diagnostics;

namespace SreAiAgent.Infrastructure.Observability;

/// <summary>
/// Samples every investigation and leaves everything else to a parent-based
/// ratio (ARCHITECTURE.md 16.1).
/// </summary>
/// <remarks>
/// The name check happens before the parent is consulted, and that ordering is
/// the whole point. An investigation span is never a root: it starts inside the
/// Kafka consumer span that triggered it, which in turn continues the trace of
/// the HTTP request that accepted the alert. Wrapping this sampler in a
/// ParentBasedSampler therefore never consulted it at all, and the investigation
/// inherited the ingress decision, so at a ten percent ingress ratio nine
/// investigations in ten were dropped. Those are precisely the traces someone
/// asks for by name.
///
/// Everything below an investigation is left to the parent-based delegate. Its
/// parent is sampled, so the agent, model and tool spans come with it, and the
/// trace stays whole rather than becoming a scattering of unrelated spans.
///
/// The cost of always sampling is bounded by how rare investigations are. This
/// is the trade section 16.1 asks for: keep the low-volume, high-value traces
/// whole and sample the high-volume ingress, most of which is health probes.
/// </remarks>
/// <param name="ratio">Fraction of non-investigation traces to keep.</param>
internal sealed class InvestigationAwareSampler(double ratio) : Sampler
{
    private readonly Sampler _delegate = new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio));

    /// <inheritdoc />
    public override SamplingResult ShouldSample(in SamplingParameters parameters)
        => string.Equals(parameters.Name, SreSpanNames.Investigation, StringComparison.Ordinal)
            ? new SamplingResult(SamplingDecision.RecordAndSample)
            : _delegate.ShouldSample(in parameters);
}
