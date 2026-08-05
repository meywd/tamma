using Tamma.Api.Services.Agents;
using Tamma.Core.Audit;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Actions;

// ─────────────────────────────────────────────────────────────────────────────
// THE DESCRIPTOR TABLE (Stories 43-2 AC10 + 43-3 AC3/AC5/AC7).
//
// One entry per catalogued member, enum-referenced (`AgentAction.Deploy.ToWire()`,
// never "deploy") so a renamed member is a COMPILE error — the RolePhaseMap
// posture. Sorted by namespace, then by owning-enum declaration order.
//
// GROUPS — the partition rule (43-3 D1): by KIND OF CONSEQUENCE AT COMPLETION.
// The four contested assignments carry inline comments with the rule applied and
// the rejected alternative (43-3 AC12/D5).
//
// LEVELS — Story 43-11 (the zone model, Amendment 3 + the 2026-08-01 caller-kind
// re-audit). Every DIAL-GOVERNED action (155 of the 197) carries an
// explicitly-chosen zone level in [1,100]: automated iff currentDial >= level.
// The 42 MACHINERY rows (all automation:* + all platform-task:* + the 5
// plumbing-only effects) are OFF the dial — IsMachinery = true (Story 43-13), the
// evaluator short-circuits them before the dial comparison, and their
// DefaultMinAutonomy is not-applicable (left at AutonomyDial.Min for a valid int;
// the level tests filter on !IsMachinery). No shipped descriptor carries
// AutonomyDial.AlwaysHuman any more (43-11 M6) — the four that did
// (design/sprint-plan/threat-model acceptances, mcp.tool.invoke) came onto real
// levels.
//
// D2 REVERSAL of 43-3's "never literals" rule: a zone level is written as a PLAIN
// INTEGER LITERAL (min: 45), NOT an AutonomyDial.* constant. 43-3's rule existed
// because 193 rows all meant "the floor, whatever the floor is" and a literal
// would not move with the dial. A level of 45 does NOT mean "the floor"; it means
// forty-five, and it must NOT move when Min moves. The exact per-action levels are
// pinned by ActionCatalogLevelTests (the reviewable 155-row table). Do not "fix"
// these back into AutonomyDial.Min. (Machinery rows keep AutonomyDial.Min because
// their value is inert, not because it is "the floor".)
// ─────────────────────────────────────────────────────────────────────────────
public static partial class ActionCatalog
{
    /// <summary>Shared declaration site for the agent-action plane (registry vocabulary; exempt from site uniqueness).</summary>
    private const string AgentSite = "Tamma.Core/Agents/AgentAction.cs via RolePhaseMap dispatch";

    /// <summary>Shared declaration site for the document-type plane (registry vocabulary; exempt from site uniqueness).</summary>
    private const string DocumentSite = "Tamma.Core/Documents/DocumentTypeRegistry.cs acceptance decision";

    private static ActionDescriptor Agent(
        AgentAction action, ActionGroup group, ActionRisk risk, string title, string summary,
        bool reversible = true, int min = AutonomyDial.Min) =>
        new(new ActionKey(ActionNamespace.AgentAction, action.ToWire()), group, risk, reversible,
            title, summary, min, AgentSite);

    private static ActionDescriptor Doc(
        DocumentTypeKey type, string title, string summary, int min = AutonomyDial.Min) =>
        new(new ActionKey(ActionNamespace.DocumentType, type.ToWire()), ActionGroup.ReviewAndAcceptance,
            ActionRisk.Mutating, Reversible: true, title, summary, min, DocumentSite);

    private static ActionDescriptor Tool(
        ToolAction tool, ActionGroup group, ActionRisk risk, string title, string summary, string site,
        bool reversible = true, int min = AutonomyDial.Min) =>
        new(new ActionKey(ActionNamespace.Tool, tool.ToWire()), group, risk, reversible,
            title, summary, min, site);

    private static ActionDescriptor Effect(
        ExternalEffect effect, ActionGroup group, ActionRisk risk, string title, string summary, string site,
        bool reversible = true, string? sensitive = null, bool enforceable = true,
        int min = AutonomyDial.Min, bool machinery = false) =>
        // `machinery` (Story 43-13): TRUE only for the 5 plumbing-only effects
        // in 43-11's machinery inventory — deterministic writes executing
        // decisions gated elsewhere. Every other effect is dial-governed.
        new(new ActionKey(ActionNamespace.Effect, effect.ToWire()), group, risk, reversible,
            title, summary, min, site, sensitive, EscalatableToHuman: true, Enforceable: enforceable,
            IsMachinery: machinery);

    private static ActionDescriptor Automation(
        BackgroundActor actor, ActionGroup group, ActionRisk risk, string title, string summary, string site,
        bool reversible = true, string? sensitive = null) =>
        // EscalatableToHuman is FALSE for the whole plane: a sweeper cannot
        // suspend for a person — Seam D (43-9) can only deny (pinned by
        // ActionDescriptorMetadataTests). IsMachinery is TRUE for the whole
        // plane (Story 43-13 / 43-11 Amendment 4): a background service is
        // deterministic machinery and never resolves through the dial.
        new(new ActionKey(ActionNamespace.Automation, actor.ToWire()), group, risk, reversible,
            title, summary, AutonomyDial.Min, site, sensitive, EscalatableToHuman: false,
            IsMachinery: true);

    private static ActionDescriptor Task(
        PlatformTaskKind kind, ActionRisk risk, string title, string summary, string site,
        bool reversible = true, string? sensitive = null) =>
        // IsMachinery is TRUE for the whole plane (Story 43-13): a task handler
        // executes an admin request (a human, never gated) or an external
        // system's webhook — the ACTION is the request, not the handler.
        new(new ActionKey(ActionNamespace.PlatformTask, kind.ToWire()), ActionGroup.PlatformAutomation,
            risk, reversible, title, summary, AutonomyDial.Min, site, sensitive,
            IsMachinery: true);

    private static IReadOnlyList<ActionDescriptor> BuildDescriptors() =>
        BuildDescriptors(ShellExecutionProfile.ShippedMinAutonomy);

