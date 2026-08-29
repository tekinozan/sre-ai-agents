using System.Net;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability;
using SreAiAgent.Infrastructure.Registries;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// What the Prometheus adapter sends, and what it makes of what comes back.
/// </summary>
/// <remarks>
/// These run against the tool artifacts that ship, not fixtures, so the queries
/// asserted here are the queries production sends.
///
/// The reason this suite exists at all: until this adapter, nothing in the
/// platform put a model-supplied value into a query language. The fake ignores
/// labels entirely. Every escaping test below covers a surface V1 has never had.
/// </remarks>
public sealed class PrometheusMetricsProviderTests
{
    private const string EmptySuccess = """{"status":"success","data":{"resultType":"matrix","result":[]}}""";

    private static readonly DateTimeOffset At = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheQueryComesFromTheShippedToolArtifact()
    {
        StubHttpMessageHandler handler = new(EmptySuccess);

        await Provider(handler).QueryAsync(Query("http_requests_total"), TestContext.Current.CancellationToken);

        handler.Parameter("query")
            .ShouldBe("""sum(rate(http_requests_total{service="checkout",environment="production"}[5m]))""");
    }

    [Fact]
    public async Task ALabelValueCannotCloseTheMatcherAndOpenAnother()
    {
        StubHttpMessageHandler handler = new(EmptySuccess);

        // The tool schema permits any string up to 256 characters, so this is a
        // value a model can legally supply — and injected log content reaching a
        // model is an explicit threat (ARCHITECTURE.md 15.4). Escaped, the whole
        // thing stays one label value; unescaped it would close the selector and
        // start a second series expression.
        const string BreakOut = """checkout-1"} or up{job="admin""";

        await Provider(handler).QueryAsync(
            Query("http_requests_total", ("pod", BreakOut)),
            TestContext.Current.CancellationToken);

        string query = handler.Parameter("query");
        string escaped = BreakOut.Replace("\"", "\\\"", StringComparison.Ordinal);

        query.ShouldContain($"pod=\"{escaped}\"");

        // The pair matters: the escaped form present and the raw form absent is
        // what says every quote the value carried is still inside the value.
        query.ShouldNotContain(BreakOut);
    }

    [Fact]
    public async Task AModelCannotRelabelItsWayOutOfTheServiceAllowlist()
    {
        StubHttpMessageHandler handler = new(EmptySuccess);

        // The policy engine checks the `service` argument against the execution
        // allowlist. It has no reason to look inside `labels`, so without an
        // explicit refusal here an agent scoped to checkout could read payments
        // with every authorization check having passed.
        await Should.ThrowAsync<ObservabilityQueryException>(async () =>
            await Provider(handler).QueryAsync(
                Query("http_requests_total", ("service", "payments")),
                TestContext.Current.CancellationToken));

        handler.Requests.ShouldBeEmpty("Nothing should reach the backend once the query is refused.");
    }

