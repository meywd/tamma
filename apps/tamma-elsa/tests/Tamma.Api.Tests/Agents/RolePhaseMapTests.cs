using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Tests for the static role ↔ action mapping, rebuilt on the
/// <see cref="AgentRole"/> / <see cref="AgentAction"/> enums (SPEC §4).
///
/// The 11 roles: developer, tester, security, devops, architect, product_owner,
/// senior_developer, tech_writer, plus the Epic 41 three (Story 41-1a):
/// scrum_master, project_manager, ux_designer.
///
/// The 96 actions are the union of the per-role action sets in SPEC §4
/// (72 original + 2 assessment actions: generate-assessment-questions,
/// analyze-assessment-response under product_owner — added in assessment P0 —
/// + 1 research action under product_owner, Story 3.4
/// + 1 score-ambiguity action under product_owner, Story 3.6
/// + 1 decompose-issue action under senior_developer, Story 2.14
/// + 1 incorporate-answers action under product_owner and 1 propose-design
/// action under architect — taxonomy split so each (role, action) cell carries
/// exactly one output contract).
/// Which (role, action) pairs are valid is the per-role eligibility matrix.
/// </summary>
[TestFixture]
public class RolePhaseMapTests
{
    // -----------------------------------------------------------------------
    // Roles / Actions constants — derived from the enums
    // -----------------------------------------------------------------------

    [Test]
    public void ValidRoles_Should_Contain_All_Eleven_Roles()
    {
        // 8 → 11 (Story 41-1a): + scrum_master, project_manager, ux_designer.
        RolePhaseMap.ValidRoles.Should().BeEquivalentTo(new[]
        {
            "developer", "tester", "security", "devops",
            "architect", "product_owner", "senior_developer", "tech_writer",
            "scrum_master", "project_manager", "ux_designer"
        });
    }

    [Test]
    public void ValidRoles_Should_Be_Derived_From_AgentRole_Enum()
    {
        RolePhaseMap.ValidRoles.Should().BeEquivalentTo(
            Enum.GetValues<AgentRole>().Select(r => r.ToWire()));
    }

    [Test]
    public void ValidActions_Should_Contain_Ninety_Six_Actions()
    {
        // 72 original actions + 2 assessment actions added in assessment P0
        // (generate-assessment-questions, analyze-assessment-response under product_owner)
        // + 1 research action (Story 3.4 — dedicated research token under product_owner)
        // + 1 score-ambiguity action (Story 3.6 — dedicated ambiguity-scoring token
        // under product_owner) + 1 decompose-issue action (Story 2.14 — dedicated
        // issue-decomposition token under senior_developer)
        // + 1 incorporate-answers action (product_owner — answer incorporation split
        // out of clarify-requirements) + 1 propose-design action (architect — design
        // proposal split out of plan-system-design), so each (role, action) cell
        // carries exactly one output contract.
        // + 1 triage-context-scan action (Story 39-15 D5 — developer; the Findings-producing
        // triage-context cell split out of the free-text context-scan). 79 → 80.
        // + 16 Epic 41 tokens (Story 41-1a): plan-sprint, synthesize-standup,
        // facilitate-retro, track-impediments, write-retro-narrative (41-8 Phase B
        // lockstep) under scrum_master; report-status, coordinate-release under
        // project_manager; draft-user-flow, author-ui-spec, review-design,
        // audit-accessibility under ux_designer; triage-tech-debt + design-system
        // (architect), triage-pr (senior_developer), manage-regression (tester),
        // incident-rootcause (devops). 80 → 96.
        RolePhaseMap.ValidActions.Should().HaveCount(96);
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

    // Story 41-1a (C3/D6) — every new role needs an s_primaryAction row: the map
    // is a raw indexer, so a missing row would throw for a valid role.

    [Test]
    public void GetPrimaryPhaseForRole_ScrumMaster_Returns_PlanSprint()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("scrum_master").Should().Be("plan-sprint");
    }

