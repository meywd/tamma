using System.Collections.ObjectModel;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Api.Auth;

/// <summary>
/// Immutable description of a system-shipped prompt template.
/// Corresponds to <c>PromptTemplate</c> in <c>default-prompts.ts</c>.
/// </summary>
/// <param name="Role">Agent role (developer, tester, security, etc.).</param>
/// <param name="Action">The action this prompt is for (context-scan, plan-implementation, etc.).</param>
/// <param name="Template">The user-facing prompt template with <c>{{variable}}</c> placeholders.</param>
/// <param name="SystemPrompt">System prompt (role identity preamble).</param>
/// <param name="Variables">Variable names expected by the template.</param>
/// <param name="EnableTools">Whether tool use is enabled for this prompt.</param>
/// <param name="MaxTokens">Maximum tokens for the LLM response.</param>
/// <param name="Version">Monotonically increasing version number.</param>
public sealed record PromptTemplate(
    string? Role,
    string Action,
    string Template,
    string SystemPrompt,
    IReadOnlyList<string> Variables,
    bool EnableTools,
    int MaxTokens,
    int Version = 1);

/// <summary>
/// System-shipped prompt registry. Immutable at runtime.
///
/// <para>
/// <b>Story 27-18 — taxonomy reshape.</b> The flat 8×10 cartesian product
/// (80 cells) and the generic <c>action-default</c> safety-net tier are GONE.
/// The registry is now the jagged per-role <c>(role, action)</c> taxonomy of
/// <see cref="RolePhaseMap"/> (SPEC §4 — 8 roles × their specific action sets,
/// ~72 cells total). Prompts key off the IDENTICAL <c>(role, action)</c>
/// taxonomy that conventions use; there is no generic fallback action anywhere.
/// </para>
///
/// <para>
/// Exposes two layers used by <c>PromptStoreService</c>:
/// <list type="bullet">
///   <item><see cref="RoleSystemPrompts"/> — 8 role identity preambles keyed by role wire string.</item>
///   <item><see cref="RoleActionTemplates"/> — the jagged per-role <c>(role, action)</c> templates
///         (one non-empty body per cell in each role's <see cref="RolePhaseMap"/> action set).</item>
/// </list>
/// There is intentionally no third "generic action-default" tier — resolution is
/// <c>override → system default → TammaError</c> (see <c>PromptStoreService</c>).
/// </para>
///
/// <para>
/// <b>Transitional bodies (SPEC §3.5).</b> The body text for each of the ~72
/// cells is, in this story, MIGRATED from the 10 original action body builders
/// (<see cref="ContextScan"/>, <see cref="Plan"/>, …) by mapping each specific
/// action to its closest body family. The bodies follow the thin-prompt style:
/// a one-line task statement, the injected <c>{{variable}}</c> context sections,
/// any hard task constraints, and the output contract — project specifics arrive
/// via <c>{{conventions}}</c>/context, never hardcoded prompt text. These are
/// real, non-empty prompt bodies — NOT placeholders — and serve as the
/// authoritative system defaults until Story 27-16 codegen regenerates per-cell
/// authoritative bodies. The mapping lives in <see cref="BodyBuilderFor"/>; the
/// rationale per cell is documented there.
/// </para>
/// </summary>
public static class SystemPrompts
{
    // -----------------------------------------------------------------------
    // Role catalogue (derived from the AgentRole taxonomy)
    // -----------------------------------------------------------------------

    /// <summary>The 8 agent roles, as wire strings (from <see cref="AgentRole"/>).</summary>
    public static readonly IReadOnlyList<string> Roles =
        Enum.GetValues<AgentRole>().Select(r => r.ToWire()).ToArray();

    // -----------------------------------------------------------------------
    // Layer 1 — System prompts (role identity preambles)
    // -----------------------------------------------------------------------

