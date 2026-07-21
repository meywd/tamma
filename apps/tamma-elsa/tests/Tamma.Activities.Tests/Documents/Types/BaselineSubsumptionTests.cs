using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Ambiguity;
using Tamma.Activities.Decomposition;
using Tamma.Activities.Research;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-3 AC6 — proves the typed validators SUBSUME the fail-closed baseline
/// parsers. This suite lives in <c>Tamma.Activities.Tests</c> (Design Decision D7)
/// because it references BOTH the OLD parsers (<c>Tamma.Activities</c>) and the NEW
/// types (<c>Tamma.Core</c>); <c>Tamma.Core.Tests</c> stays dependency-light.
///
/// <para><b>Two kinds of negative (D8):</b></para>
/// <list type="bullet">
///   <item><b>JSON-shaped negatives</b> (missing summary, empty subtasks, out-of-range
///   score, missing rationale, all-shell items) — valid JSON that the baseline parser
///   returns null on; the typed path rejects them via a named <c>Validate</c> violation.</item>
///   <item><b>Text-level negatives</b> ("no json here at all", "{ not valid json", empty,
///   whitespace) — these can never REACH <c>Validate</c>: the 39-2 envelope boundary
///   (<c>JsonDocument.Parse</c> / the payload deserialize) throws first. This suite
///   asserts they throw at that boundary — a loud rejection, never a silent default.</item>
/// </list>
///
/// <para>It also asserts the <b>lenient-spelling divergences</b>: inputs the baseline
/// ACCEPTED by normalizing/pruning ("Trivial.", dangling ST-99, negative hours,
/// uncited findings) now produce violations — each cites the completion-notes entry,
/// making the AC6 divergence list executable.</para>
/// </summary>
[TestFixture]
public class BaselineSubsumptionTests
{
    private static readonly DecompositionDocumentType Decomposition = new();
    private static readonly FindingsDocumentType Findings = new();
    private static readonly AmbiguityAssessmentDocumentType Ambiguity = new();

    private static DocumentValidationResult Validate(IDocumentType type, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return type.Validate(doc.RootElement);
    }

    // Copied fixture constants from the baseline *ParsingTests.cs (in-assembly reuse,
    // per D7 — the parser tests live in this same project but are private constants).

    // ── Decomposition JSON-shaped negatives (baseline → null) ────────────────
    private const string NoSummary = """{ "subtasks": [ { "id": "ST-1", "title": "a" } ] }""";
    private const string EmptySubtasks = """{ "summary": "s", "subtasks": [] }""";
    private const string AllShells =
        """{ "summary": "s", "subtasks": [ { "id": "", "title": "" }, { "id": "ST-1", "title": "", "description": "" } ] }""";

    [Test]
    public void Decomposition_json_negatives_rejected_by_both()
    {
        DecompositionParsing.ParseDecomposition(NoSummary).Should().BeNull("baseline floor");
        Validate(Decomposition, NoSummary).IsValid.Should().BeFalse("typed path must also reject a missing summary");

        DecompositionParsing.ParseDecomposition(EmptySubtasks).Should().BeNull("baseline floor");
        Validate(Decomposition, EmptySubtasks).IsValid.Should().BeFalse("typed path must also reject empty subtasks");

        DecompositionParsing.ParseDecomposition(AllShells).Should().BeNull("baseline floor");
        Validate(Decomposition, AllShells).IsValid.Should().BeFalse("typed path must also reject all-shell subtasks");
    }

    // ── Research JSON-shaped negatives (baseline → null) ─────────────────────
    private const string NoSummaryReport = """{ "findings": [ { "summary": "a" } ] }""";
    private const string EmptyFindings = """{ "summary": "s", "findings": [] }""";
    private const string AllShellFindings = """{ "summary": "s", "findings": [ { "title": "", "summary": "" } ] }""";

    [Test]
    public void Findings_json_negatives_rejected_by_both()
    {
        ResearchParsing.ParseReport(NoSummaryReport).Should().BeNull("baseline floor");
        Validate(Findings, NoSummaryReport).IsValid.Should().BeFalse("typed path must also reject a missing summary");

        ResearchParsing.ParseReport(EmptyFindings).Should().BeNull("baseline floor");
        Validate(Findings, EmptyFindings).IsValid.Should().BeFalse("typed path must also reject empty findings");

        ResearchParsing.ParseReport(AllShellFindings).Should().BeNull("baseline floor");
        Validate(Findings, AllShellFindings).IsValid.Should().BeFalse("typed path must also reject all-shell findings");
    }

    // ── Ambiguity JSON-shaped negatives (baseline → null) ────────────────────
    private const string NoScore = """{ "rationale": "r", "ambiguities": [] }""";
    private const string BadScore = """{ "score": "high", "rationale": "r" }""";
    private const string NoRationale = """{ "score": 0.5, "ambiguities": [] }""";

    [TestCase("1.5")]
    [TestCase("-0.2")]
    [TestCase("42")]
    public void Ambiguity_out_of_range_score_rejected_by_both(string raw)
    {
        var json = $$"""{ "score": {{raw}}, "rationale": "r" }""";
        AmbiguityParsing.ParseAssessment(json).Should().BeNull("baseline floor");
        Validate(Ambiguity, json).IsValid.Should().BeFalse("typed path must also reject an out-of-range score");
    }

    [Test]
    public void Ambiguity_json_negatives_rejected_by_both()
    {
        AmbiguityParsing.ParseAssessment(NoScore).Should().BeNull("baseline floor");
        Validate(Ambiguity, NoScore).IsValid.Should().BeFalse("typed path must also reject a missing score");

        AmbiguityParsing.ParseAssessment(BadScore).Should().BeNull("baseline floor");
        Validate(Ambiguity, BadScore).IsValid.Should().BeFalse("typed path must also reject a non-numeric score");

        AmbiguityParsing.ParseAssessment(NoRationale).Should().BeNull("baseline floor");
        Validate(Ambiguity, NoRationale).IsValid.Should().BeFalse("typed path must also reject a missing rationale");
    }

