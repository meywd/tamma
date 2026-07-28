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
// DEFAULTS — behaviour-preserving (epic decision D1: v1 ENFORCES, so shipped
// defaults must change nothing). The derivation rule (43-3 D4): a member ships
// AlwaysHuman if and only if, TODAY, a person must act before it can complete.
// Applying it yields a ONE-member AlwaysHuman set: document-type:design
// (AcceptanceDefaults.For(Design) ships AcceptorRequirement.Human — its only
// production occurrence, AcceptanceDefaults.cs; design.md §3.1's "10 document
// types" is VERIFIED FALSE, see 43-3 C2). Everything else ships AutonomyDial.Min.
// Never literals — a literal would not move when the dial does.
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
        bool reversible = true) =>
        new(new ActionKey(ActionNamespace.Tool, tool.ToWire()), group, risk, reversible,
            title, summary, AutonomyDial.Min, site);

    private static ActionDescriptor Effect(
        ExternalEffect effect, ActionGroup group, ActionRisk risk, string title, string summary, string site,
        bool reversible = true, string? sensitive = null, bool enforceable = true) =>
        new(new ActionKey(ActionNamespace.Effect, effect.ToWire()), group, risk, reversible,
            title, summary, AutonomyDial.Min, site, sensitive, EscalatableToHuman: true, Enforceable: enforceable);

    private static ActionDescriptor Automation(
        BackgroundActor actor, ActionGroup group, ActionRisk risk, string title, string summary, string site,
        bool reversible = true, string? sensitive = null) =>
        // EscalatableToHuman is FALSE for the whole plane: a sweeper cannot
        // suspend for a person — Seam D (43-9) can only deny (pinned by
        // ActionDescriptorMetadataTests).
        new(new ActionKey(ActionNamespace.Automation, actor.ToWire()), group, risk, reversible,
            title, summary, AutonomyDial.Min, site, sensitive, EscalatableToHuman: false);

    private static ActionDescriptor Task(
        PlatformTaskKind kind, ActionRisk risk, string title, string summary, string site,
        bool reversible = true, string? sensitive = null) =>
        new(new ActionKey(ActionNamespace.PlatformTask, kind.ToWire()), ActionGroup.PlatformAutomation,
            risk, reversible, title, summary, AutonomyDial.Min, site, sensitive);

    private static IReadOnlyList<ActionDescriptor> BuildDescriptors() => new[]
    {
        // ── agent-action (80) — AgentAction.cs declaration order ─────────────

        Agent(AgentAction.ContextScan, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Context scan", "Scan the repository and issue context to build understanding before work starts."),
        Agent(AgentAction.TriageIntake, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage intake", "Classify and route an incoming issue. NOTE: ships at Min despite the live AlwaysEscalate entry (TriageBindingHelper) — the floor comes from the legacy surface via 43-5's max() composition, and duplicating it as a catalog default would make deleting the legacy entry fail to lower the threshold (43-3 D7; see 43-5's ShippedTriageDefault_StillEscalates)."),
        Agent(AgentAction.ClarifyRequirements, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Clarify requirements", "Formulate clarification questions for ambiguous requirements."),
        Agent(AgentAction.PlanScope, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Plan scope", "Define what is in and out of scope; produces ordering, not a binding artifact."),
        Agent(AgentAction.DefineAcceptanceCriteria, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Define acceptance criteria", "Derive testable acceptance criteria from requirements."),
        Agent(AgentAction.PrioritizeBacklog, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Prioritize backlog", "Order backlog items by value and risk."),
        Agent(AgentAction.PlanRoadmap, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Plan roadmap", "Lay out delivery sequencing across epics; ordering/analysis, not a binding plan (43-3 D5.3)."),
        Agent(AgentAction.SummarizeStakeholder, ActionGroup.Docs, ActionRisk.Mutating, "Stakeholder summary", "Write a stakeholder-facing summary of work already done."),
        Agent(AgentAction.ReviewAcceptance, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Acceptance review", "Decide whether delivered work meets its acceptance criteria."),
        Agent(AgentAction.ReviewScope, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Scope review", "Verdict on whether proposed scope matches the request."),
        Agent(AgentAction.GenerateAssessmentQuestions, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Generate assessment questions", "Produce questions that probe understanding of an issue."),
        Agent(AgentAction.AnalyzeAssessmentResponse, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Analyze assessment response", "Evaluate a human's answers to assessment questions."),
        Agent(AgentAction.Research, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Research", "Investigate approaches, libraries and prior art."),
        Agent(AgentAction.ScoreAmbiguity, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Score ambiguity", "Quantify how ambiguous a request is for escalation routing."),
        Agent(AgentAction.IncorporateAnswers, ActionGroup.Authoring, ActionRisk.Mutating, "Incorporate answers", "Fold clarification answers back into the binding requirement artifact."),

        Agent(AgentAction.TriageTechnical, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Technical triage", "Assess technical shape and routing of an issue."),
        Agent(AgentAction.PlanSystemDesign, ActionGroup.Authoring, ActionRisk.Mutating, "System design", "Produce the system design others build against (binding artifact — 43-3 D5.3)."),
        Agent(AgentAction.DesignApiContract, ActionGroup.Authoring, ActionRisk.Mutating, "API contract design", "Author an API contract others implement against."),
        Agent(AgentAction.DesignDataModel, ActionGroup.Authoring, ActionRisk.Mutating, "Data model design", "Author the data model others build against."),
        Agent(AgentAction.DesignIntegration, ActionGroup.Authoring, ActionRisk.Mutating, "Integration design", "Author the integration design between components/systems."),
        Agent(AgentAction.PlanMigrationStrategy, ActionGroup.Authoring, ActionRisk.Mutating, "Migration strategy", "Author the migration plan others execute (binding — 43-3 D5.3)."),
        Agent(AgentAction.WriteAdr, ActionGroup.Docs, ActionRisk.Mutating, "Write ADR", "Record an architecture decision already made."),
        Agent(AgentAction.PlanReview, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Plan review", "Review verdict on an implementation plan."),
        Agent(AgentAction.CodeReviewArchitecture, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Architecture code review", "Architecture-focused review verdict on a change."),
        Agent(AgentAction.AssessTechnicalRisk, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Assess technical risk", "Estimate the technical risk of a proposed change."),
        Agent(AgentAction.ProposeDesign, ActionGroup.Authoring, ActionRisk.Mutating, "Propose design", "Author a design proposal (the artifact the human-pinned design acceptance decides on)."),

        Agent(AgentAction.CreateTasks, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Create tasks", "Break work into ordered tasks; ordering, not a binding artifact."),
        Agent(AgentAction.PlanImplementation, ActionGroup.Authoring, ActionRisk.Mutating, "Implementation plan", "Author the implementation plan the developer codes to (binding — 43-3 D5.3)."),
        Agent(AgentAction.CodeReview, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Code review", "General review verdict on a change."),
        Agent(AgentAction.PlanRefactor, ActionGroup.Authoring, ActionRisk.Mutating, "Refactor plan", "Author the refactoring plan others execute (binding — 43-3 D5.3)."),
        Agent(AgentAction.DebugRootcause, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Root-cause analysis", "Analyze a defect to its root cause; produces understanding."),
        Agent(AgentAction.SummarizeTechnical, ActionGroup.Docs, ActionRisk.Mutating, "Technical summary", "Write a technical summary of work already done."),
        Agent(AgentAction.ResolveBlocker, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Resolve blocker", "Work out how to clear a blocking condition."),
        Agent(AgentAction.MentorFeedback, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Mentor feedback", "Mentoring review feedback on a contributor's work."),
        Agent(AgentAction.DecomposeIssue, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Decompose issue", "Split an issue into smaller workable pieces."),

        Agent(AgentAction.PlanFix, ActionGroup.Authoring, ActionRisk.Mutating, "Fix plan", "Author the fix plan the developer codes to (binding — 43-3 D5.3)."),
        Agent(AgentAction.PlanDebugging, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Debugging plan", "Order the debugging investigation; analysis, not a binding artifact (43-3 D5.3)."),
        Agent(AgentAction.ImplementFeature, ActionGroup.Authoring, ActionRisk.Mutating, "Implement feature", "Write the code for a feature."),
        Agent(AgentAction.ImplementFix, ActionGroup.Authoring, ActionRisk.Mutating, "Implement fix", "Write the code for a fix."),
        // 43-3 D5.2 — write-tests → authoring, not ci-and-test: ci-and-test is
        // EXECUTING tests; writing test code is authoring code. Rejected: a single
        // "testing" group fusing low-risk execution with code authorship.
        Agent(AgentAction.WriteTests, ActionGroup.Authoring, ActionRisk.Mutating, "Write tests", "Author test code for a change (authoring, not test execution — 43-3 D5.2)."),
        Agent(AgentAction.Refactor, ActionGroup.Authoring, ActionRisk.Mutating, "Refactor", "Restructure code without changing behaviour."),
        Agent(AgentAction.Debug, ActionGroup.Authoring, ActionRisk.Mutating, "Debug", "Instrument, reproduce and fix through code changes."),
        Agent(AgentAction.AddressReviewComments, ActionGroup.Authoring, ActionRisk.Mutating, "Address review comments", "Apply code changes answering review feedback."),
        Agent(AgentAction.SelfReview, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Self review", "The author's own pre-submission review verdict."),
        Agent(AgentAction.ReviewFeasibility, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Feasibility review", "Verdict on whether a plan is feasible as written."),
        Agent(AgentAction.TriageContextScan, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage context scan", "The Findings-producing triage context scan (document contract; Story 39-15 D5)."),

        Agent(AgentAction.PlanTestStrategy, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Test strategy", "Decide the testing approach; ordering/analysis, not a binding artifact (43-3 D5.3)."),
        Agent(AgentAction.WriteTestCases, ActionGroup.Authoring, ActionRisk.Mutating, "Write test cases", "Author test-case code/specs (authoring — 43-3 D5.2)."),
        Agent(AgentAction.WriteRegressionTest, ActionGroup.Authoring, ActionRisk.Mutating, "Write regression test", "Author a regression test pinning a fixed defect (authoring — 43-3 D5.2)."),
        Agent(AgentAction.ExploratoryTest, ActionGroup.CiAndTest, ActionRisk.Command, "Exploratory test", "Execute exploratory testing against the system (executing, not writing tests)."),
        Agent(AgentAction.VerifyAcceptance, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Verify acceptance", "Verify delivered work against its acceptance criteria."),
        Agent(AgentAction.CodeReviewCoverage, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Coverage review", "Coverage-focused review verdict on a change."),
        Agent(AgentAction.TriageDefect, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Triage defect", "Classify and route a reported defect."),
        Agent(AgentAction.ReviewTestability, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Testability review", "Verdict on whether a design/plan is testable."),

        Agent(AgentAction.ThreatModel, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Threat model", "Model threats against a design; produces understanding."),
        Agent(AgentAction.PlanReviewSecurity, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Security plan review", "Security-focused review verdict on a plan."),
        Agent(AgentAction.CodeReviewSecurity, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Security code review", "Security-focused review verdict on a change."),
        Agent(AgentAction.AssessVulnerability, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Assess vulnerability", "Assess the impact and exploitability of a vulnerability."),
        // audit-dependencies stays in planning-and-analysis: it touches no secret
        // material (contrast audit-secrets, 43-3 D5.4).
        Agent(AgentAction.AuditDependencies, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Audit dependencies", "Audit third-party dependencies for risk; no secret material involved."),
        // 43-3 D5.4 — audit-secrets → secrets, not planning-and-analysis: for the
        // secrets group the SUBJECT dominates the verb — the group exists so an
        // admin can gate everything touching secret material in one move.
        // Rejected: planning-and-analysis by verb-consistency with audit-*/assess-*.
        Agent(AgentAction.AuditSecrets, ActionGroup.Secrets, ActionRisk.ReadOnly, "Audit secrets", "Audit secret handling and exposure (in the secrets group: subject dominates verb — 43-3 D5.4)."),
        Agent(AgentAction.ReviewCompliance, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Compliance review", "Compliance-focused review verdict."),
        Agent(AgentAction.AnalyzeSecurityIncident, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Analyze security incident", "Analyze a security incident; produces understanding."),

        Agent(AgentAction.PlanDeployment, ActionGroup.DeployControl, ActionRisk.ReadOnly, "Deployment plan", "Plan a deployment (deploy-control: subject matter dominates — 43-3 D5.3)."),
        // 43-3 D5.1 — implement-infrastructure → authoring, not deploy-control:
        // the rule is consequence AT COMPLETION — IaC written into a branch has
        // code-write consequence; the production consequence arrives at deploy,
        // which is separately gated. Rejected: deploy-control on intent. THE
        // ASSIGNMENT MOST LIKELY TO BE OVERRULED — an admin raising deploy-control
        // to AlwaysHuman has NOT gated Terraform edits (both group descriptions
        // say so).
        Agent(AgentAction.ImplementInfrastructure, ActionGroup.Authoring, ActionRisk.Mutating, "Implement infrastructure", "Author infrastructure-as-code changes (authoring, not deploy-control — 43-3 D5.1)."),
        Agent(AgentAction.ConfigureCicd, ActionGroup.DeployControl, ActionRisk.Mutating, "Configure CI/CD", "Change CI/CD pipeline configuration."),
        Agent(AgentAction.Deploy, ActionGroup.DeployControl, ActionRisk.Destructive, "Deploy", "Deploy to an environment (the work phase; the prod stage transition is effect:deploy.promote-prod).", reversible: false),
        Agent(AgentAction.Rollback, ActionGroup.DeployControl, ActionRisk.Destructive, "Rollback", "Roll an environment back (the work phase; the prod branch is effect:deploy.rollback).", reversible: false),
        Agent(AgentAction.MonitorHealth, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Monitor health", "Observe system health signals."),
        Agent(AgentAction.DiagnoseIncident, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Diagnose incident", "Diagnose a production incident; produces understanding."),
        Agent(AgentAction.PlanIncidentResponse, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Incident response plan", "Order the incident response; analysis, not a binding artifact (43-3 D5.3)."),
        Agent(AgentAction.WritePostmortem, ActionGroup.Docs, ActionRisk.Mutating, "Write postmortem", "Write the postmortem for a resolved incident."),
        Agent(AgentAction.AssessCapacity, ActionGroup.PlanningAndAnalysis, ActionRisk.ReadOnly, "Assess capacity", "Assess capacity and scaling headroom."),
        Agent(AgentAction.ReviewOperability, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Operability review", "Verdict on operational readiness."),

        Agent(AgentAction.SummarizeChanges, ActionGroup.Docs, ActionRisk.Mutating, "Summarize changes", "Write a change summary for an audience."),
        Agent(AgentAction.WriteUserDocs, ActionGroup.Docs, ActionRisk.Mutating, "Write user docs", "Write end-user documentation."),
        Agent(AgentAction.WriteApiDocs, ActionGroup.Docs, ActionRisk.Mutating, "Write API docs", "Write API documentation."),
        Agent(AgentAction.WriteReleaseNotes, ActionGroup.Docs, ActionRisk.Mutating, "Write release notes", "Write release notes for a shipped version."),
        Agent(AgentAction.WriteRunbook, ActionGroup.Docs, ActionRisk.Mutating, "Write runbook", "Write an operational runbook."),
        Agent(AgentAction.UpdateChangelog, ActionGroup.Docs, ActionRisk.Mutating, "Update changelog", "Update the changelog."),
        Agent(AgentAction.ReviewDocs, ActionGroup.ReviewAndAcceptance, ActionRisk.Mutating, "Docs review", "Review verdict on documentation."),

        // ── document-type (10) — all review-and-acceptance (43-3 AC4): they are
        //    acceptance decisions by construction ────────────────────────────

        Doc(DocumentTypeKey.Findings, "Findings acceptance", "Accept/route a findings document."),
        Doc(DocumentTypeKey.AmbiguityAssessment, "Ambiguity assessment acceptance", "Accept/route an ambiguity assessment."),
        Doc(DocumentTypeKey.Clarification, "Clarification acceptance", "Accept/route a clarification document."),
        Doc(DocumentTypeKey.Decomposition, "Decomposition acceptance", "Accept/route a decomposition document."),
        Doc(DocumentTypeKey.Plan, "Plan acceptance", "Accept/route a plan document (ships panel SELECTION — a multi-reviewer roster, not a human acceptor; stays Min per 43-3 C2)."),
        // THE one-member AlwaysHuman set (43-3 D4/AC8): AcceptanceDefaults.For(Design)
        // ships AcceptorRequirement.Human — the ONLY production occurrence. Pinned
        // against the real switch by DesignDocumentType_MatchesAcceptanceDefaults.
        Doc(DocumentTypeKey.Design, "Design acceptance", "Accept a design proposal — pinned to a human acceptor today (AcceptanceDefaults.For(Design), Story 39-13 D4).", min: AutonomyDial.AlwaysHuman),
        Doc(DocumentTypeKey.Review, "Review acceptance", "Accept/route a review document (panel selection, not a human acceptor; stays Min per 43-3 C2)."),
        Doc(DocumentTypeKey.TriageDecision, "Triage decision acceptance", "Accept/route a triage decision document."),
        Doc(DocumentTypeKey.Diagnosis, "Diagnosis acceptance", "Accept/route a diagnosis document."),
        Doc(DocumentTypeKey.TestSpec, "Test spec acceptance", "Accept/route a test specification document."),

        // ── tool (8) ─────────────────────────────────────────────────────────

        Tool(ToolAction.FileRead, ActionGroup.CodeRead, ActionRisk.ReadOnly, "Read file", "Read a file in the workspace.",
            "Tamma.Activities.LlmCall.Tools.FileReadTool"),
        Tool(ToolAction.FileWrite, ActionGroup.CodeWrite, ActionRisk.Mutating, "Write file", "Write a file in the workspace (single undifferentiated member — no per-path selector; known bypass surface).",
            "Tamma.Activities.LlmCall.Tools.FileWriteTool"),
        Tool(ToolAction.SearchCode, ActionGroup.CodeRead, ActionRisk.ReadOnly, "Search code", "Search code in the workspace.",
            "Tamma.Activities.LlmCall.Tools.SearchCodeTool"),
        Tool(ToolAction.ShellExecute, ActionGroup.CommandExecution, ActionRisk.Command, "Execute shell command", "Run a shell command in the workspace (known bypass: can reach any governed route by curl).",
            "Tamma.Activities.LlmCall.Tools.ShellExecuteTool", reversible: false),
        Tool(ToolAction.RunTests, ActionGroup.CiAndTest, ActionRisk.Command, "Run tests", "Execute the test suite in the workspace.",
            "Tamma.Activities.LlmCall.Tools.RunTestsTool"),
        Tool(ToolAction.GetAcceptanceRules, ActionGroup.CodeRead, ActionRisk.ReadOnly, "Read acceptance rules", "Read the resolved acceptance policy (principal-bound, per-session tool; deliberately not DI-registered — Story 39-5 D6).",
            "Tamma.Api.Services.AcceptanceRules.GetAcceptanceRulesTool"),
        Tool(ToolAction.GitOperationsRead, ActionGroup.SourceControlRead, ActionRisk.ReadOnly, "Git read operations", "Read-graded git subcommands (status/diff/log/show/rev-parse/ls-files/fetch/branch).",
            "Tamma.Activities.LlmCall.Tools.GitOperationsTool (read-graded GitSubcommand members)"),
        Tool(ToolAction.GitOperationsWrite, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Git write operations", "Write-graded git subcommands (add/commit/push/checkout/stash/pull) — includes push.",
            "Tamma.Activities.LlmCall.Tools.GitOperationsTool (write-graded GitSubcommand members)"),

        // ── effect (22) ──────────────────────────────────────────────────────

        Effect(ExternalEffect.EngineEventsAppend, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Append domain events", "Engine appends DCB events through the mediation seam.",
            "POST /api/engine/events — EngineEndpoints.AppendEvents", reversible: false),
        Effect(ExternalEffect.EnginePlatformEventsAppend, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Append platform events", "Engine appends platform events through the mediation seam.",
            "POST /api/engine/platform-events — EngineEndpoints.AppendPlatformEvents", reversible: false),
        Effect(ExternalEffect.EngineDocumentPersist, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Persist document", "Engine persists a document envelope through the mediation seam.",
            "POST /api/engine/documents — DocumentEndpoints.PersistFromEngine"),
        Effect(ExternalEffect.EngineDocumentSetStatus, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Set document status", "Engine transitions a document's lifecycle status.",
            "POST /api/engine/documents/{documentId}/status — DocumentEndpoints.SetStatusFromEngine"),
        Effect(ExternalEffect.EngineChannelOutboxEnqueue, ActionGroup.PlatformAutomation, ActionRisk.Mutating, "Enqueue channel message", "Engine enqueues an outbound channel message.",
            "POST /api/engine/channel/outbox — ChannelEndpoints.EnqueueFromEngine"),
        Effect(ExternalEffect.LlmCall, ActionGroup.ModelInvocation, ActionRisk.Mutating, "LLM call", "Dispatch an LLM call (Seam A observes and never blocks — epic decision D1: 44 of 45 calling workflows have no human route).",
            "POST /api/v1/llm/call — LlmCallEndpoints.CallLlm", reversible: false),
        Effect(ExternalEffect.GitBranchCreate, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Create branch", "Create a branch on the git platform.",
            "POST /api/v1/git/{owner}/{repo}/branches — GitEndpoints.CreateBranch"),
        Effect(ExternalEffect.GitBranchDelete, ActionGroup.SourceControlWrite, ActionRisk.Destructive, "Delete branch", "Delete a branch on the git platform.",
            "DELETE /api/v1/git/{owner}/{repo}/branches — GitEndpoints.DeleteBranch", reversible: false),
        Effect(ExternalEffect.GitPullRequestCreate, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Create pull request", "Open a pull request on the git platform (known bypass: defeatable by git push under tool:git_operations.write).",
            "POST /api/v1/git/{owner}/{repo}/pull-requests — GitEndpoints.CreatePullRequest"),
        Effect(ExternalEffect.GitPullRequestMerge, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Merge pull request", "Merge a pull request on the git platform.",
            "PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/merge — GitEndpoints.MergePullRequest", reversible: false),
        Effect(ExternalEffect.GitReleaseCreate, ActionGroup.SourceControlWrite, ActionRisk.Mutating, "Create release", "Cut a release on the git platform.",
            "POST /api/v1/git/{owner}/{repo}/releases — GitEndpoints.CreateRelease"),
        Effect(ExternalEffect.GitIssuePatch, ActionGroup.IssueTracking, ActionRisk.Mutating, "Update issue", "Update an issue on the git platform.",
            "PATCH /api/v1/git/{owner}/{repo}/issues/{n} — GitEndpoints.UpdateIssue"),
        Effect(ExternalEffect.JiraTicketPatch, ActionGroup.IssueTracking, ActionRisk.Mutating, "Update Jira ticket", "Update a Jira ticket.",
            "PATCH /api/v1/jira/tickets/{ticketId} — JiraEndpoints.UpdateTicket"),
        Effect(ExternalEffect.CiTestsTrigger, ActionGroup.CiAndTest, ActionRisk.Command, "Trigger CI tests", "Trigger a CI test run.",
            "POST /api/v1/ci/{owner}/{repo}/test-runs — CiEndpoints.TriggerTests"),
        Effect(ExternalEffect.AgentDispatchRun, ActionGroup.ModelInvocation, ActionRisk.Command, "Dispatch agent run", "Trigger an external agent run.",
            "POST /api/v1/agent-dispatch/{owner}/{repo}/runs — AgentDispatchEndpoints.TriggerRun",
            sensitive: SensitiveActionCatalog.AgentDispatchSucceeded),
        Effect(ExternalEffect.NotifySlackQueue, ActionGroup.ExternalComms, ActionRisk.Mutating, "Queue Slack message", "Queue an outbound Slack message.",
            "POST /api/v1/notifications/slack — NotificationEndpoints.QueueSlack", reversible: false),
        Effect(ExternalEffect.NotifyEmailSend, ActionGroup.ExternalComms, ActionRisk.Mutating, "Send email", "Send an outbound email (a sent message cannot be unsent).",
            "POST /api/v1/notifications/email — EmailEndpoints.SendEmail", reversible: false),
        // Ships at Min, NOT AlwaysHuman (43-3 D3/C4, binding deviation from design.md
        // §3.1): under enforcing-v1 (epic D1) an AlwaysHuman default would gate every
        // MCP invocation on day one — a behaviour change in a behaviour-preserving
        // story. The admin opts in. Pinned by DeployAndMcp_ShipAtMin_PerEpicDecisionD1.
        Effect(ExternalEffect.McpToolInvoke, ActionGroup.ModelInvocation, ActionRisk.Command, "Invoke MCP tool", "Invoke an MCP tool (ONE COARSE MEMBER — no per-server/per-tool granularity; recorded hole).",
            "POST /api/kb/mcp/servers/{id}/start|stop — KbEndpoints (invocation in intelligence-server sidecar)", reversible: false),
        // INFORMATIONAL ONLY, NEVER ENFORCEABLE (epic README OQ2, answered
        // 2026-07-25): the reveal is how an authorized action gets its credential;
        // gating it would demand a human per credential fetch. Enforceable=false is
        // the descriptor-property modelling the answer requires of 43-2.
        Effect(ExternalEffect.SecretReveal, ActionGroup.Secrets, ActionRisk.ReadOnly, "Reveal secret", "Read a secret value for an already-authorized use (informational only — never enforceable; what governs a secret is the action that needs it).",
            "GET /api/v1/secrets/reveal/{token} — SecretEndpoints.RevealSecret",
            sensitive: SensitiveActionCatalog.SecretReveal, enforceable: false),
        Effect(ExternalEffect.ProcessSpawn, ActionGroup.CommandExecution, ActionRisk.Command, "Spawn process", "Spawn an OS process inside the tool loop.",
            "Tamma.Activities.LlmCall.Tools.ShellExecuteTool → ProcessStartInfo", reversible: false),
        // Ships at Min, NOT AlwaysHuman (43-3 D3/C4): v1 enforces, so AlwaysHuman
        // here would gate every production deploy on upgrade day. The existing
        // business-mode human gate (DeploymentPipelineWorkflow.cs:243 →
        // WaitForDeploymentApprovalActivity) is UNTOUCHED and 43-9 joins it by OR.
        Effect(ExternalEffect.DeployPromoteProd, ActionGroup.DeployControl, ActionRisk.Destructive, "Promote to production", "Production promotion stage transition (the deploy itself runs inside the LLM tool loop — see the deploy-control group description).",
            "Tamma.ElsaServer.Workflows.DeploymentPipelineWorkflow — production stage transition", reversible: false),
        Effect(ExternalEffect.DeployRollback, ActionGroup.DeployControl, ActionRisk.Destructive, "Roll back production", "Production rollback branch (same LLM-tool-loop limitation as promote).",
            "Tamma.ElsaServer.Workflows.DeploymentPipelineWorkflow — RollbackProduction branch", reversible: false),

        // ── automation (26) — EscalatableToHuman=false for the whole plane ────

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
