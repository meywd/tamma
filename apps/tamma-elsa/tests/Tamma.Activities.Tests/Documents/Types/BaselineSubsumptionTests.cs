using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
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
    // Story 39-12: the decomposition baseline rows retired with DecompositionParsing (the
    // "old parser still parses" transition-window purpose ends when the old parser ceases to
    // exist). The typed-validator coverage lives in DecompositionDocumentTypeTests.
    private static readonly FindingsDocumentType Findings = new();
    private static readonly AmbiguityAssessmentDocumentType Ambiguity = new();

    private static DocumentValidationResult Validate(IDocumentType type, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return type.Validate(doc.RootElement);
    }

    // Copied fixture constants from the baseline *ParsingTests.cs (in-assembly reuse,
    // per D7 — the parser tests live in this same project but are private constants).

    // ── Research JSON-shaped negatives (baseline → null) ─────────────────────
    private const string NoSummaryReport = """{ "findings": [ { "summary": "a" } ] }""";
    private const string EmptyFindings = """{ "summary": "s", "findings": [] }""";
    private const string AllShellFindings = """{ "summary": "s", "findings": [ { "title": "", "summary": "" } ] }""";

    [Test]
    public void Findings_json_negatives_rejected_by_both()
    {
        // Story 39-13 D9: the baseline ResearchParsing probe retired with the parser; the typed
        // validator still rejects each JSON-shaped negative the baseline fail-closed on.
        Validate(Findings, NoSummaryReport).IsValid.Should().BeFalse("typed path must reject a missing summary");
        Validate(Findings, EmptyFindings).IsValid.Should().BeFalse("typed path must reject empty findings");
        Validate(Findings, AllShellFindings).IsValid.Should().BeFalse("typed path must reject all-shell findings");
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
        Validate(Ambiguity, json).IsValid.Should().BeFalse("typed path must reject an out-of-range score");
    }

    [Test]
    public void Ambiguity_json_negatives_rejected_by_both()
    {
        // Story 39-13 D9: the baseline AmbiguityParsing probe retired with the parser.
        Validate(Ambiguity, NoScore).IsValid.Should().BeFalse("typed path must reject a missing score");
        Validate(Ambiguity, BadScore).IsValid.Should().BeFalse("typed path must reject a non-numeric score");
        Validate(Ambiguity, NoRationale).IsValid.Should().BeFalse("typed path must reject a missing rationale");
    }

    // ── Text-level negatives: never reach Validate — throw at the JSON boundary (D8) ──
    // Story 39-12: the decomposition baseline-parser probe was dropped with DecompositionParsing;
    // the type-agnostic JSON-boundary assertion (a loud rejection, never a silent default) stays.
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no json here at all")]
    [TestCase("{ not valid json")]
    public void Text_level_negatives_throw_at_the_json_boundary(string input)
    {
        var act = () => JsonDocument.Parse(input);
        act.Should().Throw<JsonException>(
            "malformed / empty text cannot reach Validate — it fails loud at the JSON parse boundary (D8)");
    }

    // ── Lenient-spelling divergences: baseline ACCEPTED, typed path now REJECTS ──
    // Each assertion cites its completion-notes.md divergence entry (AC6 executable).
    // Story 39-12: the three decomposition divergences (strict complexity, dangling/self,
    // 2–8h sizing) retired with DecompositionParsing; the typed-side coverage lives in
    // DecompositionDocumentTypeTests.

    [Test]
    public void Divergence_evidence_required()
    {
        // completion-notes: "evidence required" — baseline never required citations.
        const string uncited =
            """{ "summary": "s", "findings": [ { "title": "F", "summary": "b", "relevance": 0.5, "confidence": 0.5 } ] }""";

        // Baseline accepted an uncited finding (parser now retired, D9); the typed validator rejects it.
        Validate(Findings, uncited).Violations.Select(v => v.Code)
            .Should().Contain(FindingsDocumentType.MissingEvidence,
                "completion-notes: evidence required (product_owner/research)");
    }

    [Test]
    public void Divergence_ambiguity_confidence_rejected_not_clamped()
    {
        // completion-notes: "ranges rejected not clamped" — baseline CLAMPS ambiguity confidence.
        const string highConf = """{ "score": 0.5, "confidence": 1.4, "rationale": "r" }""";

        // Baseline clamped confidence (parser now retired, D9); the typed validator rejects out-of-range.
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

        // Baseline folded "unclear" → vague (parser now retired, D9); the typed validator rejects it.
        Validate(Ambiguity, unclear).Violations.Select(v => v.Code)
            .Should().Contain(AmbiguityAssessmentDocumentType.UnknownAmbiguityType,
                "completion-notes: strict label sets (product_owner/score-ambiguity)");
    }
}
