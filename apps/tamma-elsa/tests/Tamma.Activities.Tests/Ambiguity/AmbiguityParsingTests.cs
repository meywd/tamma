using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Ambiguity;
using Tamma.Activities.Ambiguity.Models;

namespace Tamma.Activities.Tests.Ambiguity;

/// <summary>
/// Story 3.6 — unit coverage for <see cref="AmbiguityParsing.ParseAssessment"/>. Proves the
/// parser recovers the structured assessment on a well-formed scoring response, NORMALISES the
/// ambiguity type / severity labels, keeps a valid empty-breakdown ("clear requirement")
/// result, and FAILS CLOSED (returns null) on every degraded / empty / malformed / out-of-range
/// input so the workflow routes to AMBIGUITY.FAILED rather than fabricating a score.
/// </summary>
[TestFixture]
public class AmbiguityParsingTests
{
    private const string ValidAssessment =
        """
        Here is the ambiguity assessment:
        {
          "score": 0.72,
          "confidence": 0.8,
          "rationale": "The requirement is missing acceptance criteria and uses vague wording.",
          "ambiguities": [
            { "type": "missing", "description": "No acceptance criteria", "severity": "high", "recommendation": "Ask for measurable ACs" },
            { "type": "Vague.", "description": "\"fast\" is not quantified", "severity": "medium", "recommendation": "Define a latency target" }
          ]
        }
        """;

    [Test]
    public void ParseAssessment_ValidResponse_RecoversAssessment()
    {
        var a = AmbiguityParsing.ParseAssessment(ValidAssessment);

        a.Should().NotBeNull();
        a!.Score.Should().Be(0.72m);
        a.Confidence.Should().Be(0.8m);
        a.Rationale.Should().Contain("missing acceptance criteria");
        a.Ambiguities.Should().HaveCount(2);
    }

    [Test]
    public void ParseAssessment_NormalisesTypeAndSeverityLabels()
    {
        var a = AmbiguityParsing.ParseAssessment(ValidAssessment);

        // "Vague." → canonical "vague" (trimmed, lower-cased, trailing dot removed).
        a!.Ambiguities.Select(x => x.Type).Should().Contain(AmbiguityTypes.Vague);
        a.Ambiguities.Select(x => x.Type).Should().Contain(AmbiguityTypes.Missing);
        a.Ambiguities.Select(x => x.Severity).Should().OnlyContain(
            s => s == AmbiguitySeverities.High || s == AmbiguitySeverities.Medium);
    }

    [Test]
    public void ParseAssessment_ClearRequirement_EmptyBreakdown_IsValid()
    {
        const string clear =
            """{ "score": 0.05, "confidence": 0.9, "rationale": "Fully specified with clear ACs.", "ambiguities": [] }""";

        var a = AmbiguityParsing.ParseAssessment(clear);

        a.Should().NotBeNull(
            "a genuinely clear requirement scores near 0 with an empty breakdown — that is a " +
            "valid assessment, not a failure");
        a!.Score.Should().Be(0.05m);
        a.Ambiguities.Should().BeEmpty();
    }

    [Test]
    public void ParseAssessment_ScoreZero_IsValid()
    {
        const string zero = """{ "score": 0.0, "rationale": "Crystal clear." }""";

        var a = AmbiguityParsing.ParseAssessment(zero);

        a.Should().NotBeNull("a score of exactly 0.0 is a valid 'perfectly clear' result");
        a!.Score.Should().Be(0m);
    }

    [Test]
    public void ParseAssessment_MissingConfidence_DefaultsToZero()
    {
        const string noConf = """{ "score": 0.4, "rationale": "Some gaps." }""";

        var a = AmbiguityParsing.ParseAssessment(noConf);

        a!.Confidence.Should().Be(0m, "absent confidence is not fabricated — defaults to 0");
    }

