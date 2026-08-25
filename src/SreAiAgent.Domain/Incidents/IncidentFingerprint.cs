using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace SreAiAgent.Domain.Incidents;

/// <summary>
/// Derives the deduplication key for an alert (ARCHITECTURE.md 9.3). The
/// database enforces uniqueness on (source, fingerprint, active window), so this
/// value decides whether a second page for the same failure opens a second
/// incident or lands on the existing timeline.
/// </summary>
/// <remarks>
/// The caller chooses which labels are identity-bearing. That choice belongs to
/// the alert source adapter, which knows that `pod` is volatile for a Kubernetes
/// alert while `cluster` is not; folding every label in would make each pod
/// restart a new incident.
/// </remarks>
public static class IncidentFingerprint
{
    private const byte FieldSeparator = 0x1F;
    private const int StackThreshold = 256;

    /// <summary>
    /// Computes a stable, lowercase hex SHA-256 fingerprint over the normalized
    /// identity of an alert.
    /// </summary>
    /// <param name="source">The alert source, for example <c>alertmanager</c>.</param>
    /// <param name="service">The owning service.</param>
    /// <param name="environment">The environment, for example <c>production</c>.</param>
    /// <param name="alertName">The provider's alert or monitor name.</param>
    /// <param name="identityLabels">
    /// Labels that participate in identity. Order does not matter: they are
    /// sorted by ordinal key so two orderings of the same set agree.
    /// </param>
    public static string Compute(
        string source,
        string service,
        string environment,
        string alertName,
        IReadOnlyCollection<KeyValuePair<string, string>>? identityLabels = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendNormalized(hash, source);
        AppendNormalized(hash, service);
        AppendNormalized(hash, environment);
        AppendNormalized(hash, alertName);

        if (identityLabels is { Count: > 0 })
        {
            KeyValuePair<string, string>[] ordered = ArrayPool<KeyValuePair<string, string>>.Shared
                .Rent(identityLabels.Count);
            try
            {
                int count = 0;
                foreach (KeyValuePair<string, string> label in identityLabels)
                {
                    ordered[count++] = label;
                }

                Array.Sort(ordered, 0, count, LabelKeyComparer.Instance);

                for (int i = 0; i < count; i++)
                {
                    AppendNormalized(hash, ordered[i].Key);
                    AppendNormalized(hash, ordered[i].Value);
                }
            }
            finally
            {
                ArrayPool<KeyValuePair<string, string>>.Shared.Return(ordered, clearArray: true);
            }
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetCurrentHash(digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Feeds one trimmed, case-folded component into the hash followed by a
    /// separator. The separator prevents ("ab", "c") and ("a", "bc") colliding.
    /// </summary>
    private static void AppendNormalized(IncrementalHash hash, ReadOnlySpan<char> value)
    {
        ReadOnlySpan<char> trimmed = value.Trim();

        char[]? rentedChars = null;
        Span<char> folded = trimmed.Length <= StackThreshold
            ? stackalloc char[StackThreshold]
            : (rentedChars = ArrayPool<char>.Shared.Rent(trimmed.Length));

        byte[]? rentedBytes = null;
        try
        {
            int foldedLength = trimmed.ToLowerInvariant(folded);
            ReadOnlySpan<char> normalized = foldedLength < 0 ? trimmed : folded[..foldedLength];

            int maxBytes = Encoding.UTF8.GetMaxByteCount(normalized.Length);
            Span<byte> utf8 = maxBytes <= StackThreshold
                ? stackalloc byte[StackThreshold]
                : (rentedBytes = ArrayPool<byte>.Shared.Rent(maxBytes));

            int byteCount = Encoding.UTF8.GetBytes(normalized, utf8);
            hash.AppendData(utf8[..byteCount]);
            hash.AppendData([FieldSeparator]);
        }
        finally
        {
            if (rentedChars is not null)
            {
                ArrayPool<char>.Shared.Return(rentedChars);
            }

            if (rentedBytes is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedBytes);
            }
        }
    }

    private sealed class LabelKeyComparer : IComparer<KeyValuePair<string, string>>
    {
        public static readonly LabelKeyComparer Instance = new();

        public int Compare(KeyValuePair<string, string> x, KeyValuePair<string, string> y)
            => string.CompareOrdinal(x.Key, y.Key);
    }
}
