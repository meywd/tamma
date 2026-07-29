using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b AC2 (TestPlan) — one rejecting and one accepting fixture per
/// rule, each asserting the named violation code: risk areas ranked in a total
/// order, every strategy line mapped to a declared risk area with a coverage
/// target, entry/exit criteria stated.
/// </summary>
[TestFixture]
public class TestPlanTypeTests
{
    private static readonly TestPlanDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    private const string ValidDoc =
        """
        {
          "scope": "The tenant rate-limiter; UI out of scope.",
          "riskAreas": [
            { "name": "concurrency", "rank": 1, "rationale": "Shared counters." },
            { "name": "config", "rank": 2, "rationale": "Operator-supplied limits." }
          ],
          "strategyLines": [
            { "description": "Parallel-request integration tests", "coverageTarget": "all limiter branches", "riskAreaRef": "concurrency" },
            { "description": "Property tests over configs", "coverageTarget": "config parse paths", "riskAreaRef": "config" }
          ],
          "environments": ["ci"],
          "entryCriteria": ["limiter merged behind a flag"],
          "exitCriteria": ["all lines green twice"]
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
        Codes(Validate("""{ "riskAreas": "none" }""")).Should().Contain(TestPlanDocumentType.MalformedPayload);

    // ── SCOPE_MISSING ───────────────────────────────────────────────────────

    [Test]
    public void Missing_scope_is_reported()
    {
        var r = Validate(ValidDoc.Replace("The tenant rate-limiter; UI out of scope.", "  "));
        Codes(r).Should().Contain(TestPlanDocumentType.ScopeMissing);
    }

    [Test]
    public void Present_scope_is_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(TestPlanDocumentType.ScopeMissing);

    // ── NO_RISK_AREAS / RISK_AREA_NAME_MISSING ──────────────────────────────

    [Test]
    public void Empty_risk_areas_are_reported()
    {
        var r = Validate(
            """
            {
              "scope": "s",
              "riskAreas": [],
              "strategyLines": [],
              "entryCriteria": ["e"],
              "exitCriteria": ["x"]
            }
            """);
        Codes(r).Should().Contain(TestPlanDocumentType.NoRiskAreas);
    }

    [Test]
    public void Unnamed_risk_area_is_reported()
    {
        var r = Validate(
            """
            {
              "scope": "s",
              "riskAreas": [ { "name": "", "rank": 1, "rationale": "r" } ],
              "strategyLines": [ { "description": "d", "coverageTarget": "c", "riskAreaRef": "x" } ],
              "entryCriteria": ["e"],
              "exitCriteria": ["x"]
            }
            """);
        Codes(r).Should().Contain(TestPlanDocumentType.RiskAreaNameMissing);
    }

    [Test]
    public void Named_risk_areas_are_accepted()
    {
        var codes = Codes(Validate(ValidDoc));
        codes.Should().NotContain(TestPlanDocumentType.NoRiskAreas);
        codes.Should().NotContain(TestPlanDocumentType.RiskAreaNameMissing);
    }

    // ── RISK_RANK_NOT_TOTAL_ORDER ───────────────────────────────────────────

    [Test]
    public void Tied_risk_ranks_are_reported()
    {
        var r = Validate(ValidDoc.Replace("\"rank\": 2", "\"rank\": 1"));
        Codes(r).Should().Contain(TestPlanDocumentType.RiskRankNotTotalOrder);
    }

    [Test]
    public void Gapped_risk_ranks_are_reported()
    {
        var r = Validate(ValidDoc.Replace("\"rank\": 2", "\"rank\": 5"));
        Codes(r).Should().Contain(TestPlanDocumentType.RiskRankNotTotalOrder);
    }

    [Test]
    public void Unranked_risk_area_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"rank\": 2, ", ""));
        Codes(r).Should().Contain(TestPlanDocumentType.RiskRankNotTotalOrder);
    }

    [Test]
    public void Total_order_ranks_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(TestPlanDocumentType.RiskRankNotTotalOrder);

    // ── NO_STRATEGY_LINES / STRATEGY_LINE_UNMAPPED_RISK_AREA ────────────────

    [Test]
    public void Empty_strategy_lines_are_reported()
    {
        var r = Validate(
            """
            {
              "scope": "s",
              "riskAreas": [ { "name": "a", "rank": 1, "rationale": "r" } ],
              "strategyLines": [],
              "entryCriteria": ["e"],
              "exitCriteria": ["x"]
            }
            """);
        Codes(r).Should().Contain(TestPlanDocumentType.NoStrategyLines);
    }

    [Test]
    public void Strategy_line_referencing_undeclared_risk_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"riskAreaRef\": \"config\"", "\"riskAreaRef\": \"ui\""));
        Codes(r).Should().Contain(TestPlanDocumentType.StrategyLineUnmappedRiskArea);
    }

    [Test]
    public void Mapped_strategy_lines_are_accepted()
    {
        var codes = Codes(Validate(ValidDoc));
        codes.Should().NotContain(TestPlanDocumentType.NoStrategyLines);
        codes.Should().NotContain(TestPlanDocumentType.StrategyLineUnmappedRiskArea);
    }

    // ── STRATEGY_LINE_MISSING_COVERAGE_TARGET ───────────────────────────────

    [Test]
    public void Strategy_line_without_coverage_target_is_reported()
    {
        var r = Validate(ValidDoc.Replace("\"coverageTarget\": \"config parse paths\"", "\"coverageTarget\": \" \""));
        Codes(r).Should().Contain(TestPlanDocumentType.StrategyLineMissingCoverageTarget);
    }

    [Test]
    public void Coverage_targets_present_are_accepted() =>
        Codes(Validate(ValidDoc)).Should().NotContain(TestPlanDocumentType.StrategyLineMissingCoverageTarget);

    // ── ENTRY_CRITERIA_MISSING / EXIT_CRITERIA_MISSING ──────────────────────

    [Test]
    public void Missing_entry_criteria_are_reported()
    {
        var r = Validate(ValidDoc.Replace("\"entryCriteria\": [\"limiter merged behind a flag\"]", "\"entryCriteria\": []"));
        Codes(r).Should().Contain(TestPlanDocumentType.EntryCriteriaMissing);
    }

    [Test]
    public void Missing_exit_criteria_are_reported()
    {
        var r = Validate(ValidDoc.Replace("\"exitCriteria\": [\"all lines green twice\"]", "\"exitCriteria\": []"));
        Codes(r).Should().Contain(TestPlanDocumentType.ExitCriteriaMissing);
    }

    [Test]
    public void Stated_entry_and_exit_criteria_are_accepted()
    {
        var codes = Codes(Validate(ValidDoc));
        codes.Should().NotContain(TestPlanDocumentType.EntryCriteriaMissing);
        codes.Should().NotContain(TestPlanDocumentType.ExitCriteriaMissing);
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
        var doc = JsonSerializer.Deserialize<TestPlan>(ValidDoc, DocumentJson.Options)!;
        var json = JsonSerializer.Serialize(doc, DocumentJson.Options);
        var back = JsonSerializer.Deserialize<TestPlan>(json, DocumentJson.Options)!;
        back.Should().BeEquivalentTo(doc);
        using var parsed = JsonDocument.Parse(json);
        Type.Validate(parsed.RootElement).IsValid.Should().BeTrue("the re-serialized shape must still validate");
    }
}
