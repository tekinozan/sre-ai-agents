using System.Text.Json;
using System.Text.Json.Serialization;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>
/// Rebuilds an <see cref="Observation"/> through its factory methods rather than
/// through its fields.
/// </summary>
/// <remarks>
/// <see cref="Observation"/> has no public constructor on purpose: the only way
/// to obtain a verified observation is to supply evidence for it. That is also
/// why a serializer cannot rebuild one unaided, and the failure is not subtle
/// (deserialization simply throws), so a stored finding could never be read back
/// at all. Correlation reads every prior finding through this path.
///
/// The reconstruction deliberately re-applies the invariant instead of restoring
/// whatever the document claimed. A row asserting <c>is_verified</c> with no
/// citations is downgraded, because every consumer downstream reads
/// <see cref="Observation.IsVerified"/> as a promise that evidence exists, and a
/// hand-edited row or an older schema must not be able to forge that promise.
///
/// Property names are written in snake_case to match the naming policy of the
/// contexts this converter is used from. A converter owns its own wire shape;
/// the policy on the surrounding options does not reach inside it.
/// </remarks>
public sealed class ObservationJsonConverter : JsonConverter<Observation>
{
    private static readonly JsonEncodedText StatementName = JsonEncodedText.Encode("statement");
    private static readonly JsonEncodedText EvidenceIdsName = JsonEncodedText.Encode("evidence_ids");
    private static readonly JsonEncodedText IsVerifiedName = JsonEncodedText.Encode("is_verified");

    /// <inheritdoc />
    public override Observation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("An observation must be a JSON object.");
        }

        string? statement = null;
        List<EvidenceId>? evidenceIds = null;
        bool isVerified = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals(StatementName.EncodedUtf8Bytes))
            {
                reader.Read();
                statement = reader.GetString();
            }
            else if (reader.ValueTextEquals(EvidenceIdsName.EncodedUtf8Bytes))
            {
                reader.Read();
                evidenceIds = ReadEvidenceIds(ref reader);
            }
            else if (reader.ValueTextEquals(IsVerifiedName.EncodedUtf8Bytes))
            {
                reader.Read();
                isVerified = reader.GetBoolean();
            }
            else
            {
                // An unknown member from a newer writer. Skipping it keeps a
                // rolling deployment readable in both directions.
                reader.Read();
                reader.Skip();
            }
        }

        if (string.IsNullOrWhiteSpace(statement))
        {
            throw new JsonException("An observation must carry a statement.");
        }

        return isVerified && evidenceIds is { Count: > 0 }
            ? Observation.Verified(statement, evidenceIds)
            : Observation.Unverified(statement);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Observation value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString(StatementName, value.Statement);

        writer.WritePropertyName(EvidenceIdsName);
        writer.WriteStartArray();
        foreach (EvidenceId evidenceId in value.EvidenceIds)
        {
            writer.WriteStringValue(evidenceId.Value);
        }

        writer.WriteEndArray();

        writer.WriteBoolean(IsVerifiedName, value.IsVerified);
        writer.WriteEndObject();
    }

    private static List<EvidenceId> ReadEvidenceIds(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Evidence ids must be a JSON array.");
        }

        List<EvidenceId> evidenceIds = [];

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            evidenceIds.Add(EvidenceId.From(reader.GetGuid()));
        }

        return evidenceIds;
    }
}