    [Test]
    public void ParseAssessment_DropsEmptyShellAmbiguities()
    {
        const string withShell =
            """
            { "score": 0.6, "rationale": "r", "ambiguities": [
              { "type": "vague", "description": "" },
              { "type": "missing", "description": "real gap" }
            ] }
            """;

        var a = AmbiguityParsing.ParseAssessment(withShell);

        a!.Ambiguities.Should().ContainSingle(x => x.Description == "real gap",
            "empty-shell ambiguities (no description) must be dropped, not admitted blank");
    }

    /// <summary>
    /// A sample matching the EXACT shape the (product_owner, score-ambiguity) system-default
    /// prompt template (SystemPrompts.ScoreAmbiguityBody, Story 3.6) instructs the LLM to emit.
    /// Proves the template's documented output is parseable end-to-end, so the
    /// AmbiguityScoringWorkflow happy path emits a real AMBIGUITY.SCORED assessment.
    /// </summary>
    private const string TemplateShapedAssessment =
        """
        {
          "score": 0.65,
          "confidence": 0.75,
          "rationale": "The feature request omits the target platform and contradicts itself on auth.",
          "ambiguities": [
            { "type": "missing", "description": "Target platform (web vs mobile) is unstated.", "severity": "high", "recommendation": "Confirm the platform with the stakeholder." },
            { "type": "contradictory", "description": "Says 'no login' but also 'per-user history'.", "severity": "high", "recommendation": "Resolve whether accounts are required." },
            { "type": "implicit", "description": "Assumes English-only content.", "severity": "low", "recommendation": "Confirm i18n scope." }
          ]
        }
        """;

    [Test]
    public void ParseAssessment_TemplateShapedOutput_RecoversAssessment()
    {
        var a = AmbiguityParsing.ParseAssessment(TemplateShapedAssessment);

        a.Should().NotBeNull(
            "the (product_owner, score-ambiguity) template's documented JSON shape must parse " +
            "into a real assessment");
        a!.Score.Should().Be(0.65m);
        a.Rationale.Should().NotBeNullOrWhiteSpace();
        a.Ambiguities.Should().HaveCount(3);
        a.Ambiguities.Select(x => x.Type).Should().Contain(new[]
        {
            AmbiguityTypes.Missing, AmbiguityTypes.Contradictory, AmbiguityTypes.Implicit,
        });
    }

    // ── Fail-closed cases (all → null) ─────────────────────────────────
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no json here at all")]
    [TestCase("{ not valid json")]
    public void ParseAssessment_DegradedInput_FailsClosed(string? input)
    {
        AmbiguityParsing.ParseAssessment(input).Should().BeNull(
            "degraded/empty/malformed scoring output must fail closed (no fabricated score)");
    }

    [Test]
    public void ParseAssessment_MissingScore_FailsClosed()
    {
        const string noScore = """{ "rationale": "r", "ambiguities": [] }""";
        AmbiguityParsing.ParseAssessment(noScore).Should().BeNull(
            "the score is the whole point — an assessment without it must fail closed");
    }

    [Test]
    public void ParseAssessment_NonNumericScore_FailsClosed()
    {
        const string badScore = """{ "score": "high", "rationale": "r" }""";
        AmbiguityParsing.ParseAssessment(badScore).Should().BeNull(
            "a non-numeric score cannot be acted on — fail closed");
    }

    [TestCase("1.5")]
    [TestCase("-0.2")]
    [TestCase("42")]
    public void ParseAssessment_OutOfRangeScore_FailsClosed(string raw)
    {
        var outOfRange = $$"""{ "score": {{raw}}, "rationale": "r" }""";
        AmbiguityParsing.ParseAssessment(outOfRange).Should().BeNull(
            "a score outside [0,1] is nonsensical — fail closed rather than clamp a fabricated value");
    }

    [Test]
    public void ParseAssessment_MissingRationale_FailsClosed()
    {
        const string noRationale = """{ "score": 0.5, "ambiguities": [] }""";
        AmbiguityParsing.ParseAssessment(noRationale).Should().BeNull(
            "a score with no rationale is not auditable/actionable — fail closed");
    }
}
