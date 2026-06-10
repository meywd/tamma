using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Tests for the static role ↔ action mapping, rebuilt on the
/// <see cref="AgentRole"/> / <see cref="AgentAction"/> enums (SPEC §4).
///
/// The 8 roles: developer, tester, security, devops, architect, product_owner,
/// senior_developer, tech_writer.
///
/// The 72 actions are the union of the per-role action sets in SPEC §4.
/// Which (role, action) pairs are valid is the per-role eligibility matrix.
/// </summary>
[TestFixture]
public class RolePhaseMapTests
{
    // -----------------------------------------------------------------------
    // Roles / Actions constants — derived from the enums
    // -----------------------------------------------------------------------

    [Test]
    public void ValidRoles_Should_Contain_All_Eight_Roles()
    {
        RolePhaseMap.ValidRoles.Should().BeEquivalentTo(new[]
        {
            "developer", "tester", "security", "devops",
            "architect", "product_owner", "senior_developer", "tech_writer"
        });
    }

    [Test]
    public void ValidRoles_Should_Be_Derived_From_AgentRole_Enum()
    {
        RolePhaseMap.ValidRoles.Should().BeEquivalentTo(
            Enum.GetValues<AgentRole>().Select(r => r.ToWire()));
    }

    [Test]
    public void ValidActions_Should_Contain_Seventy_Two_Actions()
    {
        RolePhaseMap.ValidActions.Should().HaveCount(72);
    }

    [Test]
    public void ValidActions_Should_Be_Derived_From_AgentAction_Enum()
    {
        RolePhaseMap.ValidActions.Should().BeEquivalentTo(
            Enum.GetValues<AgentAction>().Select(a => a.ToWire()));
    }

    // -----------------------------------------------------------------------
    // Role → primary action
    // -----------------------------------------------------------------------

