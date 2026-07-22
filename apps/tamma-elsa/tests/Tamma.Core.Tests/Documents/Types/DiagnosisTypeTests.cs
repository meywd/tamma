using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC7 (Diagnosis half) — <see cref="DiagnosisDocumentType"/> rules plus
/// the snake_case legacy bridge (D4). Pure half; the cross-parser round-trip against
/// the internal <c>AIDiagnosisActivity.ParseDiagnosisResponse</c> lives in
/// Activities.Tests (D8).
/// </summary>
[TestFixture]
public class DiagnosisTypeTests
{
    private static readonly DiagnosisDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    [Test]
    public void Valid_ranked_hypotheses_pass()
    {
        var r = Validate(
            """
            {
              "analysisSummary": "s",
              "hypotheses": [
                { "rank": 1, "description": "a", "confidence": 0.9, "suggestedFix": "fix a", "affectedFiles": ["a.cs"] },
                { "rank": 2, "description": "b", "confidence": 0.4, "suggestedFix": "", "affectedFiles": [] }
              ]
            }
            """);
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [TestCase("1.4")]
    [TestCase("-0.1")]
    public void Confidence_out_of_range_is_rejected_not_clamped(string conf)
    {
        var r = Validate(
            $$"""{ "analysisSummary": "s", "hypotheses": [ { "rank": 1, "description": "a", "confidence": {{conf}} } ] }""");
        Codes(r).Should().Contain(DiagnosisDocumentType.ConfidenceOutOfRange);
    }

    [Test]
    public void Duplicate_ranks_are_reported()
    {
        var r = Validate(
            """
            { "analysisSummary": "s", "hypotheses": [
              { "rank": 1, "description": "a", "confidence": 0.8 },
              { "rank": 1, "description": "b", "confidence": 0.7 }
            ] }
            """);
        Codes(r).Should().Contain(DiagnosisDocumentType.DuplicateRank);
    }

    [Test]
    public void Rank_confidence_mismatch_is_reported()
    {
        // rank 2 has HIGHER confidence than rank 1 → order contradicts confidence.
        var r = Validate(
            """
            { "analysisSummary": "s", "hypotheses": [
              { "rank": 1, "description": "a", "confidence": 0.3 },
              { "rank": 2, "description": "b", "confidence": 0.9 }
            ] }
            """);
        Codes(r).Should().Contain(DiagnosisDocumentType.RankConfidenceMismatch);
    }

    [Test]
    public void Fix_without_affected_files_is_reported()
    {
        var r = Validate(
            """{ "analysisSummary": "s", "hypotheses": [ { "rank": 1, "description": "a", "confidence": 0.5, "suggestedFix": "patch", "affectedFiles": [] } ] }""");
        Codes(r).Should().Contain(DiagnosisDocumentType.FixMissingAffectedFiles);
    }

    // ── legacy snake_case bridge (D4) ───────────────────────────────────────────

    [Test]
    public void Legacy_snake_case_fixture_reads_validates_and_round_trips_tokens()
    {
        const string snake =
            """
            {
              "analysis_summary": "Null ref in resolver",
              "hypotheses": [
                { "rank": 1, "description": "Resolver returns null", "confidence": 0.85, "suggested_fix": "Guard the miss", "affected_files": ["src/Resolver.cs"] }
              ]
            }
            """;

        var typed = Diagnosis.FromLegacyJson(snake);
        typed.AnalysisSummary.Should().Be("Null ref in resolver");
        typed.Hypotheses.Should().HaveCount(1);
        typed.Hypotheses[0].AffectedFiles.Should().Contain("src/Resolver.cs");

        var camel = JsonSerializer.Serialize(typed, DocumentJson.Options);
        using var doc = JsonDocument.Parse(camel);
        Type.Validate(doc.RootElement).IsValid.Should().BeTrue();

        var backToLegacy = typed.ToLegacyJson();
        backToLegacy.Should().Contain("analysis_summary").And.Contain("suggested_fix").And.Contain("affected_files");
    }

    [Test]
    public void Legacy_garbage_throws_typed_error_no_fabricated_hypotheses()
    {
        var act = () => Diagnosis.FromLegacyJson("I could not analyze this, sorry.");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.DIAGNOSIS.LEGACY_UNPARSEABLE");
    }

    [Test]
    public void Contract_is_camelcase_and_deterministic()
    {
        var contract = Type.RenderContract();
        contract.Should().Contain("\"analysisSummary\"").And.Contain("\"suggestedFix\"").And.Contain("\"affectedFiles\"");
        Type.RenderContract().Should().Be(contract);
    }
}
