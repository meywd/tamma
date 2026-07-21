using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents;

/// <summary>
/// The single canonical <see cref="JsonSerializerOptions"/> for document
/// envelopes (Design Decision D8). Because every wire property carries an
/// explicit <c>[JsonPropertyName]</c>, no naming policy is applied; the options
/// only wire the two converters:
/// <list type="bullet">
/// <item><see cref="WireEnumJsonConverter{TEnum}"/> for <see cref="DocumentState"/>
/// (serializes as its <c>[Wire]</c> string).</item>
/// <item><see cref="MillisecondIso8601Converter"/> for timestamps
/// (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>).</item>
/// </list>
/// Every caller serializes/deserializes through <see cref="Serialize"/> /
/// <see cref="Deserialize"/> so the contract is uniform.
/// </summary>
public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            // Explicit [JsonPropertyName] everywhere makes any naming policy
            // irrelevant (D8); leave it unset so nothing is inferred.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new WireEnumJsonConverter<DocumentState>());
        options.Converters.Add(new MillisecondIso8601Converter());
        return options;
    }

    /// <summary>Serialize an envelope with the canonical options.</summary>
    public static string Serialize(DocumentEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    /// <summary>Deserialize an envelope with the canonical options.</summary>
    /// <exception cref="JsonException">
    /// Missing required properties (e.g. <c>issueId</c>, <c>type</c>), an unknown
    /// state wire string, or otherwise malformed JSON.
    /// </exception>
    public static DocumentEnvelope Deserialize(string json) =>
        JsonSerializer.Deserialize<DocumentEnvelope>(json, Options)
        ?? throw new JsonException("Document envelope JSON deserialized to null.");
}

/// <summary>
/// Serializes a <c>[Wire]</c>-mapped enum as its canonical wire string, and reads
/// it back case-sensitively. Throws <see cref="JsonException"/> on a non-wire
/// string so a bad token fails loud on the boundary (D8).
/// </summary>
internal sealed class WireEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var wire = reader.GetString();
        if (wire is not null && EnumWire<TEnum>.TryParse(wire, out var value))
            return value;

        throw new JsonException($"Unknown wire value '{wire}' for enum {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EnumWire<TEnum>.ToWire(value));
}

/// <summary>
/// Serializes <see cref="DateTimeOffset"/> as UTC ISO 8601 with millisecond
/// precision (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>) and parses it back to a UTC offset
/// (D8).
/// </summary>
internal sealed class MillisecondIso8601Converter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is null)
            throw new JsonException("Expected an ISO 8601 timestamp string, got null.");

        return DateTimeOffset.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
