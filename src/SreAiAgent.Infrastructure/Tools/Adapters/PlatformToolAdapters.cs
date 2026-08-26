using System.Text.Json;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Tools.Adapters;

/// <summary>Adapter for <c>kubernetes.pods</c>.</summary>
internal sealed class KubernetesPodsAdapter : IToolAdapter
{
    private readonly IKubernetesObservability _kubernetes;
    private readonly ObservabilityOptions _options;

    public KubernetesPodsAdapter(
        IKubernetesObservability kubernetes,
        IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _kubernetes = kubernetes;
        _options = options.Value;
    }

    /// <inheritdoc />
    public string ToolName => "kubernetes.pods";

    /// <inheritdoc />
    public async Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        string requested = ToolArguments.GetString(arguments, "namespace");
        string service = ToolArguments.GetString(arguments, "service");

        // The namespace comes from configuration, not from the model. A model
        // that names a namespace it should not see would otherwise be limited only
        // by whatever the service account happened to be granted, and cluster RBAC
        // is not something this code should be relying on alone.
        string cluster = context.Environment;
        string ns = _options.KubernetesNamespaceByCluster.TryGetValue(cluster, out string? configured)
            ? configured
            : requested;

        PodSummary summary = await _kubernetes.GetPodsAsync(
            new KubernetesScope(cluster, ns, context.Environment, ToolArguments.GetLabels(arguments, "label_selector")),
            cancellationToken).ConfigureAwait(false);

        return new ToolAdapterResult(
            Summary: SummarizePods(service, ns, summary),
            ContentJson: ToolArguments.Serialize(
                KubernetesEnvelope.From(ns, summary), ToolResultJsonContext.Default.KubernetesEnvelope),
            SourceType: EvidenceSourceType.Kubernetes,
            Provider: summary.Provider,
            WindowStart: null,
            WindowEnd: null,
            IsPartial: summary.IsTruncated);
    }

    private static string SummarizePods(string service, string ns, PodSummary summary)
    {
        int notReady = summary.Pods.Count(static pod => !pod.IsReady);
        int restarts = summary.Pods.Sum(static pod => pod.RestartCount);
        string? worstReason = summary.Pods
            .FirstOrDefault(static pod => pod.LastTerminationReason is not null and not "Completed")
            ?.LastTerminationReason;

        string health = notReady == 0
            ? "all pods are ready"
            : $"{notReady.ToString(CultureInfo.InvariantCulture)} of "
              + $"{summary.Pods.Count.ToString(CultureInfo.InvariantCulture)} pods are not ready";

        return $"'{service}' in namespace '{ns}': {health}, "
               + $"{summary.ReadyReplicas.ToString(CultureInfo.InvariantCulture)} of "
               + $"{summary.DesiredReplicas.ToString(CultureInfo.InvariantCulture)} replicas available, "
               + $"{restarts.ToString(CultureInfo.InvariantCulture)} restarts total since pod creation"
               + (worstReason is null ? "." : $", most recent termination reason '{worstReason}'.");
    }
}

/// <summary>Adapter for <c>kubernetes.events</c>.</summary>
internal sealed class KubernetesEventsAdapter : IToolAdapter
{
    private readonly IKubernetesObservability _kubernetes;
    private readonly ObservabilityOptions _options;
    private readonly TimeProvider _timeProvider;

