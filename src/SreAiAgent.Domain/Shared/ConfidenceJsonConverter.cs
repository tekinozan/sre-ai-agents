using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Serializes a <see cref="Confidence"/> as a bare JSON number.
/// </summary>
/// <remarks>
/// Without this converter the type round-trips to zero and says nothing about
/// it. <see cref="Confidence"/> keeps its constructor private so the [0, 1]
/// bound cannot be bypassed, and a serializer that cannot reach a constructor
/// falls back to creating the default struct and assigning properties; the only
/// property is get-only, so every score read back from a JSONB column or a
/// workflow payload would silently become zero. A confidence of zero on a report
/// is not a visible failure, it is a wrong answer that looks like a cautious one.
///
/// Reading clamps rather than throws. The stored value came from the platform
/// itself, so an out-of-range number means a hand-edited row or a schema change,
/// and refusing to render an entire report because one score reads 1.0000001 is
/// the wrong trade for a value that is already bounded by definition.
/// </remarks>
public sealed class ConfidenceJsonConverter : JsonConverter<Confidence>
{
    /// <inheritdoc />
    public override Confidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Confidence.Clamp(reader.GetDecimal());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Confidence value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
