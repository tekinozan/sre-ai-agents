using System.Security.Cryptography;
using System.Text;

namespace SreAiAgent.Infrastructure.Messaging;

/// <summary>
/// Derives deterministic identifiers for dead-letter records.
/// </summary>
/// <remarks>
/// A retryable dead-letter record must be idempotent: a crash after publish
/// and before source-offset commit republishes the same record. Deriving a
/// deterministic UUID from (topic, partition, offset, consumer) ensures that
/// any downstream subscriber or replay tool can deduplicate dead-letter records
/// by event identity.
/// </remarks>
internal static class DeadLetterIdentity
{
    public static Guid CreateDeterministicEventId(
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        string consumerName)
    {
        ArgumentNullException.ThrowIfNull(sourceTopic);
        ArgumentNullException.ThrowIfNull(consumerName);

        string canonical = $"{sourceTopic}|{sourcePartition.ToString(CultureInfo.InvariantCulture)}|{sourceOffset.ToString(CultureInfo.InvariantCulture)}|{consumerName}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        Span<byte> uuidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(uuidBytes);

        // RFC 9562 / RFC 4122 name-based UUID:
        // Set version bits to 5 (0101 in high 4 bits of time_hi_and_version / byte 6)
        uuidBytes[6] = (byte)((uuidBytes[6] & 0x0F) | 0x50);
        // Set variant bits to 2 (10xx in high 2 bits of clock_seq_hi_and_reserved / byte 8)
        uuidBytes[8] = (byte)((uuidBytes[8] & 0x3F) | 0x80);

        return new Guid(uuidBytes, bigEndian: true);
    }

    /// <summary>
    /// Derives a deterministic correlation ID formatted as a 32-digit hex string from source identity.
    /// </summary>
    public static string CreateDeterministicCorrelationId(
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        string consumerName)
        => CreateDeterministicEventId(sourceTopic, sourcePartition, sourceOffset, consumerName)
            .ToString("N", CultureInfo.InvariantCulture);
}
