using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Domain.Shared;

/// <summary>Reads and writes a <see cref="Result{T}"/>.</summary>
/// <typeparam name="T">The success value type.</typeparam>
internal sealed class ValueResultJsonConverter<T> : JsonConverter<Result<T>>
{
    /// <inheritdoc />
    public override Result<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A result must be a JSON object.");
        }

        bool ok = false;
        T? value = default;
        Error? error = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals(ResultJsonConverter.PropertyNames.Ok.EncodedUtf8Bytes))
            {
                reader.Read();
                ok = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals(ResultJsonConverter.PropertyNames.Value.EncodedUtf8Bytes))
            {
                reader.Read();
                value = JsonSerializer.Deserialize<T>(ref reader, options);
            }
            else if (reader.ValueTextEquals(ResultJsonConverter.PropertyNames.Error.EncodedUtf8Bytes))
            {
                reader.Read();
                error = JsonSerializer.Deserialize<Error>(ref reader, options);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (ok)
        {
            return value is null
                ? throw new JsonException("A successful result must carry a value.")
                : Result<T>.Success(value);
        }

        return Result<T>.Failure(
            error ?? throw new JsonException("A failed result must carry an error."));
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Result<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteBoolean(ResultJsonConverter.PropertyNames.Ok, value.IsSuccess);

        if (value.IsSuccess)
        {
            writer.WritePropertyName(ResultJsonConverter.PropertyNames.Value);
            JsonSerializer.Serialize(writer, value.Value, options);
        }
        else
        {
            writer.WritePropertyName(ResultJsonConverter.PropertyNames.Error);
            JsonSerializer.Serialize(writer, value.Error, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Reads and writes a <see cref="Result"/>, which has no success value.</summary>
internal sealed class UnitResultJsonConverter : JsonConverter<Result>
{
    /// <inheritdoc />
    public override Result Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A result must be a JSON object.");
        }

        bool ok = false;
        Error? error = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals(ResultJsonConverter.PropertyNames.Ok.EncodedUtf8Bytes))
            {
                reader.Read();
                ok = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals(ResultJsonConverter.PropertyNames.Error.EncodedUtf8Bytes))
            {
                reader.Read();
                error = JsonSerializer.Deserialize<Error>(ref reader, options);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return ok
            ? Result.Success()
            : Result.Failure(error ?? throw new JsonException("A failed result must carry an error."));
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Result value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteBoolean(ResultJsonConverter.PropertyNames.Ok, value.IsSuccess);

        if (value.IsFailure)
        {
            writer.WritePropertyName(ResultJsonConverter.PropertyNames.Error);
            JsonSerializer.Serialize(writer, value.Error, options);
        }

        writer.WriteEndObject();
    }
}