    public static readonly IReadOnlyDictionary<string, string> RoleSystemPrompts = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>
        {
            ["developer"] =
                "You are an expert software developer who writes production-quality code with proper error handling. Follow the project's conventions and context provided in each task.",
            ["tester"] =
                "You are a testing specialist who writes thorough, maintainable unit, integration, and contract tests. Follow the project's conventions and context provided in each task.",
            ["security"] =
                "You are a security engineer specializing in application security: you identify vulnerabilities (OWASP Top 10), injection attacks, credential leaks, insecure configurations, and weak authentication or authorization boundaries. Follow the project's conventions and context provided in each task.",
            ["devops"] =
                "You are a DevOps engineer specializing in CI/CD pipelines, containerization, and infrastructure automation, evaluating deployment strategies and operational concerns. Follow the project's conventions and context provided in each task.",
            ["architect"] =
                "You are a software architect specializing in distributed systems and event-driven architectures, with deep knowledge of DDD, CQRS, and event sourcing. Follow the project's conventions and context provided in each task.",
            ["product_owner"] =
                "You are a product owner with expertise in agile development, user story management, and feature prioritization, assessing business value, scope, and user impact. Follow the project's conventions and context provided in each task.",
            ["senior_developer"] =
                "You are a senior developer and technical lead who creates detailed implementation plans, decomposes complex tasks, and balances code quality with delivery speed. Follow the project's conventions and context provided in each task.",
            ["tech_writer"] =
                "You are a technical writer who produces clear, concise, unambiguous documentation for developer audiences. Follow the project's conventions and context provided in each task.",
        });

    // -----------------------------------------------------------------------
    // Layer 2 — Role + action templates (jagged per-role taxonomy, ~72 cells)
    // -----------------------------------------------------------------------

    public static readonly IReadOnlyList<PromptTemplate> RoleActionTemplates = BuildRoleActionTemplates();

    private static readonly IReadOnlyDictionary<string, PromptTemplate> RoleActionIndex =
        RoleActionTemplates.ToDictionary(t => Key(t.Role!, t.Action));

    // -----------------------------------------------------------------------
    // Lookups
    // -----------------------------------------------------------------------

    /// <summary>Resolve the system-default role+action template, or null if unknown.</summary>
    public static PromptTemplate? GetRoleAction(string role, string action)
        => RoleActionIndex.TryGetValue(Key(role, action), out var t) ? t : null;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string Key(string role, string action) => $"{role}:{action}";

    /// <summary>
    /// Build the jagged <c>(role, action)</c> template matrix directly from the
    /// authoritative <see cref="RolePhaseMap.EligibleActions"/> SPEC §4 sets, so
    /// the prompt registry and the resolver share a single source of truth. Each
    /// cell's body is produced by mapping the action to its closest body family
    /// via <see cref="BodyBuilderFor"/> (transitional seeds, SPEC §3.5).
    /// </summary>
    private static IReadOnlyList<PromptTemplate> BuildRoleActionTemplates()
    {
        var list = new List<PromptTemplate>(72);

        foreach (var (role, actions) in RolePhaseMap.EligibleActions)
        {
            var roleWire = role.ToWire();
            foreach (var action in actions)
            {
                var actionWire = action.ToWire();
                var builder = BodyBuilderFor(action);
                list.Add(builder(roleWire, actionWire));
            }
        }

        return list.AsReadOnly();
    }

    /// <summary>
    /// Map each specific <see cref="AgentAction"/> to the original body builder of
    /// the closest family (Story 27-18 family mapping — TRANSITIONAL, SPEC §3.5,
    /// pending Story 27-16 authoritative per-cell codegen). Atomic actions map to
    /// their namesake builder; planning/design actions → <see cref="Plan"/>;
    /// review-of-plan lenses → <see cref="PlanReview"/>; review-of-code/audit
    /// lenses → <see cref="CodeReview"/>; documentation/write-ups →
    /// <see cref="Summarize"/>; diagnose/incident actions → <see cref="Debug"/>;
    /// triage/assess/classify → <see cref="Triage"/>; build/execute actions →
    /// <see cref="Implement"/>.
    /// </summary>
    private static Func<string, string, PromptTemplate> BodyBuilderFor(AgentAction action) => action switch
    {
        // ── context-scan (atomic, shared) ──
        AgentAction.ContextScan => ContextScan,

        // ── planning / design / decomposition → Plan ──
        AgentAction.ClarifyRequirements => Plan,
        AgentAction.PlanScope => Plan,
        AgentAction.DefineAcceptanceCriteria => Plan,
        AgentAction.PlanRoadmap => Plan,
        AgentAction.PlanSystemDesign => Plan,
        AgentAction.DesignApiContract => Plan,
        AgentAction.DesignDataModel => Plan,
        AgentAction.DesignIntegration => Plan,
        AgentAction.PlanMigrationStrategy => Plan,
        AgentAction.CreateTasks => Plan,
        AgentAction.PlanImplementation => Plan,
        AgentAction.PlanRefactor => Plan,
        AgentAction.PlanFix => Plan,
        AgentAction.PlanDebugging => Plan,
        AgentAction.PlanTestStrategy => Plan,
        AgentAction.PlanDeployment => Plan,
        AgentAction.PlanIncidentResponse => Plan,

        // ── review-of-plan / risk / feasibility / testability lenses → PlanReview ──
        AgentAction.PlanReview => PlanReview,
        AgentAction.ReviewAcceptance => PlanReview,
        AgentAction.ReviewScope => PlanReview,
        AgentAction.AssessTechnicalRisk => PlanReview,
        AgentAction.ReviewFeasibility => PlanReview,
        AgentAction.ReviewTestability => PlanReview,
        AgentAction.ThreatModel => PlanReview,
        AgentAction.PlanReviewSecurity => PlanReview,
        AgentAction.ReviewOperability => PlanReview,

        // ── review-of-code / audit / verify lenses → CodeReview ──
        AgentAction.CodeReview => CodeReview,
        AgentAction.CodeReviewArchitecture => CodeReview,
        AgentAction.MentorFeedback => CodeReview,
        AgentAction.SelfReview => CodeReview,
        AgentAction.VerifyAcceptance => CodeReview,
        AgentAction.CodeReviewCoverage => CodeReview,
        AgentAction.CodeReviewSecurity => CodeReview,
        AgentAction.AssessVulnerability => CodeReview,
        AgentAction.AuditDependencies => CodeReview,
        AgentAction.AuditSecrets => CodeReview,
        AgentAction.ReviewCompliance => CodeReview,
        AgentAction.ReviewDocs => CodeReview,

        // ── implementation / build / execute → Implement ──
        AgentAction.ImplementFeature => Implement,
        AgentAction.ImplementFix => Implement,
        AgentAction.AddressReviewComments => Implement,
        AgentAction.ImplementInfrastructure => Implement,
        AgentAction.ConfigureCicd => Implement,
        AgentAction.Deploy => Implement,
        AgentAction.Rollback => Implement,

        // ── tests → WriteTests ──
        AgentAction.WriteTests => WriteTests,
        AgentAction.WriteTestCases => WriteTests,
        AgentAction.WriteRegressionTest => WriteTests,
        AgentAction.ExploratoryTest => WriteTests,

        // ── refactor (atomic) ──
        AgentAction.Refactor => Refactor,

        // ── debug / diagnose / incident analysis → Debug ──
        AgentAction.Debug => Debug,
        AgentAction.DebugRootcause => Debug,
        AgentAction.ResolveBlocker => Debug,
        AgentAction.DiagnoseIncident => Debug,
        AgentAction.AnalyzeSecurityIncident => Debug,

        // ── triage / assess / classify / monitor → Triage ──
        AgentAction.TriageIntake => Triage,
        AgentAction.TriageTechnical => Triage,
        AgentAction.TriageDefect => Triage,
        AgentAction.PrioritizeBacklog => Triage,
        AgentAction.MonitorHealth => Triage,
        AgentAction.AssessCapacity => Triage,

        // ── assessment (assessment P0 — real per-cell bodies, not a transitional family) ──
        AgentAction.GenerateAssessmentQuestions => GenerateAssessmentQuestionsBody,
        AgentAction.AnalyzeAssessmentResponse => AnalyzeAssessmentResponseBody,

        // ── research (Story 3.4 — real per-cell body producing the ranked-findings JSON
        //    ResearchParsing recovers; NOT a transitional family) ──
        AgentAction.Research => ResearchBody,

        // ── score-ambiguity (Story 3.6 — real per-cell body producing the structured score
        //    JSON AmbiguityParsing recovers; NOT a transitional family) ──
        AgentAction.ScoreAmbiguity => ScoreAmbiguityBody,

        // ── decompose-issue (Story 2.14 — real per-cell body producing the ordered sub-task
        //    JSON DecompositionParsing recovers; NOT a transitional family) ──
        AgentAction.DecomposeIssue => DecomposeIssueBody,

        // ── summarize / documentation / write-ups → Summarize ──
        AgentAction.SummarizeStakeholder => Summarize,
        AgentAction.SummarizeTechnical => Summarize,
        AgentAction.SummarizeChanges => Summarize,
        AgentAction.WriteAdr => Summarize,
        AgentAction.WritePostmortem => Summarize,
        AgentAction.WriteUserDocs => Summarize,
        AgentAction.WriteApiDocs => Summarize,
        AgentAction.WriteReleaseNotes => Summarize,
        AgentAction.WriteRunbook => Summarize,
        AgentAction.UpdateChangelog => Summarize,

        // Exhaustive over the AgentAction enum. A newly-added token with no arm
        // here is a hard failure rather than a silent default — keeps the
        // transitional mapping honest until 27-16 replaces it.
        _ => throw new TammaError(
            "PROMPT.SEED.NO_BODY_FAMILY",
            $"No transitional body family mapped for action '{action.ToWire()}'. " +
            "Add a mapping in SystemPrompts.BodyBuilderFor (Story 27-18) or regenerate via Story 27-16.",
            new Dictionary<string, object?> { ["action"] = action.ToWire() },
            retryable: false,
            severity: TammaErrorSeverity.Critical),
    };

    private static string SystemFor(string role) => RoleSystemPrompts.TryGetValue(role, out var s)
        ? s
        : RoleSystemPrompts["developer"];

    // -----------------------------------------------------------------------
    // Individual action body builders (migrated bodies — TRANSITIONAL, §3.5)
    //
    // Each builder takes (role, action) and is role-parameterized via {{role}}
    // and SystemFor(role). The bodies are thin-prompt style (task statement +
    // injected context sections + output contract); the Action field is the
    // SPECIFIC taxonomy action token (e.g. "plan-implementation"), not the old
    // generic one ("plan"). The deliberate review-lens behaviour (RoleReviewLens
    // / RoleReviewLensForCodeReview) is retained.
    //
    // These are the system defaults until Story 27-16 regenerates authoritative
    // per-cell bodies (SPEC §3.5 "transitional seed" state).
    // -----------------------------------------------------------------------

    private static PromptTemplate ContextScan(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} scanning a codebase for a {{workItemType}} work item.\n\n" +
            "## Work Item\n{{workItemJson}}\n\n" +
            "## Previous Findings\n{{previousFindings}}\n\n" +
            "Output your findings as a JSON object:\n" +
            "```json\n{\n  \"relevantFiles\": [{\"path\": \"...\", \"reason\": \"...\"}],\n  \"interfaces\": [{\"name\": \"...\", \"location\": \"...\", \"impact\": \"create|modify|consume\"}],\n  \"dependencies\": [{\"name\": \"...\", \"type\": \"internal|external\"}],\n  \"conventions\": [\"...\"],\n  \"risks\": [{\"description\": \"...\", \"severity\": \"low|medium|high\"}]\n}\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemType", "workItemJson", "previousFindings"],
        EnableTools: true,
        MaxTokens: 4096);

    private static PromptTemplate Plan(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} creating an implementation plan.\n\n" +
            "## Work Item\n{{workItemJson}}\n\n" +
            "## Context\n{{contextFindings}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "Break the work item into discrete, ordered tasks.\n\n" +
            "Output as JSON:\n" +
            "```json\n{\n  \"tasks\": [\n    {\n      \"id\": \"T1\",\n      \"description\": \"...\",\n      \"files\": [{\"path\": \"...\", \"action\": \"create|modify\"}],\n      \"dependencies\": [],\n      \"complexity\": \"small|medium|large\",\n      \"testing\": \"...\"\n    }\n  ],\n  \"totalComplexity\": \"small|medium|large\",\n  \"estimatedDuration\": \"...\"\n}\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "contextFindings", "conventions"],
        EnableTools: true,
        MaxTokens: 8192);

    private static PromptTemplate PlanReview(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} reviewing an implementation plan.\n\n" +
            "## Work Item\n{{workItemJson}}\n\n" +
            "## Plan\n{{planJson}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "Verify the plan addresses all requirements in the work item. " +
            "Review with your {{role}} lens:\n" +
            RoleReviewLens(role) +
            "\n" +
            "Output as JSON:\n" +
            "```json\n{\n  \"issues\": [\n    {\n      \"task\": \"T1|General\",\n      \"severity\": \"critical|major|minor|suggestion\",\n      \"category\": \"...\",\n      \"issue\": \"...\",\n      \"recommendation\": \"...\"\n    }\n  ],\n  \"verdict\": {\n    \"decision\": \"APPROVE|REQUEST_CHANGES|NEEDS_DISCUSSION\",\n    \"summary\": \"...\",\n    \"blockingIssues\": []\n  }\n}\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "planJson", "conventions"],
        EnableTools: false,
        MaxTokens: 4096);

    private static PromptTemplate Implement(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} implementing code changes.\n\n" +
            "## Work Item\n{{workItemJson}}\n\n" +
            "## Plan\n{{planJson}}\n\n" +
            "## Current Task\n{{currentTask}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "## Existing Code Context\n{{codeContext}}\n\n" +
            "For each file, provide the complete implementation. " +
            "Follow the project conventions provided above.\n\n" +
            "Output each file as:\n" +
            "```path/to/file\n// file contents\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "planJson", "currentTask", "conventions", "codeContext"],
        EnableTools: true,
        MaxTokens: 16384);

    private static PromptTemplate WriteTests(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} writing tests.\n\n" +
            "## Test Target\n{{testTarget}}\n\n" +
            "## Source Code\n{{sourceCode}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "Write the test file, covering happy paths, error paths, and edge cases. " +
            "Follow the project conventions provided above.\n\n" +
            "File format:\n" +
            "```path/to/file\n// test contents\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "testTarget", "sourceCode", "conventions"],
        EnableTools: true,
        MaxTokens: 8192);

    private static PromptTemplate Refactor(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} analyzing and refactoring code.\n\n" +
            "## Target Code\n{{targetCode}}\n\n" +
            "## Refactoring Goal\n{{refactoringGoal}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "The refactoring must preserve behavior — no functional changes. " +
            "Follow the project conventions provided above.\n\n" +
            "Provide the complete refactored code for each file.\n\n" +
            "Output each file as:\n" +
            "```path/to/file\n// refactored contents\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "targetCode", "refactoringGoal", "conventions"],
        EnableTools: true,
        MaxTokens: 8192);

    private static PromptTemplate CodeReview(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} reviewing code changes in a pull request.\n\n" +
            "## PR Description\n{{prDescription}}\n\n" +
            "## Diff\n{{diff}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "Review with your {{role}} lens:\n" +
            RoleReviewLensForCodeReview(role) +
            "\n" +
            "If no issues are found, explicitly state \"No issues found\" with a brief explanation of what you verified.\n\n" +
            "Output as JSON:\n" +
            "```json\n{\n  \"issues\": [\n    {\n      \"file\": \"...\",\n      \"line\": \"...\",\n      \"severity\": \"critical|major|minor|style\",\n      \"category\": \"bug|security|performance|convention|test-coverage\",\n      \"issue\": \"...\",\n      \"fix\": \"...\"\n    }\n  ],\n  \"summary\": {\n    \"decision\": \"APPROVE|REQUEST_CHANGES|COMMENT\",\n    \"text\": \"...\",\n    \"filesReviewed\": 0,\n    \"issuesBySeverity\": {\"critical\": 0, \"major\": 0, \"minor\": 0, \"style\": 0}\n  }\n}\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "prDescription", "diff", "conventions"],
        EnableTools: false,
        MaxTokens: 8192);

    private static PromptTemplate Triage(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} triaging an issue or alert.\n\n" +
            "## Issue / Alert\n{{issueJson}}\n\n" +
            "## Repository Context\n{{repoContext}}\n\n" +
            "Classify the issue's type, severity, priority, owning role, and estimated effort.\n\n" +
            "Output as JSON:\n" +
            "```json\n{\n  \"type\": \"...\",\n  \"severity\": \"...\",\n  \"priority\": \"P0|P1|P2|P3\",\n  \"ownerRole\": \"...\",\n  \"estimatedEffort\": \"small|medium|large|epic\",\n  \"labels\": [\"...\"],\n  \"relatedIssues\": [],\n  \"reasoning\": \"...\"\n}\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "issueJson", "repoContext"],
        EnableTools: false,
        MaxTokens: 2048);

    private static PromptTemplate Summarize(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} summarizing findings for an issue comment.\n\n" +
            "## Work Item\n{{workItemJson}}\n\n" +
            "## Findings\n{{findings}}\n\n" +
            "## Target Audience\n{{audience}}\n\n" +
            "Write a concise summary suitable for posting as an issue comment, pitched at the target audience.\n\n" +
            "Format:\n" +
            "## Summary\n" +
            "Brief 1-2 sentence overview.\n\n" +
            "### Key Findings\n" +
            "- Bullet points of important findings\n\n" +
            "### Action Items\n" +
            "- [ ] Actionable tasks (if any)\n\n" +
            "### Details\n" +
            "Only include if there are important technical details the audience needs.\n\n" +
            "Keep the summary under 500 words. Prefer clarity over completeness.",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "findings", "audience"],
        EnableTools: false,
        MaxTokens: 2048);

    private static PromptTemplate Debug(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} diagnosing and fixing a failure.\n\n" +
            "## Error Context\n{{errorContext}}\n\n" +
            "## Stack Trace\n{{stackTrace}}\n\n" +
            "## Relevant Code\n{{relevantCode}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "## Recent Changes\n{{recentChanges}}\n\n" +
            "Identify the root cause (not just the symptom) and provide the minimal fix that addresses it.\n\n" +
            "Output as JSON:\n" +
            "```json\n{\n  \"diagnosis\": {\n    \"error\": \"...\",\n    \"rootCause\": \"...\",\n    \"affectedFiles\": [\"...\"],\n    \"fixStrategy\": \"...\",\n    \"confidence\": \"high|medium|low\"\n  },\n  \"fix\": {\n    \"files\": [{\"path\": \"...\", \"changes\": \"...\"}]\n  },\n  \"verification\": {\n    \"commands\": [\"...\"],\n    \"expectedOutput\": \"...\",\n    \"edgeCases\": [\"...\"]\n  }\n}\n```",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "errorContext", "stackTrace", "relevantCode", "conventions", "recentChanges"],
        EnableTools: true,
        MaxTokens: 8192);

    // -----------------------------------------------------------------------
    // Assessment-specific body builders (assessment P0)
    //
    // These are purpose-written for the AssessmentWorkflow llm-call dispatch
    // (docs/superpowers/plans/2026-06-30-assessment-p0-llm-call.md). Unlike the
    // transitional bodies above, these are authoritative per-cell prompts that use
    // the exact Shared-contract variable names Task 2 passes in the dispatch.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prompt for <c>(product_owner, generate-assessment-questions)</c>: produce a
    /// JSON array of skill-appropriate questions about the story so the
    /// <c>AssessmentWorkflow</c> can present them to the junior developer.
    /// Shared-contract variables: <c>storyContext</c>, <c>skillLevel</c>,
    /// <c>questionCount</c>, <c>previousGaps</c>.
    /// </summary>
    private static PromptTemplate GenerateAssessmentQuestionsBody(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are assessing a junior developer's understanding of a story they are about to implement.\n\n" +
            "## Story Context\n{{storyContext}}\n\n" +
            "## Developer Skill Level\n{{skillLevel}}\n\n" +
            "## Previously Identified Gaps (do not re-ask about these)\n{{previousGaps}}\n\n" +
            "Generate exactly {{questionCount}} open-ended (not yes/no) assessment questions calibrated to a " +
            "{{skillLevel}} developer, specific to THIS story (not generic software engineering), covering the " +
            "story's requirements, technical design, testing considerations, edge cases, and risks — avoiding " +
            "topics already covered in the previously identified gaps above.\n\n" +
            "Return ONLY a JSON array of question strings with no wrapper object:\n" +
            "```json\n[\"Question 1 text?\", \"Question 2 text?\", ...]\n```\n\n" +
            "Do not include numbering, explanations, or any text outside the JSON array.",
        SystemPrompt: SystemFor(role),
        Variables: ["storyContext", "skillLevel", "questionCount", "previousGaps"],
        EnableTools: false,
        MaxTokens: 2048);

    /// <summary>
    /// Prompt for <c>(product_owner, analyze-assessment-response)</c>: evaluate a
    /// junior developer's answers against the questions and story context, returning
    /// a structured JSON result that <c>AssessmentWorkflow</c> feeds into
    /// <c>ClassifyResultActivity</c> (confidence) and <c>SetOutputResult</c>
    /// (rationale).
    /// Shared-contract variables: <c>storyContext</c>, <c>questions</c>,
    /// <c>response</c>, <c>skillLevel</c>.
    /// </summary>
    private static PromptTemplate AnalyzeAssessmentResponseBody(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are evaluating a junior developer's assessment response to determine their readiness to implement a story.\n\n" +
            "## Story Context\n{{storyContext}}\n\n" +
            "## Assessment Questions\n{{questions}}\n\n" +
            "## Developer's Response\n{{response}}\n\n" +
            "## Developer Skill Level\n{{skillLevel}}\n\n" +
            "Analyze the developer's response against the questions and story context: assess correctness, " +
            "depth of understanding, knowledge gaps that could cause problems during implementation, " +
            "strengths, and readiness to implement this story. " +
            "Calibrate your confidence score to the developer's {{skillLevel}} level — a junior developer " +
            "is not expected to have senior-level depth; assess relative to appropriate expectations.\n\n" +
            "Return ONLY a JSON object (no markdown fences, no wrapper):\n" +
            "{\"status\":\"Correct|Partial|Incorrect\",\"confidence\":0.0,\"gaps\":[\"...\"],\"strengths\":[\"...\"],\"rationale\":\"...\"}\n\n" +
            "Where `confidence` is a decimal between 0.0 and 1.0, and `status` follows the classification:\n" +
            "- `Correct` = developer is ready, confidence ≥ 0.7\n" +
            "- `Partial` = developer has gaps but shows some understanding, 0.4 ≤ confidence < 0.7\n" +
            "- `Incorrect` = developer is not ready, confidence < 0.4",
        SystemPrompt: SystemFor(role),
        Variables: ["storyContext", "questions", "response", "skillLevel"],
        EnableTools: false,
        MaxTokens: 2048);

    // -----------------------------------------------------------------------
    // Research-specific body builder (Story 3.4)
    //
    // Purpose-written for the ResearchWorkflow llm-call dispatch
    // (Tamma.ElsaServer/Workflows/ResearchWorkflow.cs). It synthesises the
    // codebase / prior-art context already gathered by the context-gathering
    // sub-workflow into a RANKED, confidence-scored research report and returns
    // the EXACT JSON object ResearchParsing.ParseReport recovers:
    //   { "topic", "summary", "findings":[{ "title","summary","relevance",
    //     "confidence","citations":[...] }], "overallConfidence" }
    // The parser fails closed on a missing summary or zero usable findings, so the
    // template is explicit that both are load-bearing (resolution is
    // tenant→system→error — this body is never empty/plain).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prompt for <c>(product_owner, research)</c>: investigate the given work item
    /// using the gathered context and emit a ranked, confidence-scored research
    /// report as the structured JSON <see cref="Tamma.Activities.Research.ResearchParsing"/>
    /// parses. Variables: <c>workItemJson</c> (the topic / issue under investigation)
    /// and <c>findings</c> (the codebase / prior-art context from context-gathering).
    /// </summary>
    private static PromptTemplate ResearchBody(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} investigating a work item to produce a ranked, " +
            "confidence-scored research report for the engineering team.\n\n" +
            "## Work Item / Topic\n{{workItemJson}}\n\n" +
            "## Gathered Context (codebase / prior art)\n{{findings}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "Base every finding on the gathered context — do NOT invent findings or citations that the " +
            "context does not support. If the context is thin, return only the findings it genuinely supports.\n\n" +
            "Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:\n" +
            "```json\n" +
            "{\n" +
            "  \"topic\": \"the question / topic investigated\",\n" +
            "  \"summary\": \"1-3 sentence overview of what the research concluded\",\n" +
            "  \"findings\": [\n" +
            "    {\n" +
            "      \"title\": \"short headline for the finding\",\n" +
            "      \"summary\": \"what was learned and why it matters\",\n" +
            "      \"relevance\": 0.0,\n" +
            "      \"confidence\": 0.0,\n" +
            "      \"citations\": [\"path/to/file\", \"https://...\"]\n" +
            "    }\n" +
            "  ],\n" +
            "  \"overallConfidence\": 0.0\n" +
            "}\n" +
            "```\n\n" +
            "Requirements (the downstream parser fails closed if these are not met):\n" +
            "- `summary` MUST be a non-empty overview — it is load-bearing.\n" +
            "- `findings` MUST contain at least one real finding, each with a non-empty `title` or `summary`.\n" +
            "- `relevance` and `confidence` are decimals between 0.0 and 1.0.\n" +
            "- Order `findings` by `relevance` descending, then `confidence` descending.\n" +
            "- `overallConfidence` is a decimal in [0,1] reflecting confidence across the findings.",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "findings", "conventions"],
        EnableTools: false,
        MaxTokens: 4096);

    // -----------------------------------------------------------------------
    // Ambiguity-scoring body builder (Story 3.6)
    //
    // Purpose-written for the AmbiguityScoringWorkflow llm-call dispatch
    // (Tamma.ElsaServer/Workflows/AmbiguityScoringWorkflow.cs). It scores how
    // ambiguous / underspecified a requirement is and returns the EXACT JSON object
    // AmbiguityParsing.ParseAssessment recovers:
    //   { "score", "confidence", "rationale", "ambiguities":[{ "type","description",
    //     "severity","recommendation" }] }
    // The parser fails closed on a missing / out-of-range score or a missing
    // rationale, so the template is explicit that both are load-bearing (resolution
    // is tenant→system→error — this body is never empty/plain).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prompt for <c>(product_owner, score-ambiguity)</c>: score how ambiguous /
    /// underspecified the given requirement is and emit a structured assessment as the JSON
    /// <see cref="Tamma.Activities.Ambiguity.AmbiguityParsing"/> parses. Variables:
    /// <c>workItemJson</c> (the requirement under assessment) and <c>contextFindings</c>
    /// (any domain / codebase context that informs the scoring).
    /// </summary>
    private static PromptTemplate ScoreAmbiguityBody(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} scoring how ambiguous or underspecified a requirement is, so the " +
            "team can decide whether to ask clarifying questions before implementation begins.\n\n" +
            "## Requirement / Work Item\n{{workItemJson}}\n\n" +
            "## Context (domain / codebase / prior decisions)\n{{contextFindings}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "The overall score runs 0.0 = crystal clear and fully specified to 1.0 = so ambiguous it " +
            "cannot be implemented as written. " +
            "Base the assessment on the requirement and context provided — do NOT invent problems " +
            "that are not there. A genuinely clear requirement should score near 0 with an empty " +
            "`ambiguities` list; do not manufacture ambiguities to justify a higher score.\n\n" +
            "Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:\n" +
            "```json\n" +
            "{\n" +
            "  \"score\": 0.0,\n" +
            "  \"confidence\": 0.0,\n" +
            "  \"rationale\": \"1-3 sentence explanation of the overall score\",\n" +
            "  \"ambiguities\": [\n" +
            "    {\n" +
            "      \"type\": \"vague|missing|contradictory|implicit\",\n" +
            "      \"description\": \"what is unclear / missing / contradictory / implicit\",\n" +
            "      \"severity\": \"low|medium|high\",\n" +
            "      \"recommendation\": \"a specific action to resolve this ambiguity\"\n" +
            "    }\n" +
            "  ]\n" +
            "}\n" +
            "```\n\n" +
            "Requirements (the downstream parser fails closed if these are not met):\n" +
            "- `score` MUST be a decimal between 0.0 and 1.0 — it is load-bearing.\n" +
            "- `rationale` MUST be a non-empty explanation of the score — it is load-bearing.\n" +
            "- `confidence` is a decimal between 0.0 and 1.0.\n" +
            "- `ambiguities` MAY be empty when the requirement is genuinely clear; otherwise each " +
            "item MUST carry a non-empty `description`.\n" +
            "- `type` MUST be one of: `vague`, `missing`, `contradictory`, `implicit`.",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "contextFindings", "conventions"],
        EnableTools: false,
        MaxTokens: 2048);

    // -----------------------------------------------------------------------
    // Issue-decomposition body builder (Story 2.14)
    //
    // Purpose-written for the IssueDecompositionWorkflow llm-call dispatch
    // (Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs). It breaks a
    // complex issue into an ORDERED set of smaller, implementable sub-tasks — each
    // with a rationale, a definition of done, a rough sizing, a complexity, and its
    // declared prerequisite dependencies — and returns the EXACT JSON object
    // DecompositionParsing.ParseDecomposition recovers:
    //   { "summary", "subtasks":[{ "id","title","description","acceptanceCriteria",
    //     "estimateHours","complexity","dependsOn":[...] }] }
    // The parser fails closed on a missing summary or zero usable sub-tasks, so the
    // template is explicit that both are load-bearing (resolution is
    // tenant→system→error — this body is never empty/plain). Sub-task ids +
    // dependsOn are the FOUNDATION contract for Story 2.15 (#138 dependency mapping)
    // and Story 2.16 (#139 sequencing).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prompt for <c>(senior_developer, decompose-issue)</c>: break a complex issue into an
    /// ordered set of smaller, implementable sub-tasks (with rationale, sizing and dependencies)
    /// and emit the structured decomposition as the JSON
    /// <see cref="Tamma.Activities.Decomposition.DecompositionParsing"/> parses. Variables:
    /// <c>workItemJson</c> (the issue under decomposition) and <c>findings</c> (the codebase /
    /// prior-art context that informs the breakdown's scope/dependency judgement).
    /// </summary>
    private static PromptTemplate DecomposeIssueBody(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} decomposing a complex issue into an ORDERED set of smaller, " +
            "independently implementable sub-tasks so the team can deliver it incrementally with " +
            "continuous integration.\n\n" +
            "## Issue / Work Item\n{{workItemJson}}\n\n" +
            "## Gathered Context (codebase / prior art)\n{{findings}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "Break the work into sub-tasks each sized ROUGHLY 2-8 hours with a clear definition of " +
            "done; together the sub-tasks must fully deliver the parent issue's intent. " +
            "Base the breakdown on the issue and context provided — do NOT invent scope the issue " +
            "does not call for, and do NOT fabricate dependencies. Only reference sub-task ids you " +
            "actually define.\n\n" +
            "Return ONLY a single JSON object (no markdown fences, no prose outside it) of this " +
            "EXACT shape:\n" +
            "```json\n" +
            "{\n" +
            "  \"summary\": \"1-3 sentence overview of the breakdown and how it preserves the " +
            "issue's intent\",\n" +
            "  \"subtasks\": [\n" +
            "    {\n" +
            "      \"id\": \"ST-1\",\n" +
            "      \"title\": \"short headline for the sub-task\",\n" +
            "      \"description\": \"what to implement in this sub-task\",\n" +
            "      \"acceptanceCriteria\": \"the definition of done for this sub-task\",\n" +
            "      \"estimateHours\": 4,\n" +
            "      \"complexity\": \"low|medium|high\",\n" +
            "      \"dependsOn\": [\"ST-0\"]\n" +
            "    }\n" +
            "  ]\n" +
            "}\n" +
            "```\n\n" +
            "Requirements (the downstream parser fails closed if these are not met):\n" +
            "- `summary` MUST be a non-empty overview — it is load-bearing (it records intent " +
            "preservation).\n" +
            "- `subtasks` MUST contain at least one sub-task; each MUST carry a non-empty `id` and " +
            "at least a `title` or `description`.\n" +
            "- `id`s MUST be unique within the decomposition.\n" +
            "- `estimateHours` is a number (rough hours); `complexity` is one of `low`, `medium`, " +
            "`high`.\n" +
            "- Every entry in `dependsOn` MUST be the `id` of another sub-task in this " +
            "decomposition (no self-references, no dangling ids).\n" +
            "- Order `subtasks` so each sub-task's prerequisites appear before it.",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "findings", "conventions"],
        EnableTools: false,
        MaxTokens: 4096);

    // -----------------------------------------------------------------------
    // Role-specific review lenses (inlined in plan-review / code-review bodies)
    // -----------------------------------------------------------------------

    private static string RoleReviewLens(string role) => role switch
    {
        "security" =>
            "   - Check for security implications in each task\n" +
            "   - Verify input validation and auth concerns are addressed\n",
        "tester" =>
            "   - Check that testing strategy is comprehensive\n" +
            "   - Verify edge cases and error paths are covered\n",
        "architect" =>
            "   - Check that architectural patterns are followed\n" +
            "   - Verify service boundaries and interface contracts\n",
        "devops" =>
            "   - Check for deployment and infrastructure impact\n" +
            "   - Verify CI/CD pipeline compatibility\n",
        _ => "   - Apply your role-specific expertise to the plan\n",
    };

    private static string RoleReviewLensForCodeReview(string role) => role switch
    {
        "security" =>
            "   - Look for credential leaks, injection vulnerabilities, unsafe input handling\n" +
            "   - Verify authentication and authorization checks\n",
        "tester" =>
            "   - Verify test coverage for new/changed code paths\n" +
            "   - Check test quality (assertions, edge cases, mocking)\n",
        "architect" =>
            "   - Verify architectural patterns (DDD, CQRS, event sourcing)\n" +
            "   - Check interface contracts and service boundaries\n",
        "devops" =>
            "   - Check for deployment impact, config changes, migration needs\n" +
            "   - Verify CI pipeline compatibility\n",
        _ => "   - Apply your role-specific expertise to the diff\n",
    };
}
