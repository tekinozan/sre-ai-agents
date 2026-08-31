using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Observability.Providers.Kubernetes;

/// <summary>
/// Reads pods and events from the API server of the cluster it runs in.
/// </summary>
/// <remarks>
/// Read-only by construction: the port has no method that mutates anything and
/// no method that accepts a manifest or a command, so arbitrary kubectl
/// execution is not something this adapter declines — it is something it cannot
/// express (ARCHITECTURE.md 15.2).
///
/// Only the investigation worker mounts a service account token, so only that
/// host can select this provider. The other two would fail at the API server,
/// which is the right outcome but a late one; the namespace check below fails
/// earlier and says why.
/// </remarks>
internal sealed class KubernetesObservability : IKubernetesObservability
{
    private const string ProviderName = "kubernetes";

    private readonly IKubernetes _client;
    private readonly ObservabilityOptions _options;

    public KubernetesObservability(IKubernetes client, IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<PodSummary> GetPodsAsync(KubernetesScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        (string ns, string selector) = Resolve(scope);

        V1PodList pods = await _client.CoreV1
            .ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        List<PodState> states = [];
        foreach (V1Pod pod in pods.Items.Take(_options.MaxKubernetesItems))
        {
            states.Add(ToPodState(pod));
        }

        // Desired replicas belong to the workload, not to the pods: a deployment
        // scaled to five with two crash-looping has five desired and two pods
        // that exist. Counting pods would report the outage as the intent.
        V1DeploymentList deployments = await _client.AppsV1
            .ListNamespacedDeploymentAsync(ns, labelSelector: selector, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        int desired = deployments.Items.Count > 0
            ? deployments.Items.Sum(static deployment => deployment.Spec?.Replicas ?? 0)
            : states.Count;

        return new PodSummary(
            states,
            desired,
            states.Count(static pod => pod.IsReady),
            pods.Items.Count > _options.MaxKubernetesItems,
            ProviderName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KubernetesEvent>> GetEventsAsync(
        KubernetesScope scope,
        TimeWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        (string ns, _) = Resolve(scope);

        // Warnings only. Normal events are almost entirely scheduling noise, and
        // in a window wide enough to be useful they would crowd the events that
        // explain a failure out of the prompt.
        Corev1EventList events = await _client.CoreV1
            .ListNamespacedEventAsync(ns, fieldSelector: "type=Warning", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        List<KubernetesEvent> matched = [];

        foreach (Corev1Event item in events.Items)
        {
            DateTime? seen = item.LastTimestamp ?? item.EventTime ?? item.FirstTimestamp;
            if (seen is null)
            {
                continue;
            }

            DateTimeOffset lastSeen = new(DateTime.SpecifyKind(seen.Value, DateTimeKind.Utc));
            if (lastSeen < window.Start || lastSeen > window.End)
            {
                continue;
            }

            matched.Add(new KubernetesEvent(
                item.Type ?? "Warning",
                item.Reason ?? "Unknown",
                item.Message ?? string.Empty,
                $"{item.InvolvedObject?.Kind}/{item.InvolvedObject?.Name}",
                item.Count ?? 1,
                lastSeen));
        }

        matched.Sort(static (left, right) => right.LastSeenAt.CompareTo(left.LastSeenAt));

        return matched.Count > _options.MaxKubernetesItems
            ? matched[.._options.MaxKubernetesItems]
            : matched;
    }

    private static PodState ToPodState(V1Pod pod)
    {
        IList<V1ContainerStatus> containers = pod.Status?.ContainerStatuses ?? [];

        // The reason the last container exited is the single most useful field
        // here: OOMKilled and a restart count together explain a crash loop
        // without needing logs at all.
        string? lastTermination = containers
            .Select(static container => container.LastState?.Terminated?.Reason)
            .FirstOrDefault(static reason => !string.IsNullOrWhiteSpace(reason));

        bool ready = pod.Status?.Conditions?.Any(static condition =>
            string.Equals(condition.Type, "Ready", StringComparison.Ordinal)
            && string.Equals(condition.Status, "True", StringComparison.Ordinal)) ?? false;

        return new PodState(
            pod.Metadata?.Name ?? "unknown",
            pod.Status?.Phase ?? "Unknown",
            ready,
            containers.Sum(static container => container.RestartCount),
            lastTermination,
            pod.Status?.StartTime is { } started
                ? new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc))
                : null,
            pod.Spec?.NodeName);
    }

    private (string Namespace, string Selector) Resolve(KubernetesScope scope)
        => (KubernetesSelector.ResolveNamespace(
                _options.KubernetesNamespaceByCluster, scope.Cluster, scope.Namespace),
            KubernetesSelector.Build(scope.LabelSelector));
}
