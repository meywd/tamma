using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-9 — property pins for <see cref="AdrBindingHelper"/>, the pure Elsa-free core of the
/// ADR binding and the reference shape for the prose family. Covers AC2 (typed exits) and AC3's
/// payload half: the consumer shape a 39-11 store read hands back must survive round-tripping
/// through this helper with kind/audience/title/body intact.
/// </summary>
[TestFixture]
public class AdrBindingHelperTests
{
    private const string ValidProse = """
        {"kind":"adr","audience":"engineering","title":"ADR: scope prose by producer",
         "body":"## Context\nSeven producers write prose for one issue.\n\n## Decision\nScope by producer."}
        """;

    // ── the closed vocabularies (41-1c) ──────────────────────────────────

    [Test]
    public void Kind_IsTheAdrVocabularyMember_AndDefaultAudienceIsEngineering()
    {
        AdrBindingHelper.Kind.Should().Be("adr");
        AdrBindingHelper.DefaultAudience.Should().Be("engineering");
        ProseKindExtensions.TryParse(AdrBindingHelper.Kind, out _).Should().BeTrue();
        ProseAudienceExtensions.TryParse(AdrBindingHelper.DefaultAudience, out _).Should().BeTrue();
    }

    [TestCase("engineering", "engineering")]
    [TestCase("team", "team")]
    [TestCase("  stakeholder  ", "stakeholder")]
    [TestCase("", "engineering")]
    [TestCase(null, "engineering")]
    [TestCase("marketing", "engineering")]
    [TestCase("Engineering", "engineering")] // the vocabulary is ORDINAL — a case variant is not a member
    public void ResolveAudience_GuardsTheCallerInput_AgainstTheClosedVocabulary(string? requested, string expected)
        => AdrBindingHelper.ResolveAudience(requested).Should().Be(expected);

    // ── BuildDecisionContext (D5 — the declared carrier) ─────────────────

    [Test]
    public void BuildDecisionContext_CarriesEachSuppliedSourceUnderALabelledHeading()
    {
        var carrier = AdrBindingHelper.BuildDecisionContext(
            """{"summary":"two candidate designs"}""",
            """{"summary":"the limiter is per-process"}""",
            "the team argued about this in #412");

        carrier.Should().Contain("## Decision Context").And.Contain("#412");
        carrier.Should().Contain("## Accepted Design").And.Contain("two candidate designs");
        carrier.Should().Contain("## Accepted Findings").And.Contain("per-process");
    }

    [Test]
    public void BuildDecisionContext_IsEmptyWhenNothingIsSupplied_NotAHardFail()
    {
        // An ADR is writable from the work item alone; the 39-14 read seam reports "not found"
        // as the empty carrier "{}", which must not reach the prompt.
        AdrBindingHelper.BuildDecisionContext(null, null, null).Should().BeEmpty();
        AdrBindingHelper.BuildDecisionContext("{}", "{}", "  ").Should().BeEmpty();
    }

    // ── ProjectAdrBody / ReadAudience (fail-closed) ──────────────────────

    [Test]
    public void ProjectAdrBody_SurfacesTheWholeEnvelope_NotJustTheMarkdown()
    {
        var projected = AdrBindingHelper.ProjectAdrBody(ValidProse);
        using var doc = JsonDocument.Parse(projected);
        doc.RootElement.GetProperty("kind").GetString().Should().Be("adr");
        doc.RootElement.GetProperty("audience").GetString().Should().Be("engineering",
            "a consumer that drops the audience tag cannot filter — the tag is the point of the type");
        doc.RootElement.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("body").GetString().Should().Contain("## Decision");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("[1,2,3]")]
    public void ProjectAdrBody_IsFailClosed_NeverThrows(string? input)
        => AdrBindingHelper.ProjectAdrBody(input).Should().BeEmpty();

    [Test]
    public void ReadAudience_ReadsTheTag_AndIsFailClosed()
    {
        AdrBindingHelper.ReadAudience(ValidProse).Should().Be("engineering");
        AdrBindingHelper.ReadAudience("{}").Should().BeEmpty();
        AdrBindingHelper.ReadAudience("not json").Should().BeEmpty();
        AdrBindingHelper.ReadAudience(null).Should().BeEmpty();
        AdrBindingHelper.ReadAudience("""{"audience":42}""").Should().BeEmpty();
    }

    // ── typed exits (AC2) ────────────────────────────────────────────────

    [Test]
    public void BuildFailureDetail_NamesEveryReachableTypedOutcomeWire_AndTheRejectedStatus()
    {
        foreach (var outcome in System.Enum.GetValues<DocumentLifecycleOutcome>())
        {
            var exit = new LifecycleBindingHelper.LifecycleExit(
                DocumentLifecycleResult.StatusEscalated, outcome.ToWire(), null, "{}", "");
            AdrBindingHelper.BuildFailureDetail(exit).Should().Contain(outcome.ToWire());
        }

        var rejected = new LifecycleBindingHelper.LifecycleExit("rejected", null, null, "{}", "");
        AdrBindingHelper.BuildFailureDetail(rejected).Should().Contain("rejected");
    }

    // ── the prose contract actually bites (D4 / 41-1c AC2) ───────────────

    [Test]
    public void TheProseValidator_AcceptsArbitraryMarkdown_ButRejectsAnEmptyBody()
    {
        var type = DocumentTypeRegistry.Resolve(DocumentTypeKey.Prose);

        using var arbitrary = JsonDocument.Parse("""
            {"kind":"adr","audience":"engineering","title":"t","body":"no headings at all, just a sentence."}
            """);
        type.Validate(arbitrary.RootElement).IsValid.Should().BeTrue(
            "prose is NOT schema-checked — the four-token envelope contract is thin BY CONSTRUCTION");

        using var empty = JsonDocument.Parse("""
            {"kind":"adr","audience":"engineering","title":"t","body":"   \n\t "}
            """);
        var result = type.Validate(empty.RootElement);
        result.IsValid.Should().BeFalse("…but the gate is not vacuous: the envelope rules bite");
        result.Violations.Select(v => v.Code).Should().Contain(ProseDocumentType.BodyEmpty);
    }

    [Test]
    public void TheAdrEnvelopeThisBindingProduces_ValidatesAgainstTheRegisteredProseType()
    {
        var type = DocumentTypeRegistry.Resolve(DocumentTypeKey.Prose);
        using var doc = JsonDocument.Parse(AdrBindingHelper.ProjectAdrBody(ValidProse));
        type.Validate(doc.RootElement).IsValid.Should().BeTrue();
    }

    [Test]
    public void ProseAcceptancePosture_IsChosen_NotTheArchitectCatchAll()
    {
        // 41-1c D6 / AC6 — prose must not reach `_ => Rules` by accident. D7: this binding does
        // NOT edit that per-TYPE row (an ADR's architect preference is per-KIND); it forwards a
        // caller-supplied acceptanceRulesJson instead.
        var rules = Tamma.Core.Documents.Policy.AcceptanceDefaults.For(DocumentTypeKey.Prose);
        rules.Should().NotBeSameAs(Tamma.Core.Documents.Policy.AcceptanceDefaults.Rules);
        rules.ReviewerSelection.Mode.Should().Be(Tamma.Core.Documents.Policy.ReviewerMode.SingleReviewer);
        rules.ReviewerSelection.ReviewerRole.Should().Be("tech_writer");
    }
}
