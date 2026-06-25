using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriagePODecision.md</c>) — the CORE regression
/// coverage for the triage PO-decision build-out: <see cref="TriagePoDecisionHelper"/>
/// must be <b>fail-closed / no-false-success</b>:
/// <list type="bullet">
///   <item><description>#1 — a failed LLM call yields an explicit
///     <c>llm-failed</c>/<c>triage-failed</c> marker, NOT a clean
///     <c>needs-human</c>/<c>priority-normal</c> applied decision;</description></item>
///   <item><description>#2 — prose / unparseable output is marked <c>unparsed</c>
///     (needs-human-review), never a clean classified decision;</description></item>
///   <item><description>#4 — out-of-vocab classification fields are clamped to the
///     safe default AND flagged in the comment, never passed straight to labels;</description></item>
///   <item><description>#7 — empty input short-circuits to a <c>skipped</c> marker.</description></item>
/// </list>
/// Pure-function tests, independent of the Elsa runtime.
/// </summary>
[TestFixture]
public class TriagePoDecisionHelperTests
{
    // ================================================================
    // #7 — IsUsableInput / empty-input guard
    // ================================================================

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{}")]
    [TestCase("  {}  ")]
    public void IsUsableInput_BlankOrEmptyObject_IsNotUsable(string? itemJson)
    {
        TriagePoDecisionHelper.IsUsableInput(itemJson).Should().BeFalse();
    }

    [Test]
    public void IsUsableInput_RealItem_IsUsable()
    {
        TriagePoDecisionHelper.IsUsableInput("""{"number":42,"title":"x"}""").Should().BeTrue();
    }

    [Test]
    public void BuildSkippedDecision_IsLoud_NeedsHuman_NotFabricatedClean()
    {
        var d = TriagePoDecisionHelper.BuildSkippedDecision();

        d.Status.Should().Be(TriagePoDecisionHelper.StatusSkipped);
        d.Automation.Should().Be("needs-human");
        d.Labels.Should().Contain("triage-skipped");
        d.Labels.Should().Contain("needs-human");
        // It must NOT present a confident classification a downstream apply would
        // treat as a real PO decision.
        d.Comment.Should().Contain("skipped");
    }

    // ================================================================
    // #1 — LLM-failure marker (no false success)
    // ================================================================

    [Test]
    public void BuildFailureDecision_IsExplicitFailure_NotCleanNeedsHuman()
    {
        var d = TriagePoDecisionHelper.BuildFailureDecision("all providers failed");

        d.Status.Should().Be(TriagePoDecisionHelper.StatusLlmFailed);
        d.Automation.Should().Be("needs-human");
        // The honest labels — NOT a fabricated priority-normal/feature set.
        d.Labels.Should().BeEquivalentTo(new[] { "needs-human", "triage-failed" });
        d.Comment.Should().Contain("LLM call failed");
    }

    [Test]
    public void BuildFailureDecision_NullDiagnostics_StillHasGenericSummary()
    {
        var d = TriagePoDecisionHelper.BuildFailureDecision(null);
        d.Comment.Should().Contain("all providers failed");
    }

    [Test]
    public void SummarizeFailure_ReadsErrorMessage_NeverThrows()
    {
        TriagePoDecisionHelper.SummarizeFailure(
            """{"success":false,"errorMessage":"All providers in the chain failed"}""")
            .Should().Be("All providers in the chain failed");

        TriagePoDecisionHelper.SummarizeFailure(null).Should().Be("all providers failed");
        TriagePoDecisionHelper.SummarizeFailure("not json").Should().Be("all providers failed");
        TriagePoDecisionHelper.SummarizeFailure("{}").Should().Be("all providers failed");
    }

    // ================================================================
    // #2 — unparsed prose → needs-human-review, NOT a clean decision
    // ================================================================

    [Test]
    public void ParseDecision_Prose_IsUnparsed_NeedsHumanReview_NotCleanDecision()
    {
        var d = TriagePoDecisionHelper.ParseDecision(
            "I think this is probably a bug but I'm not totally sure, you should look at it.");

        d.Status.Should().Be(TriagePoDecisionHelper.StatusUnparsed);
        d.Automation.Should().Be("needs-human");
        d.Labels.Should().ContainSingle().Which.Should().Be("needs-human-review");
        // The raw prose is preserved for a human, NOT silently dropped.
        d.Comment.Should().Contain("probably a bug");
    }

    [Test]
    public void ParseDecision_Empty_IsUnparsed_WithPlaceholderComment()
    {
        var d = TriagePoDecisionHelper.ParseDecision("");
        d.Status.Should().Be(TriagePoDecisionHelper.StatusUnparsed);
        d.Comment.Should().Contain("requires human triage");
    }

    [Test]
    public void ParseDecision_NonObjectJson_IsUnparsed()
    {
        // A bare array between braces is not a decision object.
        TriagePoDecisionHelper.ParseDecision("[1,2,3]")
            .Status.Should().Be(TriagePoDecisionHelper.StatusUnparsed);
    }

    // ================================================================
    // Happy path — real JSON decision parses + populates reasoning
    // ================================================================

