// NOTE: This type lives in the Tamma.Core assembly but intentionally keeps the
// `Tamma.Api.Services.Agents` namespace. It was moved here (Story 27-19) so the
// Elsa workflows (Tamma.ElsaServer) can reference the taxonomy without a
// dependency cycle through Tamma.Api. The namespace is preserved to avoid
// churning every caller's `using`. A future cleanup story may realign the
// namespace to Tamma.Core.Agents and relocate the tests to Tamma.Core.Tests.
namespace Tamma.Api.Services.Agents;

/// <summary>
/// The canonical union of all distinct workflow action tokens across roles
/// (SPEC §4). Which (role, action) pairs are valid is defined by
/// <see cref="RolePhaseMap"/>, not by this enum — shared tokens
/// (context-scan, code-review, plan-review, write-tests) appear once and are
/// reused across roles.
/// </summary>
public enum AgentAction
{
    // shared / cross-role
    [Wire("context-scan")] ContextScan,

    // product_owner
    [Wire("triage-intake")] TriageIntake,
    [Wire("clarify-requirements")] ClarifyRequirements,
    [Wire("plan-scope")] PlanScope,
    [Wire("define-acceptance-criteria")] DefineAcceptanceCriteria,
    [Wire("prioritize-backlog")] PrioritizeBacklog,
    [Wire("plan-roadmap")] PlanRoadmap,
    [Wire("summarize-stakeholder")] SummarizeStakeholder,
    [Wire("review-acceptance")] ReviewAcceptance,
    [Wire("review-scope")] ReviewScope,
    [Wire("generate-assessment-questions")] GenerateAssessmentQuestions,
    [Wire("analyze-assessment-response")] AnalyzeAssessmentResponse,
    [Wire("research")] Research,
    [Wire("score-ambiguity")] ScoreAmbiguity,
    [Wire("incorporate-answers")] IncorporateAnswers,

    // architect
    [Wire("triage-technical")] TriageTechnical,
    [Wire("plan-system-design")] PlanSystemDesign,
    [Wire("design-api-contract")] DesignApiContract,
    [Wire("design-data-model")] DesignDataModel,
    [Wire("design-integration")] DesignIntegration,
    [Wire("plan-migration-strategy")] PlanMigrationStrategy,
    [Wire("write-adr")] WriteAdr,
    [Wire("plan-review")] PlanReview,
    [Wire("code-review-architecture")] CodeReviewArchitecture,
    [Wire("assess-technical-risk")] AssessTechnicalRisk,
    [Wire("propose-design")] ProposeDesign,
    // Story 41-1a — 41-11's tech-debt/risk triage cell.
    [Wire("triage-tech-debt")] TriageTechDebt,
    // Story 41-1a — 41-10's Design producer. Distinct from plan-system-design,
    // which is reserved as plan-generation's Plan producer (ContractBindingTests).
    [Wire("design-system")] DesignSystem,

    // senior_developer
    [Wire("create-tasks")] CreateTasks,
    [Wire("plan-implementation")] PlanImplementation,
    [Wire("code-review")] CodeReview,
    [Wire("plan-refactor")] PlanRefactor,
    [Wire("debug-rootcause")] DebugRootcause,
    [Wire("summarize-technical")] SummarizeTechnical,
    [Wire("resolve-blocker")] ResolveBlocker,
    [Wire("mentor-feedback")] MentorFeedback,
    [Wire("decompose-issue")] DecomposeIssue,
    // Story 41-1a — 41-17's PR-triage cell (the PR queue half; code review is separate).
    [Wire("triage-pr")] TriagePr,

    // developer
    [Wire("plan-fix")] PlanFix,
    [Wire("plan-debugging")] PlanDebugging,
    [Wire("implement-feature")] ImplementFeature,
    [Wire("implement-fix")] ImplementFix,
    [Wire("write-tests")] WriteTests,
    [Wire("refactor")] Refactor,
    [Wire("debug")] Debug,
    [Wire("address-review-comments")] AddressReviewComments,
    [Wire("self-review")] SelfReview,
    [Wire("review-feasibility")] ReviewFeasibility,
    // Story 39-15 (D5) — the SPLIT triage-context producer cell. Distinct from the
    // free-text (developer, context-scan) so the Findings-producing triage-context
    // use is a document contract while ContextGatheringWorkflow keeps context-scan free-text.
    [Wire("triage-context-scan")] TriageContextScan,