    /// <summary>
    /// Story 42-10 (D4, D9) — the catalog-build seam that makes the shell/process.spawn
    /// shipped level a PROFILE input. The public parameterless overload reads
    /// <see cref="ShellExecutionProfile.ShippedMinAutonomy"/> (80 unsandboxed / 40
    /// sandboxed); this overload lets the 43-11 level-table test exercise BOTH arms
    /// in one run without touching the static profile ambient.
    /// </summary>
    internal static IReadOnlyList<ActionDescriptor> BuildDescriptors(int shellShippedLevel) => new[]
    {
        // ── agent-action (96) — AgentAction.cs declaration order (80 + the 16
        //    Epic 41 tokens, Story 41-1a; groups follow the 43-3 partition rule
        //    exactly as the incumbent 80 do, MinAutonomy = Min per the
        //    behaviour-preserving rule) ─────────────────────────────────────────

        Agent(AgentAction.ContextScan, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Context scan", "Scan the repository and issue context to build understanding before work starts.", min: 5),
        Agent(AgentAction.TriageIntake, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage intake", "Classify and route an incoming issue. NOTE: ships at Min despite the live AlwaysEscalate entry (TriageBindingHelper) — the floor comes from the legacy surface via 43-5's max() composition, and duplicating it as a catalog default would make deleting the legacy entry fail to lower the threshold (43-3 D7; see 43-5's ShippedTriageDefault_StillEscalates).", min: 5),
        Agent(AgentAction.ClarifyRequirements, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Clarify requirements", "Formulate clarification questions for ambiguous requirements.", min: 5),
        Agent(AgentAction.PlanScope, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Plan scope", "Define what is in and out of scope; produces ordering, not a binding artifact.", min: 5),
        Agent(AgentAction.DefineAcceptanceCriteria, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Define acceptance criteria", "Derive testable acceptance criteria from requirements.", min: 5),
        Agent(AgentAction.PrioritizeBacklog, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Prioritize backlog", "Order backlog items by value and risk.", min: 5),
        Agent(AgentAction.PlanRoadmap, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Plan roadmap", "Lay out delivery sequencing across epics; ordering/analysis, not a binding plan (43-3 D5.3).", min: 5),
        Agent(AgentAction.SummarizeStakeholder, ActionGroup.Docs, ActionRisk.Mutating, "Stakeholder summary", "Write a stakeholder-facing summary of work already done.", min: 15),
        Agent(AgentAction.ReviewAcceptance, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Acceptance review", "Decide whether delivered work meets its acceptance criteria.", min: 40),
        Agent(AgentAction.ReviewScope, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Scope review", "Verdict on whether proposed scope matches the request.", min: 40),
        Agent(AgentAction.GenerateAssessmentQuestions, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Generate assessment questions", "Produce questions that probe understanding of an issue.", min: 5),
        Agent(AgentAction.AnalyzeAssessmentResponse, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Analyze assessment response", "Evaluate a human's answers to assessment questions.", min: 5),
        Agent(AgentAction.Research, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Research", "Investigate approaches, libraries and prior art.", min: 5),
        Agent(AgentAction.ScoreAmbiguity, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Score ambiguity", "Quantify how ambiguous a request is for escalation routing.", min: 5),
        Agent(AgentAction.IncorporateAnswers, ActionGroup.Authoring, ActionRisk.Mutating, "Incorporate answers", "Fold clarification answers back into the binding requirement artifact.", min: 25),

        Agent(AgentAction.TriageTechnical, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Technical triage", "Assess technical shape and routing of an issue.", min: 5),
        Agent(AgentAction.PlanSystemDesign, ActionGroup.Authoring, ActionRisk.Mutating, "System design", "Produce the system design others build against (binding artifact — 43-3 D5.3).", min: 25),
        Agent(AgentAction.DesignApiContract, ActionGroup.Authoring, ActionRisk.Mutating, "API contract design", "Author an API contract others implement against.", min: 25),
        Agent(AgentAction.DesignDataModel, ActionGroup.Authoring, ActionRisk.Mutating, "Data model design", "Author the data model others build against.", min: 25),
        Agent(AgentAction.DesignIntegration, ActionGroup.Authoring, ActionRisk.Mutating, "Integration design", "Author the integration design between components/systems.", min: 25),
        Agent(AgentAction.PlanMigrationStrategy, ActionGroup.Authoring, ActionRisk.Mutating, "Migration strategy", "Author the migration plan others execute (binding — 43-3 D5.3).", min: 25),
        Agent(AgentAction.WriteAdr, ActionGroup.Docs, ActionRisk.Mutating, "Write ADR", "Record an architecture decision already made.", min: 15),
        Agent(AgentAction.PlanReview, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Plan review", "Review verdict on an implementation plan.", min: 40),
        Agent(AgentAction.CodeReviewArchitecture, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Architecture code review", "Architecture-focused review verdict on a change.", min: 40),
        Agent(AgentAction.AssessTechnicalRisk, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Assess technical risk", "Estimate the technical risk of a proposed change.", min: 5),
        Agent(AgentAction.ProposeDesign, ActionGroup.Authoring, ActionRisk.Mutating, "Propose design", "Author a design proposal (the artifact the human-pinned design acceptance decides on).", min: 25),
        // Story 41-1a — 41-11: classify/route debt items, produces understanding, like triage-technical.
        Agent(AgentAction.TriageTechDebt, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage tech debt", "Classify and prioritise accumulated technical debt and standing risks.", min: 5),
        // Story 41-1a — 41-10: the Design document others build against (binding artifact — 43-3 D5.3, like design-api-contract).
        Agent(AgentAction.DesignSystem, ActionGroup.Authoring, ActionRisk.Mutating, "System design document", "Author the full system-design document (API contract, data model, integration points) others build against.", min: 25),

        Agent(AgentAction.CreateTasks, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Create tasks", "Break work into ordered tasks; ordering, not a binding artifact.", min: 5),
        Agent(AgentAction.PlanImplementation, ActionGroup.Authoring, ActionRisk.Mutating, "Implementation plan", "Author the implementation plan the developer codes to (binding — 43-3 D5.3).", min: 25),
        Agent(AgentAction.CodeReview, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Code review", "General review verdict on a change.", min: 40),
        Agent(AgentAction.PlanRefactor, ActionGroup.Authoring, ActionRisk.Mutating, "Refactor plan", "Author the refactoring plan others execute (binding — 43-3 D5.3).", min: 25),
        Agent(AgentAction.DebugRootcause, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Root-cause analysis", "Analyze a defect to its root cause; produces understanding.", min: 5),
        Agent(AgentAction.SummarizeTechnical, ActionGroup.Docs, ActionRisk.Mutating, "Technical summary", "Write a technical summary of work already done.", min: 15),
        Agent(AgentAction.ResolveBlocker, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Resolve blocker", "Work out how to clear a blocking condition.", min: 5),
        Agent(AgentAction.MentorFeedback, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Mentor feedback", "Mentoring review feedback on a contributor's work.", min: 40),
        Agent(AgentAction.DecomposeIssue, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Decompose issue", "Split an issue into smaller workable pieces.", min: 5),
        // Story 41-1a — 41-17: classify/route open PRs, produces routing, like triage-defect.
        Agent(AgentAction.TriagePr, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage pull request", "Classify, prioritise and route an open pull request in the review queue.", min: 5),

        Agent(AgentAction.PlanFix, ActionGroup.Authoring, ActionRisk.Mutating, "Fix plan", "Author the fix plan the developer codes to (binding — 43-3 D5.3).", min: 25),
        Agent(AgentAction.PlanDebugging, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Debugging plan", "Order the debugging investigation; analysis, not a binding artifact (43-3 D5.3).", min: 5),
        Agent(AgentAction.ImplementFeature, ActionGroup.Authoring, ActionRisk.Mutating, "Implement feature", "Write the code for a feature.", min: 25),
        Agent(AgentAction.ImplementFix, ActionGroup.Authoring, ActionRisk.Mutating, "Implement fix", "Write the code for a fix.", min: 25),
        // 43-3 D5.2 — write-tests → authoring, not ci-and-test: ci-and-test is
        // EXECUTING tests; writing test code is authoring code. Rejected: a single
        // "testing" group fusing low-risk execution with code authorship.
        Agent(AgentAction.WriteTests, ActionGroup.Authoring, ActionRisk.Mutating, "Write tests", "Author test code for a change (authoring, not test execution — 43-3 D5.2).", min: 25),
        Agent(AgentAction.Refactor, ActionGroup.Authoring, ActionRisk.Mutating, "Refactor", "Restructure code without changing behaviour.", min: 25),
        Agent(AgentAction.Debug, ActionGroup.Authoring, ActionRisk.Mutating, "Debug", "Instrument, reproduce and fix through code changes.", min: 30),
        Agent(AgentAction.AddressReviewComments, ActionGroup.Authoring, ActionRisk.Mutating, "Address review comments", "Apply code changes answering review feedback.", min: 25),
        Agent(AgentAction.SelfReview, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Self review", "The author's own pre-submission review verdict.", min: 40),
        Agent(AgentAction.ReviewFeasibility, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Feasibility review", "Verdict on whether a plan is feasible as written.", min: 40),
        Agent(AgentAction.TriageContextScan, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage context scan", "The Findings-producing triage context scan (document contract; Story 39-15 D5).", min: 5),

        Agent(AgentAction.PlanTestStrategy, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Test strategy", "Decide the testing approach; ordering/analysis, not a binding artifact (43-3 D5.3).", min: 5),
        Agent(AgentAction.WriteTestCases, ActionGroup.Authoring, ActionRisk.Mutating, "Write test cases", "Author test-case code/specs (authoring — 43-3 D5.2).", min: 25),
        Agent(AgentAction.WriteRegressionTest, ActionGroup.Authoring, ActionRisk.Mutating, "Write regression test", "Author a regression test pinning a fixed defect (authoring — 43-3 D5.2).", min: 25),
        Agent(AgentAction.ExploratoryTest, ActionGroup.CiAndTest, ActionRisk.Command, "Exploratory test", "Execute exploratory testing against the system (executing, not writing tests).", min: 30),
        Agent(AgentAction.VerifyAcceptance, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Verify acceptance", "Verify delivered work against its acceptance criteria.", min: 40),
        Agent(AgentAction.CodeReviewCoverage, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Coverage review", "Coverage-focused review verdict on a change.", min: 40),
        Agent(AgentAction.TriageDefect, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage defect", "Classify and route a reported defect.", min: 5),
        Agent(AgentAction.ReviewTestability, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Testability review", "Verdict on whether a design/plan is testable.", min: 40),
        // Story 41-1a — 41-16: classify failures (regression|flaky|environmental); analysis, not test authoring.
        Agent(AgentAction.ManageRegression, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Manage regressions", "Mine CI/DCB history for repeated and flaky failures and triage each suspect test.", min: 5),

        Agent(AgentAction.ThreatModel, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Threat model", "Model threats against a design; produces understanding.", min: 5),
        Agent(AgentAction.PlanReviewSecurity, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Security plan review", "Security-focused review verdict on a plan.", min: 40),
        Agent(AgentAction.CodeReviewSecurity, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Security code review", "Security-focused review verdict on a change.", min: 40),
        Agent(AgentAction.AssessVulnerability, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Assess vulnerability", "Assess the impact and exploitability of a vulnerability.", min: 5),
        // audit-dependencies stays in planning-and-analysis: it touches no secret
        // material (contrast audit-secrets, 43-3 D5.4).
        Agent(AgentAction.AuditDependencies, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Audit dependencies", "Audit third-party dependencies for risk; no secret material involved.", min: 5),
        // 43-3 D5.4 — audit-secrets → secrets, not planning-and-analysis: for the
        // secrets group the SUBJECT dominates the verb — the group exists so an
        // admin can gate everything touching secret material in one move.
        // Rejected: planning-and-analysis by verb-consistency with audit-*/assess-*.
        Agent(AgentAction.AuditSecrets, ActionGroup.Secrets, ActionRisk.ReadOnly, "Audit secrets", "Audit secret handling and exposure (in the secrets group: subject dominates verb — 43-3 D5.4).", min: 10),
        Agent(AgentAction.ReviewCompliance, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Compliance review", "Compliance-focused review verdict.", min: 40),
        Agent(AgentAction.AnalyzeSecurityIncident, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Analyze security incident", "Analyze a security incident; produces understanding.", min: 5),

        Agent(AgentAction.PlanDeployment, ActionGroup.DeployControl, ActionRisk.ReadOnly, "Deployment plan", "Plan a deployment (deploy-control: subject matter dominates — 43-3 D5.3).", min: 5),
        // 43-3 D5.1 — implement-infrastructure → authoring, not deploy-control:
        // the rule is consequence AT COMPLETION — IaC written into a branch has
        // code-write consequence; the production consequence arrives at deploy,
        // which is separately gated. Rejected: deploy-control on intent. THE
        // ASSIGNMENT MOST LIKELY TO BE OVERRULED — an admin raising deploy-control
        // to AlwaysHuman has NOT gated Terraform edits (both group descriptions
        // say so).
        Agent(AgentAction.ImplementInfrastructure, ActionGroup.Authoring, ActionRisk.Mutating, "Implement infrastructure", "Author infrastructure-as-code changes (authoring, not deploy-control — 43-3 D5.1).", min: 25),
        Agent(AgentAction.ConfigureCicd, ActionGroup.DeployControl, ActionRisk.Mutating, "Configure CI/CD", "Change CI/CD pipeline configuration.", min: 50),
        // Story 43-12 AC7 — agent-action:deploy / :rollback STAY (they are
        // RolePhaseMap prompt-taxonomy cells, not effect seams; retiring them would
        // break the role/action taxonomy for no governance gain). Pinned at 90 / 95
        // (= the worst per-env targets). Enforcement keys on the per-environment
        // effect:deploy.* keys (dev 70 / qa 75 / uat 80 / staging 85 / prod 90) and
        // effect:deploy.rollback — not on these agent-action cells.
        Agent(AgentAction.Deploy, ActionGroup.DeployControl, ActionRisk.Destructive, "Deploy", "Deploy to an environment (the work phase; enforcement is on the per-environment effect:deploy.* keys — prod is effect:deploy.prod).", reversible: false, min: 90),
        Agent(AgentAction.Rollback, ActionGroup.DeployControl, ActionRisk.Destructive, "Rollback", "Roll an environment back (the work phase; the prod branch is effect:deploy.rollback).", reversible: false, min: 95),
        Agent(AgentAction.MonitorHealth, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Monitor health", "Observe system health signals.", min: 5),
        Agent(AgentAction.DiagnoseIncident, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Diagnose incident", "Diagnose a production incident; produces understanding.", min: 5),
        Agent(AgentAction.PlanIncidentResponse, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Incident response plan", "Order the incident response; analysis, not a binding artifact (43-3 D5.3).", min: 5),
        Agent(AgentAction.WritePostmortem, ActionGroup.Docs, ActionRisk.Mutating, "Write postmortem", "Write the postmortem for a resolved incident.", min: 15),
        Agent(AgentAction.AssessCapacity, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Assess capacity", "Assess capacity and scaling headroom.", min: 5),
        Agent(AgentAction.ReviewOperability, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Operability review", "Verdict on operational readiness.", min: 40),
        // Story 41-1a — 41-22: root-cause an incident to a Diagnosis; produces understanding, like debug-rootcause.
        Agent(AgentAction.IncidentRootcause, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Incident root cause", "Analyze an operational incident to its root cause (produces a Diagnosis; diagnose-incident stays the triage-panel lens).", min: 5),

        Agent(AgentAction.SummarizeChanges, ActionGroup.Docs, ActionRisk.Mutating, "Summarize changes", "Write a change summary for an audience.", min: 15),
        Agent(AgentAction.WriteUserDocs, ActionGroup.Docs, ActionRisk.Mutating, "Write user docs", "Write end-user documentation.", min: 15),
        Agent(AgentAction.WriteApiDocs, ActionGroup.Docs, ActionRisk.Mutating, "Write API docs", "Write API documentation.", min: 15),
        Agent(AgentAction.WriteReleaseNotes, ActionGroup.Docs, ActionRisk.Mutating, "Write release notes", "Write release notes for a shipped version.", min: 15),
        Agent(AgentAction.WriteRunbook, ActionGroup.Docs, ActionRisk.Mutating, "Write runbook", "Write an operational runbook.", min: 15),
        Agent(AgentAction.UpdateChangelog, ActionGroup.Docs, ActionRisk.Mutating, "Update changelog", "Update the changelog.", min: 15),
        Agent(AgentAction.ReviewDocs, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Docs review", "Review verdict on documentation.", min: 40),

        // scrum_master (Story 41-1a)
        // plan-sprint → authoring: the SprintPlan is the commitment the team executes
        // against (binding — 43-3 D5.3, like plan-implementation; contrast plan-roadmap).
        Agent(AgentAction.PlanSprint, ActionGroup.Authoring, ActionRisk.Mutating, "Plan sprint", "Author the sprint commitment (capacity-bounded scope, owners, estimates) the team executes against.", min: 25),
        // synthesize-standup → docs: a digest of work already done, like summarize-technical.
        Agent(AgentAction.SynthesizeStandup, ActionGroup.Docs, ActionRisk.Mutating, "Synthesize standup", "Write the daily standup digest (what moved, what's blocked, what's at risk) from the event stream.", min: 15),
        // facilitate-retro → planning-and-analysis: retro findings are analysis of what
        // happened, producing understanding (a Findings document), not a binding artifact.
        Agent(AgentAction.FacilitateRetro, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Facilitate retrospective", "Assemble retrospective findings (went well / didn't / action items) from a sprint's history.", min: 5),
        // track-impediments → planning-and-analysis: impediment surfacing/routing, like resolve-blocker.
        Agent(AgentAction.TrackImpediments, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Track impediments", "Surface, classify and route standing impediments and blockers.", min: 5),
        // write-retro-narrative → docs: prose narrative of a retro already held (41-8 Phase B lockstep), like write-postmortem.
        Agent(AgentAction.WriteRetroNarrative, ActionGroup.Docs, ActionRisk.Mutating, "Write retro narrative", "Write the prose retrospective narrative for a completed sprint retro.", min: 15),

        // project_manager (Story 41-1a)
        // report-status → docs: a stakeholder-facing report of work already done, like summarize-stakeholder.
        Agent(AgentAction.ReportStatus, ActionGroup.Docs, ActionRisk.Mutating, "Report status", "Write an audience-tagged status report of progress against commitments.", min: 15),
        // coordinate-release → planning-and-analysis: cross-team sequencing/ordering, not
        // pipeline control — deploy-control gates pipeline/production actions, which this is not.
        Agent(AgentAction.CoordinateRelease, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Coordinate release", "Sequence a release across teams: readiness, sign-offs, timeline and communications.", min: 5),

        // ux_designer (Story 41-1a)
        // draft-user-flow / author-ui-spec → authoring: the UxSpec is the artifact
        // implementation builds against (binding — 43-3 D5.3).
        Agent(AgentAction.DraftUserFlow, ActionGroup.Authoring, ActionRisk.Mutating, "Draft user flow", "Author the user flows (screens, states, transitions) for a feature.", min: 25),
        Agent(AgentAction.AuthorUiSpec, ActionGroup.Authoring, ActionRisk.Mutating, "Author UI spec", "Author the structured UI specification implementation builds against.", min: 25),
        // review-design → review-and-acceptance: a review verdict, like review-docs.
        Agent(AgentAction.ReviewDesign, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Design review", "Review verdict on a UX/design artifact against usability heuristics.", min: 40),
        // audit-accessibility → planning-and-analysis: an audit producing findings with
        // no secret material, like audit-dependencies (contrast audit-secrets, 43-3 D5.4).
        Agent(AgentAction.AuditAccessibility, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Audit accessibility", "Audit a spec or shipped UI against accessibility standards; no secret material involved.", min: 5),

        // ── document-type (17) — all review-and-acceptance (43-3 AC4): they are
        //    acceptance decisions by construction (16 + prose, Story 41-1c) ────

        Doc(DocumentTypeKey.Findings, "Findings acceptance", "Accept/route a findings document.", min: 40),
        Doc(DocumentTypeKey.AmbiguityAssessment, "Ambiguity assessment acceptance", "Accept/route an ambiguity assessment.", min: 40),
        Doc(DocumentTypeKey.Clarification, "Clarification acceptance", "Accept/route a clarification document.", min: 40),
        Doc(DocumentTypeKey.Decomposition, "Decomposition acceptance", "Accept/route a decomposition document.", min: 40),
        Doc(DocumentTypeKey.Plan, "Plan acceptance", "Accept/route a plan document (ships panel SELECTION — a multi-reviewer roster, not a human acceptor; stays Min per 43-3 C2).", min: 45),
        // THE one-member AlwaysHuman set (43-3 D4/AC8): AcceptanceDefaults.For(Design)
        // ships AcceptorRequirement.Human — the ONLY production occurrence. Pinned
        // against the real switch by DesignDocumentType_MatchesAcceptanceDefaults.
        Doc(DocumentTypeKey.Design, "Design acceptance", "Accept a design proposal — binding-doc acceptance zone (45). A person accepts while the dial is below 45; the human floor is DERIVED (Story 43-16), not stored.", min: 45),
        Doc(DocumentTypeKey.Review, "Review acceptance", "Accept/route a review document (panel selection, not a human acceptor; stays Min per 43-3 C2).", min: 45),
        Doc(DocumentTypeKey.TriageDecision, "Triage decision acceptance", "Accept/route a triage decision document.", min: 40),
        Doc(DocumentTypeKey.Diagnosis, "Diagnosis acceptance", "Accept/route a diagnosis document.", min: 40),
        Doc(DocumentTypeKey.TestSpec, "Test spec acceptance", "Accept/route a test specification document.", min: 40),
        // Story 41-1b — the six Epic 41 types (document-type plane 10 -> 16).
        // MinAutonomy follows AcceptanceDefaults.For (the 43-3 D4 derivation, read
        // by DesignDocumentType_MatchesAcceptanceDefaults): sprint-plan and
        // threat-model ship AcceptorRequirement.Human, so they are AlwaysHuman;
        // the other four ship Min.
        Doc(DocumentTypeKey.AcceptanceCriteria, "Acceptance criteria acceptance", "Accept/route an acceptance-criteria document (panel selection, not a human acceptor; ships Min per 43-3 C2).", min: 45),
        Doc(DocumentTypeKey.BacklogOrdering, "Backlog ordering acceptance", "Accept/route a backlog-ordering document (product_owner reviewer; no human acceptor).", min: 40),
        Doc(DocumentTypeKey.SprintPlan, "Sprint plan acceptance", "Accept a sprint plan — level 95 (product owner 2026-08-03: sprint acceptance stays human until a near-max dial). The human floor is DERIVED (Story 43-16), not stored.", min: 95),
        Doc(DocumentTypeKey.TestPlan, "Test plan acceptance", "Accept/route a test-plan document (tester reviewer; no human acceptor).", min: 40),
        Doc(DocumentTypeKey.ThreatModel, "Threat model acceptance", "Accept a threat model — binding-doc acceptance zone (45). A person accepts while the dial is below 45; the human floor is DERIVED (Story 43-16), not stored.", min: 45),
        Doc(DocumentTypeKey.UxSpec, "UX spec acceptance", "Accept/route a ux-spec document (panel selection, not a human acceptor; ships Min per 43-3 C2).", min: 45),
        // Story 41-1c — prose (document-type plane 16 -> 17): one type for the
        // whole prose family (ADR, postmortem, release notes, changelog, docs,
        // runbook, roadmap, status update, retro narrative). MinAutonomy follows
        // AcceptanceDefaults.For (43-3 D4): a single tech_writer reviewer, no
        // human acceptor, so it ships Min.
        Doc(DocumentTypeKey.Prose, "Prose document acceptance", "Accept/route a prose document (tech_writer reviewer; audience-tagged free markdown, no human acceptor).", min: 40),

        // ── tool (8) ─────────────────────────────────────────────────────────

        Tool(ToolAction.FileRead, ActionGroup.CodeRead, ActionRisk.ReadOnly, "Read file", "Read a file in the workspace.",
            "Tamma.Activities.LlmCall.Tools.FileReadTool", min: 5),
        Tool(ToolAction.FileWrite, ActionGroup.CodeWrite, ActionRisk.Mutating, "Write file", "Write a file in the workspace (single undifferentiated member — no per-path selector; known bypass surface).",
            "Tamma.Activities.LlmCall.Tools.FileWriteTool", min: 25),
        Tool(ToolAction.SearchCode, ActionGroup.CodeRead, ActionRisk.ReadOnly, "Search code", "Search code in the workspace.",
            "Tamma.Activities.LlmCall.Tools.SearchCodeTool", min: 5),
        // Story 42-10 (AC3, D4) — profile-dependent: 80 unsandboxed / 40 sandboxed.
        // The value is a catalog-build INPUT (max() cannot lower a shipped level), and
        // process.spawn below shares it (same executor).
        Tool(ToolAction.ShellExecute, ActionGroup.CommandExecution, ActionRisk.Command, "Execute shell command", "Run a shell command in the workspace (known bypass: can reach any governed route by curl; the sandboxed profile blocks egress and confines CWD, earning the lower level).",
            "Tamma.Activities.LlmCall.Tools.ShellExecuteTool", reversible: false, min: shellShippedLevel),
        Tool(ToolAction.RunTests, ActionGroup.CiAndTest, ActionRisk.Command, "Run tests", "Execute the test suite in the workspace.",
            "Tamma.Activities.LlmCall.Tools.RunTestsTool", min: 30),
        Tool(ToolAction.GetAcceptanceRules, ActionGroup.CodeRead, ActionRisk.ReadOnly, "Read acceptance rules", "Read the resolved acceptance policy (principal-bound, per-session tool; deliberately not DI-registered — Story 39-5 D6).",
            "Tamma.Api.Services.AcceptanceRules.GetAcceptanceRulesTool", min: 5),
        // KNOWN HOLE (recorded 2026-07-29, 43-4 review — same candid family as
        // the file_write/shell_execute disclosures above): the read/write split
        // grades by SUBCOMMAND ONLY, while the call's args are screened only
        // for shell metacharacters, never for semantics. A read-graded call can
        // therefore still mutate: {"subcommand":"log","args":"--output=FILE"}
        // writes a file into the workspace; "branch -D x" deletes local refs
        // ("fetch"/"branch" are deliberately graded Read by the local-refs
        // rationale in GitSubcommand.cs:60-64). Harmless while both members
        // ship at Min; MUST be revisited the moment tool:git_operations.write
        // is human-gated — at that point the Read grade is a bypass of the
        // gate, not a nuance.
        Tool(ToolAction.GitOperationsRead, ActionGroup.SourceControlRead, ActionRisk.ReadOnly, "Git read operations", "Read-graded git subcommands (status/diff/log/show/rev-parse/ls-files/fetch/branch). Known hole: grading is subcommand-only — args can still mutate (log --output=FILE writes; branch -D deletes local refs); revisit when git_operations.write is human-gated.",
            "Tamma.Activities.LlmCall.Tools.GitOperationsTool (read-graded GitSubcommand members)", min: 5),
        Tool(ToolAction.GitOperationsWrite, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Git write operations", "Write-graded git subcommands (add/commit/push/checkout/stash/pull) — includes push.",
            "Tamma.Activities.LlmCall.Tools.GitOperationsTool (write-graded GitSubcommand members)", min: 25),

        // ── effect (59) — 48 + 11 (Story 31-13: 7 PR ops + 4 issue callbacks) ─

        // The five `machinery: true` effects below are 43-11's "effects fired
        // only by plumbing" (Story 43-13): automatic event flushes, the
        // deterministic persist of what the LLM authored, lifecycle mechanics,
        // and the system's own reveal-token exchange. Gating any of them gates
        // bookkeeping, not a decision.
        Effect(ExternalEffect.EngineEventsAppend, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Append domain events", "Engine appends DCB events through the mediation seam.",
            "POST /api/engine/events — EngineEndpoints.AppendEvents", reversible: false, machinery: true),
        Effect(ExternalEffect.EnginePlatformEventsAppend, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Append platform events", "Engine appends platform events through the mediation seam.",
            "POST /api/engine/platform-events — EngineEndpoints.AppendPlatformEvents", reversible: false, machinery: true),
        Effect(ExternalEffect.EngineDocumentPersist, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Persist document", "Engine persists a document envelope through the mediation seam.",
            "POST /api/engine/documents — DocumentEndpoints.PersistFromEngine", machinery: true),
        // SiteKey carries the ROUTE CONSTRAINT `{documentId:guid}`, corrected
        // 2026-07-30 (Story 43-8 AC1 step 3, carve-out §A1 #2). The live pattern is
        // `engine.MapPost("/documents/{documentId:guid}/status", …)`; 43-8's binding
        // sweep compares RoutePartOf(SiteKey) with the endpoint's RawText ORDINALLY
        // and does not strip constraints, so the prettified "{documentId}" would have
        // been rejected the moment the route was bound — the same class of defect as
        // the six tracker SiteKeys corrected in wave 4 (adversarial review MODERATE-5).
        Effect(ExternalEffect.EngineDocumentSetStatus, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Set document status", "Engine transitions a document's lifecycle status.",
            "POST /api/engine/documents/{documentId:guid}/status — DocumentEndpoints.SetStatusFromEngine", machinery: true),
        Effect(ExternalEffect.EngineChannelOutboxEnqueue, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Enqueue channel message", "Engine enqueues an outbound channel message.",
            "POST /api/engine/channel/outbox — ChannelEndpoints.EnqueueFromEngine", min: 75),
        Effect(ExternalEffect.LlmCall, ActionGroup.ModelInvocation, ActionRisk.Mutating, "LLM call", "Dispatch an LLM call (Seam A observes and never blocks — epic decision D1: 44 of 45 calling workflows have no human route).",
            "POST /api/v1/llm/call — LlmCallEndpoints.CallLlm", reversible: false, min: 20),
        Effect(ExternalEffect.GitBranchCreate, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Create branch", "Create a branch on the git platform.",
            "POST /api/v1/git/{owner}/{repo}/branches — GitEndpoints.CreateBranch", min: 35),
        Effect(ExternalEffect.GitBranchDelete, ActionGroup.SourceControlWrite, ActionRisk.Destructive, "Delete branch", "Delete a branch on the git platform.",
            "DELETE /api/v1/git/{owner}/{repo}/branches — GitEndpoints.DeleteBranch", reversible: false, min: 95),
        Effect(ExternalEffect.GitPullRequestCreate, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Create pull request", "Open a pull request on the git platform (known bypass: defeatable by git push under tool:git_operations.write).",
            "POST /api/v1/git/{owner}/{repo}/pull-requests — GitEndpoints.CreatePullRequest", min: 35),
        // Story 43-12 — the coarse effect:git.pull-request.merge is RETIRED; merge
        // splits by PR base branch into the zone-ladder trio (55/60/65). All THREE
        // bind the ONE merge route (multi-binding, Program.cs) and a per-request
        // selector picks the key from the PR base, failing closed to git.merge.main.
        // Grading is the coarse key's, carried: SourceControlWrite, Mutating,
        // reversible:false (a merge cannot be un-merged). The three SiteKeys share
        // the SAME route part (so the binding sweep's RoutePartOf == route holds for
        // all three) but differ in the handler-description suffix, so the effect
        // plane's DUPLICATE_SITE_KEY uniqueness is satisfied.
        Effect(ExternalEffect.GitMergeDev, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Merge pull request to dev", "Merge a pull request whose base branch is the dev trunk.",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/merge — GitEndpoints.MergePullRequest (PR base 'dev')", reversible: false, min: 55),
        Effect(ExternalEffect.GitMergeQa, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Merge pull request to qa", "Merge a pull request whose base branch is the qa trunk.",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/merge — GitEndpoints.MergePullRequest (PR base 'qa')", reversible: false, min: 60),
        Effect(ExternalEffect.GitMergeMain, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Merge pull request to main", "Merge a pull request whose base branch is main (and the fail-closed default for any other or unreadable base).",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/merge — GitEndpoints.MergePullRequest (PR base 'main'; fail-closed default)", reversible: false, min: 65),
        Effect(ExternalEffect.GitReleaseCreate, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Create release", "Cut a release on the git platform.",
            "POST /api/v1/git/{owner}/{repo}/releases — GitEndpoints.CreateRelease", min: 35),
        // Story 43-12 — RESERVED rows (no performer in the tree). Each carries a
        // reserved SiteKey that matches no route, so the binding sweep ignores it and
        // IActionEnforcementSites.For(key) is empty — which is exactly the
        // "not enforced anywhere yet" state AC5 requires the policy view to render.
        // git.checks.bypass → SourceControlWrite, Mutating, reversible:false (a merge
        // that rode the bypass cannot be un-ridden).
        Effect(ExternalEffect.GitChecksBypass, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Bypass required checks", "Bypass the required-status-checks gate on a merge. RESERVED (Story 43-12): zone level 50, no action in the tree performs it — the key is reserved before anything does so the first caller cannot ship ungoverned.",
            "RESERVED (Story 43-12) — no performer in the tree: nothing bypasses required checks yet", reversible: false, min: 50),
        // git.webhook.register → SourceControlWrite, Mutating, reversible:true (a
        // webhook can be deleted). DUAL-dormant (Story 43-13 pointer): drivers
        // implement IGitPlatformClient.RegisterWebhookAsync but no caller exists;
        // classification is DUAL (admin setup by hand, or an LLM onboarding flow) and
        // per Story 43-13 the level binds only an LLM path. If the first real caller
        // turns out to be provisioning plumbing, this row moves to the machinery
        // inventory in the wiring PR.
        Effect(ExternalEffect.GitWebhookRegister, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Register webhook", "Register a repo webhook, minting a durable ingress path (the level-85 'create infrastructure' zone). RESERVED / DUAL-dormant (Story 43-12): drivers implement RegisterWebhookAsync but no caller exists; classification is DUAL and per Story 43-13 the level binds only an LLM path. If the first caller is provisioning plumbing, this row moves to the machinery inventory in the wiring PR.",
            "RESERVED (Story 43-12) — no performer in the tree: IGitPlatformClient.RegisterWebhookAsync is implemented by drivers but has no production caller", min: 85),
        // Story 31-13 — PR operations (source-control-write). Enforceable-but-unbound:
        // the routes named in each SiteKey do not exist yet (no .Governs binding), the
        // same green pattern effect:secret.read used when first minted. review-comment
        // is PR review OUTPUT, so it sits in the level-40 "Approve PRs" zone; the rest
        // are level 35 (create branch / PR zone).
        Effect(ExternalEffect.GitPullRequestClose, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Close pull request", "Close a pull request on the git platform.",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/close — GitEndpoints.ClosePullRequest", min: 35),
        Effect(ExternalEffect.GitPullRequestReopen, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Reopen pull request", "Reopen a closed pull request on the git platform.",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/reopen — GitEndpoints.ReopenPullRequest", min: 35),
        Effect(ExternalEffect.GitPullRequestComment, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Comment on pull request", "Post a comment on a pull request.",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/comments — GitEndpoints.PostPullRequestComment", min: 35),
        Effect(ExternalEffect.GitPullRequestReviewComment, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Post PR review comment", "Post a review comment on a pull request (review output — the 'Approve PRs' zone).",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/review-comments — GitEndpoints.PostPullRequestReviewComment", min: 40),
        Effect(ExternalEffect.GitPullRequestRequestReviewers, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Request PR reviewers", "Request reviewers on a pull request.",
            "POST /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/reviewers — GitEndpoints.RequestReviewers", min: 35),
        Effect(ExternalEffect.GitPullRequestLabel, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Set pull request labels", "Set the labels on a pull request.",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/labels — GitEndpoints.SetPullRequestLabels", min: 35),
        Effect(ExternalEffect.GitPullRequestSetDraft, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Set pull request draft state", "Toggle a pull request between draft and ready-for-review.",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n:int}/draft — GitEndpoints.SetPullRequestDraft", min: 35),
        // SiteKey carries `{n:int}` (corrected 2026-07-30, 43-8 AC1 step 3) — the
        // live pattern is MapPatch("/api/v1/git/{owner}/{repo}/issues/{n:int}").
        Effect(ExternalEffect.GitIssuePatch, ActionGroup.IssueTracking, ActionRisk.Mutating, "Update issue", "Update an issue on the git platform.",
            "PATCH /api/v1/git/{owner}/{repo}/issues/{n:int} — GitEndpoints.UpdateIssue", min: 35),
        Effect(ExternalEffect.JiraTicketPatch, ActionGroup.IssueTracking, ActionRisk.Mutating, "Update Jira ticket", "Update a Jira ticket.",
            "PATCH /api/v1/jira/tickets/{ticketId} — JiraEndpoints.UpdateTicket", min: 35),
        // Story 31-13 — the formerly-ungoverned issue callbacks (issue-tracking).
        // Enforceable-but-unbound: the /api/engine/* routes named here do not exist
        // yet (no .Governs binding) — the same green pattern effect:secret.read used
        // when first minted. All level 35.
        Effect(ExternalEffect.GitIssueCreate, ActionGroup.IssueTracking, ActionRisk.Mutating, "Create issue", "Create an issue on the git platform.",
            "POST /api/engine/create-issue — EngineEndpoints.CreateIssue", min: 35),
        Effect(ExternalEffect.GitIssueComment, ActionGroup.IssueTracking, ActionRisk.Mutating, "Comment on issue", "Post a comment on an issue.",
            "POST /api/engine/issue-comment — EngineEndpoints.PostIssueComment", min: 35),
        Effect(ExternalEffect.GitIssueLabelsSet, ActionGroup.IssueTracking, ActionRisk.Mutating, "Set issue labels", "Add labels to an issue.",
            "POST /api/engine/issue-labels — EngineEndpoints.PostIssueLabels", min: 35),
        Effect(ExternalEffect.GitIssueLabelsRemove, ActionGroup.IssueTracking, ActionRisk.Mutating, "Remove issue label", "Remove a label from an issue.",
            "DELETE /api/engine/issue-labels/{repo}/{issueNumber}/{label} — EngineEndpoints.DeleteIssueLabel", min: 35),
        Effect(ExternalEffect.CiTestsTrigger, ActionGroup.CiAndTest, ActionRisk.Command, "Trigger CI tests", "Trigger a CI test run.",
            "POST /api/v1/ci/{owner}/{repo}/test-runs — CiEndpoints.TriggerTests", min: 30),
        Effect(ExternalEffect.AgentDispatchRun, ActionGroup.ModelInvocation, ActionRisk.Command, "Dispatch agent run", "Trigger an external agent run.",
            "POST /api/v1/agent-dispatch/{owner}/{repo}/runs — AgentDispatchEndpoints.TriggerRun",
            sensitive: SensitiveActionCatalog.AgentDispatchSucceeded, min: 80),
        Effect(ExternalEffect.NotifySlackQueue, ActionGroup.ExternalComms, ActionRisk.Mutating, "Queue Slack message", "Queue an outbound Slack message.",
            "POST /api/v1/notifications/slack — NotificationEndpoints.QueueSlack", reversible: false, min: 75),
        Effect(ExternalEffect.NotifyEmailSend, ActionGroup.ExternalComms, ActionRisk.Mutating, "Send email", "Send an outbound email (a sent message cannot be unsent).",
            "POST /api/v1/notifications/email — EmailEndpoints.SendEmail", reversible: false, min: 75),
        // SHIPS AT LEVEL 80 (the unbounded-execution zone) — the earlier
        // AlwaysHuman treatment (reversed 2026-07-30) was itself superseded by the
        // zone model (Story 43-11, 2026-08-03). MCP is no longer human-at-every-dial;
        // it is human at the SHIPPED default dial of 70 (80 > 70 → below-min-autonomy)
        // and automates once a deployment dials to ≥ 80, alongside shell_execute and
        // agent-dispatch, which is the honest cost of not having a per-server/per-tool
        // gate. The MCP-needs-a-person default therefore holds only until an operator
        // raises the dial, not unconditionally. See docs/stories/epic-43/story-43-11
        // (Amendment 4 / re-audit) for why 80 rather than AlwaysHuman.
        //
        // The reasoning that put MCP above the ordinary tolerance still stands and
        // is why 80 (not 25/35 like the other tool effects):
        //
        //  1. THE SAFETY ARGUMENT DOES NOT HOLD FOR MCP. Epic D2 tolerates an
        //     unclassified action at RUNTIME because the drift harnesses make it
        //     UNMERGEABLE in CI. No harness can enumerate an MCP server's tools —
        //     the tool list lives in a separate process behind
        //     POST /api/kb/mcp/tools/invoke and is not derivable from this tree —
        //     so for MCP the CI half of that bargain does not exist and never
        //     becomes unmergeable. An open capability class that is both
        //     unenforceable and drift-invisible is not a tolerated gap; it is the
        //     hole the epic exists to close.
        //  2. THE BLAST RADIUS IS EMPTY TODAY. No MCP tool executor is registered
        //     (ToolExecutorRegistry holds file_read/file_write/search_code/
        //     shell_execute/git_operations/run_tests), so an `mcp__*` name emitted
        //     into the tool loop already terminates as "Unknown tool"; and the one
        //     route that does invoke MCP is a human-authenticated
        //     SettingsManage endpoint, not an agent path. Nothing that works today
        //     stops working.
        //
        // Reversible by an admin lowering the dial or, once MCP can be catalogued
        // per server/tool and swept, a per-action policy row.
        // Pinned by McpToolInvoke_ShipsAtTheUnboundedExecutionZone.
        //
        // SiteKey corrected 2026-07-29 (adversarial review F16). It previously read
        // "POST /api/kb/mcp/servers/{id}/start|stop", which is not a route pattern:
        // the alternation matches no registered route, so 43-8's binding sweep
        // (RoutePartOf(SiteKey) == $"{method} {RawText}", ordinal) could NEVER bind
        // this member to anything — not the start route, not the stop route, and not
        // the invocation route, which the SiteKey did not name at all. It now names
        // the ONE registered route that actually invokes a tool
        // (Program.cs `kb.MapPost("/mcp/tools/invoke", KbEndpoints.InvokeMcpTool)`),
        // verbatim. The server start/stop pair is MCP-server LIFECYCLE, not tool
        // invocation; it has no catalog member of its own and gaining one is a
        // vocabulary decision, not a SiteKey repair. Risk grade deliberately
        // UNCHANGED (the DefaultMinAutonomy is not — see the block above).
        Effect(ExternalEffect.McpToolInvoke, ActionGroup.ModelInvocation, ActionRisk.Command, "Invoke MCP tool", "Invoke an MCP tool. ONE COARSE MEMBER — no per-server/per-tool granularity, and NO drift signal: adding a server, or a tool on an existing server, changes nothing in this catalog and nothing in CI. Because the 'unclassified is unmergeable in CI' half of epic D2 cannot exist here, it sits in the unbounded-execution zone (level 80): a person decides at the shipped default dial, and it automates only once an operator raises the dial to 80 or higher.",
            "POST /api/kb/mcp/tools/invoke — KbEndpoints.InvokeMcpTool", reversible: false,
            min: 80),
        // MACHINERY (43-11 Amendment 4): the reveal is the plumbing credential
        // fetch — how an authorized action gets its credential; gating it would
        // demand a human per fetch. Enforceable=false, machinery=true, off the
        // dial. What the dial governs is effect:secret.read below.
        Effect(ExternalEffect.SecretReveal, ActionGroup.Secrets, ActionRisk.ReadOnly, "Reveal secret", "Plumbing credential fetch for an already-authorized use (machinery — off the dial; what governs an LLM seeing a secret value is effect:secret.read).",
            "GET /api/v1/secrets/reveal/{token} — SecretEndpoints.RevealSecret",
            sensitive: SensitiveActionCatalog.SecretReveal, enforceable: false, machinery: true),
        // 43-11 Amendment 4 / Story 42-10: an LLM reading a secret VALUE into model
        // context is ONE action at 90 (manage-secrets zone). A value in a transcript
        // can leak, so it is always a gated, audited decision. Distinct SiteKey from
        // secret.reveal or DUPLICATE_SITE_KEY boots the app. Caller-kind LLM (43-13).
        Effect(ExternalEffect.SecretRead, ActionGroup.Secrets, ActionRisk.ReadOnly, "Read secret value", "An LLM reads a secret value into model context (top-zone, level 90). Enforced at the reveal route for LLM callers; best-effort graded for shell reads in the tool loop.",
            "GET /api/v1/secrets/reveal/{token} — LLM-caller value read into model context (42-10)",
            reversible: false, sensitive: SensitiveActionCatalog.SecretRead, enforceable: true, min: 90),
        // Story 42-10 (AC3, D4) — same executor as tool:shell_execute, same
        // profile-dependent shipped level (80 unsandboxed / 40 sandboxed).
        Effect(ExternalEffect.ProcessSpawn, ActionGroup.CommandExecution, ActionRisk.Command, "Spawn process", "Spawn an OS process inside the tool loop.",
            "Tamma.Activities.LlmCall.Tools.ShellExecuteTool → ProcessStartInfo", reversible: false, min: shellShippedLevel),
        // Story 43-12 — the coarse effect:deploy.promote-prod is RETIRED; deploy
        // splits by target environment into the zone-ladder quintet
        // (dev 70 / qa 75 / uat 80 / staging 85 / prod 90). CORRECTION to Amendment
        // 3: the shipped pipeline is QA -> UAT -> Prod ONLY
        // (DeploymentPipelineWorkflow.cs:113) — no dev or staging stage exists — so
        // effect:deploy.dev and effect:deploy.staging are RESERVED rows (real
        // catalog rows at their zone levels, no performer) until a pipeline stage
        // exists. deploy.prod ships behaviour-identically to the retired
        // promote-prod: Seam E gates it at the prod-approval decision (the existing
        // business-mode human gate is UNTOUCHED; 43-9 joins it by OR). Grading:
        // deploy.prod → Destructive/irreversible (promote-prod's grading, carried);
        // the non-prod environments → Command/reversible:true (a non-prod
        // environment is redeployable — a judgement call).
        Effect(ExternalEffect.DeployDev, ActionGroup.DeployControl, ActionRisk.Command, "Deploy to dev", "Deploy to the dev environment. RESERVED (Story 43-12): zone level 70, no dev stage exists in DeploymentPipelineWorkflow (QA->UAT->Prod only) — nothing performs it.",
            "RESERVED (Story 43-12) — no performer in the tree: the dev stage does not exist in DeploymentPipelineWorkflow", min: 70),
        Effect(ExternalEffect.DeployQa, ActionGroup.DeployControl, ActionRisk.Command, "Deploy to qa", "QA stage transition (the deploy itself runs inside the LLM tool loop — see the deploy-control group description). A non-prod environment is redeployable, so reversible.",
            "Tamma.ElsaServer.Workflows.DeploymentPipelineWorkflow — QA stage transition", min: 75),
        Effect(ExternalEffect.DeployUat, ActionGroup.DeployControl, ActionRisk.Command, "Deploy to uat", "UAT stage transition (the deploy itself runs inside the LLM tool loop — see the deploy-control group description). A non-prod environment is redeployable, so reversible.",
            "Tamma.ElsaServer.Workflows.DeploymentPipelineWorkflow — UAT stage transition", min: 80),
        Effect(ExternalEffect.DeployStaging, ActionGroup.DeployControl, ActionRisk.Command, "Deploy to staging", "Deploy to the staging environment. RESERVED (Story 43-12): zone level 85, no staging stage exists in DeploymentPipelineWorkflow (QA->UAT->Prod only) — nothing performs it.",
            "RESERVED (Story 43-12) — no performer in the tree: the staging stage does not exist in DeploymentPipelineWorkflow", min: 85),
        // deploy.prod ships behaviour-identically to the retired promote-prod: Seam E
        // gates it at the prod-approval decision; the existing business-mode human
        // gate is UNTOUCHED and 43-9 joins it by OR.
        Effect(ExternalEffect.DeployProd, ActionGroup.DeployControl, ActionRisk.Destructive, "Promote to production", "Production promotion stage transition (the deploy itself runs inside the LLM tool loop — see the deploy-control group description).",
            "Tamma.ElsaServer.Workflows.DeploymentPipelineWorkflow — production stage transition", reversible: false, min: 90),
        Effect(ExternalEffect.DeployRollback, ActionGroup.DeployControl, ActionRisk.Destructive, "Roll back production", "Production rollback branch (same LLM-tool-loop limitation as promote).",
            "Tamma.ElsaServer.Workflows.DeploymentPipelineWorkflow — RollbackProduction branch", reversible: false, min: 95),
        // Story 41-30 (D8) — the scheduled-trigger admin mutations. Grouped
        // platform-automation (43-3's grouping for the seam): the consequence
        // at completion is arming/changing/stopping platform automation, not a
        // source-control or deploy effect. All Mutating + reversible (a
        // schedule row can be re-created; the workflows a schedule DISPATCHES
        // are separately gated by their own catalog members).
        Effect(ExternalEffect.ScheduleCreate, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Create schedule", "Create a scheduled trigger (arms a recurring per-tenant workflow dispatch; run-now claims a manual window on an existing schedule).",
            "POST /api/admin/scheduled-triggers — ScheduledTriggerEndpoints.Create", min: 20),
        Effect(ExternalEffect.ScheduleUpdate, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Update schedule", "Update a scheduled trigger's cron / target / enabled flag / input.",
            "PUT /api/admin/scheduled-triggers/{id} — ScheduledTriggerEndpoints.Update", min: 20),
        Effect(ExternalEffect.ScheduleDelete, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Delete schedule", "Delete a scheduled trigger (stops a tenant's recurring audit; audited via SCHEDULE.TRIGGER.CHANGED).",
            "DELETE /api/admin/scheduled-triggers/{id} — ScheduledTriggerEndpoints.Delete", min: 20),
        // Story 44-2 (AC10) — the native tracker's ten mutating routes. ALL ten
        // land in issue-tracking: the group's partition rule is KIND OF
        // CONSEQUENCE AT COMPLETION, and every one of these completes by
        // changing what the tracker says the work is. That includes the
        // preferences pair — rejected alternative: platform-automation "because
        // it is configuration", which would put a tenant's default project in
        // the same admin lever as the outbox sweeper and hide it from anyone
        // gating the tracker. The group description already reads "issues and
        // tickets on the configured tracker platforms"; the native tracker IS
        // one of those, and 44-2 is the story that makes it the default one.
        // MinAutonomy = AutonomyDial.Min throughout (behaviour-preserving, epic
        // decision D1: nothing gates these today). Risk grades the CONSEQUENCE,
        // not the caller: the two deletes that can destroy user work are
        // Destructive + irreversible; the preferences delete is Mutating
        // because re-setting it restores the state exactly.
        //
        // SiteKeys carry the ROUTE CONSTRAINTS (`{projectId:guid}`, `{id:guid}`),
        // corrected 2026-07-29 (adversarial review MODERATE-5). 43-8's binding sweep
        // compares RoutePartOf(SiteKey) against the endpoint's RawText ORDINALLY and
        // does not strip constraints, so the six constraint-bearing SiteKeys as first
        // written ("{projectId}", "{id}") would have been rejected the moment 43-9
        // bound them. The SiteKey must be the live pattern verbatim, not a prettified
        // rendering of it.
        Effect(ExternalEffect.TrackerProjectCreate, ActionGroup.IssueTracking, ActionRisk.Mutating, "Create project", "Create a native tracker project, minting the frozen key prefix every work item in it inherits.",
            "POST /api/projects — TrackerEndpoints.CreateProject", min: 20),
        Effect(ExternalEffect.TrackerProjectUpdate, ActionGroup.IssueTracking, ActionRisk.Mutating, "Update project", "Update a native tracker project's name, description, repository binding, estimate scale or archive state.",
            "PATCH /api/projects/{projectId:guid} — TrackerEndpoints.PatchProject", min: 20),
        Effect(ExternalEffect.TrackerProjectDelete, ActionGroup.IssueTracking, ActionRisk.Destructive, "Delete project", "Delete a native tracker project (refused while it holds work items — FK RESTRICT → 409 — but an empty project's removal is not undoable).",
            "DELETE /api/projects/{projectId:guid} — TrackerEndpoints.DeleteProject", reversible: false, min: 95),
        Effect(ExternalEffect.TrackerWorkItemCreate, ActionGroup.IssueTracking, ActionRisk.Mutating, "Create work item", "File a native work item, consuming the project's number sequence (the minted key is frozen from that moment).",
            "POST /api/work-items — TrackerEndpoints.CreateWorkItem", min: 20),
        Effect(ExternalEffect.TrackerWorkItemUpdate, ActionGroup.IssueTracking, ActionRisk.Mutating, "Update work item", "Patch a native work item's title, description, kind, priority, type, iteration, estimate or external ref (single-field tri-state patch).",
            "PATCH /api/work-items/{id:guid} — TrackerEndpoints.PatchWorkItem", min: 20),
        Effect(ExternalEffect.TrackerWorkItemDelete, ActionGroup.IssueTracking, ActionRisk.Destructive, "Delete work item", "Delete a native work item (refused while children exist — 409 naming them; otherwise the row and its relation edges are gone).",
            "DELETE /api/work-items/{id:guid} — TrackerEndpoints.DeleteWorkItem", reversible: false, min: 95),
        Effect(ExternalEffect.TrackerWorkItemAssign, ActionGroup.IssueTracking, ActionRisk.Mutating, "Assign work item", "Set or clear a native work item's assignee (its own member, because assignment is the axis Story 39-20's access model will govern).",
            "POST /api/work-items/{id:guid}/assign — TrackerEndpoints.AssignWorkItem", min: 20),
        Effect(ExternalEffect.TrackerWorkItemSetStatus, ActionGroup.IssueTracking, ActionRisk.Mutating, "Move work item status", "Transition a native work item's status (its own member: an admin may plausibly gate a status move without gating a title edit).",
            "POST /api/work-items/{id:guid}/status — TrackerEndpoints.SetWorkItemStatus", min: 20),
        Effect(ExternalEffect.TrackerPreferencesSet, ActionGroup.IssueTracking, ActionRisk.Mutating, "Set tracker preferences", "Write the tracker preference row (default project / default kind / board grouping) — TENANT-wide configuration in SaaS, not a personal setting.",
            "PUT /api/tracker/preferences — TrackerEndpoints.PutPreferences", min: 20),
        Effect(ExternalEffect.TrackerPreferencesDelete, ActionGroup.IssueTracking, ActionRisk.Mutating, "Reset tracker preferences", "Delete the tracker preference row so the shipped defaults apply again.",
            "DELETE /api/tracker/preferences — TrackerEndpoints.DeletePreferences", min: 20),

        // Story 43-8 (AC1 step 2, carve-out §A1 #1 closed 2026-07-30) — the four
        // MentorshipController [HttpPost] actions, the repo's only attribute-routed
        // controller and the only day-one users of the [Governs] attribute shape.
        //
        // GROUP — model-invocation, by the 43-3 D1 partition rule (kind of
        // consequence AT COMPLETION): completing any of these leaves an autonomous,
        // LLM-driven agent run started / suspended / resumed / terminated. That is
        // the same consequence as effect:agent-dispatch.run, which already sits
        // here. REJECTED alternative: platform-automation "because it starts a
        // workflow" — that group is platform HOUSEKEEPING (engine mediation writes,
        // sweepers, platform tasks), and filing an agent-run control there would
        // bury it in the same admin lever as the outbox sweeper, invisible to
        // anyone gating model invocation.
        //
        // RISK — graded on the CONSEQUENCE, not the caller (the 44-2 rule):
        // start is Command (it causes agent execution) and irreversible (the
        // guidance, reviews and commits the run performs while it lives cannot be
        // undone by cancelling the session afterwards); pause/resume are Mutating
        // and exactly reversible by each other; cancel is Destructive and
        // irreversible (the workflow instance is terminated and the in-flight run
        // abandoned — a new session is a new run, not a restoration).
        //
        // MinAutonomy — AutonomyDial.Min for all four (the Effect() helper's only
        // value): behaviour-preserving per epic decision D1. Nothing gates these
        // today and cataloguing them changes nothing at runtime.
        //
        // SiteKeys are the LIVE attribute-routed patterns verbatim, constraints
        // included ({sessionId:guid}), normalised with a leading slash the way
        // GovernanceHostFixture normalises controller RawText.
        Effect(ExternalEffect.MentorshipSessionStart, ActionGroup.ModelInvocation, ActionRisk.Command, "Start mentorship session", "Open a mentorship session and dispatch the tamma-autonomous-mentorship workflow — after this completes an autonomous agent run is under way.",
            "POST /api/Mentorship/start — MentorshipController.StartMentorship", reversible: false, min: 20),
        Effect(ExternalEffect.MentorshipSessionPause, ActionGroup.ModelInvocation, ActionRisk.Mutating, "Pause mentorship session", "Suspend a running mentorship workflow (resume restores it exactly).",
            "POST /api/Mentorship/sessions/{sessionId:guid}/pause — MentorshipController.PauseSession", min: 20),
        Effect(ExternalEffect.MentorshipSessionResume, ActionGroup.ModelInvocation, ActionRisk.Mutating, "Resume mentorship session", "Put a paused mentorship workflow back into execution.",
            "POST /api/Mentorship/sessions/{sessionId:guid}/resume — MentorshipController.ResumeSession", min: 20),
        Effect(ExternalEffect.MentorshipSessionCancel, ActionGroup.ModelInvocation, ActionRisk.Destructive, "Cancel mentorship session", "Terminate a mentorship workflow instance — the in-flight agent run is abandoned and cannot be resumed.",
            "POST /api/Mentorship/sessions/{sessionId:guid}/cancel — MentorshipController.CancelSession", reversible: false, min: 20),

        // Story 43-17 follow-up — the two /api/engine callbacks that 43-17 flagged as
        // having NO OWNER: live, LLM-reachable, sitting directly beside four siblings
        // 31-13 governed, with no catalog member and no enforcement. Each takes the
        // level of the effect CLASS it belongs to (not a new judgement), so both stay
        // below the shipped dial of 70 and this is behaviour-preserving.
        Effect(ExternalEffect.CiWorkflowDispatch, ActionGroup.CiAndTest, ActionRisk.Command, "Dispatch CI workflow", "Dispatch a CI workflow run (GitHub Actions workflow_dispatch) via the engine callback.",
            "POST /api/engine/trigger-ci — EngineEndpoints.TriggerCi", min: 30),
        // TOOLS: ExecuteTaskRequest carries EnableTools, so this route can run the
        // tool loop — it belongs on the model-invocation plane at llm.call's level,
        // not treated as a bookkeeping callback.
        Effect(ExternalEffect.LlmTaskExecute, ActionGroup.ModelInvocation, ActionRisk.Mutating, "Execute LLM task", "Run an LLM-driven task (optionally with tools) via the engine callback.",
            "POST /api/engine/execute-task — EngineEndpoints.ExecuteTask", reversible: false, min: 20),

        // ── automation (27) — EscalatableToHuman=false for the whole plane ────

        Automation(BackgroundActor.HourlyAnalyticsRollupScheduler, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Hourly analytics rollup", "Rolls up analytics hourly.",
            "Tamma.ElsaServer.Workflows.HourlyAnalyticsRollupScheduler"),
        Automation(BackgroundActor.TenantCleanupRequestedTrigger, ActionGroup.PlatformAutomation, ActionRisk.Destructive, "Tenant cleanup trigger", "Starts tenant cleanup workflows on request events.",
            "Tamma.ElsaServer.Workflows.TenantCleanupRequestedTrigger", reversible: false),
        Automation(BackgroundActor.TenantDeleteRequestedTrigger, ActionGroup.PlatformAutomation, ActionRisk.Destructive, "Tenant delete trigger", "Starts tenant delete workflows on request events.",
            "Tamma.ElsaServer.Workflows.TenantDeleteRequestedTrigger", reversible: false),
        Automation(BackgroundActor.WorkflowSeeder, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Workflow seeder", "Seeds Elsa workflow definitions at startup.",
            "Tamma.ElsaServer.WorkflowSeeder"),
        Automation(BackgroundActor.AgentSeeder, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Agent seeder", "Seeds agent definitions at startup.",
            "Tamma.ElsaServer.AgentSeeder"),
        // Story 41-30 — the tenant-aware scheduled-trigger seam. Mutating at
        // Min (the ProviderSettingsStorePrimingService precedent for risk
        // honesty + the behaviour-preserving default rule): the service
        // writes ledger/registry rows and STARTS workflows, but every
        // workflow it dispatches is separately governed by that workflow's
        // own catalog members — the dial governs the dispatched work, not
        // the dispatcher (story 41-30 "Autonomy behavior"). Ships
        // Enabled=false (AC9), so cataloguing it changes nothing at runtime.
        Automation(BackgroundActor.TenantScheduledTriggerService, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Tenant scheduled-trigger service", "Fires tenant-scoped scheduled workflow dispatches at most once per (tenant, trigger, window).",
            "Tamma.ElsaServer.Workflows.TenantScheduledTriggerService"),
        Automation(BackgroundActor.PoolWarmupService, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Pool warmup", "Warms tenant connection pools.",
            "Tamma.Api.Services.PoolWarmupService"),
        Automation(BackgroundActor.WorkflowSyncService, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Workflow sync", "Synchronizes workflow definitions.",
            "Tamma.Api.Services.WorkflowSyncService"),
        Automation(BackgroundActor.ChannelOutboxSweeper, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Channel outbox sweeper", "Drains the channel outbox.",
            "Tamma.Api.Services.Channels.ChannelOutboxSweeper"),
        Automation(BackgroundActor.SecretAutoRotationScheduler, ActionGroup.Secrets, ActionRisk.Mutating, "Secret auto-rotation scheduler", "Schedules automatic secret rotations.",
            "Tamma.Api.Services.Secrets.Rotation.SecretAutoRotationScheduler",
            sensitive: SensitiveActionCatalog.SecretRotateStarted),
        Automation(BackgroundActor.RetireSweep, ActionGroup.Secrets, ActionRisk.Mutating, "Secret retire sweep", "Retires superseded secret versions (periodic fallback for the platform-task path).",
            "Tamma.Api.Services.Secrets.Rotation.RetireSweepHostedService",
            sensitive: SensitiveActionCatalog.SecretVersionRevoked),
        Automation(BackgroundActor.EngineRegistryHeartbeatService, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Engine registry heartbeat", "Heartbeats engine registrations.",
            "Tamma.Api.Services.Engine.Lifecycle.EngineRegistryHeartbeatService"),
        Automation(BackgroundActor.TenantStatusInvalidationListener, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Tenant status invalidation listener", "Fans out tenant status cache invalidations (FACTORY-registered hosted service — null ImplementationType; flagged for 43-8's registration sweep).",
            "Tamma.Api.Services.TenantStatus.TenantStatusInvalidationListener"),
        // ReadOnly: primes an in-process snapshot from a DB read at startup —
        // it writes nothing anywhere (Epic 46 review F1).
        Automation(BackgroundActor.ProviderSettingsStorePrimingService, ActionGroup.PlatformAutomation, ActionRisk.ReadOnly, "Provider settings store primer", "Primes the provider-settings snapshot before the host serves traffic (fail-soft; the lazy TTL refresh is the fallback).",
            "Tamma.Api.Services.Providers.ProviderSettingsStorePrimingService"),
        Automation(BackgroundActor.EntitlementCacheInvalidationListener, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Entitlement cache invalidation listener", "Fans out entitlement cache invalidations.",
            "Tamma.Api.Services.Pricing.EntitlementCacheInvalidationListener"),
        Automation(BackgroundActor.ConventionStoreSeeder, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Convention store seeder", "Seeds convention templates at startup.",
            "Tamma.Api.Services.Conventions.ConventionStoreSeeder"),
        Automation(BackgroundActor.ProviderSessionCleanupService, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Provider session cleanup", "Cleans up expired provider sessions.",
            "Tamma.Api.Services.Providers.ProviderSessionCleanupService"),
        Automation(BackgroundActor.TaskQueueProcessor, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Task queue processor", "Processes queued background tasks.",
            "Tamma.Api.Services.TaskQueue.TaskQueueProcessor"),
        Automation(BackgroundActor.OutboxSlackSender, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Outbox Slack sender", "Sends queued Slack notifications.",
            "Tamma.Api.Services.Notifications.OutboxSlackSender", reversible: false),
        Automation(BackgroundActor.OutboxSmtpSender, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Outbox SMTP sender", "Sends queued email.",
            "Tamma.Api.Services.Email.OutboxSmtpSender", reversible: false),
        Automation(BackgroundActor.AuditChainCheckpointScheduler, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Audit chain checkpoint scheduler", "Writes audit-chain checkpoints.",
            "Tamma.Api.Services.Audit.AuditChainCheckpointScheduler"),
        Automation(BackgroundActor.RevealTokenSweeper, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Reveal token sweeper", "Expires stale secret-reveal tokens.",
            "Tamma.Api.Services.Secrets.Reveal.RevealTokenSweeper"),
        Automation(BackgroundActor.NotificationDispatcher, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Alert notification dispatcher", "Dispatches alert notifications.",
            "Tamma.Api.Services.Alerts.NotificationDispatcher"),
        Automation(BackgroundActor.BuiltInAlertRuleSeeder, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Built-in alert rule seeder", "Seeds built-in alert rules at startup.",
            "Tamma.Api.Services.Alerts.Rules.BuiltInAlertRuleSeeder"),
        Automation(BackgroundActor.AlertRuleEvaluator, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Alert rule evaluator", "Evaluates alert rules on a cadence.",
            "Tamma.Api.Services.Alerts.Rules.AlertRuleEvaluator"),
        Automation(BackgroundActor.AuditProjector, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Audit projector", "Projects sensitive-action audit rows from events.",
            "Tamma.Api.Services.Audit.AuditProjectorBackgroundService"),
        Automation(BackgroundActor.ActionCatalogStartupValidator, ActionGroup.PlatformAutomation, ActionRisk.ReadOnly, "Action-catalog startup validator", "Boot-time fail-loud check that the tool vocabularies agree with the action catalog (Story 43-4); mutates nothing — it can only refuse to start the Tamma.Api host.",
            "Tamma.Api.Services.Actions.ActionCatalogStartupValidator"),
        // ReadOnly: primes the action-assignments policy snapshot from one CP
        // read at startup — writes nothing (the ProviderSettingsStorePrimingService
        // risk-honesty precedent; Story 43-5).
        Automation(BackgroundActor.GovernancePolicySnapshotPrimingService, ActionGroup.PlatformAutomation, ActionRisk.ReadOnly, "Governance policy snapshot primer", "Primes the action-assignments policy snapshot before the host serves traffic (fail-soft; the lazy TTL refresh is the fallback).",
            "Tamma.Api.Services.Actions.GovernancePolicySnapshotPrimingService"),
        Automation(BackgroundActor.PlatformTaskWorker, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Platform task worker", "Drains the platform task queue (one task at a time per process; RunOnStartup ships false).",
            "Tamma.Api.Services.PlatformTasks.PlatformTaskWorker"),

        // ── platform-task (8) — all platform-automation ──────────────────────

        Task(PlatformTaskKind.RetireSecretVersion, ActionRisk.Mutating, "Retire secret version", "Retires a superseded secret version.",
            "Tamma.Api.Services.Secrets.Rotation.RetireSecretVersionTaskHandler",
            sensitive: SensitiveActionCatalog.SecretVersionRevoked),
        Task(PlatformTaskKind.ActivateScheduledPlan, ActionRisk.Mutating, "Activate scheduled plan", "Activates a scheduled pricing plan version.",
            "Tamma.Api.Services.Provisioning.ActivateScheduledPlanTaskHandler"),
        Task(PlatformTaskKind.MoveTenant, ActionRisk.Mutating, "Move tenant", "Moves a tenant between pool databases.",
            "Tamma.Api.Services.Provisioning.MoveTenantTaskHandler",
            sensitive: SensitiveActionCatalog.TenantMoveRequested),
        Task(PlatformTaskKind.ProvisionTenant, ActionRisk.Mutating, "Provision tenant (Cranl)", "Runs the Cranl provisioning workflow for a tenant.",
            "Tamma.Api.Services.Provisioning.CranlProvisionPlatformTaskHandler"),
        Task(PlatformTaskKind.ProvisionTenantV2, ActionRisk.Mutating, "Provision tenant (V2 saga)", "Runs the V2 tenant provisioning saga.",
            "Tamma.Api.Services.Provisioning.V2.ProvisionTenantV2TaskHandler"),
        Task(PlatformTaskKind.DeprovisionTenant, ActionRisk.Destructive, "Deprovision tenant", "Tears down a tenant's Cranl-minted infrastructure.",
            "Tamma.Api.Services.Provisioning.CranlDeprovisionPlatformTaskHandler", reversible: false),
        Task(PlatformTaskKind.BillingWebhookFollowup, ActionRisk.Mutating, "Billing webhook follow-up", "Processes deferred billing webhook work.",
            "Tamma.Api.Services.Billing.BillingWebhookFollowupTaskHandler"),
        Task(PlatformTaskKind.CreateBillingCustomer, ActionRisk.Mutating, "Create billing customer", "Creates the billing-provider customer for a tenant.",
            "Tamma.Api.Services.Billing.Tasks.CreateBillingCustomerTaskHandler",
            sensitive: SensitiveActionCatalog.BillingCustomerCreated),
    };
}