    [Test]
    public void ParseDecision_ValidJson_IsOk_AndCarriesFields()
    {
        var json = """
        Here is my decision:
        {"priority":"high","type":"bug","complexity":"medium","automation":"tamma-auto",
         "labels":["bug","priority-high"],"comment":"Clear defect.","reasoning":"NPE under load"}
        """;

        var d = TriagePoDecisionHelper.ParseDecision(json);

        d.Status.Should().Be(TriagePoDecisionHelper.StatusOk);
        d.Priority.Should().Be("high");
        d.Type.Should().Be("bug");
        d.Complexity.Should().Be("medium");
        d.Automation.Should().Be("tamma-auto");
        d.Labels.Should().BeEquivalentTo(new[] { "bug", "priority-high" });
        d.Comment.Should().Be("Clear defect.");
        d.Reasoning.Should().Be("NPE under load");
    }

    [Test]
    public void ParseDecision_MissingFields_DefaultSilently_NoClampNote()
    {
        var d = TriagePoDecisionHelper.ParseDecision("""{"comment":"only a comment"}""");

        d.Status.Should().Be(TriagePoDecisionHelper.StatusOk);
        d.Priority.Should().Be(TriagePoDecisionHelper.DefaultPriority);
        d.Type.Should().Be(TriagePoDecisionHelper.DefaultType);
        d.Automation.Should().Be(TriagePoDecisionHelper.DefaultAutomation);
        // Absent (not invalid) fields must NOT produce a clamp note.
        d.Comment.Should().Be("only a comment");
    }

    [TestCase("urgent")]
    [TestCase("critical")]
    [TestCase("normal")]
    [TestCase("medium")]
    [TestCase("low")]
    public void ParseDecision_PrioritySynonyms_AreAccepted(string priority)
    {
        var d = TriagePoDecisionHelper.ParseDecision($$"""{"priority":"{{priority}}"}""");
        d.Priority.Should().Be(priority);
        // No clamp note for an in-vocab value.
        d.Comment.Should().NotContain("invalid priority");
    }

    // ================================================================
    // #4 — out-of-vocab clamping + flag in comment
    // ================================================================

    [Test]
    public void ParseDecision_OutOfVocabPriority_IsClamped_AndFlagged()
    {
        // "P0" is the exact example from the spec — must NOT flow to labels.
        var d = TriagePoDecisionHelper.ParseDecision("""{"priority":"P0","comment":"prod down"}""");

        d.Priority.Should().Be(TriagePoDecisionHelper.DefaultPriority,
            "an out-of-vocab priority is clamped to the safe default");
        d.Comment.Should().Contain("prod down");
        d.Comment.Should().Contain("invalid priority=\"P0\"");
        d.Comment.Should().Contain($"defaulted to \"{TriagePoDecisionHelper.DefaultPriority}\"");
    }

    [Test]
    public void ParseDecision_OutOfVocabAutomation_IsClamped_AndFlagged()
    {
        // "auto" is the exact example from the spec.
        var d = TriagePoDecisionHelper.ParseDecision("""{"automation":"auto"}""");

        d.Automation.Should().Be(TriagePoDecisionHelper.DefaultAutomation);
        d.Comment.Should().Contain("invalid automation=\"auto\"");
    }

    [Test]
    public void ParseDecision_MultipleInvalidFields_AllFlagged()
    {
        var d = TriagePoDecisionHelper.ParseDecision(
            """{"priority":"P0","type":"epic","complexity":"huge","automation":"auto"}""");

        d.Comment.Should().Contain("invalid priority=\"P0\"");
        d.Comment.Should().Contain("invalid type=\"epic\"");      // "epic" is a complexity, not a type
        d.Comment.Should().Contain("invalid complexity=\"huge\"");
        d.Comment.Should().Contain("invalid automation=\"auto\"");
    }

    // ================================================================
    // Serialize — preserves the contract additively, labels as a JSON array
    // ================================================================

    [Test]
    public void Serialize_OkDecision_HasLabelsArray_AndStatus_AndReasoning()
    {
        var d = TriagePoDecisionHelper.ParseDecision(
            """{"priority":"high","type":"bug","labels":["bug"],"comment":"x","reasoning":"r"}""");
        var json = TriagePoDecisionHelper.Serialize(d);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("ok");
        root.GetProperty("priority").GetString().Should().Be("high");
        // labels MUST be a real JSON array so the consumer's List<string> binds.
        root.GetProperty("labels").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("labels").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("bug");
        root.GetProperty("reasoning").GetString().Should().Be("r");
    }

    [Test]
    public void Serialize_FailureDecision_RoundTripsIntoConsumerShape()
    {
        var json = TriagePoDecisionHelper.Serialize(TriagePoDecisionHelper.BuildFailureDecision("boom"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("llm-failed");
        root.GetProperty("automation").GetString().Should().Be("needs-human");
        root.GetProperty("labels").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("triage-failed");
    }

    [Test]
    public void ParseItemNumber_DelegatesToPanelHelper()
    {
        TriagePoDecisionHelper.ParseItemNumber("""{"number":7}""").Should().Be(7);
        TriagePoDecisionHelper.ParseItemNumber(null).Should().Be(0);
    }
}
