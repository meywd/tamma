using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>
/// Drift tests pinning the shipped acceptance defaults (Story 39-5 AC5). Same
/// posture as <c>RolePhaseMapTests</c> — changing a default is a conscious,
/// reviewed edit here. Pins the shared knobs AND the per-type reviewer defaults
/// (panel-for-plan/review vs single-architect-otherwise) so 39-14's PlanReview
/// migration cannot silently regress.
/// </summary>
[TestFixture]
public class AcceptanceDefaultsDriftTests
{
    [Test]
    public void Shared_knobs_are_pinned()
    {
        var r = AcceptanceDefaults.Rules;
        r.AutonomyLevel.Should().Be(70);
        r.MaxRevisionRounds.Should().Be(2);
        r.MaxValidationRepairAttempts.Should().Be(2);
        r.AmbiguityEscalationThreshold.Should().Be(0.7);
        r.AlwaysEscalate.Should().BeEmpty();
        r.DecisionGuidance.Should().NotBeNullOrWhiteSpace();
        r.RoutingGuidance.Should().NotBeNullOrWhiteSpace();
        r.AcceptorRequirement.Should().Be(AcceptorRequirement.Any,
            "the shared base row imposes no autonomy floor — the pre-39-13 behavior");
    }

    [Test]
    public void Base_row_is_single_architect_unanimous()
    {
        var sel = AcceptanceDefaults.Rules.ReviewerSelection;
        sel.Mode.Should().Be(ReviewerMode.SingleReviewer);
        sel.ReviewerRole.Should().Be(AgentRole.Architect.ToWire());
        sel.PanelRoles.Should().BeEmpty();
        sel.DecisionRule.Should().Be(ReviewDecisionRule.Unanimous);
    }

    [Test]
    public void Panel_roster_is_the_exact_seven_roles()
    {
        AcceptanceDefaults.PanelRoster.Should().Equal(
            AgentRole.Architect.ToWire(),
            AgentRole.Developer.ToWire(),
            AgentRole.Tester.ToWire(),
            AgentRole.Security.ToWire(),
            AgentRole.Devops.ToWire(),
            AgentRole.ProductOwner.ToWire(),
            AgentRole.SeniorDeveloper.ToWire());
        AcceptanceDefaults.PanelRoster.Should().HaveCount(7);
        AcceptanceDefaults.PanelRoster.Should().NotContain(AgentRole.TechWriter.ToWire());
    }

    [TestCase(DocumentTypeKey.Plan)]
    [TestCase(DocumentTypeKey.Review)]
    public void Plan_and_review_default_to_a_majority_panel(DocumentTypeKey type)
    {
        var sel = AcceptanceDefaults.For(type).ReviewerSelection;
        sel.Mode.Should().Be(ReviewerMode.Panel);
        sel.DecisionRule.Should().Be(ReviewDecisionRule.Majority);
        sel.ReviewerRole.Should().BeNull();
        sel.PanelRoles.Should().Equal(AcceptanceDefaults.PanelRoster);
    }

    [TestCase(DocumentTypeKey.Findings)]
    [TestCase(DocumentTypeKey.AmbiguityAssessment)]
    [TestCase(DocumentTypeKey.Clarification)]
    [TestCase(DocumentTypeKey.Decomposition)]
    [TestCase(DocumentTypeKey.Design)]
    [TestCase(DocumentTypeKey.TriageDecision)]
    [TestCase(DocumentTypeKey.Diagnosis)]
    [TestCase(DocumentTypeKey.TestSpec)]
    public void Every_other_type_defaults_to_single_architect_unanimous(DocumentTypeKey type)
    {
        var sel = AcceptanceDefaults.For(type).ReviewerSelection;
        sel.Mode.Should().Be(ReviewerMode.SingleReviewer);
        sel.ReviewerRole.Should().Be(AgentRole.Architect.ToWire());
        sel.DecisionRule.Should().Be(ReviewDecisionRule.Unanimous);
    }

    // ── Story 39-13 D4 — the per-type acceptor requirement (autonomy floor) ──

    [Test]
    public void Design_defaults_to_a_human_acceptor()
    {
        // 39-13 D4: "Design pinned to human by default". Reviewer selection is UNCHANGED
        // (single architect, unanimous) — only who answers the accept decision is pinned.
        var rules = AcceptanceDefaults.For(DocumentTypeKey.Design);
        rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
        rules.ReviewerSelection.Should().Be(AcceptanceDefaults.Rules.ReviewerSelection);
        rules.AutonomyLevel.Should().Be(AcceptanceDefaults.DefaultAutonomyLevel,
            "the human pin is independent of the autonomy dial, not a lower dial");
    }

    [TestCase(DocumentTypeKey.Findings)]
    [TestCase(DocumentTypeKey.AmbiguityAssessment)]
    [TestCase(DocumentTypeKey.Clarification)]
    [TestCase(DocumentTypeKey.Decomposition)]
    [TestCase(DocumentTypeKey.Plan)]
    [TestCase(DocumentTypeKey.Review)]
    [TestCase(DocumentTypeKey.TriageDecision)]
    [TestCase(DocumentTypeKey.Diagnosis)]
    [TestCase(DocumentTypeKey.TestSpec)]
    public void Every_type_but_design_imposes_no_acceptor_floor(DocumentTypeKey type) =>
        AcceptanceDefaults.For(type).AcceptorRequirement.Should().Be(AcceptorRequirement.Any,
            "the field is additive — only 'design' ships a non-default value");

    [Test]
    public void Design_is_the_only_type_with_an_acceptor_floor() =>
        Enum.GetValues<DocumentTypeKey>()
            .Where(t => AcceptanceDefaults.For(t).AcceptorRequirement != AcceptorRequirement.Any)
            .Should().Equal(DocumentTypeKey.Design);

    [Test]
    public void Every_per_type_default_is_valid()
    {
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
            AcceptanceDefaults.For(type).Invoking(r => r.Validate()).Should().NotThrow();
    }
}
