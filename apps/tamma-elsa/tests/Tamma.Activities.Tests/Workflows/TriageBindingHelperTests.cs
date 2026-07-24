using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-15 (D5/D6/D9) — pins for <see cref="TriageBindingHelper"/>, the pure decision
/// core of the two triage lifecycle bindings. Fail-closed on garbage; the legacy
/// <c>decisionJson</c> projection round-trips through
/// <see cref="TriagePoDecisionHelper.ParseDecision"/> to <c>StatusOk</c> (39-4 D6 pin);
/// the default rules are a valid panel-over-triage-roster; the panel mirror is honest.
/// </summary>
[TestFixture]
public class TriageBindingHelperTests
{
    [Test]
    public void ProjectLegacyDecisionJson_AcceptedDecision_RoundTripsToStatusOk_NoClamp()
    {
        var accepted = """
        {
          "priority": "high",
          "type": "bug",
          "complexity": "simple",
          "automation": "tamma-auto",
          "reasoning": "reproducible null-ref with a clear fix scope",
          "labels": ["bug"]
        }
        """;

        var legacy = TriageBindingHelper.ProjectLegacyDecisionJson(accepted);
        var reparsed = TriagePoDecisionHelper.ParseDecision(legacy);

        reparsed.Status.Should().Be(TriagePoDecisionHelper.StatusOk,
            "an accepted TriageDecision projects to a clean StatusOk legacy decision with no clamp notes");
        reparsed.Priority.Should().Be("high");
        reparsed.Type.Should().Be("bug");
        reparsed.Complexity.Should().Be("simple");
        reparsed.Automation.Should().Be("tamma-auto");
        reparsed.Labels.Should().Contain("bug");
    }

    [Test]
    public void ProjectLegacyDecisionJson_Garbage_FailsClosedToUnparsedNeedsHuman()
    {
        foreach (var junk in new[] { "", "   ", "not json", "[1,2,3]", "{}" })
        {
            var legacy = TriageBindingHelper.ProjectLegacyDecisionJson(junk);
            var reparsed = TriagePoDecisionHelper.ParseDecision(legacy);
            // The honest fallback is a needs-human decision carrying the needs-human-review marker —
            // never a fabricated clean priority-normal/feature classification.
            reparsed.Automation.Should().Be(TriagePoDecisionHelper.DefaultAutomation,
                $"'{junk}' must fall closed to needs-human, not a fabricated safe-to-automate decision");
            reparsed.Labels.Should().Contain("needs-human-review",
                $"'{junk}' must carry the honest needs-human-review marker");
        }
    }

    [Test]
    public void ReadPanelMirror_Accepted_DerivesFullRosterUsable_WhenResultCarriesNoPanelData()
    {
        var mirror = TriageBindingHelper.ReadPanelMirror("{}", accepted: true, rosterSize: 4);
        mirror.MemberCount.Should().Be(4);
        mirror.SucceededCount.Should().Be(4);
        mirror.FailedRolesJson.Should().Be("[]");
    }

    [Test]
    public void ReadPanelMirror_NonAccept_DerivesZeroUsable()
    {
        var mirror = TriageBindingHelper.ReadPanelMirror(null, accepted: false, rosterSize: 4);
        mirror.MemberCount.Should().Be(4);
        mirror.SucceededCount.Should().Be(0);
    }

    [Test]
    public void ReadPanelMirror_PrefersExplicitPanelDataFromLineage()
    {
        var result = """{"panelMemberCount":4,"panelSucceededCount":3,"panelFailedRoles":["devops"]}""";
        var mirror = TriageBindingHelper.ReadPanelMirror(result, accepted: true, rosterSize: 4);
        mirror.MemberCount.Should().Be(4);
        mirror.SucceededCount.Should().Be(3);
        mirror.FailedRolesJson.Should().Contain("devops");
    }

    [Test]
    public void DefaultTriageRulesJson_IsAValidPanelOverTheTriageRoster_WithQuorumTwo()
    {
        var json = TriageBindingHelper.DefaultTriageRulesJson();
        var rules = AcceptanceRulesJson.Deserialize(json);
        rules.ReviewerSelection.Mode.Should().Be(ReviewerMode.Panel);
        rules.ReviewerSelection.PanelRoles.Should().BeEquivalentTo(new[]
        {
            "security", "developer", "tester", "devops",
        });
        rules.ReviewerSelection.Quorum.Should().Be(2);
        rules.AlwaysEscalate.Should().NotBeEmpty("a needs-human triage decision always escalates to a human");
        // A valid ruleset does not throw on Validate.
        ((System.Action)(() => rules.Validate())).Should().NotThrow();
    }

    [Test]
    public void ParseItemNumber_ReadsNumber_ElseZero()
    {
        TriageBindingHelper.ParseItemNumber("""{"number":42}""").Should().Be(42);
        TriageBindingHelper.ParseItemNumber("").Should().Be(0);
        TriageBindingHelper.ParseItemNumber("not json").Should().Be(0);
        TriageBindingHelper.ParseItemNumber("""{"title":"no number"}""").Should().Be(0);
    }

    [Test]
    public void BuildFailureDetail_NamesStatusAndOutcome()
    {
        var exit = new LifecycleBindingHelper.LifecycleExit("escalated", "validation-exhausted", null, "{}", "");
        TriageBindingHelper.BuildFailureDetail(exit).Should().Contain("escalated").And.Contain("validation-exhausted");
    }
}
