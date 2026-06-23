using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness audit 2026-06-22 — the CORE regression coverage for the
/// triage-panel build-out: <see cref="TriagePanelAggregationHelper"/> must be
/// <b>fail-closed</b>. A failed/empty per-role review is NEVER coalesced to a
/// <c>{}</c> participant reported as a success; it surfaces a per-role failure
/// signal (<c>status="failed"</c>) and is counted in <c>failedRoles</c>, and a
/// wholly-failed panel reports <c>panelStatus="failed"</c> (the no-false-success
/// rule). These are pure-function tests independent of the Elsa runtime.
/// </summary>
[TestFixture]
public class TriagePanelAggregationHelperTests
{
    private static readonly IReadOnlyList<string> Roster =
        new[] { "security", "developer", "devops", "tester" };

    // ================================================================
    // ClassifyRole — the fail-closed unit (no {}-as-participant)
    // ================================================================

    [Test]
    public void ClassifyRole_EmptyObjectSentinel_IsFailed_NotParticipant()
    {
        // The "{}" sentinel is the no-usable-review marker — it must classify as
        // FAILED, never as an ok participant. This is the headline bug.
        var rr = TriagePanelAggregationHelper.ClassifyRole("security", "{}");

        rr.Ok.Should().BeFalse("an empty {} review is not a usable assessment");
        rr.Role.Should().Be("security");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ClassifyRole_NullOrBlank_IsFailed(string? review)
    {
        TriagePanelAggregationHelper.ClassifyRole("developer", review)
            .Ok.Should().BeFalse();
    }

    [Test]
    public void ClassifyRole_UnparseableJson_IsFailed_FailClosed()
    {
        // Garbage from the LLM must NOT pass as a usable assessment.
        TriagePanelAggregationHelper.ClassifyRole("devops", "{not valid json")
            .Ok.Should().BeFalse();
    }

    [Test]
    public void ClassifyRole_NonObjectJson_IsFailed()
    {
        // A bare JSON array / scalar is not a structured assessment object.
        TriagePanelAggregationHelper.ClassifyRole("tester", "[1,2,3]")
            .Ok.Should().BeFalse();
    }

    [Test]
    public void ClassifyRole_NonEmptyObject_IsOk_AndParsesStructuredFields()
    {
        var json = """
        {"verdict":"defect","severity":"high","suggestedLabels":["bug","p1"],"notes":"npe on null path"}
        """;

        var rr = TriagePanelAggregationHelper.ClassifyRole("developer", json);

        rr.Ok.Should().BeTrue();
        rr.Verdict.Should().Be("defect");
        rr.Severity.Should().Be("high");
        rr.SuggestedLabels.Should().BeEquivalentTo("bug", "p1");
        rr.Notes.Should().Be("npe on null path");
        rr.RawAssessment.Should().Be(json);
    }

    [Test]
    public void ClassifyRole_RawAssessmentWrapper_IsOk_EvenWithoutTypedFields()
    {
        // Free-form prose wrapped as {"rawAssessment": "..."} is still a usable
        // assessment — its absence of verdict/severity does NOT make it failed.
        var json = """{"rawAssessment":"This looks like a config drift incident."}""";

        var rr = TriagePanelAggregationHelper.ClassifyRole("devops", json);

        rr.Ok.Should().BeTrue();
        rr.Verdict.Should().BeEmpty();
    }

    // ================================================================
    // Aggregate — panel health + status (the contract the PO consumes)
    // ================================================================

    [Test]
    public void Aggregate_AllRolesUsable_IsOk_WithFullSucceededCount()
    {
        var reviews = AllOk();

        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 2);

        result.PanelStatus.Should().Be(TriagePanelAggregationHelper.StatusOk);
        result.SucceededCount.Should().Be(4);
        result.ReviewCount.Should().Be(4);
        result.FailedRoles.Should().BeEmpty();
    }

    [Test]
    public void Aggregate_OneRoleFailed_AtQuorum_IsPartial_NotOk_NotFailed()
    {
        var reviews = AllOk();
        reviews["tester"] = "{}"; // tester failed

        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 2);