    [Fact]
    public async Task AnIllegalLabelNameIsRefusedRatherThanDropped()
    {
        StubHttpMessageHandler handler = new(EmptySuccess);

        // Dropping it would widen the query rather than narrow it, and a broader
        // answer that looks ordinary is the worst of the available failures.
        await Should.ThrowAsync<ObservabilityQueryException>(async () =>
            await Provider(handler).QueryAsync(
                Query("http_requests_total", ("pod-name", "checkout-1")),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WarningsFromTheBackendSurfaceAsAPartialAnswer()
    {
        StubHttpMessageHandler handler = new(
            """{"status":"success","warnings":["1 shard failed"],"data":{"resultType":"matrix","result":[]}}""");

        MetricQueryResult result = await Provider(handler)
            .QueryAsync(Query("http_requests_total"), TestContext.Current.CancellationToken);

        // An agent reading "no errors" out of a scrape that lost a shard is the
        // confident wrong answer this platform exists to avoid.
        result.IsPartial.ShouldBeTrue();
        result.Provider.ShouldBe("prometheus");
    }

    [Fact]
    public async Task AnErrorStatusBecomesAFailedToolCallRatherThanAnEmptyResult()
    {
        StubHttpMessageHandler handler = new(
            """{"status":"error","errorType":"bad_data","error":"invalid expression"}""");

        // Returning an empty series here would be indistinguishable from "the
        // metric genuinely has no data", which an agent would cite as evidence.
        await Should.ThrowAsync<ObservabilityQueryException>(async () =>
            await Provider(handler).QueryAsync(Query("http_requests_total"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnHttpFailureIsNotReadAsAnAnswer()
    {
        StubHttpMessageHandler handler = new(EmptySuccess, HttpStatusCode.ServiceUnavailable);

        await Should.ThrowAsync<ObservabilityQueryException>(async () =>
            await Provider(handler).QueryAsync(Query("http_requests_total"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWideWindowWidensTheStepInsteadOfFailing()
    {
        StubHttpMessageHandler handler = new(EmptySuccess);

        // Six hours at the schema minimum is 1,440 points per series; Prometheus
        // refuses outright above eleven thousand. A coarser answer beats an error
        // in the middle of an investigation.
        await Provider(handler).QueryRangeAsync(
            new MetricRangeQuery(
                "checkout",
                "production",
                "http_requests_total",
                new Dictionary<string, string>(StringComparer.Ordinal),
                TimeWindow.EndingAt(At, TimeSpan.FromHours(6)),
                TimeSpan.FromSeconds(15)),
            TestContext.Current.CancellationToken);

        int step = int.Parse(handler.Parameter("step"), CultureInfo.InvariantCulture);

        step.ShouldBeGreaterThan(15);
        (TimeSpan.FromHours(6).TotalSeconds / step).ShouldBeLessThanOrEqualTo(720);
    }

    [Fact]
    public async Task NonFiniteSamplesAreDroppedRatherThanCarriedIntoEvidence()
    {
        StubHttpMessageHandler handler = new(
            """
            {"status":"success","data":{"resultType":"matrix","result":[{"metric":{"service":"checkout"},
            "values":[[1756382400,"1.5"],[1756382460,"NaN"],[1756382520,"+Inf"],[1756382580,"2.5"]]}]}}
            """);

        MetricQueryResult result = await Provider(handler)
            .QueryRangeAsync(RangeQuery("http_requests_total"), TestContext.Current.CancellationToken);

        // A NaN reaching a prompt is a value an agent cannot reason about but
        // will try to. A gap in the series is honest and readable.
        result.Series.ShouldHaveSingleItem().Points.Select(static point => point.Value).ShouldBe([1.5, 2.5]);
    }

    [Fact]
    public async Task AMetricWithNoConfiguredExpressionIsRefused()
    {
        StubHttpMessageHandler handler = new(EmptySuccess);

        await Should.ThrowAsync<ObservabilityQueryException>(async () =>
            await Provider(handler).QueryAsync(Query("disk_io_saturation"), TestContext.Current.CancellationToken));
    }

    private static MetricQuery Query(string metric, params (string Name, string Value)[] labels)
        => new(
            "checkout",
            "production",
            metric,
            labels.ToDictionary(static pair => pair.Name, static pair => pair.Value, StringComparer.Ordinal),
            At);

    private static MetricRangeQuery RangeQuery(string metric)
        => new(
            "checkout",
            "production",
            metric,
            new Dictionary<string, string>(StringComparer.Ordinal),
            TimeWindow.EndingAt(At, TimeSpan.FromMinutes(30)),
            TimeSpan.FromSeconds(60));

    private static PrometheusMetricsProvider Provider(StubHttpMessageHandler handler)
        => new(
            new StubHttpClientFactory(handler),
            ShippedTools(),
            Options.Create(new ObservabilityOptions()));

    /// <summary>The real tool release, so the queries asserted are the shipped ones.</summary>
    private static ReadOnlyToolRegistry ShippedTools()
        => new ReadOnlyToolRegistry(
            Options.Create(new RegistryOptions
            {
                ConfigurationRoot = RepositoryPaths.Config,
                PromptRoot = RepositoryPaths.Prompts,
                ToolRelease = "tools-2026.08.25.1",
            }),
            new YamlArtifactLoader());
}
