using SreAiAgent.Application.Abstractions.Observability;

namespace SreAiAgent.Infrastructure.Observability.Providers.Fakes;

// Deterministic observability backends for local development and CI
// (ARCHITECTURE.md 19.3: a local environment does not reach production
// observability endpoints).
//
// These are not empty stubs. They return a coherent incident: a connection pool
// filling up, waiting clients appearing, latency following, and error logs
// arriving after both. An agent reasoning over this data can reach a defensible
// conclusion, which is what makes the vertical slice a real test of the pipeline
// rather than a test that the pipeline does not crash.
//
// Everything is a pure function of the query. There is no randomness and no wall
// clock beyond the injected TimeProvider, so a workflow test asserting a specific
// finding is stable rather than occasionally right.

/// <summary>Shape-generating helpers shared by the fakes.</summary>
internal static class FakeSignals
{
    /// <summary>
    /// A metric that is flat and then climbs over the last third of the window,
    /// which is what a saturating resource looks like.
    /// </summary>
    public static IReadOnlyList<MetricSample> Ramp(
        TimeWindow window,
        TimeSpan step,
        double baseline,
        double peak)
    {
        List<MetricSample> samples = [];
        int count = (int)Math.Clamp(window.Duration / step, 1, 720);
        int rampStart = (int)(count * 0.66);

        for (int i = 0; i < count; i++)
        {
            double value = i < rampStart
                ? baseline
                : baseline + ((peak - baseline) * (i - rampStart) / Math.Max(1, count - rampStart));

            samples.Add(new MetricSample(window.Start + (step * i), Math.Round(value, 3)));
        }

        return samples;
    }

    /// <summary>A metric that stays flat, for signals that are not the cause.</summary>
    public static IReadOnlyList<MetricSample> Flat(TimeWindow window, TimeSpan step, double value)
    {
        List<MetricSample> samples = [];
        int count = (int)Math.Clamp(window.Duration / step, 1, 720);

        for (int i = 0; i < count; i++)
        {
            samples.Add(new MetricSample(window.Start + (step * i), value));
        }

        return samples;
    }
}

/// <summary>Deterministic metrics backend.</summary>
internal sealed class FakeMetricsProvider : IMetricsProvider
{
    private const string ProviderName = "fake-prometheus";

    private readonly TimeProvider _timeProvider;

    public FakeMetricsProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<MetricQueryResult> QueryAsync(MetricQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        TimeWindow window = TimeWindow.EndingAt(query.At, TimeSpan.FromMinutes(5));
        return Task.FromResult(Build(query.MetricSelector, query.Service, window, TimeSpan.FromMinutes(5)));
    }

    /// <inheritdoc />
    public Task<MetricQueryResult> QueryRangeAsync(MetricRangeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Build(query.MetricSelector, query.Service, query.Window, query.Step));
    }

    private static MetricQueryResult Build(string metric, string service, TimeWindow window, TimeSpan step)
    {
        Dictionary<string, string> labels = new(StringComparer.Ordinal)
        {
            ["service"] = service,
            ["metric"] = metric,
        };

        IReadOnlyList<MetricSample> samples = metric switch
        {
            // The pool fills and clients start waiting: the cause.
            "db_connection_pool_active" => FakeSignals.Ramp(window, step, baseline: 12, peak: 40),
            "db_connection_pool_waiting" => FakeSignals.Ramp(window, step, baseline: 0, peak: 27),
            "db_connection_pool_idle" => FakeSignals.Ramp(window, step, baseline: 28, peak: 0),

            // Latency follows the pool, slightly later: the symptom.
            "http_request_duration_p99_seconds" => FakeSignals.Ramp(window, step, baseline: 0.18, peak: 1.24),
            "http_request_duration_p50_seconds" => FakeSignals.Ramp(window, step, baseline: 0.04, peak: 0.09),
            "db_query_duration_p99_seconds" => FakeSignals.Ramp(window, step, baseline: 0.02, peak: 0.031),

            // Traffic is flat, which rules out a load spike and is exactly the
            // kind of negative evidence a good investigation should surface.
            "http_requests_total" => FakeSignals.Flat(window, step, 840),
            "http_requests_error_total" => FakeSignals.Ramp(window, step, baseline: 2, peak: 34),

            "container_memory_working_set_bytes" => FakeSignals.Flat(window, step, 412_000_000),
            "container_cpu_usage_seconds_total" => FakeSignals.Flat(window, step, 0.42),
            "kafka_consumer_lag" => FakeSignals.Flat(window, step, 3),

            _ => [],
        };

        return new MetricQueryResult(
            samples.Count == 0 ? [] : [new MetricSeries(labels, samples)],
            IsPartial: false,
            Provider: ProviderName);
    }
}

/// <summary>Deterministic log backend.</summary>
internal sealed class FakeLogProvider : ILogProvider
{
    private const string ProviderName = "fake-loki";

