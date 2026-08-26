using System.Text.Json.Serialization;

namespace SreAiAgent.Domain.Shared;

/// <summary>
/// A confidence score constrained to the closed interval [0, 1].
/// ARCHITECTURE.md 5.2 requires this to be validated at every boundary, so the
/// constraint is a type rather than a convention: an out-of-range value cannot
/// be represented, and a model that returns 1.7 is rejected at the edge.
/// </summary>
/// <remarks>
/// <see cref="decimal"/> matches the finding contract in ARCHITECTURE.md 7.3.
/// The values originate as JSON literals, and decimal round-trips them exactly,
/// which matters when a threshold comparison decides whether an agent result is
/// shown to an operator.
/// </remarks>
[JsonConverter(typeof(ConfidenceJsonConverter))]
public readonly record struct Confidence : IComparable<Confidence>
{
    public const decimal MinimumValue = 0m;
    public const decimal MaximumValue = 1m;

    private Confidence(decimal value) => Value = value;

    /// <summary>The score, guaranteed to be within [0, 1].</summary>
    public decimal Value { get; }

    /// <summary>No confidence at all.</summary>
    public static Confidence None => new(MinimumValue);

    /// <summary>Full confidence.</summary>
    public static Confidence Certain => new(MaximumValue);

    /// <summary>Creates a score, throwing when the value is outside [0, 1].</summary>
    /// <exception cref="DomainException">The value is outside the allowed range.</exception>
    public static Confidence From(decimal value)
        => TryFrom(value, out Confidence confidence)
            ? confidence
            : throw new DomainException(
                DomainErrorCodes.ConfidenceOutOfRange,
                $"Confidence must be within [{MinimumValue}, {MaximumValue}] but was {value.ToString(CultureInfo.InvariantCulture)}.");

    /// <summary>Creates a score without throwing. Use at untrusted boundaries.</summary>
    public static bool TryFrom(decimal value, out Confidence confidence)
    {
        if (value is < MinimumValue or > MaximumValue)
        {
            confidence = default;
            return false;
        }

        confidence = new Confidence(value);
        return true;
    }

    /// <summary>Clamps an arbitrary value into range instead of rejecting it.</summary>
    public static Confidence Clamp(decimal value)
        => new(Math.Clamp(value, MinimumValue, MaximumValue));

    public int CompareTo(Confidence other) => Value.CompareTo(other.Value);

    public static bool operator <(Confidence left, Confidence right) => left.CompareTo(right) < 0;

    public static bool operator <=(Confidence left, Confidence right) => left.CompareTo(right) <= 0;

    public static bool operator >(Confidence left, Confidence right) => left.CompareTo(right) > 0;

    public static bool operator >=(Confidence left, Confidence right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture);
}
