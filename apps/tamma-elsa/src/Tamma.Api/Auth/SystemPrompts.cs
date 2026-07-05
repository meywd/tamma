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
/// action to its closest body family. These are real, non-empty prompt bodies —
/// NOT placeholders — and serve as the authoritative system defaults until Story
/// 27-16 codegen regenerates per-cell authoritative bodies. The mapping lives in
/// <see cref="BodyBuilderFor"/>; the rationale per cell is documented there.
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
                "You are an expert software developer working on the Tamma project. You write production-quality TypeScript code that passes strict compilation, follows established conventions, and includes proper error handling. You have deep expertise in Node.js, Fastify, PostgreSQL, and event-driven architectures.",
            ["tester"] =
                "You are a testing specialist for the Tamma project. You write thorough, maintainable tests using Vitest 3.x with colocated test files. You have expertise in unit testing, integration testing, contract testing, and mocking strategies using MSW and vi.mock.",
            ["security"] =
                "You are a security engineer specializing in application security for TypeScript/Node.js systems. You identify vulnerabilities (OWASP Top 10), review code for injection attacks, credential leaks, and insecure configurations. You validate input sanitization, authentication flows, and authorization boundaries.",
            ["devops"] =
                "You are a DevOps engineer specializing in CI/CD pipelines, Docker containerization, Kubernetes orchestration, and infrastructure automation. You evaluate deployment strategies, infrastructure impact, and operational concerns for the Tamma platform.",
            ["architect"] =
                "You are a software architect specializing in distributed systems, microservices, and event-driven architectures. You review system design, interface contracts, service boundaries, and architectural patterns. You have deep knowledge of DDD, CQRS, event sourcing, and the Tamma DCB pattern.",
            ["product_owner"] =
                "You are a product owner with expertise in agile development, user story management, and feature prioritization. You assess business value, scope decisions, and user impact. You communicate clearly with both technical and non-technical stakeholders.",
            ["senior_developer"] =
                "You are a senior developer and technical lead on the Tamma project. You create detailed implementation plans, decompose complex tasks, and make technology decisions. You balance code quality with delivery speed and mentor other developers through your plans.",
            ["tech_writer"] =
                "You are a technical writer who produces clear, concise documentation for developer audiences. You summarize technical findings, write issue comments, create PR descriptions, and produce changelog entries. You use precise language and avoid ambiguity.",
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
    // and SystemFor(role). The body text is preserved from the original 10
    // builders; the Action field is now the SPECIFIC taxonomy action token (e.g.
    // "plan-implementation"), not the old generic one ("plan"). The deliberate
    // review-lens behaviour (RoleReviewLens / RoleReviewLensForCodeReview) is
    // retained.
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Identify what files, interfaces, and modules are relevant to this work item\n" +
            "2. Determine dependencies and downstream consumers that may be affected\n" +
            "3. Note any existing patterns or conventions that must be followed\n" +
            "4. Flag potential risks or conflicts with ongoing work\n" +
            "</thinking>\n\n" +
            "Scan the codebase and provide structured findings:\n\n" +
            "<findings>\n" +
            "- **Relevant Files**: List files directly related to this work item with a one-line description of their role\n" +
            "- **Interfaces & Types**: Key interfaces/types that will be created, modified, or consumed\n" +
            "- **Dependencies**: External packages, internal modules, and services involved\n" +
            "- **Conventions**: Project patterns observed that must be followed (naming, error handling, testing)\n" +
            "- **Risks**: Potential conflicts, breaking changes, or areas needing extra care\n" +
            "</findings>\n\n" +
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Break down the work item into discrete, ordered tasks\n" +
            "2. For each task, identify which files need changes and what the changes are\n" +
            "3. Consider the testing strategy for each task\n" +
            "4. Identify dependencies between tasks (what must happen first)\n" +
            "5. Estimate relative complexity of each task\n" +
            "</thinking>\n\n" +
            "<plan>\n" +
            "Produce a structured implementation plan:\n\n" +
            "For each task:\n" +
            "- **Task ID**: Sequential identifier (T1, T2, ...)\n" +
            "- **Description**: What this task accomplishes\n" +
            "- **Files**: Which files to create or modify\n" +
            "- **Dependencies**: Which tasks must complete before this one\n" +
            "- **Complexity**: small | medium | large\n" +
            "- **Testing**: What tests are needed for this task\n" +
            "</plan>\n\n" +
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Verify the plan addresses all requirements in the work item\n" +
            "2. Check for missing tasks or overlooked edge cases\n" +
            "3. Review from your specific expertise as a {{role}}:\n" +
            RoleReviewLens(role) +
            "4. Identify risks or improvements\n" +
            "</thinking>\n\n" +
            "<review>\n" +
            "For each issue found:\n" +
            "- **Task**: Which task ID is affected (or \"General\" for plan-wide issues)\n" +
            "- **Severity**: critical | major | minor | suggestion\n" +
            "- **Category**: missing-task | security | performance | convention | testing | architecture\n" +
            "- **Issue**: Description of the problem\n" +
            "- **Recommendation**: Specific suggestion to fix it\n" +
            "</review>\n\n" +
            "<verdict>\n" +
            "- **Decision**: APPROVE | REQUEST_CHANGES | NEEDS_DISCUSSION\n" +
            "- **Summary**: 1-3 sentence summary of the review\n" +
            "- **Blocking Issues**: List any critical/major issues that must be resolved\n" +
            "</verdict>\n\n" +
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Analyze the requirements for this specific task\n" +
            "2. Check existing code patterns that should be followed\n" +
            "3. Identify edge cases and error conditions\n" +
            "4. Plan the implementation order (interfaces first, then implementations, then tests)\n" +
            "</thinking>\n\n" +
            "<implementation>\n" +
            "For each file, provide the complete implementation.\n\n" +
            "Rules:\n" +
            "- Follow the import order: Node.js built-ins, external deps, internal packages (@tamma/*), relative\n" +
            "- Use async/await exclusively, never .then()/.catch()\n" +
            "- All errors must use the TammaError class with code, message, context, retryable, severity\n" +
            "- Boolean functions must use is/has/should prefix\n" +
            "- Private functions must use _ prefix\n" +
            "- Constants must use SCREAMING_SNAKE_CASE\n" +
            "- Files use kebab-case, test files are colocated as *.test.ts\n" +
            "- All imports use .js extension (ESM)\n" +
            "- Never mutate state -- always create new objects with spread\n" +
            "- TypeScript strict mode: no implicit any, no unchecked index access\n\n" +
            "Output each file as:\n" +
            "```path/to/file.ts\n// file contents\n```\n" +
            "</implementation>",
        SystemPrompt: SystemFor(role),
        Variables: ["role", "workItemJson", "planJson", "currentTask", "conventions", "codeContext"],
        EnableTools: true,
        MaxTokens: 16384);

    private static PromptTemplate WriteTests(string role, string action) => new(
        Role: role,
        Action: action,
        Template:
            "You are a {{role}} writing tests for the Tamma project.\n\n" +
            "## Test Target\n{{testTarget}}\n\n" +
            "## Source Code\n{{sourceCode}}\n\n" +
            "## Conventions\n{{conventions}}\n\n" +
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Identify the public API surface to test\n" +
            "2. List happy path scenarios\n" +
            "3. List error/edge case scenarios\n" +
            "4. Identify dependencies that need mocking (use MSW for HTTP, vi.mock for modules)\n" +
            "5. Determine coverage targets (80% line, 75% branch, 85% function)\n" +
            "</thinking>\n\n" +
            "<test_plan>\n" +
            "List each test case with:\n" +
            "- Description (should read like documentation)\n" +
            "- Category: unit | integration | edge-case | error-handling\n" +
            "- Expected behavior\n" +
            "</test_plan>\n\n" +
            "<tests>\n" +
            "Write the test file. Rules:\n" +
            "- Use describe/it blocks with descriptive names\n" +
            "- Each test should test ONE thing\n" +
            "- Use vi.mock() factories that are self-contained (hoisted -- put mock classes inside factory)\n" +
            "- Mock external APIs with MSW\n" +
            "- Assert specific values, not just truthiness\n" +
            "- Test error paths explicitly (expect(...).rejects.toThrow)\n" +
            "- Clean up after each test (afterEach)\n" +
            "- Use beforeEach for common setup\n" +
            "- Prefer toBe/toEqual over toBeTruthy\n\n" +
            "File format:\n" +
            "```path/to/file.test.ts\n// test contents\n```\n" +
            "</tests>",
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Understand the current code structure and its purpose\n" +
            "2. Identify code smells, duplication, or convention violations\n" +
            "3. Plan refactoring steps that preserve behavior (no functional changes)\n" +
            "4. Consider impact on tests and downstream consumers\n" +
            "5. Verify the refactoring improves readability, maintainability, or performance\n" +
            "</thinking>\n\n" +
            "<analysis>\n" +
            "- **Current Issues**: List specific problems in the code\n" +
            "- **Proposed Changes**: Describe each refactoring step\n" +
            "- **Risk Assessment**: What could break and how to verify it doesn't\n" +
            "</analysis>\n\n" +
            "<refactored>\n" +
            "Provide the complete refactored code for each file.\n\n" +
            "Output each file as:\n" +
            "```path/to/file.ts\n// refactored contents\n```\n" +
            "</refactored>\n\n" +
            "<verification>\n" +
            "- Commands to run to verify the refactoring works\n" +
            "- Expected test outcomes\n" +
            "- Any manual verification steps needed\n" +
            "</verification>",
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Read the full diff to understand the change's intent\n" +
            "2. Check each file against project conventions\n" +
            "3. Review from your expertise as a {{role}}:\n" +
            RoleReviewLensForCodeReview(role) +
            "4. Identify logical errors, edge cases, and missing error handling\n" +
            "5. Verify test coverage for new/changed code paths\n" +
            "</thinking>\n\n" +
            "<review>\n" +
            "For each issue found:\n" +
            "- **File**: path/to/file.ts\n" +
            "- **Line**: line number or range\n" +
            "- **Severity**: critical | major | minor | style\n" +
            "- **Category**: bug | security | performance | convention | test-coverage\n" +
            "- **Issue**: Description of the problem\n" +
            "- **Fix**: Specific code change to resolve it\n\n" +
            "If no issues are found, explicitly state \"No issues found\" with a brief explanation of what you verified.\n" +
            "</review>\n\n" +
            "<summary>\n" +
            "- 1-3 sentence summary of the review\n" +
            "- **Decision**: APPROVE | REQUEST_CHANGES | COMMENT\n" +
            "- **Files Reviewed**: count\n" +
            "- **Issues Found**: count by severity\n" +
            "</summary>\n\n" +
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Understand the issue description and any error details\n" +
            "2. Classify the issue type (bug, feature, task, chore, security)\n" +
            "3. Assess severity and impact on users/system\n" +
            "4. Determine priority based on severity and business impact\n" +
            "5. Identify which team or role should own this\n" +
            "6. Estimate effort required\n" +
            "</thinking>\n\n" +
            "<triage>\n" +
            "- **Type**: bug | feature | task | chore | security\n" +
            "- **Severity**: critical | high | medium | low\n" +
            "- **Priority**: P0 (immediate) | P1 (this sprint) | P2 (next sprint) | P3 (backlog)\n" +
            "- **Owner Role**: developer | tester | security | devops | architect\n" +
            "- **Estimated Effort**: small (< 1 day) | medium (1-3 days) | large (3-5 days) | epic (> 5 days)\n" +
            "- **Labels**: suggested labels for the issue\n" +
            "- **Related Issues**: any known related or duplicate issues\n" +
            "</triage>\n\n" +
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Identify the key findings that the audience needs to know\n" +
            "2. Determine the appropriate level of technical detail for the audience\n" +
            "3. Structure the summary for quick scanning (headers, bullet points)\n" +
            "4. Highlight any action items or decisions needed\n" +
            "</thinking>\n\n" +
            "Write a concise summary suitable for posting as a GitHub issue comment.\n\n" +
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Parse the error message and stack trace to identify the immediate cause\n" +
            "2. Identify the root cause (not just the symptom)\n" +
            "3. Check if this is a known pattern (common TypeScript/Node.js issues)\n" +
            "4. Determine the minimal fix that addresses the root cause\n" +
            "5. Verify the fix doesn't introduce regressions\n" +
            "</thinking>\n\n" +
            "<diagnosis>\n" +
            "- **Error**: One-line description of the error\n" +
            "- **Root Cause**: Explanation of why this happens\n" +
            "- **Affected Files**: List of files involved\n" +
            "- **Fix Strategy**: Approach to resolve\n" +
            "- **Confidence**: high | medium | low (based on available evidence)\n" +
            "</diagnosis>\n\n" +
            "<fix>\n" +
            "Provide the exact code changes needed.\n\n" +
            "For each file:\n" +
            "```path/to/file.ts\n// fixed contents\n```\n" +
            "</fix>\n\n" +
            "<verification>\n" +
            "- Commands to run to verify the fix\n" +
            "- Expected output\n" +
            "- Edge cases to test\n" +
            "</verification>\n\n" +
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
            "## Instructions\n\n" +
            "Generate exactly {{questionCount}} assessment questions that:\n" +
            "- Are appropriate for a {{skillLevel}} developer (calibrate depth and terminology accordingly)\n" +
            "- Test understanding of the story's requirements, technical approach, and edge cases\n" +
            "- Cover different aspects: functional requirements, technical design, testing considerations, risks\n" +
            "- Avoid topics already covered in the previousGaps list above\n" +
            "- Are open-ended enough to reveal genuine understanding (not yes/no)\n" +
            "- Are specific to THIS story, not generic software engineering questions\n\n" +
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
            "## Instructions\n\n" +
            "Analyze the developer's response against the questions and story context. Assess:\n" +
            "- **Correctness**: Are the answers factually correct and complete?\n" +
            "- **Depth**: Does the developer show genuine understanding or superficial familiarity?\n" +
            "- **Gaps**: What knowledge gaps are revealed that could cause problems during implementation?\n" +
            "- **Strengths**: What does the developer clearly understand well?\n" +
            "- **Readiness**: Given their {{skillLevel}} level, are they ready to implement this story?\n\n" +
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
            "## Instructions\n\n" +
            "<thinking>\n" +
            "1. Identify the concrete question(s) the work item raises that research must answer\n" +
            "2. Mine the gathered context for evidence — relevant files, existing patterns, prior decisions, gaps\n" +
            "3. Distil each piece of evidence into a discrete finding: what was learned and why it matters\n" +
            "4. Score each finding for RELEVANCE (how directly it bears on the topic) and CONFIDENCE " +
            "(how well the gathered context supports it), each a decimal in [0,1]\n" +
            "5. Attach citations (file paths, URLs, or doc references from the context) that back each finding\n" +
            "6. Rank findings most-relevant-first, then compute an overall confidence across them\n" +
            "</thinking>\n\n" +
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
            "      \"citations\": [\"path/to/file.cs\", \"https://...\"]\n" +
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