    // ── Text-level negatives: never reach Validate — throw at the JSON boundary (D8) ──
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no json here at all")]
    [TestCase("{ not valid json")]
    public void Text_level_negatives_throw_at_the_json_boundary(string input)
    {
        // The baseline parsers fail closed (null) on these; in the typed pipeline the
        // 39-2 boundary throws BEFORE any Validate is reached — a loud rejection.
        DecompositionParsing.ParseDecomposition(input).Should().BeNull("baseline floor");

        var act = () => JsonDocument.Parse(input);
        act.Should().Throw<JsonException>(
            "malformed / empty text cannot reach Validate — it fails loud at the JSON parse boundary (D8)");
    }

    // ── Lenient-spelling divergences: baseline ACCEPTED, typed path now REJECTS ──
    // Each assertion cites its completion-notes.md divergence entry (AC6 executable).

    [Test]
    public void Divergence_strict_complexity_label()
    {
        // completion-notes: "Strict label sets" — baseline folds "Trivial." → low; typed rejects.
        const string messy = """{ "summary": "s", "subtasks": [ { "id": "A", "title": "t", "complexity": "Trivial.", "estimateHours": 4 } ] }""";

        DecompositionParsing.ParseDecomposition(messy).Should().NotBeNull("baseline accepts and folds the label");
        Validate(Decomposition, messy).Violations.Select(v => v.Code)
            .Should().Contain(DecompositionDocumentType.UnknownComplexity,
                "completion-notes: strict label sets (senior_developer/decompose-issue)");
    }

    [Test]
    public void Divergence_dangling_and_self_now_loud()
    {
        // completion-notes: "dangling/self/duplicate now loud" — baseline prunes silently.
        const string withBadDeps =
            """
            { "summary": "s", "subtasks": [
              { "id": "ST-1", "title": "a", "estimateHours": 4, "dependsOn": ["ST-1", "ST-99"] },
              { "id": "ST-2", "title": "b", "estimateHours": 4, "dependsOn": ["ST-1", "ST-1"] }
            ] }
            """;

        DecompositionParsing.ParseDecomposition(withBadDeps).Should().NotBeNull("baseline accepts and prunes edges");
        var codes = Validate(Decomposition, withBadDeps).Violations.Select(v => v.Code).ToList();
        codes.Should().Contain(DecompositionDocumentType.SelfDependsOn,
            "completion-notes: self-dependency now loud (senior_developer/decompose-issue)");
        codes.Should().Contain(DecompositionDocumentType.DanglingDependsOn,
            "completion-notes: dangling dependency now loud (senior_developer/decompose-issue)");
    }

    [Test]
    public void Divergence_negative_and_missing_hours_now_out_of_range()
    {
        // completion-notes: "sizing 2–8h" — baseline clamped negatives to 0 and called 2–8h a soft guide.
        const string negativeHours = """{ "summary": "s", "subtasks": [ { "id": "A", "title": "t", "estimateHours": -4 } ] }""";

        DecompositionParsing.ParseDecomposition(negativeHours).Should().NotBeNull("baseline accepts and clamps to 0");
        Validate(Decomposition, negativeHours).Violations.Select(v => v.Code)
            .Should().Contain(DecompositionDocumentType.SizingOutOfRange,
                "completion-notes: 2–8h sizing rule (senior_developer/decompose-issue)");
    }

    [Test]
    public void Divergence_evidence_required()
    {
        // completion-notes: "evidence required" — baseline never required citations.
        const string uncited =
            """{ "summary": "s", "findings": [ { "title": "F", "summary": "b", "relevance": 0.5, "confidence": 0.5 } ] }""";

        ResearchParsing.ParseReport(uncited).Should().NotBeNull("baseline accepts a finding with no citations");
        Validate(Findings, uncited).Violations.Select(v => v.Code)
            .Should().Contain(FindingsDocumentType.MissingEvidence,
                "completion-notes: evidence required (product_owner/research)");
    }

    [Test]
    public void Divergence_ambiguity_confidence_rejected_not_clamped()
    {
        // completion-notes: "ranges rejected not clamped" — baseline CLAMPS ambiguity confidence.
        const string highConf = """{ "score": 0.5, "confidence": 1.4, "rationale": "r" }""";

        AmbiguityParsing.ParseAssessment(highConf).Should().NotBeNull("baseline accepts and clamps confidence");
        Validate(Ambiguity, highConf).Violations.Select(v => v.Code)
            .Should().Contain(AmbiguityAssessmentDocumentType.ConfidenceOutOfRange,
                "completion-notes: confidence rejected not clamped (product_owner/score-ambiguity)");
    }

    [Test]
    public void Divergence_strict_ambiguity_type_label()
    {
        // completion-notes: "label sets strict" — baseline folds "unclear" → vague; typed rejects.
        const string unclear =
            """{ "score": 0.5, "rationale": "r", "ambiguities": [ { "type": "unclear", "description": "d", "severity": "high" } ] }""";

        AmbiguityParsing.ParseAssessment(unclear).Should().NotBeNull("baseline accepts and folds the label");
        Validate(Ambiguity, unclear).Violations.Select(v => v.Code)
            .Should().Contain(AmbiguityAssessmentDocumentType.UnknownAmbiguityType,
                "completion-notes: strict label sets (product_owner/score-ambiguity)");
    }
}
