using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Drift tests for the <see cref="DocumentTypeKey"/> vocabulary (Story 39-2
/// AC5b). Same posture as <c>AgentRoleTests</c>: count pin, per-member wire
/// spelling, round-trip, and fail-loud on unknown wire strings.
/// </summary>
[TestFixture]
public class DocumentTypeKeyTests
{
    [Test]
    public void Has_exactly_seventeen_document_types() =>
        // The README's 10-type table + the six Epic 41 types (Story 41-1b:
        // acceptance-criteria, backlog-ordering, sprint-plan, test-plan,
        // threat-model, ux-spec) + prose (Story 41-1c: one type for the whole
        // prose family, body unvalidated markdown). Adding/removing a type is a
        // conscious edit here AND to DocumentTypeRegistry's registration list
        // (39-3 +4, 39-4 +6, 41-1b +6, 41-1c +1).
        Enum.GetValues<DocumentTypeKey>().Length.Should().Be(17);

    [TestCase(DocumentTypeKey.Findings, "findings")]
    [TestCase(DocumentTypeKey.AmbiguityAssessment, "ambiguity-assessment")]
    [TestCase(DocumentTypeKey.Clarification, "clarification")]
    [TestCase(DocumentTypeKey.Decomposition, "decomposition")]
    [TestCase(DocumentTypeKey.Plan, "plan")]
    [TestCase(DocumentTypeKey.Design, "design")]
    [TestCase(DocumentTypeKey.Review, "review")]
    [TestCase(DocumentTypeKey.TriageDecision, "triage-decision")]
    [TestCase(DocumentTypeKey.Diagnosis, "diagnosis")]
    [TestCase(DocumentTypeKey.TestSpec, "test-spec")]
    [TestCase(DocumentTypeKey.AcceptanceCriteria, "acceptance-criteria")]
    [TestCase(DocumentTypeKey.BacklogOrdering, "backlog-ordering")]
    [TestCase(DocumentTypeKey.SprintPlan, "sprint-plan")]
    [TestCase(DocumentTypeKey.TestPlan, "test-plan")]
    [TestCase(DocumentTypeKey.ThreatModel, "threat-model")]
    [TestCase(DocumentTypeKey.UxSpec, "ux-spec")]
    [TestCase(DocumentTypeKey.Prose, "prose")]
    public void ToWire_returns_canonical_kebab_string(DocumentTypeKey key, string wire) =>
        key.ToWire().Should().Be(wire);

    [Test]
    public void Roundtrip_holds_for_every_type()
    {
        foreach (var key in Enum.GetValues<DocumentTypeKey>())
            DocumentTypeKeyExtensions.Parse(key.ToWire()).Should().Be(key);
    }

    [Test]
    public void Parse_throws_typed_unknown_error_on_unknown()
    {
        var act = () => DocumentTypeKeyExtensions.Parse("not-a-type");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.UNKNOWN");
    }

    [Test]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_throws_typed_unknown_error_on_null_or_empty(string? input)
    {
        var act = () => DocumentTypeKeyExtensions.Parse(input!);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.UNKNOWN");
    }

    [Test]
    public void Parse_is_case_sensitive()
    {
        // Wire strings are canonical lowercase kebab; non-canonical casing is rejected.
        var act = () => DocumentTypeKeyExtensions.Parse("Findings");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.UNKNOWN");
    }

    [Test]
    public void TryParse_returns_false_for_unknown_and_true_for_known()
    {
        DocumentTypeKeyExtensions.TryParse("nope", out _).Should().BeFalse();
        DocumentTypeKeyExtensions.TryParse("findings", out var key).Should().BeTrue();
        key.Should().Be(DocumentTypeKey.Findings);
    }
}
