using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 — unit pins for <see cref="ReviewerSelectionHelper"/> (Design
/// Decision D3; covers AC4's validation half).
/// </summary>
[TestFixture]
public class ReviewerSelectionHelperTests
{
    // 7 → 9 (Story 41-1a D1/D2): + TechWriter (review-docs) and UxDesigner (review-design).
    private static readonly AgentRole[] DocumentRoster =
    {
        AgentRole.Architect, AgentRole.SeniorDeveloper, AgentRole.Security,
        AgentRole.Developer, AgentRole.Tester, AgentRole.Devops, AgentRole.ProductOwner,
        AgentRole.TechWriter, AgentRole.UxDesigner,
    };

    [Test]
    public void Resolve_DocumentSubject_MatchesRolePhaseMapReviewAction()
    {
        foreach (var role in DocumentRoster)
        {
            var spec = ReviewerSelectionHelper.Resolve(role.ToWire(), null, "document", null);
            spec.Role.Should().Be(role);
            spec.Action.Should().Be(RolePhaseMap.GetReviewActionForRole(role),
                $"the document reviewer action for {role.ToWire()} comes from RolePhaseMap");
        }
    }

    [Test]
    public void Resolve_DiffSubject_PinsTheFiveDiffPairs()
    {
        ReviewerSelectionHelper.Resolve("senior_developer", null, "diff", null).Action.Should().Be(AgentAction.CodeReview);
        ReviewerSelectionHelper.Resolve("developer", null, "diff", null).Action.Should().Be(AgentAction.CodeReview);
        ReviewerSelectionHelper.Resolve("architect", null, "diff", null).Action.Should().Be(AgentAction.CodeReviewArchitecture);
        ReviewerSelectionHelper.Resolve("security", null, "diff", null).Action.Should().Be(AgentAction.CodeReviewSecurity);
        ReviewerSelectionHelper.Resolve("tester", null, "diff", null).Action.Should().Be(AgentAction.CodeReviewCoverage);
    }

    [Test]
    public void Resolve_DevopsOnDiff_ThrowsRoleNotOnDiffPanel()
    {
        var act = () => ReviewerSelectionHelper.Resolve("devops", null, "diff", null);
        act.Should().Throw<TammaError>().Which.Code.Should().Be(ReviewerSelectionHelper.RoleNotOnDiffPanelCode);
    }

    [Test]
    public void Resolve_ProductOwnerOnDiff_ThrowsRoleNotOnDiffPanel()
    {
        var act = () => ReviewerSelectionHelper.Resolve("product_owner", null, "diff", null);
        act.Should().Throw<TammaError>().Which.Code.Should().Be(ReviewerSelectionHelper.RoleNotOnDiffPanelCode);
    }

    [Test]
    public void Resolve_UnknownRole_ThrowsInvalidReviewer()
    {
        var act = () => ReviewerSelectionHelper.Resolve("nobody", null, "document", null);
        act.Should().Throw<TammaError>().Which.Code.Should().Be(ReviewerSelectionHelper.InvalidReviewerCode);
    }

    [Test]
    public void Resolve_IneligibleActionOverride_ThrowsInvalidReviewer()
    {
        // deploy is devops-only — tester is not eligible for it.
        var act = () => ReviewerSelectionHelper.Resolve("tester", "deploy", "document", null);
        act.Should().Throw<TammaError>().Which.Code.Should().Be(ReviewerSelectionHelper.InvalidReviewerCode);
    }

    [Test]
    public void Resolve_UnknownActionOverride_ThrowsInvalidReviewer()
    {
        var act = () => ReviewerSelectionHelper.Resolve("architect", "not-an-action", "document", null);
        act.Should().Throw<TammaError>().Which.Code.Should().Be(ReviewerSelectionHelper.InvalidReviewerCode);
    }

    [Test]
    public void Resolve_ActionOverride_WinsOverDerivation()
    {
        // architect is eligible for code-review-architecture; the override selects it
        // even for a document subject.
        var spec = ReviewerSelectionHelper.Resolve("architect", "code-review-architecture", "document", null);
        spec.Action.Should().Be(AgentAction.CodeReviewArchitecture);
    }

    [Test]
    public void AllDispatchablePairs_AreEighteenAndAllEligible()
    {
        // Story 39-15 — 12 → 16: the 4 triage-panel pairs (doc-type-aware) join the
        // 7 document + 5 diff pairs when TriagePanelReviewWorkflow's semantics moved to
        // the 39-7 panel over a triage-decision draft.
        // Story 41-1a — 16 → 18: (tech_writer, review-docs) (D1) and
        // (ux_designer, review-design) (D2) join the document-review pairs.
        ReviewerSelectionHelper.AllDispatchablePairs.Should().HaveCount(18);
        ReviewerSelectionHelper.AllDispatchablePairs.Should().OnlyContain(
            p => RolePhaseMap.IsRoleEligibleForPhase(p.Action, p.Role));
    }

    // ── Story 41-1a — the new document-review arms and the asserted non-panel throws ──

    [Test]
    public void Resolve_TechWriterOnDocument_ReturnsReviewDocs()
    {
        // AC3's helper half: before 41-1a this threw REVIEW.PRODUCER.INVALID_REVIEWER
        // (GetReviewActionForRole had no TechWriter arm and the helper rethrows).
        var spec = ReviewerSelectionHelper.Resolve("tech_writer", null, "document", null);
        spec.Role.Should().Be(AgentRole.TechWriter);
        spec.Action.Should().Be(AgentAction.ReviewDocs);
    }

    [Test]
    public void Resolve_UxDesignerOnDocument_ReturnsReviewDesign()
    {
        var spec = ReviewerSelectionHelper.Resolve("ux_designer", null, "document", null);
        spec.Role.Should().Be(AgentRole.UxDesigner);
        spec.Action.Should().Be(AgentAction.ReviewDesign);
    }

    [Test]
    [TestCase("scrum_master")]
    [TestCase("project_manager")]
    public void Resolve_NonPanelNewRoleOnDocument_ThrowsInvalidReviewer(string role)
    {
        // AC4's "or" branch (D2): the two roles kept off the document panel fail
        // with the typed INVALID_REVIEWER error, never a raw ArgumentOutOfRange.
        var act = () => ReviewerSelectionHelper.Resolve(role, null, "document", null);
        act.Should().Throw<TammaError>().Which.Code.Should().Be(ReviewerSelectionHelper.InvalidReviewerCode);
    }

    [Test]
    public void Resolve_TechWriterOnDiff_StillThrowsRoleNotOnDiffPanel()
    {
        // D1 adds tech_writer to the DOCUMENT panel only; the diff roster is untouched.
        var act = () => ReviewerSelectionHelper.Resolve("tech_writer", null, "diff", null);
        act.Should().Throw<TammaError>().Which.Code.Should().Be(ReviewerSelectionHelper.RoleNotOnDiffPanelCode);
    }

    // ── Story 39-15 (39-7 extension) — the doc-type-aware panel action selection ──

    [Test]
    public void Resolve_TriageDecisionSubject_YieldsTriagePerRoleActions()
    {
        // The four triage roles reviewing a triage-decision draft resolve to their TRIAGE
        // lens (GetTriageActionForRole), NOT the plan/task review lens.
        ReviewerSelectionHelper.Resolve("security", null, "document", "triage-decision").Action
            .Should().Be(AgentAction.AssessVulnerability);
        ReviewerSelectionHelper.Resolve("developer", null, "document", "triage-decision").Action
            .Should().Be(AgentAction.TriageDefect);
        ReviewerSelectionHelper.Resolve("tester", null, "document", "triage-decision").Action
            .Should().Be(AgentAction.TriageDefect);
        ReviewerSelectionHelper.Resolve("devops", null, "document", "triage-decision").Action
            .Should().Be(AgentAction.DiagnoseIncident);
    }

    [Test]
    public void Resolve_NonTriageDocument_StillYieldsReviewActions_DocPathUnchanged()
    {
        // The document path stays byte-identical for every non-triage doc type: the
        // review lens (GetReviewActionForRole) still applies (a null and a non-triage key both).
        ReviewerSelectionHelper.Resolve("architect", null, "document", "plan").Action
            .Should().Be(AgentAction.PlanReview);
        ReviewerSelectionHelper.Resolve("security", null, "document", "plan").Action
            .Should().Be(AgentAction.PlanReviewSecurity);
        ReviewerSelectionHelper.Resolve("developer", null, "document", null).Action
            .Should().Be(AgentAction.ReviewFeasibility);
    }

    [Test]
    public void AllDispatchablePairs_ContainsTheFourTriagePanelPairs()
    {
        var pairs = ReviewerSelectionHelper.AllDispatchablePairs;
        pairs.Should().Contain(("security", "assess-vulnerability"));
        pairs.Should().Contain(("developer", "triage-defect"));
        pairs.Should().Contain(("tester", "triage-defect"));
        pairs.Should().Contain(("devops", "diagnose-incident"));
    }

    [Test]
    public void ResolvePanelRoster_EmptyRules_FallsBackToSingleArchitect()
    {
        var roster = ReviewerSelectionHelper.ResolvePanelRoster("");
        roster.Should().ContainSingle().Which.Should().Be(AgentRole.Architect.ToWire());
    }

    [Test]
    public void ResolvePanelRoster_PanelRules_ReturnsFullRoster()
    {
        var json = AcceptanceRulesJson.Serialize(AcceptanceDefaults.For(DocumentTypeKey.Plan));
        var roster = ReviewerSelectionHelper.ResolvePanelRoster(json);
        roster.Should().BeEquivalentTo(AcceptanceDefaults.PanelRoster);
    }
}