    // tester
    [Wire("plan-test-strategy")] PlanTestStrategy,
    [Wire("write-test-cases")] WriteTestCases,
    [Wire("write-regression-test")] WriteRegressionTest,
    [Wire("exploratory-test")] ExploratoryTest,
    [Wire("verify-acceptance")] VerifyAcceptance,
    [Wire("code-review-coverage")] CodeReviewCoverage,
    [Wire("triage-defect")] TriageDefect,
    [Wire("review-testability")] ReviewTestability,
    // Story 41-1a — 41-16's regression/flaky-test management cell.
    [Wire("manage-regression")] ManageRegression,

    // security
    [Wire("threat-model")] ThreatModel,
    [Wire("plan-review-security")] PlanReviewSecurity,
    [Wire("code-review-security")] CodeReviewSecurity,
    [Wire("assess-vulnerability")] AssessVulnerability,
    [Wire("audit-dependencies")] AuditDependencies,
    [Wire("audit-secrets")] AuditSecrets,
    [Wire("review-compliance")] ReviewCompliance,
    [Wire("analyze-security-incident")] AnalyzeSecurityIncident,

    // devops
    [Wire("plan-deployment")] PlanDeployment,
    [Wire("implement-infrastructure")] ImplementInfrastructure,
    [Wire("configure-cicd")] ConfigureCicd,
    [Wire("deploy")] Deploy,
    [Wire("rollback")] Rollback,
    [Wire("monitor-health")] MonitorHealth,
    [Wire("diagnose-incident")] DiagnoseIncident,
    [Wire("plan-incident-response")] PlanIncidentResponse,
    [Wire("write-postmortem")] WritePostmortem,
    [Wire("assess-capacity")] AssessCapacity,
    [Wire("review-operability")] ReviewOperability,
    // Story 41-1a — 41-22's Diagnosis producer. Distinct from diagnose-incident,
    // which is the triage-panel review lens (RolePhaseMap.GetTriageActionForRole).
    [Wire("incident-rootcause")] IncidentRootcause,

    // tech_writer
    [Wire("summarize-changes")] SummarizeChanges,
    [Wire("write-user-docs")] WriteUserDocs,
    [Wire("write-api-docs")] WriteApiDocs,
    [Wire("write-release-notes")] WriteReleaseNotes,
    [Wire("write-runbook")] WriteRunbook,
    [Wire("update-changelog")] UpdateChangelog,
    [Wire("review-docs")] ReviewDocs,

    // scrum_master (Story 41-1a)
    [Wire("plan-sprint")] PlanSprint,
    [Wire("synthesize-standup")] SynthesizeStandup,
    [Wire("facilitate-retro")] FacilitateRetro,
    [Wire("track-impediments")] TrackImpediments,
    // Story 41-1a (41-8 Phase B lockstep amendment) — the prose retro-narrative
    // producer cell. One dispatch = one document; the Findings retro (facilitate-retro)
    // and the prose narrative cannot share a cell, so the narrative gets its own token.
    [Wire("write-retro-narrative")] WriteRetroNarrative,

    // project_manager (Story 41-1a)
    [Wire("report-status")] ReportStatus,
    [Wire("coordinate-release")] CoordinateRelease,

    // ux_designer (Story 41-1a)
    [Wire("draft-user-flow")] DraftUserFlow,
    [Wire("author-ui-spec")] AuthorUiSpec,
    [Wire("review-design")] ReviewDesign,
    [Wire("audit-accessibility")] AuditAccessibility,
}

public static class AgentActionExtensions
{
    /// <summary>The canonical wire string for <paramref name="action"/>.</summary>
    public static string ToWire(this AgentAction action) => EnumWire<AgentAction>.ToWire(action);

    /// <summary>
    /// Resolves a wire string (or legacy phase alias) to an <see cref="AgentAction"/>.
    /// Applies <see cref="RolePhaseMap.NormalizePhase"/> first, then exact match.
    /// </summary>
    /// <exception cref="ArgumentException">Null, empty, or unknown action.</exception>
    public static AgentAction Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Action must not be null or empty.", nameof(input));

        var normalized = RolePhaseMap.NormalizePhase(input);
        if (EnumWire<AgentAction>.TryParse(normalized, out var action)) return action;

        throw new ArgumentException($"Unknown action: '{input}'.", nameof(input));
    }
}