    /// <inheritdoc />
    public Task<LogSearchResult> SearchAsync(LogSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset start = query.Window.Start;

        List<LogEntry> entries =
        [
            new LogEntry(
                start.AddMinutes(22),
                "error",
                "Npgsql.NpgsqlException: The connection pool has been exhausted, "
                + "either raise MaxPoolSize (currently 40) or Timeout (currently 15 seconds)",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["service"] = query.Service,
                    ["exception.type"] = "NpgsqlException",
                }),
            new LogEntry(
                start.AddMinutes(24),
                "error",
                "Timeout expired while awaiting a connection from the pool for checkout.orders",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["service"] = query.Service,
                    ["operation"] = "GET /orders",
                }),
            new LogEntry(
                start.AddMinutes(26),
                "warn",
                "Request took 1240ms, exceeding the 800ms budget",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["service"] = query.Service,
                    ["operation"] = "GET /orders",
                }),
        ];

        List<LogEntry> matched = [.. entries.Where(entry => MatchesLevel(entry.Level, query.MinimumLevel))];

        // Newest first, which is what ILogProvider documents and what Loki
        // returns. This was wrong until the contract suite put the two side by
        // side: every investigation run against the fakes had been reading the
        // window in the opposite order from production, and it also decided
        // which entries a limit kept — the oldest rather than the most recent.
        matched.Sort(static (left, right) => right.Timestamp.CompareTo(left.Timestamp));

        // TotalMatched is reported separately from the returned page so an agent
        // can say "at least N" rather than reasoning over a truncated sample as
        // if it were complete.
        return Task.FromResult(new LogSearchResult(
            [.. matched.Take(query.Limit)],
            matched.Count,
            matched.Count > query.Limit,
            ProviderName));
    }

    private static bool MatchesLevel(string entryLevel, string minimumLevel)
        => Rank(entryLevel) >= Rank(minimumLevel);

    private static int Rank(string level) => level switch
    {
        "trace" => 0,
        "debug" => 1,
        "info" => 2,
        "warn" => 3,
        "error" => 4,
        "fatal" => 5,
        _ => 2,
    };
}

/// <summary>Deterministic Kubernetes backend.</summary>
internal sealed class FakeKubernetesObservability : IKubernetesObservability
{
    private const string ProviderName = "fake-kubernetes";

    private readonly TimeProvider _timeProvider;

    public FakeKubernetesObservability(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<PodSummary> GetPodsAsync(KubernetesScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset started = _timeProvider.GetUtcNow().AddDays(-6);

        // Healthy pods on purpose. The seeded incident is a database problem, and
        // an investigation that concludes "the platform layer is fine" from real
        // evidence is a correct and useful finding.
        List<PodState> pods =
        [
            new PodState("checkout-7d9f8c-abcde", "Running", IsReady: true, 0, null, started, "node-eu-1a"),
            new PodState("checkout-7d9f8c-fghij", "Running", IsReady: true, 0, null, started, "node-eu-1b"),
            new PodState("checkout-7d9f8c-klmno", "Running", IsReady: true, 1, "Completed", started, "node-eu-1c"),
        ];

        return Task.FromResult(new PodSummary(pods, 3, 3, IsTruncated: false, ProviderName));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesEvent>> GetEventsAsync(
        KubernetesScope scope,
        TimeWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<KubernetesEvent>>(
        [
            new KubernetesEvent(
                "Warning",
                "Unhealthy",
                "Readiness probe failed: HTTP probe timed out after 2s",
                $"pod/checkout-7d9f8c-klmno",
                Count: 2,
                window.Start.AddMinutes(25)),
        ]);
    }
}

/// <summary>Deterministic database backend.</summary>
internal sealed class FakeDatabaseObservability : IDatabaseObservability
{
    private const string ProviderName = "fake-postgresql";

    /// <inheritdoc />
    public Task<DatabaseHealth> GetHealthAsync(
        DatabaseScope scope,
        TimeWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        // Pinned at the maximum with clients queued. Both facts are needed: a
        // pool at 100% with nobody waiting is correctly sized and busy, not
        // exhausted, and the difference decides the recommendation.
        ConnectionPoolState pool = new(
            ActiveConnections: 40,
            IdleConnections: 0,
            MaxConnections: 40,
            WaitingClients: 27);

        List<SlowQuery> slowQueries =
        [
            new SlowQuery(
                "SELECT * FROM orders WHERE customer_id = $1 AND status = $2 ORDER BY created_at DESC",
                Calls: 18_402,
                TotalTime: TimeSpan.FromSeconds(521),
                MeanTime: TimeSpan.FromMilliseconds(28)),
            new SlowQuery(
                "UPDATE inventory SET reserved = reserved + $1 WHERE sku = $2",
                Calls: 9_120,
                TotalTime: TimeSpan.FromSeconds(96),
                MeanTime: TimeSpan.FromMilliseconds(11)),
        ];

        return Task.FromResult(new DatabaseHealth(
            pool,
            slowQueries,
            ReplicationLag: TimeSpan.FromMilliseconds(340),
            IsAcceptingConnections: true,
            Provider: ProviderName));
    }
}
