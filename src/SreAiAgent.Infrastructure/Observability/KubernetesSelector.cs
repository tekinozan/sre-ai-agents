using System.Text;

namespace SreAiAgent.Infrastructure.Observability;

/// <summary>
/// Builds the label selector for a Kubernetes list call.
/// </summary>
/// <remarks>
/// Separated from the adapter for the same reason as <see cref="PromQuery"/>:
/// it is where values a model supplied become part of a request, so it should be
/// readable on its own and testable without a cluster.
///
/// The difference from PromQL matters. A label selector has no quoting and no
/// escape sequence — <c>a=b,c=d</c> is the entire grammar — so a value carrying a
/// comma cannot be made safe, only rejected. Everything here therefore validates
/// against the identifier rules the API server itself enforces rather than trying
/// to neutralise anything.
/// </remarks>
internal static class KubernetesSelector
{
    private const int MaxNameLength = 63;

    /// <summary>
    /// Renders <c>key=value,key2=value2</c>, in a stable order.
    /// </summary>
    /// <exception cref="ObservabilityQueryException">
    /// A key or value is not a legal Kubernetes label.
    /// </exception>
    public static string Build(IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        StringBuilder selector = new(64);

        foreach ((string key, string value) in labels.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            // A key that carried a comma would add a second requirement; one
            // carrying '!' or "notin" would invert the meaning of the selector.
            // Neither can be escaped away, so neither is accepted.
            if (!IsLegalKey(key))
            {
                throw new ObservabilityQueryException(
                    $"Label selector key '{key}' is not a legal Kubernetes label name.");
            }

            if (!IsLegalValue(value))
            {
                throw new ObservabilityQueryException(
                    $"Label selector value for '{key}' is not a legal Kubernetes label value.");
            }

            if (selector.Length > 0)
            {
                selector.Append(',');
            }

            selector.Append(key).Append('=').Append(value);
        }

        return selector.ToString();
    }

    /// <summary>
    /// Confirms the scope names a namespace this deployment is allowed to read.
    /// </summary>
    /// <remarks>
    /// The service account is namespace-scoped, so a request outside it would
    /// fail at the API server anyway — but it would fail as a forbidden response
    /// an agent sees as "no pods", which is evidence. Refusing here keeps the
    /// boundary a deliberate configuration decision rather than a side effect of
    /// how the token happens to be bound.
    /// </remarks>
    public static string ResolveNamespace(
        IReadOnlyDictionary<string, string> namespaceByCluster,
        string cluster,
        string requested)
    {
        ArgumentNullException.ThrowIfNull(namespaceByCluster);

        if (!namespaceByCluster.TryGetValue(cluster, out string? allowed))
        {
            throw new ObservabilityQueryException(
                $"Cluster '{cluster}' is not configured in Observability:KubernetesNamespaceByCluster.");
        }

        if (!string.IsNullOrWhiteSpace(requested)
            && !string.Equals(requested, allowed, StringComparison.Ordinal))
        {
            throw new ObservabilityQueryException(
                $"Namespace '{requested}' is outside the namespace configured for cluster '{cluster}'.");
        }

        return allowed;
    }

    // A label key may carry an optional DNS subdomain prefix before a '/'.
    private static bool IsLegalKey(string key)
    {
        int slash = key.IndexOf('/', StringComparison.Ordinal);

        if (slash < 0)
        {
            return IsLegalName(key);
        }

        return slash > 0
            && IsLegalName(key[..slash])
            && IsLegalName(key[(slash + 1)..]);
    }

    // An empty value is legal and means "the label is present and empty".
    private static bool IsLegalValue(string value) => value.Length == 0 || IsLegalName(value);

    private static bool IsLegalName(string name)
    {
        if (name.Length is 0 or > MaxNameLength
            || !char.IsAsciiLetterOrDigit(name[0])
            || !char.IsAsciiLetterOrDigit(name[^1]))
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
