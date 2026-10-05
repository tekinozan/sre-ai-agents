using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>The position after which the next page of a keyset-paged list starts.</summary>
/// <remarks>
/// Opaque to callers. The encoding carries the instant as UTC ticks so the
/// round trip is exact; a formatted timestamp would lose the sub-millisecond
/// part PostgreSQL stores and skip or repeat rows that share a millisecond.
/// </remarks>
public sealed record PageCursor(DateTimeOffset At, Guid Id)
{
    public string Encode()
    {
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{At.UtcTicks}:{Id:N}");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? value, [NotNullWhen(true)] out PageCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');

        Span<byte> bytes = stackalloc byte[96];
        if (!Convert.TryFromBase64String(padded, bytes, out int written))
        {
            return false;
        }

        string raw = Encoding.ASCII.GetString(bytes[..written]);
        int separator = raw.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !long.TryParse(raw.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(raw.AsSpan(separator + 1), "N", out Guid id))
        {
            return false;
        }

        cursor = new PageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }
}