    [Test]
    public void GetPrimaryPhaseForRole_ProjectManager_Returns_ReportStatus()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("project_manager").Should().Be("report-status");
    }

    [Test]
    public void GetPrimaryPhaseForRole_UxDesigner_Returns_AuthorUiSpec()
    {
        RolePhaseMap.GetPrimaryPhaseForRole("ux_designer").Should().Be("author-ui-spec");
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
    public void GetEligibleRolesForPhase_ContextScan_Includes_All_Eleven_Roles()
    {
        // Story 41-1a (D4): the three new roles carry context-scan like the
        // incumbent 8 — no asymmetry in the matrix.
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
    // Story 41-1a (D4) — the three new roles carry context-scan too.
    [TestCase("scrum_master")]
    [TestCase("project_manager")]
    [TestCase("ux_designer")]
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
    // Story 3.4 — dedicated research action (product_owner-eligible)
    // -----------------------------------------------------------------------

    [Test]
    public void ValidActions_Contains_Research()
    {
        RolePhaseMap.ValidActions.Should().Contain("research");
    }

    [Test]
    public void IsRoleEligibleForPhase_Research_ProductOwner_Returns_True()
    {
        // The legacy 'researcher' role aliases onto product_owner, so the dedicated
        // research action is eligible for product_owner (Story 3.4 — ResearchWorkflow
        // dispatches (product_owner, research)).
        RolePhaseMap.IsRoleEligibleForPhase("research", "product_owner").Should().BeTrue();
    }

    [Test]
    public void GetEligibleRolesForPhase_Research_Is_ProductOwner_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("research")
            .Should().BeEquivalentTo(new[] { "product_owner" });
    }

    [Test]
    public void IsRoleEligibleForPhase_Research_Developer_Returns_False()
    {
        // research is a product_owner-only action; a developer must not be eligible.
        RolePhaseMap.IsRoleEligibleForPhase("research", "developer").Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Story 3.6 — dedicated score-ambiguity action (product_owner-eligible)
    // -----------------------------------------------------------------------

    [Test]
    public void ValidActions_Contains_ScoreAmbiguity()
    {
        RolePhaseMap.ValidActions.Should().Contain("score-ambiguity");
    }

    [Test]
    public void IsRoleEligibleForPhase_ScoreAmbiguity_ProductOwner_Returns_True()
    {
        // Requirement clarity is a product_owner concern (consistent with clarify-requirements
        // and research), so the dedicated score-ambiguity action is eligible for product_owner
        // (Story 3.6 — AmbiguityScoringWorkflow dispatches (product_owner, score-ambiguity)).
        RolePhaseMap.IsRoleEligibleForPhase("score-ambiguity", "product_owner").Should().BeTrue();
    }

    [Test]
    public void GetEligibleRolesForPhase_ScoreAmbiguity_Is_ProductOwner_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("score-ambiguity")
            .Should().BeEquivalentTo(new[] { "product_owner" });
    }

    [Test]
    public void IsRoleEligibleForPhase_ScoreAmbiguity_Developer_Returns_False()
    {
        // score-ambiguity is a product_owner-only action; a developer must not be eligible.
        RolePhaseMap.IsRoleEligibleForPhase("score-ambiguity", "developer").Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Taxonomy split — one (role, action) cell = one output contract.
    // incorporate-answers (product_owner): ClarifyingQuestionsWorkflow's answer
    // incorporation dispatch, split out of clarify-requirements.
    // propose-design (architect): DesignProposalWorkflow's proposal dispatch,
    // split out of plan-system-design.
    // -----------------------------------------------------------------------

    [Test]
    public void ValidActions_Contains_IncorporateAnswers()
    {
        RolePhaseMap.ValidActions.Should().Contain("incorporate-answers");
    }

    [Test]
    public void IsRoleEligibleForPhase_IncorporateAnswers_ProductOwner_Returns_True()
    {
        // Answer incorporation belongs to the same role that asked the clarifying
        // questions (ClarifyingQuestionsWorkflow dispatches
        // (product_owner, incorporate-answers) for its second llm-call).
        RolePhaseMap.IsRoleEligibleForPhase("incorporate-answers", "product_owner").Should().BeTrue();
    }

    [Test]
    public void GetEligibleRolesForPhase_IncorporateAnswers_Is_ProductOwner_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("incorporate-answers")
            .Should().BeEquivalentTo(new[] { "product_owner" });
    }

    [Test]
    public void ValidActions_Contains_ProposeDesign()
    {
        RolePhaseMap.ValidActions.Should().Contain("propose-design");
    }

    [Test]
    public void IsRoleEligibleForPhase_ProposeDesign_Architect_Returns_True()
    {
        // Design proposals are the architect's charter (DesignProposalWorkflow
        // dispatches (architect, propose-design)).
        RolePhaseMap.IsRoleEligibleForPhase("propose-design", "architect").Should().BeTrue();
    }

    [Test]
    public void GetEligibleRolesForPhase_ProposeDesign_Is_Architect_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("propose-design")
            .Should().BeEquivalentTo(new[] { "architect" });
    }

    // -----------------------------------------------------------------------
    // Story 2.14 — dedicated decompose-issue action (senior_developer-eligible)
    // -----------------------------------------------------------------------

    [Test]
    public void ValidActions_Contains_DecomposeIssue()
    {
        RolePhaseMap.ValidActions.Should().Contain("decompose-issue");
    }

    [Test]
    public void IsRoleEligibleForPhase_DecomposeIssue_SeniorDeveloper_Returns_True()
    {
        // Breaking a complex issue into implementable sub-tasks is the tech-lead's charter (the
        // senior_developer identity prompt is literally "decompose complex tasks"), so the
        // dedicated decompose-issue action is eligible for senior_developer (Story 2.14 —
        // IssueDecompositionWorkflow dispatches (senior_developer, decompose-issue)).
        RolePhaseMap.IsRoleEligibleForPhase("decompose-issue", "senior_developer").Should().BeTrue();
    }

    [Test]
    public void GetEligibleRolesForPhase_DecomposeIssue_Is_SeniorDeveloper_Only()
    {
        RolePhaseMap.GetEligibleRolesForPhase("decompose-issue")
            .Should().BeEquivalentTo(new[] { "senior_developer" });
    }

    [Test]
    public void IsRoleEligibleForPhase_DecomposeIssue_Developer_Returns_False()
    {
        // decompose-issue is a senior_developer-only action; a plain developer must not be eligible.
        RolePhaseMap.IsRoleEligibleForPhase("decompose-issue", "developer").Should().BeFalse();
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
    [TestCase("researcher", "product_owner")]
    public void NormalizeRole_LegacyAlias_Resolves(string legacy, string expected)
    {
        RolePhaseMap.NormalizeRole(legacy).Should().Be(expected);
    }

    [Test]
    public void NormalizeRole_ScrumMaster_IsNoLongerAliased_To_ProductOwner()
    {
        // Story 41-1a (D3) — the deliberate behaviour change carved out of AC6:
        // scrum_master used to alias to product_owner; it is now a first-class
        // role, so NormalizeRole passes it through via ValidRoles and the alias
        // table no longer contains it.
        RolePhaseMap.NormalizeRole("scrum_master").Should().Be("scrum_master");
        RolePhaseMap.LegacyRoleAliases.Should().NotContainKey("scrum_master");
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
    // Story 41-1a — D1 (tech_writer joins the document-review selector; the arm
    // 41-24/41-25/41-26's review stage requires) and D2 (ux_designer joins for 41-28).
    [TestCase(AgentRole.TechWriter, AgentAction.ReviewDocs)]
    [TestCase(AgentRole.UxDesigner, AgentAction.ReviewDesign)]
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
    [TestCase(AgentRole.TechWriter)]
    [TestCase(AgentRole.UxDesigner)]
    public void GetReviewActionForRole_Result_Is_Eligible_For_That_Role(AgentRole role)
    {
        var action = RolePhaseMap.GetReviewActionForRole(role);
        RolePhaseMap.IsRoleEligibleForPhase(action.ToWire(), role.ToWire())
            .Should().BeTrue($"({role.ToWire()}, {action.ToWire()}) must be a taxonomy-valid pair");
    }

    // Story 41-1a (AC4/D2) — the INVERTED TechWriter assertion: the old
    // GetReviewActionForRole_TechWriter_Throws pinned the selector gap this story
    // closes (D1); TechWriter now RETURNS ReviewDocs (asserted above). The two
    // roles deliberately kept OFF the document-review panel are asserted to throw
    // with the panel message, so neither reaches a selector by accident.
    [Test]
    [TestCase(AgentRole.ScrumMaster)]
    [TestCase(AgentRole.ProjectManager)]
    public void GetReviewActionForRole_NonPanelRole_Throws(AgentRole role)
    {
        Action act = () => RolePhaseMap.GetReviewActionForRole(role);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*is not on a review panel*");
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
    // Story 41-1a (AC4/D2) — none of the three new roles triages; the throw is
    // asserted, not left to accident.
    [TestCase(AgentRole.ScrumMaster)]
    [TestCase(AgentRole.ProjectManager)]
    [TestCase(AgentRole.UxDesigner)]
    public void GetTriageActionForRole_NonPanelRole_Throws(AgentRole role)
    {
        Action act = () => RolePhaseMap.GetTriageActionForRole(role);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*is not on the triage panel*");
    }

    // -----------------------------------------------------------------------
    // Story 41-1a (AC2) — every new (role, action) cell is taxonomy-eligible,
    // with the two Scope-2-correction cells as named cases.
    // -----------------------------------------------------------------------

    [Test]
    // scrum_master (incl. the 41-8 Phase B write-retro-narrative lockstep cell)
    [TestCase("context-scan", "scrum_master")]
    [TestCase("plan-sprint", "scrum_master")]
    [TestCase("synthesize-standup", "scrum_master")]
    [TestCase("facilitate-retro", "scrum_master")]
    [TestCase("track-impediments", "scrum_master")]
    [TestCase("write-retro-narrative", "scrum_master")]
    // project_manager
    [TestCase("context-scan", "project_manager")]
    [TestCase("report-status", "project_manager")]
    [TestCase("coordinate-release", "project_manager")]
    // ux_designer
    [TestCase("context-scan", "ux_designer")]
    [TestCase("draft-user-flow", "ux_designer")]
    [TestCase("author-ui-spec", "ux_designer")]
    [TestCase("review-design", "ux_designer")]
    [TestCase("audit-accessibility", "ux_designer")]
    // incumbent-role additions — design-system (41-10) and incident-rootcause
    // (41-22) are the two cells the story's Corrected note added.
    [TestCase("triage-tech-debt", "architect")]
    [TestCase("design-system", "architect")]
    [TestCase("triage-pr", "senior_developer")]
    [TestCase("manage-regression", "tester")]
    [TestCase("incident-rootcause", "devops")]
    public void IsRoleEligibleForPhase_Epic41_NewCell_Returns_True(string action, string role)
    {
        RolePhaseMap.IsRoleEligibleForPhase(action, role).Should().BeTrue(
            $"Story 41-1a mints the ({role}, {action}) cell");
    }

    [Test]
    public void Epic41_NewSingleRoleActions_Are_Owned_By_Exactly_That_Role()
    {
        RolePhaseMap.GetEligibleRolesForPhase("design-system")
            .Should().BeEquivalentTo(new[] { "architect" });
        RolePhaseMap.GetEligibleRolesForPhase("incident-rootcause")
            .Should().BeEquivalentTo(new[] { "devops" });
        RolePhaseMap.GetEligibleRolesForPhase("write-retro-narrative")
            .Should().BeEquivalentTo(new[] { "scrum_master" });
        RolePhaseMap.GetEligibleRolesForPhase("triage-pr")
            .Should().BeEquivalentTo(new[] { "senior_developer" });
    }
}
