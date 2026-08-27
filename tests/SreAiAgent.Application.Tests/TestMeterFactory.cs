using System.Diagnostics.Metrics;

namespace SreAiAgent.Application.Tests;

/// <summary>
/// A meter factory for tests that do not assert on metrics.
/// </summary>
/// <remarks>
/// SreMetrics takes an IMeterFactory because instruments have to be created
/// through the one the host owns, so that a meter is disposed with the container
/// rather than living until the process ends. A test still has to supply one, and
/// this is the smallest thing that satisfies that without pulling in the metrics
/// hosting stack.
/// </remarks>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        Meter meter = new(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (Meter meter in _meters)
        {
            meter.Dispose();
        }

        _meters.Clear();
    }
}
