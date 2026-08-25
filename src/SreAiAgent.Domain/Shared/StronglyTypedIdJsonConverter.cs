using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Serializes a strongly typed identifier as its bare GUID string, so wire
/// contracts stay identical to a plain <see cref="Guid"/> while the compiler
/// keeps the types apart.
/// </summary>
/// <typeparam name="TId">The identifier type.</typeparam>
public sealed class StronglyTypedIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <inheritdoc />
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TId.From(reader.GetGuid());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    /// <inheritdoc />
    public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TId.Parse(reader.GetString() ?? string.Empty, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value.ToString("D", CultureInfo.InvariantCulture));
}