    public KubernetesEventsAdapter(
        IKubernetesObservability kubernetes,
        IOptions<ObservabilityOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _kubernetes = kubernetes;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string ToolName => "kubernetes.events";

    /// <inheritdoc />
    public async Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        string requested = ToolArguments.GetString(arguments, "namespace");
        int windowMinutes = ToolArguments.GetInt32(arguments, "window_minutes", 30);

        string cluster = context.Environment;
        string ns = _options.KubernetesNamespaceByCluster.TryGetValue(cluster, out string? configured)
            ? configured
            : requested;

        TimeWindow window = TimeWindow.EndingAt(
            _timeProvider.GetUtcNow(), TimeSpan.FromMinutes(windowMinutes));

        IReadOnlyList<KubernetesEvent> events = await _kubernetes.GetEventsAsync(
            new KubernetesScope(cluster, ns, context.Environment, new Dictionary<string, string>(StringComparer.Ordinal)),
            window,
            cancellationToken).ConfigureAwait(false);

        KubernetesEventsEnvelope envelope = new()
        {
            Namespace = ns,
            Events = [.. events.Select(static item => new KubernetesEventEnvelope(
                item.Type, item.Reason, item.Message, item.InvolvedObject, item.Count, item.LastSeenAt))],
        };

        return new ToolAdapterResult(
            Summary: events.Count == 0
                ? $"No events in namespace '{ns}' over the last "
                  + $"{windowMinutes.ToString(CultureInfo.InvariantCulture)} minutes."
                : $"{events.Count.ToString(CultureInfo.InvariantCulture)} events in namespace '{ns}', "
                  + $"most common reason '{events.GroupBy(static item => item.Reason)
                      .OrderByDescending(static group => group.Count()).First().Key}'.",
            ContentJson: ToolArguments.Serialize(
                envelope, ToolResultJsonContext.Default.KubernetesEventsEnvelope),
            SourceType: EvidenceSourceType.Kubernetes,
            Provider: "kubernetes",
            WindowStart: window.Start,
            WindowEnd: window.End);
    }
}

/// <summary>Adapter for <c>database.health</c>.</summary>
internal sealed class DatabaseHealthAdapter : IToolAdapter
{
    private readonly IDatabaseObservability _database;
    private readonly TimeProvider _timeProvider;

    public DatabaseHealthAdapter(IDatabaseObservability database, TimeProvider timeProvider)
    {
        _database = database;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string ToolName => "database.health";

    /// <inheritdoc />
    public async Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        string service = ToolArguments.GetString(arguments, "service");
        int windowMinutes = ToolArguments.GetInt32(arguments, "window_minutes", 30);

        TimeWindow window = TimeWindow.EndingAt(
            _timeProvider.GetUtcNow(), TimeSpan.FromMinutes(windowMinutes));

        DatabaseHealth health = await _database.GetHealthAsync(
            new DatabaseScope(service, context.Environment, service),
            window,
            cancellationToken).ConfigureAwait(false);

        return new ToolAdapterResult(
            Summary: SummarizeHealth(service, health),
            ContentJson: ToolArguments.Serialize(
                DatabaseEnvelope.From(service, health), ToolResultJsonContext.Default.DatabaseEnvelope),
            SourceType: EvidenceSourceType.Database,
            Provider: health.Provider,
            WindowStart: window.Start,
            WindowEnd: window.End);
    }

    /// <summary>
    /// Says whether the pool is exhausted or merely busy, because that is the
    /// distinction the whole investigation turns on and reading it off two
    /// numbers is exactly where a model goes wrong.
    /// </summary>
    private static string SummarizeHealth(string service, DatabaseHealth health)
    {
        ConnectionPoolState pool = health.Pool;

        string poolState = pool.WaitingClients > 0
            ? $"exhausted: {pool.ActiveConnections.ToString(CultureInfo.InvariantCulture)} of "
              + $"{pool.MaxConnections.ToString(CultureInfo.InvariantCulture)} connections in use with "
              + $"{pool.WaitingClients.ToString(CultureInfo.InvariantCulture)} clients queued"
            : $"healthy: {pool.ActiveConnections.ToString(CultureInfo.InvariantCulture)} of "
              + $"{pool.MaxConnections.ToString(CultureInfo.InvariantCulture)} connections in use with no "
              + "clients waiting";

        string slowest = health.SlowQueries.Count == 0
            ? "no slow query statistics available"
            : $"slowest statement averages "
              + $"{health.SlowQueries[0].MeanTime.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)}ms "
              + $"over {health.SlowQueries[0].Calls.ToString(CultureInfo.InvariantCulture)} calls";

        return $"'{service}' connection pool is {poolState}; {slowest}."
               + (health.ReplicationLag is { } lag
                   ? $" Replication lag {lag.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms."
                   : string.Empty);
    }
}
