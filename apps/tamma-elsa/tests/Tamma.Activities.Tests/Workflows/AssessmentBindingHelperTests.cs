using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-13 (D4) — property-level coverage for <see cref="AssessmentBindingHelper"/>, the
/// pure, fail-closed core the four assessment-family bindings read their lifecycle payloads
/// through. Every read fail-closes to conservative zeros on an unreadable body; the threshold
/// falls back to the 39-5 default; the outcome/failure helpers name every wire.
/// </summary>
[TestFixture]
public class AssessmentBindingHelperTests
{
    private static LifecycleBindingHelper.LifecycleExit Exit(string status, string? outcome = null, string docJson = "{}")
        => new(status, outcome, "doc-1", docJson, "");

    // ── ReadFindings ─────────────────────────────────────────────────────
    [Test]
    public void ReadFindings_Valid_ReadsCountAndConfidence()
    {
        const string json = """{"summary":"s","findings":[{"title":"a","citations":["x"]},{"title":"b","citations":["y"]}],"overallConfidence":0.8}""";
        var (count, conf) = AssessmentBindingHelper.ReadFindings(json);
        count.Should().Be(2);
        conf.Should().BeApproximately(0.8, 1e-9);
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{}")]
    public void ReadFindings_Unreadable_FailClosedZeros(string json)
        => AssessmentBindingHelper.ReadFindings(json).Should().Be((0, 0d));

    // ── ReadAssessment ───────────────────────────────────────────────────
    [Test]
    public void ReadAssessment_Valid_ReadsScoreCountConfidence()
    {
        const string json = """{"score":0.72,"confidence":0.9,"ambiguities":[{"type":"missing","description":"d"}]}""";
        var (score, count, conf) = AssessmentBindingHelper.ReadAssessment(json);
        score.Should().BeApproximately(0.72, 1e-9);
        count.Should().Be(1);
        conf.Should().BeApproximately(0.9, 1e-9);
    }

    [TestCase("")]
    [TestCase("garbage")]
    public void ReadAssessment_Unreadable_FailClosedZeros(string json)
        => AssessmentBindingHelper.ReadAssessment(json).Should().Be((0d, 0, 0d));

    // ── EffectiveAmbiguityThreshold ──────────────────────────────────────
    [TestCase("")]
    [TestCase(null)]
    [TestCase("not json")]
    public void EffectiveAmbiguityThreshold_EmptyOrUnreadable_FallsBackToDefault(string? json)
        => AssessmentBindingHelper.EffectiveAmbiguityThreshold(json)
            .Should().Be(AcceptanceDefaults.DefaultAmbiguityEscalationThreshold);

    [Test]
    public void EffectiveAmbiguityThreshold_RulesJson_ReadsTheConfiguredValue()
    {
        var rules = AcceptanceDefaults.Rules with { AmbiguityEscalationThreshold = 0.42 };
        var json = AcceptanceRulesJson.Serialize(rules);
        AssessmentBindingHelper.EffectiveAmbiguityThreshold(json).Should().Be(0.42);
    }

    // ── IsAmbiguityOutcome ───────────────────────────────────────────────
    [Test]
    public void IsAmbiguityOutcome_OnlyTrueForTheAmbiguityWire()
    {
        AssessmentBindingHelper.IsAmbiguityOutcome(
            Exit("escalated", DocumentLifecycleOutcome.AmbiguityAboveThreshold.ToWire())).Should().BeTrue();
        AssessmentBindingHelper.IsAmbiguityOutcome(
            Exit("escalated", DocumentLifecycleOutcome.ValidationExhausted.ToWire())).Should().BeFalse();
        AssessmentBindingHelper.IsAmbiguityOutcome(Exit("accepted")).Should().BeFalse();
    }

    // ── ReadClarification ────────────────────────────────────────────────
    [Test]
    public void ReadClarification_Questions_ReadsCount()
        => AssessmentBindingHelper.ReadClarification("""{"phase":"questions","questions":["a","b"]}""")
            .Should().Be((2, false));

    [Test]
    public void ReadClarification_Resolution_ReadsResolved()
        => AssessmentBindingHelper.ReadClarification("""{"phase":"resolution","clarifiedRequirement":"x","resolved":true}""")
            .Should().Be((0, true));

    [TestCase("")]
    [TestCase("bad")]
    public void ReadClarification_Unreadable_FailClosed(string json)
        => AssessmentBindingHelper.ReadClarification(json).Should().Be((0, false));

    // ── CountAlternatives ────────────────────────────────────────────────
    [Test]
    public void CountAlternatives_Valid_Counts()
        => AssessmentBindingHelper.CountAlternatives(
            """{"summary":"s","alternatives":[{"id":"1"},{"id":"2"},{"id":"3"}]}""").Should().Be(3);

    [TestCase("")]
    [TestCase("nope")]
    public void CountAlternatives_Unreadable_Zero(string json)
        => AssessmentBindingHelper.CountAlternatives(json).Should().Be(0);

    // ── BuildFailureDetail — names status + outcome wire ─────────────────
    [Test]
    public void BuildFailureDetail_WithOutcome_NamesBoth()
        => AssessmentBindingHelper.BuildFailureDetail(Exit("escalated", "rounds-exhausted"))
            .Should().Contain("escalated").And.Contain("rounds-exhausted");

    [Test]
    public void BuildFailureDetail_NoOutcome_NamesStatus()
        => AssessmentBindingHelper.BuildFailureDetail(Exit("rejected"))
            .Should().Contain("rejected");
}
