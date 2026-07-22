using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC6 — <see cref="TriageDecisionDocumentType"/>: the four closed 26-1
/// enums (with alias folds), out-of-vocab = violation (never a clamp), reasoning
/// required. Pure half; the round-trip against <c>TriagePoDecisionHelper.ParseDecision</c>
/// lives in Activities.Tests (D8).
/// </summary>
[TestFixture]
public class TriageDecisionTypeTests
{
    private static readonly TriageDecisionDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static IEnumerable<string> Codes(DocumentValidationResult r) => r.Violations.Select(v => v.Code);

    // ── enum pins ─────────────────────────────────────────────────────────────

    [Test]
    public void Enum_member_counts_and_wires_are_pinned()
    {
        Enum.GetValues<TriagePriority>().Should().HaveCount(4);
        Enum.GetValues<TriageIssueType>().Should().HaveCount(6);
        Enum.GetValues<TriageComplexity>().Should().HaveCount(5);
        Enum.GetValues<TriageAutomation>().Should().HaveCount(3);

        EnumWire<TriagePriority>.ToWire(TriagePriority.Urgent).Should().Be("urgent");
        EnumWire<TriageIssueType>.ToWire(TriageIssueType.Security).Should().Be("security");
        EnumWire<TriageComplexity>.ToWire(TriageComplexity.Epic).Should().Be("epic");
        EnumWire<TriageAutomation>.ToWire(TriageAutomation.TammaAuto).Should().Be("tamma-auto");
        EnumWire<TriageAutomation>.ToWire(TriageAutomation.NeedsHuman).Should().Be("needs-human");
    }

    [Test]
    public void Priority_alias_folds_match_the_helper_synonyms()
    {
        TriageVocabulary.TryParsePriority("critical", out var p1).Should().BeTrue();
        p1.Should().Be(TriagePriority.Urgent);
        TriageVocabulary.TryParsePriority("medium", out var p2).Should().BeTrue();
        p2.Should().Be(TriagePriority.Normal);
        TriageVocabulary.TryParsePriority("P0", out _).Should().BeFalse();
    }

    // ── validation ─────────────────────────────────────────────────────────────

    [Test]
    public void Valid_decision_passes()
    {
        var r = Validate(
            """{ "priority": "high", "type": "bug", "complexity": "simple", "automation": "tamma-auto", "reasoning": "clear repro" }""");
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [Test]
    public void Alias_valued_fields_are_valid()
    {
        var r = Validate(
            """{ "priority": "critical", "type": "feature", "complexity": "medium", "automation": "needs-human", "reasoning": "r" }""");
        r.IsValid.Should().BeTrue(string.Join(", ", Codes(r)));
    }

    [TestCase("priority", "P0")]
    [TestCase("automation", "auto")]
    [TestCase("type", "enhancement")]
    [TestCase("complexity", "huge")]
    public void Out_of_vocab_value_is_a_violation_never_a_clamp(string field, string value)
    {
        var json = $$"""
        { "priority": "high", "type": "bug", "complexity": "simple", "automation": "tamma-auto", "reasoning": "r", "{{field}}": "{{value}}" }
        """;
        var r = Validate(json);
        r.Violations.Should().Contain(v => v.Code == TriageDecisionDocumentType.OutOfVocabulary && v.Message.Contains(value));
    }

    [Test]
    public void Empty_reasoning_is_reported()
    {
        var r = Validate(
            """{ "priority": "high", "type": "bug", "complexity": "simple", "automation": "tamma-auto", "reasoning": "" }""");
        Codes(r).Should().Contain(TriageDecisionDocumentType.ReasoningRequired);
    }

    [Test]
    public void Non_object_payload_is_malformed()
    {
        var r = Validate("""[ "prose" ]""");
        Codes(r).Should().Equal(new[] { TriageDecisionDocumentType.MalformedPayload });
    }
}
