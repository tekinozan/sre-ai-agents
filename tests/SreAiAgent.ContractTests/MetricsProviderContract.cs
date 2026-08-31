using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability.Providers.Fakes;
using SreAiAgent.Infrastructure.Observability.Providers.Prometheus;
using SreAiAgent.Infrastructure.Registries;

namespace SreAiAgent.ContractTests;

/// <summary>
/// What every <see cref="IMetricsProvider"/> has to do, whichever one it is.
/// </summary>
/// <remarks>
/// Only behaviours both a fake and a real backend can honestly satisfy are here.
/// Where they cannot agree the answer is a finding, not a weaker assertion — see
/// docs/agents/PITFALLS.md for the two this suite has produced so far.
/// </remarks>
public abstract class MetricsProviderContract
{
    /// <summary>Seeded in the fixture and ramped by the fake, so both can answer it.</summary>
    protected const string KnownMetric = "db_connection_pool_waiting";

    protected abstract IMetricsProvider Provider { get; }

    protected abstract string Service { get; }

    [Fact]
    public async Task AKnownMetricAnswersWithAtLeastOnePoint()
    {
        MetricQueryResult result = await Provider.QueryRangeAsync(
            RangeQuery(TimeSpan.FromMinutes(30)), TestContext.Current.CancellationToken);

        MetricSeries series = result.Series.ShouldHaveSingleItem();
        series.Points.ShouldNotBeEmpty();
        series.Labels.ShouldNotBeNull();
    }

    [Fact]
    public async Task EveryPointFallsInsideTheRequestedWindow()
    {
        MetricRangeQuery query = RangeQuery(TimeSpan.FromMinutes(30));

        MetricQueryResult result = await Provider.QueryRangeAsync(
            query, TestContext.Current.CancellationToken);

        // A point outside the window is a scope the caller did not ask for, and
        // an agent citing it would be reasoning about a period it never
        // requested. Both backends express the window differently, which is
        // exactly why it is worth asserting on both.
        foreach (MetricSample point in result.Series.SelectMany(static series => series.Points))
        {
            point.Timestamp.ShouldBeGreaterThanOrEqualTo(query.Window.Start);
            point.Timestamp.ShouldBeLessThanOrEqualTo(query.Window.End);
        }
    }

    [Fact]
    public async Task PointsAreOrderedByTimestamp()
    {
        MetricQueryResult result = await Provider.QueryRangeAsync(
            RangeQuery(TimeSpan.FromMinutes(30)), TestContext.Current.CancellationToken);

        // Out of order, a rising series and a falling one look the same to
        // anything that reads the first and last value.
        foreach (MetricSeries series in result.Series)
        {
            series.Points.Select(static point => point.Timestamp)
                .ShouldBe(series.Points.Select(static point => point.Timestamp).Order());
        }
    }

    [Fact]
    public async Task TheResultNamesTheBackendThatAnswered()
    {
        MetricQueryResult result = await Provider.QueryAsync(
            new MetricQuery(Service, "production", KnownMetric, Labels(), DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        // Recorded on the evidence row. Without it a report cannot say where a
        // number came from, which is the whole premise of an evidence-first
        // investigation.
        result.Provider.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ACancelledTokenIsHonoured()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Provider.QueryRangeAsync(RangeQuery(TimeSpan.FromMinutes(30)), cancelled.Token));
    }

    private MetricRangeQuery RangeQuery(TimeSpan duration)
        => new(
            Service,
            "production",
            KnownMetric,
            Labels(),
            TimeWindow.EndingAt(DateTimeOffset.UtcNow, duration),
            TimeSpan.FromSeconds(15));

    private static Dictionary<string, string> Labels() => new(StringComparer.Ordinal);
}

/// <summary>The contract, against the deterministic fake.</summary>
public sealed class FakeMetricsContractTests : MetricsProviderContract
{
    protected override IMetricsProvider Provider { get; } = new FakeMetricsProvider(TimeProvider.System);

    protected override string Service => ObservabilityFixture.SeededService;
}

/// <summary>The same contract, against a real Prometheus holding real samples.</summary>
[Collection(ObservabilityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PrometheusMetricsContractTests : MetricsProviderContract
{
    private readonly ObservabilityFixture _fixture;

    public PrometheusMetricsContractTests(ObservabilityFixture fixture)
    {
        _fixture = fixture;

        // The real tool registry, so the expressions under test are the shipped
        // ones rather than something written to make a test pass.
        Provider = new PrometheusMetricsProvider(
            new FixedAddressHttpClientFactory(fixture.PrometheusBaseAddress),
            new ReadOnlyToolRegistry(
                Options.Create(new RegistryOptions
                {
                    ConfigurationRoot = RepositoryPaths.Config,
                    PromptRoot = RepositoryPaths.Prompts,
                    ToolRelease = "tools-2026.08.25.1",
                }),
                new YamlArtifactLoader()),
            Options.Create(new ObservabilityOptions
            {
                PrometheusBaseAddress = fixture.PrometheusBaseAddress.ToString(),
            }));
    }

    protected override IMetricsProvider Provider { get; }

    protected override string Service => ObservabilityFixture.SeededService;
}
