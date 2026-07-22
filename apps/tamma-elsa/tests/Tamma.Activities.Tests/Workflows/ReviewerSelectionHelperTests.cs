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
    private static readonly AgentRole[] DocumentRoster =
    {
        AgentRole.Architect, AgentRole.SeniorDeveloper, AgentRole.Security,
        AgentRole.Developer, AgentRole.Tester, AgentRole.Devops, AgentRole.ProductOwner,
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
    public void AllDispatchablePairs_AreTwelveAndAllEligible()
    {
        ReviewerSelectionHelper.AllDispatchablePairs.Should().HaveCount(12);
        ReviewerSelectionHelper.AllDispatchablePairs.Should().OnlyContain(
            p => RolePhaseMap.IsRoleEligibleForPhase(p.Action, p.Role));
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
