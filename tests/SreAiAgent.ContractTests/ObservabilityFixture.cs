using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace SreAiAgent.ContractTests;

/// <summary>
/// A real Prometheus and a real Loki, seeded once, for the whole suite.
/// </summary>
/// <remarks>
/// Prometheus scrapes a pushgateway rather than an application. Pushing the text
/// format over HTTP is the entire ingestion path that way, where remote-write
/// would mean protobuf and snappy for data a test is only trying to state.
///
/// The seed is the same shape as <c>eng/seed-observability.sh</c> for a reason:
/// the two should not be able to drift into proving different things about the
/// same adapters.
/// </remarks>
public sealed class ObservabilityFixture : IAsyncLifetime
{
    /// <summary>Metrics the seed writes, and therefore the ones a contract may assert on.</summary>
    public const string SeededGauge = "db_connection_pool_waiting";

    public const string SeededService = "checkout";

    public const string SeededEnvironment = "production";

    /// <summary>Written to Loki at <c>error</c> so a minimum-level filter has something to keep.</summary>
    public const int SeededErrorCount = 3;

    private const string PrometheusConfiguration = """
        global:
          scrape_interval: 1s
        scrape_configs:
          - job_name: pushgateway
            honor_labels: true
            static_configs:
              - targets: ["pushgateway:9091"]
        """;

    private readonly INetwork _network;
    private readonly IContainer _pushgateway;
    private readonly IContainer _prometheus;
    private readonly IContainer _loki;

    // Built here rather than in field initializers because two of them need the
    // network, and a field initializer cannot see another field.
    public ObservabilityFixture()
    {
        _network = new NetworkBuilder().Build();

        _pushgateway = new ContainerBuilder("prom/pushgateway:v1.11.0")
            .WithNetwork(_network)
            .WithNetworkAliases("pushgateway")
            .WithPortBinding(9091, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPath("/-/ready").ForPort(9091)))
            .Build();

        _prometheus = new ContainerBuilder("prom/prometheus:v3.1.0")
            .WithNetwork(_network)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(PrometheusConfiguration), "/etc/prometheus/prometheus.yml")
            .WithCommand("--config.file=/etc/prometheus/prometheus.yml", "--storage.tsdb.retention.time=1h")
            .WithPortBinding(9090, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPath("/-/ready").ForPort(9090)))
            .Build();

        _loki = new ContainerBuilder("grafana/loki:3.3.2")
            .WithCommand("-config.file=/etc/loki/local-config.yaml")
            .WithPortBinding(3100, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPath("/ready").ForPort(3100)))
            .Build();
    }

    public Uri PrometheusBaseAddress { get; private set; } = null!;

    public Uri LokiBaseAddress { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await _network.CreateAsync(cancellationToken);

        // Prometheus resolves the pushgateway by its network alias, so the two
        // share a network; Loki is reached only from the test process and needs
        // no alias.
        await _pushgateway.StartAsync(cancellationToken);
        await _prometheus.StartAsync(cancellationToken);
        await _loki.StartAsync(cancellationToken);

        PrometheusBaseAddress = new Uri(
            $"http://{_prometheus.Hostname}:{_prometheus.GetMappedPublicPort(9090)}/");
        LokiBaseAddress = new Uri($"http://{_loki.Hostname}:{_loki.GetMappedPublicPort(3100)}/");

        await SeedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _loki.DisposeAsync();
        await _prometheus.DisposeAsync();
        await _pushgateway.DisposeAsync();
        await _network.DisposeAsync();
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = new();

        string labels = $"service=\"{SeededService}\",environment=\"{SeededEnvironment}\"";
        Uri pushgateway = new(
            $"http://{_pushgateway.Hostname}:{_pushgateway.GetMappedPublicPort(9091)}/metrics/job/contract-seed");

        // Three pushes with a gap, so a range query sees a slope rather than a
        // single point. Each push replaces the value; the history comes from
        // Prometheus scraping in between.
        int[] ramp = [0, 9, 18];

        foreach (int waiting in ramp)
        {
            string body = $"# TYPE {SeededGauge} gauge\n{SeededGauge}{{{labels}}} {waiting}\n";

            using StringContent content = new(body, Encoding.UTF8, "text/plain");
            (await client.PostAsync(pushgateway, content, cancellationToken)).EnsureSuccessStatusCode();

            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        }

        await WaitForScrapeAsync(client, cancellationToken);
        await SeedLogsAsync(client, cancellationToken);
    }

    /// <summary>
    /// Polls until the seeded series is queryable.
    /// </summary>
    /// <remarks>
    /// A fixed sleep here would be a race that passes on this machine and fails
    /// on a loaded one. Asking the question the tests will ask, until it answers,
    /// is both shorter in the common case and correct in the slow one.
    /// </remarks>
    private async Task WaitForScrapeAsync(HttpClient client, CancellationToken cancellationToken)
    {
        Uri query = new(
            PrometheusBaseAddress,
            $"api/v1/query?query={Uri.EscapeDataString($"{SeededGauge}{{service=\"{SeededService}\"}}")}");

        for (int attempt = 0; attempt < 40; attempt++)
        {
            using JsonDocument response = JsonDocument.Parse(
                await client.GetStringAsync(query, cancellationToken));

            if (response.RootElement.GetProperty("data").GetProperty("result").GetArrayLength() > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Prometheus never scraped '{SeededGauge}'. Check honor_labels on the scrape job: without it "
            + "the pushed service and environment labels are replaced and every selector matches nothing.");
    }

    private async Task SeedLogsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

        // Oldest first: Loki orders entries within a stream and rejects one that
        // goes backwards. Whether they come back newest-first is the adapter's
        // job, and one of the things the contract asserts.
        string payload = $$"""
            {"streams":[{"stream":{"service":"{{SeededService}}","environment":"{{SeededEnvironment}}","level":"error"},
            "values":[["{{now - 180_000_000_000L}}","connection pool exhausted"],
                      ["{{now - 120_000_000_000L}}","timeout awaiting a connection"],
                      ["{{now - 60_000_000_000L}}","exception while reading from stream"]]}]}
            """;

        using StringContent content = new(payload, Encoding.UTF8, "application/json");

        (await client.PostAsync(new Uri(LokiBaseAddress, "loki/api/v1/push"), content, cancellationToken))
            .EnsureSuccessStatusCode();
    }
}

/// <summary>Keeps the three containers to one set for the whole assembly.</summary>
#pragma warning disable CA1711 // "Collection" is xunit's word here, not a suffix choice.
[CollectionDefinition(Name)]
public sealed class ObservabilityCollection : ICollectionFixture<ObservabilityFixture>
{
    public const string Name = "observability";
}
#pragma warning restore CA1711
