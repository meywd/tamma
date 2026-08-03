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
        // Story 41-1a (C7): this roster is the DEFAULT PANEL MEMBERSHIP of the
        // shipped acceptance rules and is DELIBERATELY unchanged by D1/D2 —
        // tech_writer/ux_designer joined ReviewerSelectionHelper's SELECTOR
        // domain (s_documentRoster, 7 -> 9), which is a different surface.
        // Moving THIS roster would silently seat them on every existing panel
        // review; 41-24/41-25/41-26/41-28 select them as single reviewers per
        // document type instead. Do not "fix" the 7.
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
    // 41-1b D1: acceptance-criteria is the merge gate's definition of done (the
    // same breadth plan/review get); a ux-spec is cross-functional — the 7-role
    // panel is the honest default until 41-28 defines a design panel.
    [TestCase(DocumentTypeKey.AcceptanceCriteria)]
    [TestCase(DocumentTypeKey.UxSpec)]
    public void Panel_types_default_to_a_majority_panel(DocumentTypeKey type)
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

    // ── Story 41-1b D1 — the single-reviewer overrides for the new types ──

    [TestCase(DocumentTypeKey.BacklogOrdering, AgentRole.ProductOwner)]
    [TestCase(DocumentTypeKey.SprintPlan, AgentRole.ProductOwner)]
    [TestCase(DocumentTypeKey.TestPlan, AgentRole.Tester)]
    [TestCase(DocumentTypeKey.ThreatModel, AgentRole.Security)]
    public void The_41_1b_single_reviewer_types_get_their_domain_reviewer(DocumentTypeKey type, AgentRole reviewer)
    {
        // backlog-ordering: a PO judgment; sprint-plan: PO until 41-1a/41-6 add a
        // scrum_master surface; test-plan: QA reviews strategy; threat-model: a
        // security-owned call. None falls through to the architect base row.
        var rules = AcceptanceDefaults.For(type);
        rules.Should().NotBe(AcceptanceDefaults.Rules,
            $"'{type.ToWire()}' must not silently take the single-architect catch-all (41-1b D1)");
        var sel = rules.ReviewerSelection;
        sel.Mode.Should().Be(ReviewerMode.SingleReviewer);
        sel.ReviewerRole.Should().Be(reviewer.ToWire());
        sel.DecisionRule.Should().Be(ReviewDecisionRule.Unanimous);
    }

    // ── Story 41-1c D6 — prose gets a single tech_writer reviewer ──

    [Test]
    public void Prose_defaults_to_a_single_tech_writer_reviewer()
    {
        // 41-1c AC6: prose must NOT reach the `_ => Rules` architect catch-all by
        // accident, and must NOT be a panel row (PanelRoster deliberately excludes
        // tech_writer — the pin above). Acceptor requirement stays Any: the
        // autonomy dial alone decides who accepts prose.
        var rules = AcceptanceDefaults.For(DocumentTypeKey.Prose);
        rules.Should().NotBe(AcceptanceDefaults.Rules,
            "'prose' must not silently take the single-architect catch-all (41-1c AC6)");
        var sel = rules.ReviewerSelection;
        sel.Mode.Should().Be(ReviewerMode.SingleReviewer);
        sel.ReviewerRole.Should().Be(AgentRole.TechWriter.ToWire());
        sel.PanelRoles.Should().BeEmpty();
        sel.DecisionRule.Should().Be(ReviewDecisionRule.Unanimous);
        rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
    }

    // ── Story 43-16 — the acceptor requirement is DERIVED, not stored ──

    [Test]
    public void Design_ships_no_stored_human_acceptor_the_floor_is_derived()
    {
        // Story 43-16 (form α): the human acceptor for design is no longer a
        // stored constant — it is derived from design's catalog level against the
        // dial (see AcceptanceFloors.ShippedFloorFor and
        // ActionCatalogDefaultsTests.ShippedAcceptorFloor_IsTheCatalogLevel…).
        // For(Design) therefore returns Any; the reviewer stays single-architect.
        var rules = AcceptanceDefaults.For(DocumentTypeKey.Design);
        rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Any);
        rules.ReviewerSelection.Should().Be(AcceptanceDefaults.Rules.ReviewerSelection);
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
    [TestCase(DocumentTypeKey.AcceptanceCriteria)]
    [TestCase(DocumentTypeKey.BacklogOrdering)]
    [TestCase(DocumentTypeKey.TestPlan)]
    [TestCase(DocumentTypeKey.UxSpec)]
    [TestCase(DocumentTypeKey.Prose)]
    // Story 43-16 extends the set 14 → 17: no type ships a stored acceptor floor
    // any longer, so the former human-pinned trio joins the list.
    [TestCase(DocumentTypeKey.Design)]
    [TestCase(DocumentTypeKey.SprintPlan)]
    [TestCase(DocumentTypeKey.ThreatModel)]
    public void Every_type_imposes_no_stored_acceptor_floor(DocumentTypeKey type) =>
        AcceptanceDefaults.For(type).AcceptorRequirement.Should().Be(AcceptorRequirement.Any,
            "the stored acceptor requirement is uniformly Any (Story 43-16) — the "
            + "human floor for design/sprint-plan/threat-model is derived, not stored");

    [Test]
    public void No_type_ships_a_stored_acceptor_floor() =>
        Enum.GetValues<DocumentTypeKey>()
            .Where(t => AcceptanceDefaults.For(t).AcceptorRequirement != AcceptorRequirement.Any)
            .Should().BeEmpty("Story 43-16 moved the acceptor floor from a stored constant to a derivation");

    [Test]
    public void Every_per_type_default_is_valid()
    {
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
            AcceptanceDefaults.For(type).Invoking(r => r.Validate()).Should().NotThrow();
    }
}
