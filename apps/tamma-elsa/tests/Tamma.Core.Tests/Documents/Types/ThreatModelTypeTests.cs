using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b AC2 (ThreatModel) — one rejecting and one accepting fixture per
/// rule, each asserting the named violation code. The story's own named
/// counter-example (an unmitigated high-risk threat with no escalation) is the
/// <c>UNMITIGATED_HIGH_RISK_WITHOUT_ESCALATION</c> case.
/// </summary>
[TestFixture]
public class ThreatModelTypeTests
{
    private static readonly ThreatModelDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    private const string ValidDoc =
        """
        {
          "assets": [ { "id": "A1", "name": "tenant connection strings" } ],
          "threats": [
            {
              "id": "T1",
              "assetRef": "A1",
              "category": "information-disclosure",
              "description": "A log statement could leak a connection string.",
              "mitigation": "Encrypted at rest; redacted by the log sanitizer.",
              "residualRisk": "low"
            }
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
        Codes(Validate("""{ "assets": "none" }""")).Should().Contain(ThreatModelDocumentType.MalformedPayload);

    // ── NO_ASSETS / NO_THREATS ──────────────────────────────────────────────

    [Test]
    public void Empty_assets_are_reported()
    {
        var r = Validate("""{ "assets": [], "threats": [] }""");
        Codes(r).Should().Contain(ThreatModelDocumentType.NoAssets);
        Codes(r).Should().Contain(ThreatModelDocumentType.NoThreats);
    }

    [Test]
    public void Declared_assets_and_threats_are_accepted()
    {
        var codes = Codes(Validate(ValidDoc));
        codes.Should().NotContain(ThreatModelDocumentType.NoAssets);
        codes.Should().NotContain(ThreatModelDocumentType.NoThreats);
    }

    // ── THREAT_UNKNOWN_ASSET ────────────────────────────────────────────────

    [Test]
    public void Threat_referencing_undeclared_asset_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"assetRef\": \"A1\"", "\"assetRef\": \"A9\""));
        Codes(r).Should().Contain(ThreatModelDocumentType.ThreatUnknownAsset);
    }

    [Test]
    public void Threat_referencing_declared_asset_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(ThreatModelDocumentType.ThreatUnknownAsset);

    // ── THREAT_CATEGORY_OUT_OF_VOCABULARY ───────────────────────────────────

    [Test]
    public void Out_of_vocabulary_category_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"category\": \"information-disclosure\"", "\"category\": \"phishing\""));
        Codes(r).Should().Contain(ThreatModelDocumentType.ThreatCategoryOutOfVocabulary);
    }

    [Test]
    public void Stride_categories_are_accepted()
    {
        foreach (var wire in new[]
                 {
                     "spoofing", "tampering", "repudiation", "information-disclosure",
                     "denial-of-service", "elevation-of-privilege",
                 })
        {
            var r = Validate(ValidDoc.Replace("\"category\": \"information-disclosure\"", $"\"category\": \"{wire}\""));
            Codes(r).Should().NotContain(ThreatModelDocumentType.ThreatCategoryOutOfVocabulary, wire);
        }
    }

    // ── THREAT_MISSING_MITIGATION ───────────────────────────────────────────

    [Test]
    public void Threat_without_mitigation_is_reported()
    {
        var r = Validate(ValidDoc.Replace("Encrypted at rest; redacted by the log sanitizer.", " "));
        Codes(r).Should().Contain(ThreatModelDocumentType.ThreatMissingMitigation);
    }

    [Test]
    public void Mitigated_threat_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(ThreatModelDocumentType.ThreatMissingMitigation);

    // ── RESIDUAL_RISK_OUT_OF_VOCABULARY ─────────────────────────────────────

    [Test]
    public void Out_of_vocabulary_residual_risk_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"residualRisk\": \"low\"", "\"residualRisk\": \"severe\""));
        Codes(r).Should().Contain(ThreatModelDocumentType.ResidualRiskOutOfVocabulary);
    }

    [Test]
    public void Closed_residual_risk_levels_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(ThreatModelDocumentType.ResidualRiskOutOfVocabulary);

    // ── UNMITIGATED_HIGH_RISK_WITHOUT_ESCALATION (the story's counter-example) ──

    [Test]
    public void High_residual_risk_without_escalation_is_rejected()
    {
        var r = Validate(ValidDoc.Replace("\"residualRisk\": \"low\"", "\"residualRisk\": \"high\""));
        r.IsValid.Should().BeFalse();
        Codes(r).Should().Contain(ThreatModelDocumentType.UnmitigatedHighRiskWithoutEscalation);
    }

    [Test]
    public void Critical_residual_risk_without_escalation_is_rejected()
    {
        var r = Validate(ValidDoc.Replace("\"residualRisk\": \"low\"", "\"residualRisk\": \"critical\""));
        Codes(r).Should().Contain(ThreatModelDocumentType.UnmitigatedHighRiskWithoutEscalation);
    }

    [Test]
    public void High_residual_risk_with_escalation_is_accepted()
    {
        var withEscalation = ValidDoc
            .Replace("\"residualRisk\": \"low\"", "\"residualRisk\": \"high\"")
            .Replace(
                "\"threats\": [",
                "\"escalation\": \"Escalated to the security owner: pooled-role isolation gap.\",\n  \"threats\": [");
        var r = Validate(withEscalation);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Low_residual_risk_needs_no_escalation() =>
        Codes(Validate(ValidDoc)).Should().NotContain(ThreatModelDocumentType.UnmitigatedHighRiskWithoutEscalation);

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
        var doc = JsonSerializer.Deserialize<ThreatModel>(ValidDoc, DocumentJson.Options)!;
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options);
        var back = JsonSerializer.Deserialize<ThreatModel>(json, DocumentJson.Options)!;
        back.Should().BeEquivalentTo(doc);
        using var parsed = JsonDocument.Parse(json);
        Type.Validate(parsed.RootElement).IsValid.Should().BeTrue("the re-serialized shape must still validate");
    }
}
