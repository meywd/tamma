using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b AC2 (UxSpec) — one rejecting and one accepting fixture per rule,
/// each asserting the named violation code. The cross-document
/// acceptance-criteria mapping rule (D5) is covered in
/// <see cref="DocumentTypesCrossDocumentValidationTests"/>.
/// </summary>
[TestFixture]
public class UxSpecTypeTests
{
    private static readonly UxSpecDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    private const string ValidDoc =
        """
        {
          "flows": [
            {
              "id": "F1",
              "name": "sign in",
              "entryState": "signed-out landing",
              "successState": "dashboard",
              "errorStates": ["invalid credentials banner"],
              "acceptanceCriteriaRefs": ["AC-1"]
            }
          ],
          "screens": [
            { "id": "S1", "flowRef": "F1", "a11yRequirements": ["inputs labelled for screen readers"] }
          ]
        }
        """;

    [Test]
    public void Valid_document_passes_every_rule()
    {
        var r = Validate(ValidDoc);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Malformed_payload_is_reported() =>
        Codes(Validate("""{ "flows": "none" }""")).Should().Contain(UxSpecDocumentType.MalformedPayload);

    // ── NO_FLOWS ────────────────────────────────────────────────────────────

    [Test]
    public void Empty_flow_set_is_reported() =>
        Codes(Validate("""{ "flows": [], "screens": [] }""")).Should().Contain(UxSpecDocumentType.NoFlows);

    [Test]
    public void Non_empty_flow_set_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(UxSpecDocumentType.NoFlows);

    // ── FLOW_MISSING_ENTRY_STATE / SUCCESS_STATE / ERROR_STATE ──────────────

    [Test]
    public void Flow_without_entry_state_is_reported()
    {
        var r = Validate(ValidDoc.Replace("signed-out landing", " "));
        Codes(r).Should().Contain(UxSpecDocumentType.FlowMissingEntryState);
    }

    [Test]
    public void Flow_without_success_state_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"successState\": \"dashboard\"", "\"successState\": \"\""));
        Codes(r).Should().Contain(UxSpecDocumentType.FlowMissingSuccessState);
    }

    [Test]
    public void Flow_without_error_states_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"errorStates\": [\"invalid credentials banner\"]", "\"errorStates\": []"));
        Codes(r).Should().Contain(UxSpecDocumentType.FlowMissingErrorState);
    }

    [Test]
    public void Fully_stated_flow_is_accepted()
    {
        var codes = Codes(Validate(ValidDoc));
        codes.Should().NotContain(UxSpecDocumentType.FlowMissingEntryState);
        codes.Should().NotContain(UxSpecDocumentType.FlowMissingSuccessState);
        codes.Should().NotContain(UxSpecDocumentType.FlowMissingErrorState);
    }

    // ── SCREEN_UNKNOWN_FLOW ─────────────────────────────────────────────────

    [Test]
    public void Screen_referencing_undeclared_flow_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"flowRef\": \"F1\"", "\"flowRef\": \"F9\""));
        Codes(r).Should().Contain(UxSpecDocumentType.ScreenUnknownFlow);
    }

    [Test]
    public void Screen_referencing_declared_flow_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(UxSpecDocumentType.ScreenUnknownFlow);

    // ── SCREEN_MISSING_A11Y_REQUIREMENTS ────────────────────────────────────

    [Test]
    public void Screen_without_a11y_requirements_is_reported()
    {
        var r = Validate(ValidDoc.Replace(
            "\"a11yRequirements\": [\"inputs labelled for screen readers\"]", "\"a11yRequirements\": []"));
        Codes(r).Should().Contain(UxSpecDocumentType.ScreenMissingA11yRequirements);
    }

    [Test]
    public void Screen_with_a11y_requirements_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(UxSpecDocumentType.ScreenMissingA11yRequirements);

    // ── Context-free Validate never emits the cross-document code ───────────

    [Test]
    public void Payload_only_validate_never_reports_unmapped_flows()
    {
        var withoutRefs = ValidDoc.Replace(", \"acceptanceCriteriaRefs\": [\"AC-1\"]", "")
            .Replace("\"acceptanceCriteriaRefs\": [\"AC-1\"]", "\"acceptanceCriteriaRefs\": []");
        var r = Validate(withoutRefs);
        Codes(r).Should().NotContain(UxSpecDocumentType.FlowUnmappedToAcceptanceCriterion);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    // ── shared contract properties ──────────────────────────────────────────

    [Test]
    public void Contract_is_deterministic()
    {
        var first = Type.RenderContract();
        Type.RenderContract().Should().Be(first);
        first.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Typed_record_round_trips_through_document_json()
    {
        var doc = JsonSerializer.Deserialize<UxSpec>(ValidDoc, DocumentJson.Options)!;
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options);
        var back = JsonSerializer.Deserialize<UxSpec>(json, DocumentJson.Options)!;
        back.Should().BeEquivalentTo(doc);
        using var parsed = JsonDocument.Parse(json);
        Type.Validate(parsed.RootElement).IsValid.Should().BeTrue("the re-serialized shape must still validate");
    }
}
