using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-2 — property pins for <see cref="AcceptanceCriteriaBindingHelper"/>, the pure
/// Elsa-free decision core of the acceptance-criteria binding. Every function must be TOTAL and
/// FAIL-CLOSED: unreadable input yields the conservative projection, never a throw out of a
/// routing lambda. Covers AC2's exit-mapping half and AC3's single-parent lineage caveat (D4).
/// </summary>
[TestFixture]
public class AcceptanceCriteriaBindingHelperTests
{
    private const string Clarification = """{"clarifiedRequirement":"rate-limit per tenant","resolved":true}""";
    private const string Findings = """{"summary":"the limiter is per-process today"}""";

    // ── BuildContextFindings (D3 — the declared carrier) ─────────────────

    [Test]
    public void BuildContextFindings_BothPresent_CarriesBothUnderLabelledHeadings()
    {
        var carrier = AcceptanceCriteriaBindingHelper.BuildContextFindings(Clarification, Findings);
        carrier.Should().Contain("## Accepted Clarification");
        carrier.Should().Contain(Clarification);
        carrier.Should().Contain("## Accepted Findings");
        carrier.Should().Contain(Findings);
    }

    [Test]
    public void BuildContextFindings_OnlyOnePresent_CarriesOnlyThatOne()
    {
        AcceptanceCriteriaBindingHelper.BuildContextFindings(Clarification, null)
            .Should().Contain("## Accepted Clarification").And.NotContain("## Accepted Findings");
        AcceptanceCriteriaBindingHelper.BuildContextFindings(null, Findings)
            .Should().Contain("## Accepted Findings").And.NotContain("## Accepted Clarification");
    }

    [Test]
    public void BuildContextFindings_NeitherPresent_IsEmpty_NotAHardFail()
    {
        // D2 — acceptance criteria are authorable from the issue alone; the fail-closed store
        // read reports "not found" as the empty carrier "{}", which must NOT reach the prompt.
        AcceptanceCriteriaBindingHelper.BuildContextFindings(null, null).Should().BeEmpty();
        AcceptanceCriteriaBindingHelper.BuildContextFindings("", "  ").Should().BeEmpty();
        AcceptanceCriteriaBindingHelper.BuildContextFindings("{}", "{}").Should().BeEmpty();
        AcceptanceCriteriaBindingHelper.BuildContextFindings("[]", "null").Should().BeEmpty();
    }

    // ── ChooseParentDocumentId (D4 — one parent slot) ────────────────────

    [Test]
    public void ChooseParentDocumentId_PrefersTheClarification_ThenFindings_ThenNone()
    {
        AcceptanceCriteriaBindingHelper.ChooseParentDocumentId("clar-1", "find-1").Should().Be("clar-1",
            "the Clarification is the closer ancestor — it resolves the ambiguity the criteria encode");
        AcceptanceCriteriaBindingHelper.ChooseParentDocumentId("", "find-1").Should().Be("find-1");
        AcceptanceCriteriaBindingHelper.ChooseParentDocumentId("   ", "find-1").Should().Be("find-1");
        AcceptanceCriteriaBindingHelper.ChooseParentDocumentId(null, null).Should().BeEmpty();
    }

    [Test]
    public void BuildConsumedIdsJson_RecordsEveryConsumedEdgeTheParentSlotCannotExpress()
    {
        var json = AcceptanceCriteriaBindingHelper.BuildConsumedIdsJson("clar-1", "find-1");
        using var doc = JsonDocument.Parse(json);
        var consumed = doc.RootElement.GetProperty("consumedDocumentIds");
        consumed.GetProperty("clarification").GetString().Should().Be("clar-1");
        consumed.GetProperty("findings").GetString().Should().Be("find-1");

        // Absent ids are omitted, never emitted as empty strings.
        using var none = JsonDocument.Parse(AcceptanceCriteriaBindingHelper.BuildConsumedIdsJson(null, null));
        none.RootElement.GetProperty("consumedDocumentIds").EnumerateObject().Should().BeEmpty();
    }

    // ── ProjectCriteria / CountCriteria (fail-closed) ────────────────────

    [Test]
    public void ProjectCriteria_ReadsTheCriteriaArrayFromAValidAcceptedBody()
    {
        var body = """
            {"issueId":"repo#1","criteria":[
              {"id":"AC-1","form":"checklist","statement":"x","verifiable":true}]}
            """;
        var projected = AcceptanceCriteriaBindingHelper.ProjectCriteria(body);
        using var doc = JsonDocument.Parse(projected);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        AcceptanceCriteriaBindingHelper.CountCriteria(body).Should().Be(1);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json at all")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("""{"criteria":"not an array"}""")]
    [TestCase("""{"criteria":[]}""")]
    public void ProjectCriteria_IsFailClosed_NeverThrows(string? input)
    {
        AcceptanceCriteriaBindingHelper.ProjectCriteria(input).Should().Be("[]");
        AcceptanceCriteriaBindingHelper.CountCriteria(input).Should().Be(0);
    }

    [Test]
    public void ProjectCriteria_AcceptsABareArrayBody()
        => AcceptanceCriteriaBindingHelper.ProjectCriteria("""[{"id":"AC-1"}]""")
            .Should().Contain("AC-1");

    // ── BuildFailureDetail (reused verbatim — every exit names a typed outcome) ──

    [Test]
    public void BuildFailureDetail_NamesEveryReachableTypedOutcomeWire()
    {
        foreach (var outcome in System.Enum.GetValues<DocumentLifecycleOutcome>())
        {
            var exit = new LifecycleBindingHelper.LifecycleExit(
                DocumentLifecycleResult.StatusEscalated, outcome.ToWire(), null, "{}", "");
            CreationBindingHelper.BuildFailureDetail(exit).Should().Contain(outcome.ToWire(),
                "AC2 — a non-accept exit must point at a TYPED escalation, never a dead terminal");
        }

        var rejected = new LifecycleBindingHelper.LifecycleExit("rejected", null, null, "{}", "");
        CreationBindingHelper.BuildFailureDetail(rejected).Should().Contain("rejected");
    }

    [Test]
    public void ReadLifecycleResult_OnAMissingDispatchResult_IsATypedEscalation_NotASilentSuccess()
    {
        var exit = LifecycleBindingHelper.ReadLifecycleResult(null);
        LifecycleBindingHelper.IsAccepted(exit).Should().BeFalse();
        exit.Status.Should().Be(DocumentLifecycleResult.StatusEscalated);
        exit.Outcome.Should().Be(DocumentLifecycleOutcome.ValidationExhausted.ToWire());
    }

    // ── the wire the template instructs is the wire the type validates ───

    [Test]
    public void TheProjectedCriteria_ComeFromABodyTheRegisteredTypeAccepts()
    {
        // The consumer-shape pin: 41-15 reads the accepted body through the 39-11 store, so the
        // shape ProjectCriteria slices must be one AcceptanceCriteriaDocumentType.Validate accepts.
        var type = DocumentTypeRegistry.Resolve(DocumentTypeKey.AcceptanceCriteria);
        var example = type.Examples.Single(e => e.IsValid);
        using var doc = JsonDocument.Parse(example.PayloadJson);

        type.Validate(doc.RootElement).IsValid.Should().BeTrue();
        AcceptanceCriteriaBindingHelper.CountCriteria(example.PayloadJson).Should().BeGreaterThan(0);
    }
}
