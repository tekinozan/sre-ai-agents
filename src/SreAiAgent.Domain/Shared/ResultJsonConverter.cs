using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Serializes <see cref="Result"/> and <see cref="Result{T}"/> as a tagged
/// object.
/// </summary>
/// <remarks>
/// The type keeps its branches private so neither can be read without checking
/// which one is set, and its Value and Error properties throw when asked for the
/// branch that is absent. A serializer walking those properties therefore does
/// not merely fail, it fails by throwing out of a getter, which is why this
/// converter exists rather than a set of public fields.
///
/// The wire shape is a discriminated object rather than a bare value, so a
/// failure carrying no value and a success carrying null stay distinguishable.
/// This matters wherever a durable engine replays an activity result: a workflow
/// that cannot tell "no value" from "failed" would resume down the wrong branch.
/// </remarks>
public sealed class ResultJsonConverter : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert == typeof(Result)
            || (typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Result<>));
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        if (typeToConvert == typeof(Result))
        {
            return new UnitResultJsonConverter();
        }

        Type valueType = typeToConvert.GetGenericArguments()[0];

        return (JsonConverter)Activator.CreateInstance(typeof(ValueResultJsonConverter<>).MakeGenericType(valueType))!;
    }

    internal static class PropertyNames
    {
        public static readonly JsonEncodedText Ok = JsonEncodedText.Encode("ok");
        public static readonly JsonEncodedText Value = JsonEncodedText.Encode("value");
        public static readonly JsonEncodedText Error = JsonEncodedText.Encode("error");
    }
}
