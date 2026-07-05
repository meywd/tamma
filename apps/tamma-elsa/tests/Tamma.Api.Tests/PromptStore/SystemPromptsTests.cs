using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Tests for the <see cref="SystemPrompts"/> static registry after the Story
/// 27-18 taxonomy reshape: the registry is the jagged per-role
/// <c>(role, action)</c> matrix from <see cref="RolePhaseMap"/> (NOT a flat 8×10
/// product), there is no generic <c>action-default</c> tier, and every cell ships
/// a non-empty transitional system-default body (SPEC §3.5).
/// </summary>
[TestFixture]
public class SystemPromptsTests
{
    private static readonly string[] Roles =
        System.Enum.GetValues<AgentRole>().Select(r => r.ToWire()).ToArray();

    /// <summary>The exact count of jagged (role, action) cells from SPEC §4.</summary>
    private static int ExpectedCellCount =>
        RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count);

    [Test]
    public void RoleActionTemplates_MatchesJaggedTaxonomyCellCount()
    {
        // 85 cells (72 distinct action tokens; shared tokens repeat across
        // roles). Asserted against the live taxonomy so it never drifts.
        SystemPrompts.RoleActionTemplates.Should().HaveCount(ExpectedCellCount);
    }

    [Test]
    public void RoleActionTemplates_CoversEveryTaxonomyCell_AndNothingExtra()
    {
        var expected = RolePhaseMap.EligibleActions
            .SelectMany(kv => kv.Value.Select(a => (Role: kv.Key.ToWire(), Action: a.ToWire())))
            .ToHashSet();

        var actual = SystemPrompts.RoleActionTemplates
            .Select(t => (Role: t.Role!, t.Action))
            .ToHashSet();

        actual.Should().BeEquivalentTo(expected,
            "the prompt registry must be exactly the jagged RolePhaseMap taxonomy — no missing cells, no flat-product extras");
    }

    [Test]
    public void RoleActionTemplates_EveryCell_HasNonEmptyBody()
    {
        foreach (var t in SystemPrompts.RoleActionTemplates)
        {
            t.Template.Should().NotBeNullOrWhiteSpace(
                $"transitional system default for {t.Role}/{t.Action} must be a real body, not a placeholder (SPEC §3.5)");
        }
    }

    [Test]
    public void RoleSystemPrompts_ContainsAllEightRoles()
    {
        SystemPrompts.RoleSystemPrompts.Should().HaveCount(8);
        foreach (var role in Roles)
        {
            SystemPrompts.RoleSystemPrompts.Should().ContainKey(role);
            SystemPrompts.RoleSystemPrompts[role].Should().NotBeNullOrWhiteSpace();
        }
    }

    [TestCaseSource(nameof(AllTaxonomyPairs))]
    public void GetRoleAction_ReturnsTemplateForEveryTaxonomyCell(string role, string action)
    {
        var template = SystemPrompts.GetRoleAction(role, action);

        template.Should().NotBeNull();
        template!.Role.Should().Be(role);
        template.Action.Should().Be(action);
        template.Template.Should().NotBeNullOrWhiteSpace();
        template.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        template.Variables.Should().NotBeEmpty();
    }

    [Test]
    public void GetRoleAction_ReturnsNullForUnknownRole()
    {
        SystemPrompts.GetRoleAction("unknown-role", "context-scan").Should().BeNull();
    }

    [Test]
    public void GetRoleAction_ReturnsNullForUnknownAction()
    {
        SystemPrompts.GetRoleAction("developer", "unknown-action").Should().BeNull();
    }

    [Test]
    public void GetRoleAction_ReturnsNull_ForActionNotInRoleSet()
    {
        // 'deploy' is a devops-only action; the developer role does not own it,
        // so the jagged matrix has no developer/deploy cell.
        SystemPrompts.GetRoleAction("developer", "deploy").Should().BeNull();
        // Conversely it DOES exist for devops.
        SystemPrompts.GetRoleAction("devops", "deploy").Should().NotBeNull();
    }

    [Test]
    public void RoleActionTemplates_AllHaveNonEmptyVariables()
    {
        foreach (var template in SystemPrompts.RoleActionTemplates)
        {
            template.Variables.Should().NotBeEmpty(
                $"template for {template.Role}/{template.Action} should declare variables");
        }
    }

    [Test]
    public void RoleActionTemplates_EachHasSystemPromptMatchingItsRole()
    {
        foreach (var template in SystemPrompts.RoleActionTemplates)
        {
            template.SystemPrompt.Should().Be(
                SystemPrompts.RoleSystemPrompts[template.Role!],
                $"template for {template.Role}/{template.Action} should use role system prompt");
        }
    }

    [Test]
    public void Developer_ImplementFeaturePrompt_HasExpectedVariables()
    {
        // implement-feature maps to the Implement body family (Story 27-18).
        var template = SystemPrompts.GetRoleAction("developer", "implement-feature");
        template.Should().NotBeNull();
        template!.Variables.Should().Contain("workItemJson")
                            .And.Contain("planJson")
                            .And.Contain("currentTask");
    }

    [Test]
    public void ToolEnablement_ReviewStyleActions_AreDisabledForTools()
    {
        // Review/triage/summarize style families should not need tools.
        SystemPrompts.GetRoleAction("architect", "plan-review")!.EnableTools.Should().BeFalse();
        SystemPrompts.GetRoleAction("developer", "code-review")!.EnableTools.Should().BeFalse();
        SystemPrompts.GetRoleAction("developer", "triage-defect")!.EnableTools.Should().BeFalse();
        SystemPrompts.GetRoleAction("tech_writer", "summarize-changes")!.EnableTools.Should().BeFalse();
    }

    [Test]
    public void ToolEnablement_ImplementAction_EnablesTools()
    {
        SystemPrompts.GetRoleAction("developer", "implement-feature")!.EnableTools.Should().BeTrue();
    }

    public static IEnumerable<TestCaseData> AllTaxonomyPairs()
    {
        foreach (var (role, actions) in RolePhaseMap.EligibleActions)
        {
            foreach (var action in actions)
            {
                var roleWire = role.ToWire();
                var actionWire = action.ToWire();
                yield return new TestCaseData(roleWire, actionWire).SetName($"{roleWire}/{actionWire}");
            }
        }
    }

    // ------------------------------------------------------------------
    // Assessment P0 — the 2 assessment cells under product_owner
    // (task-1 of docs/superpowers/plans/2026-06-30-assessment-p0-llm-call.md)
    // TDD RED: these assertions fail BEFORE the enum/taxonomy/template additions.
    // ------------------------------------------------------------------

    [Test]
    public void AssessmentActions_ExistInTaxonomy_ProductOwner()
    {
        // Both cells must be present in the taxonomy (added to product_owner's §4
        // action set and AgentAction enum) and resolve to a non-empty template.
        var generate = SystemPrompts.GetRoleAction("product_owner", "generate-assessment-questions");
        var analyze = SystemPrompts.GetRoleAction("product_owner", "analyze-assessment-response");

        generate.Should().NotBeNull(
            "generate-assessment-questions must be in product_owner's RolePhaseMap action set " +
            "with a non-empty SystemPrompts template (assessment P0 taxonomy)");
        analyze.Should().NotBeNull(
            "analyze-assessment-response must be in product_owner's RolePhaseMap action set " +
            "with a non-empty SystemPrompts template (assessment P0 taxonomy)");

        generate!.Variables.Should().Contain("storyContext")
            .And.Contain("skillLevel")
            .And.Contain("questionCount")
            .And.Contain("previousGaps",
                "generate-assessment-questions must declare the Shared-contract variables from the plan");

        analyze!.Variables.Should().Contain("storyContext")
            .And.Contain("questions")
            .And.Contain("response")
            .And.Contain("skillLevel",
                "analyze-assessment-response must declare the Shared-contract variables from the plan");

        generate.Template.Should().NotBeNullOrWhiteSpace(
            "generate-assessment-questions template must not be empty (resolution is tenant→system→error)");
        analyze.Template.Should().NotBeNullOrWhiteSpace(
            "analyze-assessment-response template must not be empty (resolution is tenant→system→error)");
    }

    // ------------------------------------------------------------------
    // Story 3.4 — the research cell under product_owner. The ResearchWorkflow
    // dispatches (product_owner, research); resolution is tenant→system→error, so
    // the system default MUST be a real, non-empty body that instructs the exact
    // ranked-findings JSON schema ResearchParsing.ParseReport recovers.
    // ------------------------------------------------------------------

    [Test]
    public void ResearchAction_ExistsInTaxonomy_ProductOwner()
    {
        var research = SystemPrompts.GetRoleAction("product_owner", "research");

        research.Should().NotBeNull(
            "research must be in product_owner's RolePhaseMap action set with a non-empty " +
            "SystemPrompts template (Story 3.4 taxonomy)");

        research!.Template.Should().NotBeNullOrWhiteSpace(
            "research template must not be empty (resolution is tenant→system→error)");

        research.Variables.Should().Contain("workItemJson")
            .And.Contain("findings",
                "research must declare the variables ResearchWorkflow passes in the llm-call dispatch");
    }

    [Test]
    public void ResearchAction_Template_DocumentsTheParserSchema()
    {
        // The body must instruct the EXACT JSON keys ResearchParsing.ParseReport reads:
        // summary + findings[] {title, summary, relevance, confidence, citations} + overallConfidence.
        var research = SystemPrompts.GetRoleAction("product_owner", "research");

        research.Should().NotBeNull();
        var body = research!.Template;
        body.Should().Contain("\"summary\"");
        body.Should().Contain("\"findings\"");
        body.Should().Contain("\"relevance\"");
        body.Should().Contain("\"confidence\"");
        body.Should().Contain("\"citations\"");
        body.Should().Contain("\"overallConfidence\"");
    }

    // ------------------------------------------------------------------
    // Story 3.6 — the score-ambiguity cell under product_owner. The
    // AmbiguityScoringWorkflow dispatches (product_owner, score-ambiguity);
    // resolution is tenant→system→error, so the system default MUST be a real,
    // non-empty body that instructs the exact structured-score JSON schema
    // AmbiguityParsing.ParseAssessment recovers.
    // ------------------------------------------------------------------

    [Test]
    public void ScoreAmbiguityAction_ExistsInTaxonomy_ProductOwner()
    {
        var score = SystemPrompts.GetRoleAction("product_owner", "score-ambiguity");

        score.Should().NotBeNull(
            "score-ambiguity must be in product_owner's RolePhaseMap action set with a " +
            "non-empty SystemPrompts template (Story 3.6 taxonomy)");

        score!.Template.Should().NotBeNullOrWhiteSpace(
            "score-ambiguity template must not be empty (resolution is tenant→system→error)");

        score.Variables.Should().Contain("workItemJson")
            .And.Contain("contextFindings",
                "score-ambiguity must declare the variables AmbiguityScoringWorkflow passes " +
                "in the llm-call dispatch");
    }

    [Test]
    public void ScoreAmbiguityAction_Template_DocumentsTheParserSchema()
    {
        // The body must instruct the EXACT JSON keys AmbiguityParsing.ParseAssessment reads:
        // score + confidence + rationale + ambiguities[] {type, description, severity, recommendation}.
        var score = SystemPrompts.GetRoleAction("product_owner", "score-ambiguity");

        score.Should().NotBeNull();
        var body = score!.Template;
        body.Should().Contain("\"score\"");
        body.Should().Contain("\"rationale\"");
        body.Should().Contain("\"confidence\"");
        body.Should().Contain("\"ambiguities\"");
        body.Should().Contain("\"type\"");
        body.Should().Contain("\"description\"");
        body.Should().Contain("\"severity\"");
        body.Should().Contain("\"recommendation\"");
    }

    // ------------------------------------------------------------------
    // Audit prompts/001 — role-tailored review-lens shape is retained on the
    // review-family cells. The PlanReview body is used by the per-role
    // plan-review lens actions; CodeReview by the code-review lens actions.
    // ------------------------------------------------------------------

    [Test]
    public void PlanReview_SecurityRole_EmitsSecurityBullets()
    {
        // security owns 'plan-review-security' (PlanReview body family).
        var template = SystemPrompts.GetRoleAction("security", "plan-review-security");

        template.Should().NotBeNull();
        template!.Template.Should().Contain("Check for security implications in each task");
        template.Template.Should().Contain("Verify input validation and auth concerns are addressed");
    }

    [Test]
    public void PlanReview_TesterRole_EmitsTestingBullets()
    {
        // tester owns 'review-testability' (PlanReview body family).
        var template = SystemPrompts.GetRoleAction("tester", "review-testability");

        template.Should().NotBeNull();
        template!.Template.Should().Contain("Check that testing strategy is comprehensive");
    }

    [Test]
    public void PlanReview_ArchitectRole_EmitsArchitectureBullets()
    {
        var template = SystemPrompts.GetRoleAction("architect", "plan-review");

        template.Should().NotBeNull();
        template!.Template.Should().Contain("Check that architectural patterns are followed");
    }

    [Test]
    public void PlanReview_DevopsRole_EmitsDevopsBullets()
    {
        // devops owns 'review-operability' (PlanReview body family).
        var template = SystemPrompts.GetRoleAction("devops", "review-operability");

        template.Should().NotBeNull();
        template!.Template.Should().Contain("Check for deployment and infrastructure impact");
    }

    [Test]
    public void PlanReview_RoleWithoutMatchingArm_EmitsGenericFallback()
    {
        // product_owner owns 'review-scope' (PlanReview body) and has no
        // dedicated review-lens switch arm → generic fallback line.
        var template = SystemPrompts.GetRoleAction("product_owner", "review-scope");

        template.Should().NotBeNull();
        template!.Template.Should().Contain("Apply your role-specific expertise to the plan");
    }

    [Test]
    public void CodeReview_SecurityRole_EmitsSecurityBullets()
    {
        // security owns 'code-review-security' (CodeReview body family).
        var template = SystemPrompts.GetRoleAction("security", "code-review-security");

        template.Should().NotBeNull();
        template!.Template.Should().Contain("Look for credential leaks");
    }

    [Test]
    public void CodeReview_RoleWithoutMatchingArm_EmitsGenericFallback()
    {
        // developer owns 'code-review' (CodeReview body) and has no dedicated
        // code-review-lens switch arm → generic fallback line.
        var template = SystemPrompts.GetRoleAction("developer", "code-review");

        template.Should().NotBeNull();
        template!.Template.Should().Contain("Apply your role-specific expertise to the diff");
    }
}
