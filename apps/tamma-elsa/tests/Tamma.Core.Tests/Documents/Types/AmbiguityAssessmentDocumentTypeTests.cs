using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-3 AC4 — domain rules for <see cref="AmbiguityAssessmentDocumentType"/>:
/// score ∈ [0,1], closed typed set, and clear (low score) + empty ambiguities valid.
/// </summary>
[TestFixture]
public class AmbiguityAssessmentDocumentTypeTests
{
    private static readonly AmbiguityAssessmentDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    [Test]
    public void Score_zero_is_valid()
    {
        var r = Validate("""{ "score": 0.0, "rationale": "Crystal clear." }""");
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Low_score_with_empty_ambiguities_is_valid()
    {
        var r = Validate(
            """{ "score": 0.05, "confidence": 0.9, "rationale": "Fully specified.", "ambiguities": [] }""");

        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [TestCase("1.5")]
    [TestCase("-0.2")]
    public void Out_of_range_score_is_reported(string score)
    {
        var r = Validate($$"""{ "score": {{score}}, "rationale": "r" }""");
        Codes(r).Should().Contain(AmbiguityAssessmentDocumentType.ScoreOutOfRange);
    }

    [Test]
    public void Missing_rationale_is_reported()
    {
        var r = Validate("""{ "score": 0.5, "ambiguities": [] }""");
        Codes(r).Should().Contain(AmbiguityAssessmentDocumentType.MissingRationale);
    }

    [Test]
    public void Out_of_range_confidence_is_rejected_not_clamped()
    {
        var r = Validate("""{ "score": 0.5, "confidence": 1.4, "rationale": "r" }""");
        Codes(r).Should().Contain(AmbiguityAssessmentDocumentType.ConfidenceOutOfRange);
    }

    [Test]
    public void Unknown_type_is_strict()
    {
        var r = Validate(
            """
            { "score": 0.5, "rationale": "r", "ambiguities": [
              { "type": "unclear", "description": "vague", "severity": "high", "recommendation": "fix" }
            ] }
            """);

        Codes(r).Should().Contain(AmbiguityAssessmentDocumentType.UnknownAmbiguityType);
    }

    [Test]
    public void Unspecified_type_is_accepted_it_is_in_the_closed_set()
    {
        var r = Validate(
            """
            { "score": 0.5, "rationale": "r", "ambiguities": [
              { "type": "unspecified", "description": "d", "severity": "low", "recommendation": "fix" }
            ] }
            """);

        Codes(r).Should().NotContain(AmbiguityAssessmentDocumentType.UnknownAmbiguityType);
    }

    [Test]
    public void Unknown_severity_is_strict()
    {
        var r = Validate(
            """
            { "score": 0.5, "rationale": "r", "ambiguities": [
              { "type": "vague", "description": "d", "severity": "critical", "recommendation": "fix" }
            ] }
            """);

        Codes(r).Should().Contain(AmbiguityAssessmentDocumentType.UnknownSeverity);
    }

    [Test]
    public void Ambiguity_without_description_is_empty_shell()
    {
        var r = Validate(
            """
            { "score": 0.5, "rationale": "r", "ambiguities": [
              { "type": "vague", "description": "", "severity": "low", "recommendation": "fix" }
            ] }
            """);

        Codes(r).Should().Contain(AmbiguityAssessmentDocumentType.AmbiguityEmptyShell);
    }

    [Test]
    public void Missing_score_fails_at_the_deserialization_boundary()
    {
        // The score is load-bearing (required). Its absence fails loud as
        // MALFORMED_PAYLOAD — the typed subsumption of the baseline fail-closed.
        var r = Validate("""{ "rationale": "r", "ambiguities": [] }""");

        r.IsValid.Should().BeFalse();
        Codes(r).Should().Equal(new[] { AmbiguityAssessmentDocumentType.MalformedPayload });
    }

    [Test]
    public void Non_numeric_score_is_malformed_payload()
    {
        var r = Validate("""{ "score": "high", "rationale": "r" }""");

        r.IsValid.Should().BeFalse();
        Codes(r).Should().Equal(new[] { AmbiguityAssessmentDocumentType.MalformedPayload });
    }
}
