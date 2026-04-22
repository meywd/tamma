using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Tests for the static role ↔ phase mapping.
///
/// The 8 roles: developer, tester, security, devops, architect, product_owner,
/// senior_developer, tech_writer.
///
/// The 10 actions (≈ phases): context-scan, plan, plan-review, implement,
/// write-tests, refactor, code-review, triage, summarize, debug.
/// </summary>
[TestFixture]
public class RolePhaseMapTests
{
    // -----------------------------------------------------------------------
    // Roles / Actions constants
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
    public void ValidActions_Should_Contain_All_Ten_Actions()
    {
        RolePhaseMap.ValidActions.Should().BeEquivalentTo(new[]
        {
            "context-scan", "plan", "plan-review", "implement", "write-tests",
            "refactor", "code-review", "triage", "summarize", "debug"
        });
    }

    // -----------------------------------------------------------------------
    // Role → Phase (primary phase per role)
    // -----------------------------------------------------------------------

    [Test]
    public void GetPrimaryPhaseForRole_Developer_Returns_Implement()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("developer").Should().Be("implement");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Tester_Returns_WriteTests()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("tester").Should().Be("write-tests");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Security_Returns_CodeReview()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("security").Should().Be("code-review");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Devops_Returns_Implement()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("devops").Should().Be("implement");
    }

    [Test]
    public void GetPrimaryPhaseForRole_Architect_Returns_Plan()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("architect").Should().Be("plan");
    }

    [Test]
    public void GetPrimaryPhaseForRole_ProductOwner_Returns_Triage()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("product_owner").Should().Be("triage");
    }

    [Test]
    public void GetPrimaryPhaseForRole_SeniorDeveloper_Returns_PlanReview()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("senior_developer").Should().Be("plan-review");
    }

    [Test]
    public void GetPrimaryPhaseForRole_TechWriter_Returns_Summarize()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("tech_writer").Should().Be("summarize");
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
    // Phase → Eligible Roles
    // -----------------------------------------------------------------------

    [Test]
    public void GetEligibleRolesForPhase_Implement_Includes_Developer_And_Devops()
    {
        RolePhaseMap.GetEligibleRolesForPhase("implement")
            .Should().Contain(new[] { "developer", "devops" });
    }

    [Test]
    public void GetEligibleRolesForPhase_CodeReview_Includes_Security_And_Senior()
    {
        RolePhaseMap.GetEligibleRolesForPhase("code-review")
            .Should().Contain(new[] { "security", "senior_developer" });
    }

    [Test]
    public void GetEligibleRolesForPhase_WriteTests_Includes_Tester()
    {
        RolePhaseMap.GetEligibleRolesForPhase("write-tests")
            .Should().Contain("tester");
    }

    [Test]
    public void GetEligibleRolesForPhase_Plan_Includes_Architect()
    {
        RolePhaseMap.GetEligibleRolesForPhase("plan").Should().Contain("architect");
    }

    [Test]
    public void GetEligibleRolesForPhase_Triage_Includes_ProductOwner()
    {
        RolePhaseMap.GetEligibleRolesForPhase("triage").Should().Contain("product_owner");
    }

    [Test]
    public void GetEligibleRolesForPhase_Summarize_Includes_TechWriter()
    {
        RolePhaseMap.GetEligibleRolesForPhase("summarize").Should().Contain("tech_writer");
    }

    [Test]
    public void GetEligibleRolesForPhase_UnknownPhase_Throws()
    {
        Action act = () => RolePhaseMap.GetEligibleRolesForPhase("unknown-phase");
        act.Should().Throw<ArgumentException>().WithMessage("*unknown-phase*");
    }

    // -----------------------------------------------------------------------
    // Resolve (phase, role) → validates pairing
    // -----------------------------------------------------------------------

    [Test]
    public void IsRoleEligibleForPhase_Valid_Pair_Returns_True()
    {
        RolePhaseMap.IsRoleEligibleForPhase("implement", "developer").Should().BeTrue();
    }

    [Test]
    public void IsRoleEligibleForPhase_Invalid_Pair_Returns_False()
    {
        RolePhaseMap.IsRoleEligibleForPhase("plan", "tester").Should().BeFalse();
    }

    [Test]
    public void IsRoleEligibleForPhase_UnknownPhase_ReturnsFalse()
    {
        RolePhaseMap.IsRoleEligibleForPhase("bogus", "developer").Should().BeFalse();
    }
}
