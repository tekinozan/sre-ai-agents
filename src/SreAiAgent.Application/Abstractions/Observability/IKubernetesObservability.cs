namespace SreAiAgent.Application.Abstractions.Observability;

/// <summary>
/// Where in a cluster to look.
/// </summary>
/// <remarks>
/// Namespace is required rather than optional. The service account backing this
/// port is namespace-scoped and read-only (ARCHITECTURE.md 15.2); making the
/// scope mandatory in the contract keeps the code honest about that instead of
/// implying a cluster-wide query that would fail at the API server.
/// </remarks>
/// <param name="Cluster">Cluster key from configuration, never a kubeconfig path.</param>
/// <param name="Namespace">Namespace to inspect.</param>
/// <param name="Environment">Environment the cluster belongs to.</param>
/// <param name="LabelSelector">Optional label filters, as key/value pairs.</param>
public sealed record KubernetesScope(
    string Cluster,
    string Namespace,
    string Environment,
    IReadOnlyDictionary<string, string> LabelSelector);

/// <summary>State of one pod, reduced to what an investigation actually uses.</summary>
/// <param name="Name">Pod name.</param>
/// <param name="Phase">Lifecycle phase, for example <c>Running</c>.</param>
/// <param name="IsReady">Whether all readiness probes pass.</param>
/// <param name="RestartCount">Restarts since creation. A crash loop shows up here.</param>
/// <param name="LastTerminationReason">Why the last container exited, for example <c>OOMKilled</c>.</param>
/// <param name="StartedAt">When the pod started, UTC.</param>
/// <param name="NodeName">Node it is scheduled on.</param>
public sealed record PodState(
    string Name,
    string Phase,
    bool IsReady,
    int RestartCount,
    string? LastTerminationReason,
    DateTimeOffset? StartedAt,
    string? NodeName);

/// <summary>Aggregate pod health for a scope.</summary>
/// <param name="Pods">Individual pods, capped by the adapter.</param>
/// <param name="DesiredReplicas">Replicas the workload asks for.</param>
/// <param name="ReadyReplicas">Replicas currently passing readiness.</param>
/// <param name="IsTruncated">True when the pod list was capped.</param>
/// <param name="Provider">Backend that answered.</param>
public sealed record PodSummary(
    IReadOnlyList<PodState> Pods,
    int DesiredReplicas,
    int ReadyReplicas,
    bool IsTruncated,
    string Provider);

/// <summary>
/// Read-only Kubernetes inspection (ARCHITECTURE.md 6.3).
/// </summary>
/// <remarks>
/// There is no method here that mutates anything, and no method that takes a raw
/// manifest or command. Arbitrary kubectl execution is banned by section 15.2,
/// and a port that cannot express it is a stronger guarantee than a policy that
/// forbids it.
/// </remarks>
public interface IKubernetesObservability
{
    Task<PodSummary> GetPodsAsync(KubernetesScope scope, CancellationToken cancellationToken);

    /// <summary>Recent warning-level events, which usually explain a failing pod.</summary>
    Task<IReadOnlyList<KubernetesEvent>> GetEventsAsync(
        KubernetesScope scope,
        TimeWindow window,
        CancellationToken cancellationToken);
}

/// <summary>A cluster event.</summary>
/// <param name="Type">Event type, for example <c>Warning</c>.</param>
/// <param name="Reason">Short reason, for example <c>FailedScheduling</c>.</param>
/// <param name="Message">Detail. Untrusted data, like any retrieved content.</param>
/// <param name="InvolvedObject">The object it concerns.</param>
/// <param name="Count">How many times it repeated.</param>
/// <param name="LastSeenAt">Most recent occurrence, UTC.</param>
public sealed record KubernetesEvent(
    string Type,
    string Reason,
    string Message,
    string InvolvedObject,
    int Count,
    DateTimeOffset LastSeenAt);