    [Test]
    public void GetPrimaryPhaseForRole_Developer_Returns_ImplementFeature()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("developer").Should().Be("implement-feature");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Tester_Returns_WriteTests()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("tester").Should().Be("write-tests");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Security_Returns_CodeReviewSecurity()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("security").Should().Be("code-review-security");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Devops_Returns_ImplementInfrastructure()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("devops").Should().Be("implement-infrastructure");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Architect_Returns_PlanSystemDesign()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("architect").Should().Be("plan-system-design");
    }

    [Test]
    public void GetPrimaryPhaseForRole_ProductOwner_Returns_TriageIntake()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("product_owner").Should().Be("triage-intake");
    }

    [Test]
    public void GetPrimaryPhaseForRole_SeniorDeveloper_Returns_PlanReview()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("senior_developer").Should().Be("plan-review");
    }

    [Test]
    public void GetPrimaryPhaseForRole_TechWriter_Returns_SummarizeChanges()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("tech_writer").Should().Be("summarize-changes");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Every_Primary_Is_In_That_Roles_Set()
    {
        foreach (var role in RolePhaseMap.ValidRoles)
        {
            var primary = RolePhaseMap.GetPrimaryPhaseForRole(role);
            RolePhaseMap.IsRoleEligibleForPhase(primary, role)
                .Should().BeTrue($"primary action '{primary}' must be in role '{role}'s set");
        }
    }

    [Test]
    public void GetPrimaryPhaseForRole_UnknownRole_Throws()
    {
        Action act = () => RolePhaseMap.GetPrimaryPhaseForRole("unknown_role");
        act.Should().Throw<ArgumentException>().WithMessage("*unknown_role*");
    }

    [Test]
    [TestCase("__proto__")]
    [TestCase("constructor")]
    [TestCase("prototype")]
    public void GetPrimaryPhaseForRole_ForbiddenKey_Throws(string role)
    {
        Action act = () => RolePhaseMap.GetPrimaryPhaseForRole(role);
        act.Should().Throw<ArgumentException>();
    }

    // -----------------------------------------------------------------------
    // Action → eligible roles
    // -----------------------------------------------------------------------

    [Test]
    public void GetEligibleRolesForPhase_ContextScan_Includes_All_Eight_Roles()
    {
        RolePhaseMap.GetEligibleRolesForPhase("context-scan")
            .Should().BeEquivalentTo(RolePhaseMap.ValidRoles);
    }

    [Test]
    public void GetEligibleRolesForPhase_ImplementFeature_Is_Developer_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("implement-feature")
            .Should().BeEquivalentTo(new[] { "developer" });
    }

    [Test]
    public void GetEligibleRolesForPhase_CodeReview_Includes_SeniorDeveloper_And_Developer()
    {
        RolePhaseMap.GetEligibleRolesForPhase("code-review")
            .Should().BeEquivalentTo(new[] { "senior_developer", "developer" });
    }

    [Test]
    public void GetEligibleRolesForPhase_CodeReviewSecurity_Is_Security_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("code-review-security")
            .Should().BeEquivalentTo(new[] { "security" });
    }

    [Test]
    public void GetEligibleRolesForPhase_WriteTests_Includes_Tester_And_Developer()
    {
        RolePhaseMap.GetEligibleRolesForPhase("write-tests")
            .Should().BeEquivalentTo(new[] { "tester", "developer" });
    }

    [Test]
    public void GetEligibleRolesForPhase_PlanSystemDesign_Is_Architect_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("plan-system-design")
            .Should().BeEquivalentTo(new[] { "architect" });
    }

    [Test]
    public void GetEligibleRolesForPhase_PlanReview_Includes_Architect_And_SeniorDeveloper()
    {
        RolePhaseMap.GetEligibleRolesForPhase("plan-review")
            .Should().BeEquivalentTo(new[] { "architect", "senior_developer" });
    }

    [Test]
    public void GetEligibleRolesForPhase_TriageTechnical_Includes_Architect_And_SeniorDeveloper()
    {
        RolePhaseMap.GetEligibleRolesForPhase("triage-technical")
            .Should().BeEquivalentTo(new[] { "architect", "senior_developer" });
    }

    [Test]
    public void GetEligibleRolesForPhase_SummarizeChanges_Is_TechWriter_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("summarize-changes")
            .Should().BeEquivalentTo(new[] { "tech_writer" });
    }

    [Test]
    public void GetEligibleRolesForPhase_UnknownPhase_Throws()
    {
        Action act = () => RolePhaseMap.GetEligibleRolesForPhase("unknown-phase");
        act.Should().Throw<ArgumentException>().WithMessage("*unknown-phase*");
    }

    [Test]
    public void GetEligibleRolesForPhase_DeadToken_Throws()
    {
        // 'implement' and 'plan' are dead tokens from the old vocabulary.
        Action act = () => RolePhaseMap.GetEligibleRolesForPhase("implement");
        act.Should().Throw<ArgumentException>();
    }

    // -----------------------------------------------------------------------
    // (action, role) eligibility — non-throwing predicate
    // -----------------------------------------------------------------------

    [Test]
    public void IsRoleEligibleForPhase_CodeReviewSecurity_Security_Returns_True()
    {
        RolePhaseMap.IsRoleEligibleForPhase("code-review-security", "security").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_CodeReview_Security_Returns_False()
    {
        // security reviews via code-review-security, not the generic code-review.
        RolePhaseMap.IsRoleEligibleForPhase("code-review", "security").Should().BeFalse();
    }

    [Test]
    public void IsRoleEligibleForPhase_ImplementFeature_Developer_Returns_True()
    {
        RolePhaseMap.IsRoleEligibleForPhase("implement-feature", "developer").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_ImplementFeature_Tester_Returns_False()
    {
        RolePhaseMap.IsRoleEligibleForPhase("implement-feature", "tester").Should().BeFalse();
    }

    [Test]
    [TestCase("developer")]
    [TestCase("tester")]
    [TestCase("security")]
    [TestCase("devops")]
    [TestCase("architect")]
    [TestCase("product_owner")]
    [TestCase("senior_developer")]
    [TestCase("tech_writer")]
    public void IsRoleEligibleForPhase_ContextScan_True_For_Every_Role(string role)
    {
        RolePhaseMap.IsRoleEligibleForPhase("context-scan", role).Should().BeTrue();
    }

    [Test]
    [TestCase("implement")]
    [TestCase("plan")]
    [TestCase("triage")]
    [TestCase("summarize")]
    public void IsRoleEligibleForPhase_DeadToken_ReturnsFalse_Not_Throws(string deadToken)
    {
        // Dead tokens from the old 10-action vocabulary must return false,
        // never throw — AgentResolverService relies on the non-throwing path.
        RolePhaseMap.IsRoleEligibleForPhase(deadToken, "developer").Should().BeFalse();
    }

    [Test]
    public void IsRoleEligibleForPhase_UnknownRole_ReturnsFalse()
    {
        RolePhaseMap.IsRoleEligibleForPhase("context-scan", "no_such_role").Should().BeFalse();
    }

    [Test]
    public void IsRoleEligibleForPhase_UnknownPhase_ReturnsFalse()
    {
        RolePhaseMap.IsRoleEligibleForPhase("bogus", "developer").Should().BeFalse();
    }

    [Test]
    public void IsRoleEligibleForPhase_Empty_ReturnsFalse()
    {
        RolePhaseMap.IsRoleEligibleForPhase("", "developer").Should().BeFalse();
        RolePhaseMap.IsRoleEligibleForPhase("context-scan", "").Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Story 27-19 — 4 new per-role review tokens + developer/triage-defect widening
    // -----------------------------------------------------------------------

    [Test]
    public void IsRoleEligibleForPhase_ReviewFeasibility_Developer_Returns_True()
    {
        RolePhaseMap.IsRoleEligibleForPhase("review-feasibility", "developer").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_ReviewTestability_Tester_Returns_True()
    {
        RolePhaseMap.IsRoleEligibleForPhase("review-testability", "tester").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_ReviewOperability_Devops_Returns_True()
    {
        RolePhaseMap.IsRoleEligibleForPhase("review-operability", "devops").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_ReviewScope_ProductOwner_Returns_True()
    {
        RolePhaseMap.IsRoleEligibleForPhase("review-scope", "product_owner").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_TriageDefect_Developer_Returns_True()
    {
        // triage-defect widening (Story 27-19): developer can now triage defects
        RolePhaseMap.IsRoleEligibleForPhase("triage-defect", "developer").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_ReviewFeasibility_Tester_Returns_False()
    {
        // review-feasibility is single-role (developer only)
        RolePhaseMap.IsRoleEligibleForPhase("review-feasibility", "tester").Should().BeFalse();
    }

    [Test]
    public void ValidActions_Contains_All_Four_New_Review_Tokens()
    {
        RolePhaseMap.ValidActions.Should().Contain("review-feasibility");
        RolePhaseMap.ValidActions.Should().Contain("review-testability");
        RolePhaseMap.ValidActions.Should().Contain("review-operability");
        RolePhaseMap.ValidActions.Should().Contain("review-scope");
    }

    // -----------------------------------------------------------------------
    // Legacy phase aliases — repointed to surviving / best-fit new tokens
    // -----------------------------------------------------------------------

    [Test]
    [TestCase("CONTEXT_ANALYSIS", "context-scan")]
    [TestCase("CODE_REVIEW", "code-review")]
    [TestCase("TEST_EXECUTION", "write-tests")]
    [TestCase("CODE_GENERATION", "implement-feature")]
    [TestCase("PR_CREATION", "implement-feature")]
    [TestCase("PLAN_GENERATION", "plan-system-design")]
    [TestCase("ISSUE_SELECTION", "triage-intake")]
    [TestCase("STATUS_MONITORING", "triage-intake")]
    public void NormalizePhase_LegacyAlias_Resolves_To_New_Token(string legacy, string expected)
    {
        RolePhaseMap.NormalizePhase(legacy).Should().Be(expected);
    }

    [Test]
    public void NormalizePhase_Every_Alias_Target_Is_A_Valid_Action()
    {
        foreach (var (_, target) in RolePhaseMap.LegacyPhaseAliases)
        {
            RolePhaseMap.ValidActions.Should().Contain(target);
        }
    }

    [Test]
    public void NormalizePhase_CanonicalToken_Passes_Through()
    {
        RolePhaseMap.NormalizePhase("implement-feature").Should().Be("implement-feature");
    }

    // -----------------------------------------------------------------------
    // Legacy role aliases — unchanged
    // -----------------------------------------------------------------------

    [Test]
    [TestCase("implementer", "developer")]
    [TestCase("reviewer", "senior_developer")]
    [TestCase("documenter", "tech_writer")]
    [TestCase("analyst", "product_owner")]
    public void NormalizeRole_LegacyAlias_Resolves(string legacy, string expected)
    {
        RolePhaseMap.NormalizeRole(legacy).Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // Validation helpers
    // -----------------------------------------------------------------------

    [Test]
    public void AssertValidPhase_NewToken_DoesNotThrow()
    {
        Action act = () => RolePhaseMap.AssertValidPhase("plan-system-design");
        act.Should().NotThrow();
    }

    [Test]
    public void AssertValidPhase_DeadToken_Throws()
    {
        Action act = () => RolePhaseMap.AssertValidPhase("implement");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void AssertValidRole_KnownRole_DoesNotThrow()
    {
        Action act = () => RolePhaseMap.AssertValidRole("developer");
        act.Should().NotThrow();
    }

    [Test]
    public void Every_action_is_eligible_for_at_least_one_role()
    {
        foreach (var action in Enum.GetValues<AgentAction>())
            RolePhaseMap.GetEligibleRolesForPhase(action.ToWire()).Should()
                .NotBeEmpty($"action '{action.ToWire()}' must belong to at least one role's SPEC §4 set");
    }

    // -----------------------------------------------------------------------
    // Story 27-19 — per-role review / triage panel action selection
    // -----------------------------------------------------------------------

    [Test]
    [TestCase(AgentRole.Architect, AgentAction.PlanReview)]
    [TestCase(AgentRole.SeniorDeveloper, AgentAction.PlanReview)]
    [TestCase(AgentRole.Security, AgentAction.PlanReviewSecurity)]
    [TestCase(AgentRole.Developer, AgentAction.ReviewFeasibility)]
    [TestCase(AgentRole.Tester, AgentAction.ReviewTestability)]
    [TestCase(AgentRole.Devops, AgentAction.ReviewOperability)]
    [TestCase(AgentRole.ProductOwner, AgentAction.ReviewScope)]
    public void GetReviewActionForRole_Maps_Each_Panel_Role(AgentRole role, AgentAction expected)
    {
        RolePhaseMap.GetReviewActionForRole(role).Should().Be(expected);
    }

    [Test]
    [TestCase(AgentRole.Architect)]
    [TestCase(AgentRole.SeniorDeveloper)]
    [TestCase(AgentRole.Security)]
    [TestCase(AgentRole.Developer)]
    [TestCase(AgentRole.Tester)]
    [TestCase(AgentRole.Devops)]
    [TestCase(AgentRole.ProductOwner)]
    public void GetReviewActionForRole_Result_Is_Eligible_For_That_Role(AgentRole role)
    {
        var action = RolePhaseMap.GetReviewActionForRole(role);
        RolePhaseMap.IsRoleEligibleForPhase(action.ToWire(), role.ToWire())
            .Should().BeTrue($"({role.ToWire()}, {action.ToWire()}) must be a taxonomy-valid pair");
    }

    [Test]
    public void GetReviewActionForRole_TechWriter_Throws()
    {
        Action act = () => RolePhaseMap.GetReviewActionForRole(AgentRole.TechWriter);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    [TestCase(AgentRole.Security, AgentAction.AssessVulnerability)]
    [TestCase(AgentRole.Developer, AgentAction.TriageDefect)]
    [TestCase(AgentRole.Devops, AgentAction.DiagnoseIncident)]
    [TestCase(AgentRole.Tester, AgentAction.TriageDefect)]
    public void GetTriageActionForRole_Maps_Each_Panel_Role(AgentRole role, AgentAction expected)
    {
        RolePhaseMap.GetTriageActionForRole(role).Should().Be(expected);
    }

    [Test]
    [TestCase(AgentRole.Security)]
    [TestCase(AgentRole.Developer)]
    [TestCase(AgentRole.Devops)]
    [TestCase(AgentRole.Tester)]
    public void GetTriageActionForRole_Result_Is_Eligible_For_That_Role(AgentRole role)
    {
        var action = RolePhaseMap.GetTriageActionForRole(role);
        RolePhaseMap.IsRoleEligibleForPhase(action.ToWire(), role.ToWire())
            .Should().BeTrue($"({role.ToWire()}, {action.ToWire()}) must be a taxonomy-valid pair");
    }

    [Test]
    [TestCase(AgentRole.Architect)]
    [TestCase(AgentRole.SeniorDeveloper)]
    [TestCase(AgentRole.ProductOwner)]
    [TestCase(AgentRole.TechWriter)]
    public void GetTriageActionForRole_NonPanelRole_Throws(AgentRole role)
    {
        Action act = () => RolePhaseMap.GetTriageActionForRole(role);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