        result.PanelStatus.Should().Be(TriagePanelAggregationHelper.StatusPartial,
            "one failure above quorum is degraded, not a clean success and not a failure");
        result.SucceededCount.Should().Be(3);
        result.FailedRoles.Should().ContainSingle().Which.Should().Be("tester");
    }

    [Test]
    public void Aggregate_AllRolesFailed_IsFailed_NoFalseSuccess()
    {
        // THE core regression: a fully-failed panel must report panelStatus="failed"
        // — never coalesced to four {} reviews reported as a usable/ok panel.
        var reviews = new Dictionary<string, string?>
        {
            ["security"] = "{}",
            ["developer"] = null,
            ["devops"] = "garbage",
            ["tester"] = "",
        };

        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 2);

        result.PanelStatus.Should().Be(TriagePanelAggregationHelper.StatusFailed);
        result.SucceededCount.Should().Be(0);
        result.FailedRoles.Should().BeEquivalentTo(Roster);
    }

    [Test]
    public void Aggregate_BelowQuorum_IsFailed_EvenWithSomeUsableReviews()
    {
        // 1 usable review but quorum is 2 → failed (fail-closed: too few to decide).
        var reviews = new Dictionary<string, string?>
        {
            ["security"] = """{"verdict":"vuln","severity":"critical"}""",
            ["developer"] = "{}",
            ["devops"] = "{}",
            ["tester"] = "{}",
        };

        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 2);

        result.PanelStatus.Should().Be(TriagePanelAggregationHelper.StatusFailed);
        result.SucceededCount.Should().Be(1);
    }

    [Test]
    public void Aggregate_MissingRoleInDictionary_CountsAsFailed_FailClosed()
    {
        // A role entirely absent from the results map must be treated as failed,
        // not skipped — the roster, and thus the failure roster, stays complete.
        var reviews = new Dictionary<string, string?>
        {
            ["security"] = """{"verdict":"ok"}""",
            ["developer"] = """{"verdict":"ok"}""",
            // devops + tester absent
        };

        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 2);

        result.ReviewCount.Should().Be(4, "every roster role is represented");
        result.SucceededCount.Should().Be(2);
        result.FailedRoles.Should().BeEquivalentTo(new[] { "devops", "tester" });
    }

    [Test]
    public void Aggregate_QuorumBelowOne_IsClampedToOne()
    {
        var reviews = new Dictionary<string, string?>
        {
            ["security"] = "{}",
            ["developer"] = "{}",
            ["devops"] = "{}",
            ["tester"] = "{}",
        };

        // quorum 0 must not make a zero-success panel "usable".
        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 0);

        result.PanelStatus.Should().Be(TriagePanelAggregationHelper.StatusFailed);
    }

    // ================================================================
    // Serialize — failed roles present in roster but NOT as {} successes
    // ================================================================

    [Test]
    public void Serialize_FailedRole_HasStatusFailed_AndEmptyAssessment_NotBraces()
    {
        var reviews = AllOk();
        reviews["devops"] = "{}"; // failed

        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 2);
        var json = TriagePanelAggregationHelper.Serialize(result);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("panelStatus").GetString().Should().Be("partial");
        root.GetProperty("succeededCount").GetInt32().Should().Be(3);
        root.GetProperty("reviewCount").GetInt32().Should().Be(4);

        var failed = root.GetProperty("reviews").EnumerateArray()
            .Single(r => r.GetProperty("role").GetString() == "devops");

        failed.GetProperty("status").GetString().Should().Be("failed");
        // The failed role's assessment must be empty — NOT a "{}" that the PO
        // could mistake for an empty-but-present assessment.
        failed.GetProperty("assessment").GetString().Should().BeEmpty();

        var failedRoles = root.GetProperty("failedRoles").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        failedRoles.Should().Contain("devops");
    }

    [Test]
    public void Serialize_OkRole_CarriesRawAssessment()
    {
        var reviews = AllOk();
        var result = TriagePanelAggregationHelper.Aggregate(Roster, reviews, quorum: 2);
        var json = TriagePanelAggregationHelper.Serialize(result);

        using var doc = JsonDocument.Parse(json);
        var sec = doc.RootElement.GetProperty("reviews").EnumerateArray()
            .Single(r => r.GetProperty("role").GetString() == "security");

        sec.GetProperty("status").GetString().Should().Be("ok");
        sec.GetProperty("assessment").GetString().Should().NotBeEmpty();
    }

    // ================================================================
    // EventTypeForStatus + ParseItemNumber helpers
    // ================================================================

    [Test]
    public void EventTypeForStatus_MapsEachStatusToItsTerminalEvent()
    {
        TriagePanelAggregationHelper.EventTypeForStatus(TriagePanelAggregationHelper.StatusOk)
            .Should().Be(Tamma.Activities.ADL.TriageEvents.PanelCompleted);
        TriagePanelAggregationHelper.EventTypeForStatus(TriagePanelAggregationHelper.StatusPartial)
            .Should().Be(Tamma.Activities.ADL.TriageEvents.PanelPartial);
        TriagePanelAggregationHelper.EventTypeForStatus(TriagePanelAggregationHelper.StatusFailed)
            .Should().Be(Tamma.Activities.ADL.TriageEvents.PanelFailed);
        // Unknown status is treated as failed (fail-closed default).
        TriagePanelAggregationHelper.EventTypeForStatus("nonsense")
            .Should().Be(Tamma.Activities.ADL.TriageEvents.PanelFailed);
    }

    [Test]
    public void ParseItemNumber_ReadsNumberField()
    {
        TriagePanelAggregationHelper.ParseItemNumber("""{"number":42,"title":"x"}""")
            .Should().Be(42);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("""{"title":"no number"}""")]
    public void ParseItemNumber_MissingOrMalformed_ReturnsZero(string? itemJson)
    {
        TriagePanelAggregationHelper.ParseItemNumber(itemJson).Should().Be(0);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static Dictionary<string, string?> AllOk() => new()
    {
        ["security"] = """{"verdict":"vuln","severity":"high"}""",
        ["developer"] = """{"verdict":"defect","severity":"medium"}""",
        ["devops"] = """{"verdict":"incident","severity":"low"}""",
        ["tester"] = """{"verdict":"defect","severity":"medium"}""",
    };
}
