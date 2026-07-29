using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Wire-contract tests for <see cref="DocumentJson"/> (Story 39-2 AC6): the exact
/// serialized property-name set, lossless round-trip, millisecond-ISO timestamp
/// format, wire-string state, forward compatibility (unknown extra field), and
/// strict rejection of missing required fields.
/// </summary>
[TestFixture]
public class DocumentEnvelopeSerializationTests
{
    private static readonly Regex Iso8601Ms = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");

    private static DocumentEnvelope FullyPopulated()
    {
        using var doc = JsonDocument.Parse("{\"summary\":\"a decomposition\",\"count\":3}");
        var producer = DocumentProducer.Create("senior_developer", "decompose-issue", "issue-decomposition");
        return DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition,
            schemaVersion: 2,
            issueId: "issue-42",
            correlationId: "corr-7",
            producedBy: producer,
            payload: doc.RootElement.Clone(),
            parentDocumentId: UuidV7.NewGuid(),
            supersedesDocumentId: UuidV7.NewGuid(),
            now: DateTimeOffset.Parse("2026-07-21T12:34:56.789Z"));
    }

    [Test]
    public void Serialized_property_names_are_the_exact_wire_contract()
    {
        var json = DocumentJson.Serialize(FullyPopulated());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        names.Should().BeEquivalentTo(new[]
        {
            // "audience" joined the wire contract in Story 41-1c (nullable — null
            // for every non-prose document, serialized explicitly per D8).
            "id", "type", "schemaVersion", "audience", "issueId", "correlationId",
            "parentDocumentId", "supersedesDocumentId", "producedBy",
            "state", "createdAt", "updatedAt", "payload",
        });

        var producedBy = root.GetProperty("producedBy");
        producedBy.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "role", "action", "workflow" });
    }

    [Test]
    public void State_serializes_as_its_wire_string_not_the_enum_name_or_number()
    {
        var json = DocumentJson.Serialize(FullyPopulated());
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("state").GetString().Should().Be("draft");
    }

    [Test]
    public void Timestamps_serialize_as_millisecond_iso8601_utc()
    {
        var json = DocumentJson.Serialize(FullyPopulated());
        using var doc = JsonDocument.Parse(json);

        var createdAt = doc.RootElement.GetProperty("createdAt").GetString();
        createdAt.Should().MatchRegex(Iso8601Ms.ToString());
        createdAt.Should().Be("2026-07-21T12:34:56.789Z");
    }

    [Test]
    public void Round_trip_loses_nothing()
    {
        var original = FullyPopulated();
        var json = DocumentJson.Serialize(original);
        var deserialized = DocumentJson.Deserialize(json);

        deserialized.Should().Be(original);
        // Payload compared explicitly via raw text (JsonElement has no value equality).
        deserialized.Payload.GetRawText().Should().Be(original.Payload.GetRawText());
    }

    [Test]
    public void Unknown_extra_field_is_tolerated_on_read()
    {
        var json = DocumentJson.Serialize(FullyPopulated());
        using var doc = JsonDocument.Parse(json);
        // Inject a forward-compatible unknown field at the object root.
        var withExtra = AddProperty(doc.RootElement, "futureField", w => w.WriteNumberValue(1));

        var act = () => DocumentJson.Deserialize(withExtra);
        act.Should().NotThrow();
    }

    [Test]
    public void Missing_required_issue_id_throws()
    {
        var json = DocumentJson.Serialize(FullyPopulated());
        using var doc = JsonDocument.Parse(json);
        var stripped = StripProperty(doc.RootElement, "issueId");

        var act = () => DocumentJson.Deserialize(stripped);
        act.Should().Throw<JsonException>();
    }

    [Test]
    public void Missing_required_type_throws()
    {
        var json = DocumentJson.Serialize(FullyPopulated());
        using var doc = JsonDocument.Parse(json);
        var stripped = StripProperty(doc.RootElement, "type");

        var act = () => DocumentJson.Deserialize(stripped);
        act.Should().Throw<JsonException>();
    }

    private static string AddProperty(JsonElement obj, string name, Action<Utf8JsonWriter> writeValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in obj.EnumerateObject())
                prop.WriteTo(writer);
            writer.WritePropertyName(name);
            writeValue(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string StripProperty(JsonElement obj, string name)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.NameEquals(name)) continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
