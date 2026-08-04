using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Core.Documents;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Prompt-cell ↔ caller-parser CONTRACT drift test.
///
/// <para><b>Why this exists:</b> Elsa workflows dispatch <c>llm-call</c> with a
/// <c>(role, action)</c> pair and then parse the reply with FAIL-CLOSED parsers
/// (<c>ResearchParsing</c>, <c>AmbiguityParsing</c>, <c>DecompositionParsing</c>,
/// <c>ClarifyParsing</c>, <c>DesignParsing</c>, <c>PlanValidationHelper</c>, inline
/// validators…). The prompt body that TELLS the model which JSON fields to emit
/// lives in a separate artifact — <c>Prompts/{role}/{action}.md</c>, loaded by
/// <c>PromptFileLoader</c> and resolved via <see cref="SystemPrompts.GetRoleAction"/>.
/// Nothing bound the two: a cell could be reused (or its template rewritten) with a
/// reply shape the caller's parser fails closed on, and no test noticed until
/// runtime. Commit <c>580d355</c> fixed exactly two such taxonomy collisions
/// (<c>propose-design</c> and <c>incorporate-answers</c> had to be minted because a
/// shared cell was being parsed two different ways). This test turns that
/// convention into a build gate.</para>
///
/// <para><b>Mechanism — two halves:</b></para>
/// <list type="number">
///   <item><b>Binding satisfaction</b> (<see cref="EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken"/>):
///   a hand-maintained map pins, per parser-backed <c>(role, action)</c> cell, the
///   JSON field tokens the caller's parser slices. The system template for that
///   cell must literally contain every token (each requirement is a group of
///   ALTERNATIVES — e.g. <c>"tasks"|"steps"</c> — satisfied by any one member,
///   mirroring parsers that accept several spellings). Editing a template out of
///   its contract fails the build naming the cell, the missing token, and the
///   parser that fails closed on it.</item>
///   <item><b>Coverage guard</b> (<see cref="EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted"/>):
///   re-uses <see cref="TaxonomyDriftBuildTests.EnumerateAllDispatchPairs"/> (the
///   reflection over compiled workflow graphs) to enumerate every <c>(role, action)</c>
///   an <c>llm-call</c> dispatch actually emits, and asserts each pair is EITHER in
///   the binding map OR in an explicit, justified allowlist of pairs whose callers
///   consume free text / lenient-with-fallback output. A NEW dispatch pair that is
///   neither fails the build, forcing its author to declare the contract (or the
///   absence of one) the day the dispatch is written — this is what catches the
///   next cell-reuse-with-a-different-shape.</item>
/// </list>
///
/// <para><b>Known pre-existing violations</b> are baselined in
/// <see cref="KnownContractViolations"/> (a ratchet: entries may only be removed,
/// and a stale entry — one whose template now satisfies its contract — fails the
/// build so the baseline cannot rot).</para>
///
/// <para><b>Contract declared, dispatch pending</b> is a fourth state, recorded in
/// <see cref="PendingProducerCells"/> (Story 41-1b AC6). A producing cell that no
/// compiled workflow dispatches yet cannot be a live <see cref="Bindings"/> entry —
/// the stale-classification guard in
/// <see cref="EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted"/> rejects a
/// classification with no emitter — but its contract is nonetheless KNOWN, because
/// the typed document type that will validate its replies already ships. The table
/// records that contract per producing cell and fails the build the day the cell IS
/// dispatched, so the entry GRADUATES into <see cref="Bindings"/> rather than rotting
/// into a shape nobody rechecks.</para>
/// </summary>
[TestFixture]
public class ContractBindingTests
{
    // ====================================================================
    // Binding map — (role, action) → the contract its callers' parsers require
    // ====================================================================

    /// <summary>
    /// One parser-backed cell's contract. <paramref name="Parser"/> names the
    /// fail-closed parser (for the failure message); each element of
    /// <paramref name="RequiredTokenGroups"/> is a group of alternative tokens —
    /// the template must contain AT LEAST ONE member of EVERY group.
    /// </summary>
    private sealed record CellContract(string Parser, IReadOnlyList<string[]> RequiredTokenGroups);

    /// <summary>A group with a single required token.</summary>
    private static string[] One(string token) => [token];

    /// <summary>A group satisfied by any one of several alternative tokens.</summary>
    private static string[] AnyOf(params string[] alternatives) => alternatives;

    /// <summary>
    /// Tokens are the QUOTED JSON field names (<c>"summary"</c>, not <c>summary</c>)
    /// so a stray prose word cannot satisfy a contract — the template must actually
    /// show the field in its instructed JSON shape. The two bare-array cells use the
    /// phrase <c>JSON array</c> instead (their parsers take a top-level array of
    /// strings, so there is no field name to pin).
    ///
    /// Every entry below was verified against the parser source before pinning —
    /// the evidence file:line references are the parser reads themselves.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), CellContract> Bindings =
        new Dictionary<(string, string), CellContract>
        {
            // ResearchWorkflow (39-13) binds (product_owner, research) as the produce step of its
            // document-lifecycle binding; the shape authority is now the typed validator
            // Tamma.Core/Documents/Types/Findings.cs (FindingsDocumentType.Validate) — strict on
            // "summary" / non-empty "findings", per-finding "title"/"summary", "relevance",
            // "confidence", "citations", and "overallConfidence". Token groups unchanged (39-3 D2
            // pinned the wire shape verbatim); only the parser authority migrated.
            [("product_owner", "research")] = new("FindingsDocumentType.Validate",
            [
                One("\"summary\""), One("\"findings\""), One("\"title\""),
                One("\"relevance\""), One("\"confidence\""), One("\"citations\""),
                One("\"overallConfidence\""),
            ]),

            // AmbiguityScoringWorkflow (39-13) binds (product_owner, score-ambiguity) as the
            // produce step of its document-lifecycle binding; the shape authority is now the typed
            // validator Tamma.Core/Documents/Types/AmbiguityAssessment.cs
            // (AmbiguityAssessmentDocumentType.Validate) — strict on "score"/"rationale", reads
            // "confidence", per-item "type"/"description"/"severity"/"recommendation". Tokens
            // unchanged; only the parser authority migrated.
            [("product_owner", "score-ambiguity")] = new("AmbiguityAssessmentDocumentType.Validate",
            [
                One("\"score\""), One("\"confidence\""), One("\"rationale\""),
                One("\"ambiguities\""), One("\"type\""), One("\"description\""),
                One("\"severity\""), One("\"recommendation\""),
            ]),

            // IssueDecompositionWorkflow (39-12) binds the (senior_developer, decompose-issue)
            // cell as the produce step of its document-lifecycle binding; the shape authority
            // is now the typed validator Tamma.Core/Documents/Types/Decomposition.cs
            // (DecompositionDocumentType.Validate) — strict on "summary" / "subtasks",
            // per-subtask "id" + title|description, and slices "acceptanceCriteria",
            // "estimateHours", "complexity", "dependsOn". The token groups are unchanged
            // (39-3 D2 pinned the wire shape verbatim); only the parser authority migrated.
            [("senior_developer", "decompose-issue")] = new("DecompositionDocumentType.Validate",
            [
                One("\"summary\""), One("\"subtasks\""), One("\"id\""), One("\"title\""),
                One("\"description\""), One("\"acceptanceCriteria\""),
                One("\"estimateHours\""), One("\"complexity\""), One("\"dependsOn\""),
            ]),

            // ClarifyingQuestionsWorkflow (39-13) binds (product_owner, clarify-requirements) as
            // Run A's produce step; the shape authority is now the typed validator
            // Tamma.Core/Documents/Types/Clarification.cs (ClarificationDocumentType.Validate) —
            // the questions phase instructs a bare JSON array of question strings. Token unchanged.
            [("product_owner", "clarify-requirements")] = new("ClarificationDocumentType.Validate",
            [
                One("JSON array"),
            ]),

            // ClarifyingQuestionsWorkflow (39-13) binds (product_owner, incorporate-answers) as
            // Run B's produce step; the shape authority is ClarificationDocumentType.Validate —
            // the resolution phase slices "clarifiedRequirement"/"remainingAmbiguities"/"resolved".
            [("product_owner", "incorporate-answers")] = new("ClarificationDocumentType.Validate",
            [
                One("\"clarifiedRequirement\""), One("\"remainingAmbiguities\""), One("\"resolved\""),
            ]),

            // DesignProposalWorkflow (39-13) binds (architect, propose-design) as the produce step
            // of its document-lifecycle binding; the shape authority is now the typed validator
            // Tamma.Core/Documents/Types/Design.cs (DesignDocumentType.Validate) — strict on
            // "summary", slices "recommendation"/"constraintEvaluation" and "alternatives" items'
            // "name"/"tradeoffs". Tokens unchanged; only the parser authority migrated.
            [("architect", "propose-design")] = new("DesignDocumentType.Validate",
            [
                One("\"summary\""), One("\"recommendation\""), One("\"constraintEvaluation\""),
                One("\"alternatives\""), One("\"name\""), One("\"tradeoffs\""),
            ]),

            // PlanGenerationWorkflow (39-14) binds the (architect, plan-system-design) cell as
            // the produce step of its document-lifecycle binding; the shape authority is now the
            // typed validator Tamma.Core/Documents/Types/Plan.cs (PlanDocumentType.Validate) —
            // subsumes what the retired PlanValidationHelper.ValidatePlan checked (root
            // "tasks"|"steps" + "fileMap"|"files"|"filesToModify"). Tokens unchanged (39-4 D5
            // pinned round-trip compatibility; the shipped template uses "tasks" + "files"); only
            // the parser authority migrated.
            [("architect", "plan-system-design")] = new("PlanDocumentType.Validate",
            [
                AnyOf("\"tasks\"", "\"steps\""),
                AnyOf("\"fileMap\"", "\"files\"", "\"filesToModify\""),
            ]),

            // TaskCreationWorkflow (Story 39-15) binds the (senior_developer, create-tasks) cell as
            // the produce step of its document-lifecycle binding; the shape authority is now the
            // typed validator Tamma.Core/Documents/Types/Plan.cs (PlanDocumentType.Validate) —
            // subsumes what the retired inline ExtractValidate checked (a bare array OR a non-empty
            // "tasks" array). Token group unchanged (39-4 D5 pinned round-trip compatibility; the
            // shipped template uses "tasks"); only the parser authority migrated.
            [("senior_developer", "create-tasks")] = new("PlanDocumentType.Validate",
            [
                AnyOf("\"tasks\"", "JSON array"),
            ]),

            // TestCaseCreationWorkflow (Story 39-15) binds the (tester, write-tests) cell as the
            // produce step of its document-lifecycle binding; the shape authority is now the typed
            // validator Tamma.Core/Documents/Types/TestSpec.cs (TestSpecDocumentType.Validate /
            // ValidateWithContext for the cross-doc task-ID ring) — subsumes the retired inline
            // ExtractValidate. Token group unchanged; only the parser authority migrated.
            [("tester", "write-tests")] = new("TestSpecDocumentType.Validate",
            [
                AnyOf("\"testCases\"", "\"tests\"", "JSON array"),
            ]),

            // TriagePODecisionWorkflow (Story 39-15 D5) binds the (product_owner, triage-intake) cell
            // as the produce step of its document-lifecycle binding; the shape authority is now the
            // typed validator Tamma.Core/Documents/Types/TriageDecision.cs
            // (TriageDecisionDocumentType.Validate) — closed enums + required reasoning. The prompt
            // was rewritten from the P0-P3 / ownerRole vocabulary to the 26-1 TriageDecision wire (D7).
            [("product_owner", "triage-intake")] = new("TriageDecisionDocumentType.Validate",
            [
                One("\"priority\""), One("\"type\""), One("\"complexity\""),
                One("\"automation\""), One("\"reasoning\""),
            ]),

            // TriageContextGatheringWorkflow (Story 39-15 D5) binds the SPLIT
            // (developer, triage-context-scan) cell as the produce step of its document-lifecycle
            // binding; the shape authority is FindingsDocumentType.Validate (the 39-13 Research
            // recipe). Distinct from the free-text (developer, context-scan) that ContextGathering keeps.
            [("developer", "triage-context-scan")] = new("FindingsDocumentType.Validate",
            [
                One("\"summary\""), One("\"findings\""), One("\"title\""),
                One("\"relevance\""), One("\"confidence\""), One("\"citations\""),
                One("\"overallConfidence\""),
            ]),

            // DebugDiagnosisWorkflow (Story 39-15 D4) binds the (senior_developer, debug-rootcause)
            // cell as the produce step of its document-lifecycle binding; the shape authority is the
            // typed validator Tamma.Core/Documents/Types/Diagnosis.cs (DiagnosisDocumentType.Validate).
            // Replaces the retired AIDiagnosisActivity hand-parser; the prompt was rewritten from the
            // old diagnosis/fix/verification shape to the canonical camelCase Diagnosis wire.
            [("senior_developer", "debug-rootcause")] = new("DiagnosisDocumentType.Validate",
            [
                One("\"analysisSummary\""), One("\"hypotheses\""), One("\"rank\""),
                One("\"description\""), One("\"confidence\""), One("\"suggestedFix\""),
                One("\"affectedFiles\""),
            ]),

            // AssessmentWorkflow inline ParseQuestionsResult (AssessmentWorkflow.cs
            // ~l.194-245): bare JSON array of strings, or {"questions":[...]}.
            [("product_owner", "generate-assessment-questions")] = new("AssessmentWorkflow.ParseQuestionsResult",
            [
                AnyOf("JSON array", "\"questions\""),
            ]),

            // AssessmentWorkflow inline ParseAnalysisResult (AssessmentWorkflow.cs
            // ~l.340-394): fail-closed on missing/non-numeric "confidence"; slices
            // "rationale", "gaps", "strengths". "status" is part of the instructed
            // reply shape (the parser deliberately ignores it — ClassifyResultActivity
            // recomputes it from confidence — but the template documents it).
            [("product_owner", "analyze-assessment-response")] = new("AssessmentWorkflow.ParseAnalysisResult",
            [
                One("\"status\""), One("\"confidence\""), One("\"gaps\""),
                One("\"strengths\""), One("\"rationale\""),
            ]),

            // AcceptanceCriteriaAuthoringWorkflow (Story 41-2) binds the
            // (product_owner, define-acceptance-criteria) cell as the produce step of its
            // document-lifecycle binding. The shape authority is the typed validator
            // Tamma.Core/Documents/Types/AcceptanceCriteria.cs
            // (AcceptanceCriteriaDocumentType.Validate) — issueId + ≥1 criterion, unique
            // criterion ids, the closed given-when-then|checklist form with the fields that
            // form requires, an explicit verifiable attestation, and the cross-document
            // scopeRef rule. The token groups below are 41-1b's PendingProducerCells entry
            // moved VERBATIM (that table's promise: the binding story adopts the pinned
            // contract without rewriting it). The shipped template — which instructed a task
            // breakdown, i.e. the Plan wire with criteria smuggled into each task's "testing"
            // string, and carried 1 of these 10 tokens — was REWRITTEN to this wire in the
            // same change (version 1 → 2; the 39-15 D7 / 41-1b threat-model precedent), and its
            // KnownNonConformingTemplates baseline deleted.
            [("product_owner", "define-acceptance-criteria")] = new("AcceptanceCriteriaDocumentType.Validate",
            [
                One("\"issueId\""), One("\"criteria\""), One("\"id\""), One("\"form\""),
                One("\"given\""), One("\"when\""), One("\"then\""), One("\"statement\""),
                One("\"verifiable\""), One("\"scopeRef\""),
            ]),

            // AdrAuthoringWorkflow (Story 41-9) binds the (architect, write-adr) cell as the
            // produce step of its document-lifecycle binding; the shape authority is 41-1c's
            // typed validator Tamma.Core/Documents/Types/Prose.cs (ProseDocumentType.Validate).
            //
            // The token groups pin the prose ENVELOPE, not prose structure (D4). That is the
            // whole contract: ProseDocumentType checks kind + audience against their CLOSED
            // vocabularies, a non-empty title and a non-whitespace body — and NOTHING inside the
            // markdown. The alternative (an IntentionallyUnbound entry reading "prose has no
            // shape") would be wrong: prose HAS a wire shape; only its body is unvalidated, and
            // an unbound entry would let a future template edit drop "audience" with no test
            // noticing. The ADR SHAPE convention (context / decision / consequences /
            // alternatives-considered) stays guidance in the template body per 41-1c D3 and is
            // deliberately NOT a token group.
            //
            // The shipped template — which instructed a markdown issue-comment report
            // (## Summary / ### Key Findings / ### Action Items) with NO JSON fence at all, so a
            // produce through the cell could not even be INGESTED — was rewritten to the prose
            // envelope in the same change (version 1 → 2), and its
            // TemplateExampleConformanceTests.KnownNonConformingTemplates baseline deleted.
            [("architect", "write-adr")] = new("ProseDocumentType.Validate",
            [
                One("\"kind\""), One("\"audience\""), One("\"title\""), One("\"body\""),
            ]),

            // BacklogPrioritizationWorkflow (Story 41-3) binds the
            // (product_owner, prioritize-backlog) cell as the produce step of its
            // document-lifecycle binding. The shape authority is the typed validator
            // Tamma.Core/Documents/Types/BacklogOrdering.cs
            // (BacklogOrderingDocumentType.Validate) — ≥1 item, unique item ids, ranks the
            // unique gap-free 1..N sequence (no ties — arithmetic, not schema), and a
            // rationale plus BOTH a value and an effort estimate on every item. The six token
            // groups below are 41-1b's PendingProducerCells entry moved VERBATIM (that table's
            // promise: the binding story adopts the pinned contract without rewriting it), and
            // they are the same six Core-side pins RenderContractTokenTests.BacklogOrderingTokens
            // holds. The shipped template — which ranked a SINGLE item and instructed the retired
            // P0-P3 / severity / ownerRole triage vocabulary, i.e. very nearly TriageDecision's
            // wire, and carried NONE of these six tokens — was REWRITTEN to the backlog-ordering
            // wire in the same change (version 1 → 2, maxTokens 2048 → 8192; the 39-15 D7 /
            // 41-2 / 41-9 precedent), and its KnownNonConformingTemplates baseline deleted.
            [("product_owner", "prioritize-backlog")] = new("BacklogOrderingDocumentType.Validate",
            [
                One("\"items\""), One("\"itemId\""), One("\"rank\""),
                One("\"rationale\""), One("\"value\""), One("\"effort\""),
            ]),

            // DeploymentPipelineWorkflow.ParseStageStatus (DeploymentPipelineWorkflow.cs
            // ~l.669-702): FAIL-CLOSED — a stage only succeeds on an explicit
            // status:"success"; a reply with no "status" field is a failed deploy.
            [("devops", "deploy")] = new("DeploymentPipelineWorkflow.ParseStageStatus",
            [
                One("\"status\""),
            ]),
            [("devops", "rollback")] = new("DeploymentPipelineWorkflow.ParseStageStatus",
            [
                One("\"status\""),
            ]),
        };

    // ====================================================================
    // Exposed surfaces for TemplateExampleConformanceTests
    // ====================================================================

    /// <summary>
    /// Every bound cell key, whatever its parser authority. Exposed so
    /// <see cref="TemplateExampleConformanceTests"/> can assert its
    /// non-conformance baseline lists ONLY unbound cells — derived from the SAME
    /// <see cref="Bindings"/> map, so there is no second hand-maintained list to drift.
    /// </summary>
    internal static IReadOnlyCollection<(string Role, string Action)> AllBoundCells =>
        Bindings.Keys.ToList();

    /// <summary>
    /// The DocumentType-backed subset of <see cref="Bindings"/>:
    /// (role, action) → the <c>"XxxDocumentType.Validate"</c> authority string.
    /// <see cref="TemplateExampleConformanceTests"/> sweeps exactly this set — a new
    /// lifecycle binding joins the example-conformance gate the day its Bindings
    /// entry is written, with no second map to update.
    /// </summary>
    internal static IReadOnlyDictionary<(string Role, string Action), string> DocumentTypeValidatedCells =>
        Bindings
            .Where(kv => kv.Value.Parser.EndsWith("DocumentType.Validate", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Parser);

    // ====================================================================
    // Known pre-existing contract violations — a RATCHET, not an escape hatch
    // ====================================================================

    /// <summary>
    /// Cells whose shipped template ALREADY fails its callers' parser contract
    /// (discovered while authoring this test). Baselining keeps the build green
    /// while making the debt explicit and un-growable: (a) any NEW violation still
    /// fails, (b) a baselined cell whose template is fixed makes its entry STALE
    /// and fails until the entry is deleted. Entries may only ever be REMOVED.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), string> KnownContractViolations =
        new Dictionary<(string, string), string>
        {
            // EMPTY — the three violations discovered while authoring this test
            // ((tester, write-tests) and (devops, deploy)/(devops, rollback), whose
            // templates instructed file-format output their parsers fail closed on)
            // have been fixed: the templates now instruct the JSON shapes their
            // callers' parsers require. Entries may only ever be REMOVED, never added.
        };

    // ====================================================================
    // Allowlist — dispatched pairs that are INTENTIONALLY unbound
    // ====================================================================

    /// <summary>
    /// Every <c>llm-call</c> dispatch pair whose caller does NOT slice a structured
    /// reply shape — free-text consumers, code/file-format consumers read only via
    /// the <c>success</c> flag, and lenient parsers that degrade to a conservative
    /// default (never fail closed on a missing field). Each entry carries its
    /// justification; the coverage guard fails on any dispatched pair that is
    /// neither here nor in <see cref="Bindings"/>, so a new dispatch site must
    /// declare its contract (or the absence of one) to build.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), string> IntentionallyUnbound =
        new Dictionary<(string, string), string>
        {
            // ---- free-text consumers -------------------------------------------------
            [("senior_developer", "mentor-feedback")] =
                "free-text mentoring guidance: MentorshipWorkflow's dispatch discards the result; " +
                "CodeReviewWorkflow/BlockerDiagnosisWorkflow post the raw llmResponse text verbatim",
            [("senior_developer", "code-review")] =
                "CodeReviewWorkflow.StoreAnalysis keeps the raw response text (analysisText) and feeds it " +
                "into the mentor-feedback call — no structured slice",
            [("tech_writer", "summarize-changes")] =
                "PR description prose: PullRequestWorkflow.CaptureDescription takes the raw text and the " +
                "create-PR activity falls back deterministically when it is empty",
            [("product_owner", "summarize-stakeholder")] =
                "ContextGatheringWorkflow.ExtractPO opportunistically lifts summary/links from JSON but " +
                "falls back to the raw text as the summary — lenient, never fails closed",

            // ---- code/file-format output, consumed only via the success flag ---------
            [("developer", "implement-fix")] =
                "file-format code output; TestingWorkflow/BlockerDiagnosisWorkflow read only the llm-call " +
                "success flag, never the reply shape",
            [("developer", "debug")] =
                "DebuggingWorkflow.applyFix reads only the llm-call success flag (fix files come from the " +
                "selected hypothesis, not the reply)",
            [("developer", "address-review-comments")] =
                "ReviewFixWorkflow.ExtractGenerateSuccess reads only the llm-call success flag; " +
                "ApplyReviewFixesActivity consumes the response as patch text",

            // ---- context scans: free-text findings stored verbatim -------------------
            // Story 39-15 (D5) — the SPLIT: (developer, context-scan) now serves ONLY
            // ContextGatheringWorkflow's free-text feed. TriageContextGatheringWorkflow migrated to
            // the distinct (developer, triage-context-scan) Findings binding (in Bindings above), so
            // this cell no longer shares a cell with a document producer — it is contract-clean.
            [("developer", "context-scan")] =
                "ContextGatheringWorkflow stores scan findings as free text (no required reply fields)",
            [("tester", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",
            [("security", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",
            [("devops", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",
            [("architect", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",

            // ---- lenient parsers with conservative degrade (not fail-closed) ---------
            [("senior_developer", "resolve-blocker")] =
                "ClassifyBlockerActivity.ParseAIDiagnosis treats every field (blocker_type, confidence, " +
                "root_cause…) as optional with defaults and falls back to heuristic signal-based " +
                "classification — never fails closed",
            // Story 39-15 (D5) — (product_owner, triage-intake) MOVED to Bindings: it is now the
            // produce cell of the TriageDecision lifecycle binding (TriageDecisionDocumentType.Validate),
            // no longer a lenient fail-safe parse.

            // ---- review panels: missing fields degrade to a conservative verdict -----
            // These 4 plan-review-family pairs are STILL emitted by a compiled site —
            // TaskReviewWorkflow's 4-role panel (Architect/SeniorDeveloper → plan-review,
            // Developer → review-feasibility, Tester → review-testability). PlanReviewWorkflow
            // (their other former emitter) became a zero-dispatch shim in Story 39-14, but
            // TaskReviewWorkflow keeps them live here. The other 3 plan-review-family pairs
            // (security plan-review-security, devops review-operability, product_owner
            // review-scope) had NO other compiled emitter, so they moved to
            // ReviewProducerDispatchablePairs (policy-only).
            [("architect", "plan-review")] = "panel review (TaskReviewWorkflow): lenient verdict parse, missing fields default to 'concerns'",
            [("senior_developer", "plan-review")] = "panel review (TaskReviewWorkflow): lenient verdict parse, missing fields default to 'concerns'",
            [("developer", "review-feasibility")] = "panel review (TaskReviewWorkflow): lenient verdict parse, missing fields default to 'concerns'",
            [("tester", "review-testability")] = "panel review (TaskReviewWorkflow): lenient verdict parse, missing fields default to 'concerns'",

            // Story 39-15 (D5) — the 4 triage-panel pairs MOVED to ReviewProducerDispatchablePairs:
            // TriagePanelReviewWorkflow was DELETED (the panel is now the lifecycle REVIEW stage over
            // a triage-decision draft, 39-7 config), so they have NO compiled emitter and are reachable
            // only via the panel producer's policy-selected dispatch (ReviewerSelectionHelper
            // .AllDispatchablePairs, doc-type-aware).
        };

    // ====================================================================
    // Test 1 — binding satisfaction
    // ====================================================================

    [Test]
    public void EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken()
    {
        var violations = new List<string>();
        var staleBaseline = new List<string>();

        foreach (var ((role, action), contract) in Bindings)
        {
            var template = SystemPrompts.GetRoleAction(role, action);
            template.Should().NotBeNull(
                $"the binding map pins ({role}, {action}) but SystemPrompts has no template for that " +
                "cell — the prompt file was removed or the cell left the taxonomy; update the binding map");

            var missingGroups = contract.RequiredTokenGroups
                .Where(group => !group.Any(alt => template!.Template.Contains(alt, StringComparison.Ordinal)))
                .Select(group => string.Join(" | ", group))
                .ToList();

            var isBaselined = KnownContractViolations.ContainsKey((role, action));

            if (missingGroups.Count > 0 && !isBaselined)
            {
                violations.AddRange(missingGroups.Select(g =>
                    $"  ({role}, {action}): the prompt no longer asks for {g}, which {contract.Parser} " +
                    "fails closed on (or slices as part of its reply contract). Restore the field in " +
                    $"Prompts/{role}/{action}.md or change the parser AND this binding together."));
            }

            if (missingGroups.Count == 0 && isBaselined)
            {
                staleBaseline.Add(
                    $"  ({role}, {action}): baselined as a known contract violation but its template now " +
                    "satisfies the contract — delete its KnownContractViolations entry (the ratchet only turns one way).");
            }
        }

        violations.Should().BeEmpty(
            "every parser-backed (role, action) prompt cell must mention the JSON fields its callers' " +
            "fail-closed parsers require. Broken contracts:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));

        staleBaseline.Should().BeEmpty(
            "KnownContractViolations must list ONLY cells that still violate their contract:" +
            Environment.NewLine + string.Join(Environment.NewLine, staleBaseline));
    }

    [Test]
    public void KnownContractViolations_OnlyBaselineBoundCells()
    {
        // A baseline entry for a cell that is not even in the binding map is dead
        // weight (nothing checks it) — reject it so the ratchet stays meaningful.
        var orphans = KnownContractViolations.Keys
            .Where(k => !Bindings.ContainsKey(k))
            .Select(k => $"  ({k.Role}, {k.Action})")
            .ToList();

        orphans.Should().BeEmpty(
            "every KnownContractViolations entry must correspond to a binding-map cell:" +
            Environment.NewLine + string.Join(Environment.NewLine, orphans));
    }

    // ====================================================================
    // Data-driven dispatch allowlist (Story 39-6 D3)
    // ====================================================================

    /// <summary>
    /// Story 39-6 (D3) — the generic <c>DocumentLifecycleWorkflow</c> dispatches
    /// <c>llm-call</c> with a <c>(role, action)</c> read from workflow variables (the
    /// producer spec is an INPUT), so its produce/repair/revise sites materialise no
    /// constant pair and cannot join <see cref="Bindings"/> / <see cref="IntentionallyUnbound"/>
    /// (those are keyed by a concrete pair). The contract for these dispatches is
    /// carried by the PRODUCER's OWN cell — already bound by the producing family's
    /// entries when a concrete workflow (39-12+) points at this sub-workflow. Each
    /// site is justified here and cross-checked against
    /// <c>TaxonomyDriftBuildTests.EnumerateDataDrivenDispatches</c> so the two guards
    /// agree on exactly which sites are data-driven.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Workflow, string DispatchId), string>
        DataDrivenDispatchJustifications = new Dictionary<(string, string), string>
        {
            [("DocumentLifecycleWorkflow", "DispatchProduce")] =
                "contract is carried by the producer's own cell (already bound by the producing family's " +
                "entries); the lifecycle's (role, action) is an input, validated fail-loud at Init (39-6 D2).",
            [("DocumentLifecycleWorkflow", "DispatchRepair")] =
                "repair re-dispatch of the same producer spec — same cell, same binding (39-6 D2).",
            [("DocumentLifecycleWorkflow", "DispatchRevise")] =
                "revise re-dispatch of the same producer spec — same cell, same binding (39-6 D2).",
            // Story 39-7 — the single-reviewer producer's llm-call reads its (role, action)
            // from workflow variables (ReviewerRole/ReviewerAction), resolved fail-loud at
            // Init via ReviewerSelectionHelper.Resolve. Its reviewer cell contracts are
            // classified in ReviewProducerDispatchablePairs / IntentionallyUnbound.
            [("SingleReviewerWorkflow", "DispatchReviewerCall")] =
                "input-driven reviewer (role, action) resolved from policy at Init (39-7 D3); the reviewer " +
                "cell's contract is classified via AllDispatchablePairs (ReviewProducerDispatchablePairs).",
        };

    [Test]
    public void EveryDataDrivenDispatch_IsJustifiedAndInSyncWithDrift()
    {
        // The justified allowlist here must match EXACTLY the data-driven dispatch set
        // the drift test discovers — no unjustified escapees, no stale entries.
        var discovered = TaxonomyDriftBuildTests.EnumerateDataDrivenDispatches().ToHashSet();
        var justified = DataDrivenDispatchJustifications.Keys.ToHashSet();

        var unjustified = discovered.Except(justified)
            .Select(k => $"  {k.Workflow}.{k.DispatchId}")
            .ToList();
        var stale = justified.Except(discovered)
            .Select(k => $"  {k.Workflow}.{k.DispatchId}")
            .ToList();

        unjustified.Should().BeEmpty(
            "every data-driven llm-call dispatch must carry a written justification in " +
            "DataDrivenDispatchJustifications (its contract is carried by the producer's own cell):" +
            Environment.NewLine + string.Join(Environment.NewLine, unjustified));

        stale.Should().BeEmpty(
            "these DataDrivenDispatchJustifications entries no longer correspond to a data-driven dispatch:" +
            Environment.NewLine + string.Join(Environment.NewLine, stale));

        foreach (var (_, reason) in DataDrivenDispatchJustifications)
            reason.Should().NotBeNullOrWhiteSpace("every data-driven dispatch justification must be non-empty");
    }

    // ====================================================================
    // Review-producer dispatchable pairs (Story 39-7 D9)
    // ====================================================================

    /// <summary>
    /// Story 39-7 (D9) — the review-producer <c>(role, action)</c> pairs that are
    /// reachable ONLY via policy (the single-reviewer / panel producers dispatch a
    /// data-driven llm-call), so they are emitted by NO compiled dispatch site and
    /// cannot join <see cref="Bindings"/> / <see cref="IntentionallyUnbound"/> (whose
    /// staleness guard requires a live emitter). The classification test asserts every
    /// pair <see cref="ReviewerSelectionHelper.AllDispatchablePairs"/> can dispatch is
    /// classified in one of the three tables — so a reviewer cell reachable by the
    /// producers but bound nowhere fails the build. As of Story 39-14 the 3 PlanReview-EXCLUSIVE
    /// plan-review-family pairs (security plan-review-security, devops review-operability,
    /// product_owner review-scope) live HERE — PlanReviewWorkflow became a zero-dispatch shim and
    /// they have no OTHER compiled emitter, so they would go stale in
    /// <see cref="IntentionallyUnbound"/>. (The other 4 plan-review-family pairs stay in
    /// <see cref="IntentionallyUnbound"/> — TaskReviewWorkflow's compiled panel still emits them.)
    /// <c>(senior_developer, code-review)</c> also stays in <see cref="IntentionallyUnbound"/>
    /// (emitted by CodeReviewWorkflow); the code-review specialisations live here too.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), string> ReviewProducerDispatchablePairs =
        new Dictionary<(string, string), string>
        {
            // Document-review producer pairs with NO compiled emitter after Story 39-14 (only
            // PlanReviewWorkflow dispatched them; it is now a zero-dispatch shim) — reachable ONLY
            // via the panel producer's policy-selected dispatch.
            [("security", "plan-review-security")] =
                "document-review producer pair: security reviews a plan via plan-review-security; policy-only after 39-14.",
            [("devops", "review-operability")] =
                "document-review producer pair: devops reviews a plan via review-operability; policy-only after 39-14.",
            [("product_owner", "review-scope")] =
                "document-review producer pair: product_owner reviews a plan via review-scope; policy-only after " +
                "39-14 (the bespoke PO-decision phase was deleted, D2).",

            [("developer", "code-review")] =
                "diff-review producer pair (D3 diff map): developer reviews a diff via code-review; reachable " +
                "only through the single-reviewer producer's policy-selected dispatch, no compiled emitter.",
            [("architect", "code-review-architecture")] =
                "diff-review producer pair (D3 diff map): architect reviews a diff via code-review-architecture; " +
                "policy-only, no compiled emitter.",
            [("security", "code-review-security")] =
                "diff-review producer pair (D3 diff map): security reviews a diff via code-review-security; " +
                "policy-only, no compiled emitter.",
            [("tester", "code-review-coverage")] =
                "diff-review producer pair (D3 diff map): tester reviews a diff via code-review-coverage; " +
                "policy-only, no compiled emitter.",

            // Story 39-15 (D5) — the 4 TRIAGE-panel producer pairs, reachable ONLY when a
            // triage-decision draft is reviewed (RolePhaseMap.GetPanelActionForRole → triage lens).
            // TriagePanelReviewWorkflow was deleted; they have no compiled emitter and are dispatched
            // via the doc-type-aware panel producer's policy selection.
            [("security", "assess-vulnerability")] =
                "triage-panel producer pair: security reviews a triage-decision draft via assess-vulnerability; policy-only, no compiled emitter.",
            [("developer", "triage-defect")] =
                "triage-panel producer pair: developer reviews a triage-decision draft via triage-defect; policy-only, no compiled emitter.",
            [("tester", "triage-defect")] =
                "triage-panel producer pair: tester reviews a triage-decision draft via triage-defect; policy-only, no compiled emitter.",
            [("devops", "diagnose-incident")] =
                "triage-panel producer pair: devops reviews a triage-decision draft via diagnose-incident; policy-only, no compiled emitter.",

            // Story 41-1a (D1/D2) — the two new document-review producer pairs. No
            // compiled emitter exists for either (41-24/41-25/41-26 build the
            // tech_writer callers; 41-28 the ux_designer one), so they are
            // policy-only, reachable via the 39-7 producers' data-driven dispatch.
            [("tech_writer", "review-docs")] =
                "document-review producer pair: tech_writer reviews a document via review-docs (the 41-1a D1 " +
                "selector arm); policy-only, no compiled emitter until 41-24/41-25/41-26.",
            [("ux_designer", "review-design")] =
                "document-review producer pair: ux_designer reviews a design/UxSpec document via review-design " +
                "(41-1a D2); policy-only, no compiled emitter until 41-28.",
        };

    [Test]
    public void EveryReviewProducerDispatchablePair_IsClassified()
    {
        // AC4 (build-gate half) — every (role, action) the review producers can
        // dispatch must be BOUND, IntentionallyUnbound, or in the review-producer
        // table. A new reviewer cell reachable by policy but classified nowhere fails.
        var unclassified = ReviewerSelectionHelper.AllDispatchablePairs
            .Where(p => !Bindings.ContainsKey((p.Role, p.Action)) &&
                        !IntentionallyUnbound.ContainsKey((p.Role, p.Action)) &&
                        !ReviewProducerDispatchablePairs.ContainsKey((p.Role, p.Action)))
            .Select(p => $"  ({p.Role}, {p.Action})")
            .ToList();

        unclassified.Should().BeEmpty(
            "every reviewer (role, action) the 39-7 producers can dispatch (ReviewerSelectionHelper." +
            "AllDispatchablePairs) must be classified — Bindings, IntentionallyUnbound, or " +
            "ReviewProducerDispatchablePairs:" + Environment.NewLine +
            string.Join(Environment.NewLine, unclassified));
    }

    [Test]
    public void ReviewProducerDispatchablePairs_HasNoStaleEntries()
    {
        // Every entry must be a real dispatchable producer pair AND not already covered
        // by IntentionallyUnbound (that would be dead weight / a contradiction).
        var dispatchable = ReviewerSelectionHelper.AllDispatchablePairs
            .Select(p => (p.Role, p.Action)).ToHashSet();

        var stale = ReviewProducerDispatchablePairs.Keys
            .Where(k => !dispatchable.Contains(k))
            .Select(k => $"  stale (not dispatchable): ({k.Role}, {k.Action})")
            .ToList();
        var overlap = ReviewProducerDispatchablePairs.Keys
            .Where(k => IntentionallyUnbound.ContainsKey(k) || Bindings.ContainsKey(k))
            .Select(k => $"  redundant (already classified elsewhere): ({k.Role}, {k.Action})")
            .ToList();

        stale.Concat(overlap).ToList().Should().BeEmpty(
            "ReviewProducerDispatchablePairs must list ONLY policy-only dispatchable pairs not classified " +
            "elsewhere:" + Environment.NewLine + string.Join(Environment.NewLine, stale.Concat(overlap)));

        foreach (var (_, reason) in ReviewProducerDispatchablePairs)
            reason.Should().NotBeNullOrWhiteSpace("every review-producer pair must carry a justification");
    }

    [Test]
    public void ReviewerSelectionHelper_AllDispatchablePairs_HasEighteenEligiblePairs()
    {
        // Pin the D9 surface: 9 document + 5 diff + 4 triage-panel = 18, each taxonomy-eligible.
        // Story 39-15 added the 4 triage-panel pairs (doc-type-aware GetPanelActionForRole) when
        // TriagePanelReviewWorkflow's semantics moved to the 39-7 panel over a triage-decision draft.
        // Story 41-1a (D1/D2) added (tech_writer, review-docs) and (ux_designer, review-design)
        // to the document roster: 16 → 18.
        var pairs = ReviewerSelectionHelper.AllDispatchablePairs;
        pairs.Should().HaveCount(18, "9 document-review pairs + 5 diff-review pairs + 4 triage-panel pairs");
        pairs.Should().OnlyContain(p => Tamma.Api.Services.Agents.RolePhaseMap.IsRoleEligibleForPhase(p.Action, p.Role),
            "every dispatchable review pair must be taxonomy-eligible");
    }

    // ====================================================================
    // Pending producer cells (Story 41-1b AC6) — contract declared, dispatch pending
    // ====================================================================

    /// <summary>
    /// One producing cell that exists in the taxonomy but that NO compiled workflow
    /// dispatches yet: the contract its binding story will pin
    /// (<paramref name="IntendedContract"/> — moved VERBATIM into
    /// <see cref="Bindings"/> the day that story lands), the Epic 41 story that will
    /// bind it, and why it is not a live binding today.
    /// </summary>
    private sealed record PendingProducerCell(
        CellContract IntendedContract, string OwningStory, string Justification);

    /// <summary>
    /// Story 41-1b (AC6 / D4) — one entry per PRODUCING cell of the six document
    /// types 41-1b minted. Each type declares exactly one producing cell (D4), and
    /// none of those cells is dispatched by any workflow until its owning Epic 41
    /// story lands. None of the three existing tables can hold them honestly: a
    /// <see cref="Bindings"/> entry would assert a dispatch that does not exist (and
    /// trips the stale-classification guard in
    /// <see cref="EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted"/>), an
    /// <see cref="IntentionallyUnbound"/> entry would claim the cell has NO structured
    /// contract when it has a typed one (and trips the same guard), and
    /// <see cref="ReviewProducerDispatchablePairs"/> is for REVIEWER pairs the 39-7
    /// producers can reach by policy — these are produce cells, reachable by nothing.
    ///
    /// <para>It is not a free pass.
    /// <see cref="EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse"/>
    /// fails the day a compiled site starts emitting the pair — forcing the entry to
    /// GRADUATE into <see cref="Bindings"/>, where the template-token gate takes over —
    /// and <see cref="EveryPendingProducerCell_IntendedContractIsCarriedByItsDocumentType"/>
    /// checks every declared token group against the REAL <c>RenderContract()</c> of the
    /// type that will validate the reply, so the contract pinned here is one the binding
    /// story can adopt without rewriting it.</para>
    ///
    /// <para>Template SHAPE conformance is a DIFFERENT gate:
    /// <c>TemplateExampleConformanceTests</c> owns whether each cell's shipped worked
    /// example validates — its <c>ConformingUnboundCells</c> map for the cells whose
    /// templates already instruct the right wire, its <c>KnownNonConformingTemplates</c>
    /// ratchet for the ones whose binding story still has to rewrite them.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), PendingProducerCell>
        PendingProducerCells = new Dictionary<(string, string), PendingProducerCell>
        {
            // GRADUATED (Story 41-2, 2026-07-29): (product_owner, define-acceptance-criteria)
            // was the first entry in this table to reach its binding story.
            // AcceptanceCriteriaAuthoringWorkflow now dispatches the pair, so
            // EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse would fail on a
            // surviving entry here. Its IntendedContract moved VERBATIM into Bindings above —
            // exactly the graduation this table was built to force — and the template-token gate
            // (EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken) plus the
            // example-conformance gate now own the cell. 6 entries → 5.

            // GRADUATED (Story 41-3, 2026-08-01): (product_owner, prioritize-backlog).
            // BacklogPrioritizationWorkflow now dispatches the pair, so
            // EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse would fail on a
            // surviving entry here. Its IntendedContract (the six BacklogOrdering token groups)
            // moved VERBATIM into Bindings above, and the template-token gate plus the
            // example-conformance gate now own the cell. 5 entries → 4.

            // TestPlan.cs "Producing cell (41-1b D4)". Template rewrite is 41-13's job
            // (it still instructs the legacy plan wire — baselined).
            [("tester", "plan-test-strategy")] = new(
                new CellContract("TestPlanDocumentType.Validate",
                [
                    One("\"scope\""), One("\"riskAreas\""), One("\"name\""), One("\"rank\""),
                    One("\"rationale\""), One("\"strategyLines\""), One("\"description\""),
                    One("\"coverageTarget\""), One("\"riskAreaRef\""), One("\"environments\""),
                    One("\"entryCriteria\""), One("\"exitCriteria\""),
                ]),
                "41-13 (test-plan authoring)",
                "no compiled dispatch site exists until 41-13 lands its workflow; the shape authority " +
                "is typed (TestPlan.cs)"),

            // ThreatModel.cs "Producing cell (41-1b D4)". Prompts/security/threat-model.md
            // instructed a REVIEW shape ({issues, verdict}) that could never validate as a
            // threat-model and sat in NO conformance table; it now instructs the real
            // ThreatModel wire and its worked example validates with zero violations.
            [("security", "threat-model")] = new(
                new CellContract("ThreatModelDocumentType.Validate",
                [
                    One("\"assets\""), One("\"threats\""), One("\"id\""), One("\"assetRef\""),
                    One("\"category\""), One("\"description\""), One("\"mitigation\""),
                    One("\"residualRisk\""), One("\"escalation\""),
                ]),
                "41-19 (threat modeling)",
                "no compiled dispatch site exists until 41-19 lands its workflow; the shape authority " +
                "is typed (ThreatModel.cs), including the load-bearing unmitigated-high-risk escalation rule"),

            // SprintPlan.cs "Producing cell (41-1b D4)" — role + template minted by 41-1a.
            // Template already instructs the SprintPlan wire (rewritten with author-ui-spec
            // in the 2026-07-29 pass); TemplateExampleConformanceTests.ConformingUnboundCells
            // holds it to that shape until 41-6 binds it.
            [("scrum_master", "plan-sprint")] = new(
                new CellContract("SprintPlanDocumentType.Validate",
                [
                    One("\"sprintId\""), One("\"capacity\""), One("\"committed\""),
                    One("\"issueId\""), One("\"ownerRole\""), One("\"estimate\""),
                    One("\"carryOver\""), One("\"reason\""),
                ]),
                "41-6 (sprint planning)",
                "no compiled dispatch site exists until 41-6 lands its workflow; the shape authority " +
                "is typed (SprintPlan.cs)"),

            // UxSpec.cs "Producing cell (41-1b D4)" — role + template minted by 41-1a.
            // Template already instructs the UxSpec wire; ConformingUnboundCells holds it
            // to that shape until 41-27 binds it.
            [("ux_designer", "author-ui-spec")] = new(
                new CellContract("UxSpecDocumentType.Validate",
                [
                    One("\"flows\""), One("\"screens\""), One("\"id\""), One("\"name\""),
                    One("\"entryState\""), One("\"successState\""), One("\"errorStates\""),
                    One("\"acceptanceCriteriaRefs\""), One("\"flowRef\""), One("\"a11yRequirements\""),
                ]),
                "41-27 (user-flow and wireframe drafting)",
                "no compiled dispatch site exists until 41-27 lands its workflow; the shape authority " +
                "is typed (UxSpec.cs)"),
        };

    [Test]
    public void EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse()
    {
        // The graduation guard. A pending entry is only honest while NOTHING can
        // dispatch the cell: the day a compiled site (or the review producers' policy
        // selection) can reach it, the entry must move to the table that owns live
        // pairs — otherwise this table becomes the place contracts go to stop being
        // checked.
        var dispatched = TaxonomyDriftBuildTests.EnumerateAllDispatchPairs()
            .Select(p => (p.Role, p.Action))
            .ToHashSet();
        var reviewDispatchable = ReviewerSelectionHelper.AllDispatchablePairs
            .Select(p => (p.Role, p.Action))
            .ToHashSet();

        var problems = new List<string>();
        foreach (var ((role, action), pending) in PendingProducerCells)
        {
            pending.OwningStory.Should().NotBeNullOrWhiteSpace(
                $"({role}, {action}) must name the Epic 41 story that will bind it");
            pending.Justification.Should().NotBeNullOrWhiteSpace(
                $"({role}, {action}) must say why it is not a live binding today");

            if (SystemPrompts.GetRoleAction(role, action) is null)
                problems.Add(
                    $"  ({role}, {action}): no shipped template — the cell is not in the taxonomy " +
                    "(SystemPrompts has no entry); fix the key or delete the entry");

            if (dispatched.Contains((role, action)))
                problems.Add(
                    $"  ({role}, {action}): a compiled llm-call dispatch site NOW emits this pair — " +
                    $"{pending.OwningStory} has landed, so GRADUATE the entry: move its IntendedContract " +
                    "verbatim into Bindings (the template-token gate then checks it) and delete it here");

            if (reviewDispatchable.Contains((role, action)))
                problems.Add(
                    $"  ({role}, {action}): the 39-7 review producers can now dispatch this pair by " +
                    "policy — it belongs in ReviewProducerDispatchablePairs, not here (this table is " +
                    "for PRODUCE cells nothing can reach yet)");

            if (Bindings.ContainsKey((role, action)) ||
                IntentionallyUnbound.ContainsKey((role, action)) ||
                ReviewProducerDispatchablePairs.ContainsKey((role, action)))
                problems.Add(
                    $"  ({role}, {action}): already classified in Bindings / IntentionallyUnbound / " +
                    "ReviewProducerDispatchablePairs — a cell carries exactly one classification");
        }

        problems.Should().BeEmpty(
            "PendingProducerCells must list ONLY producing cells that exist in the taxonomy, are " +
            "dispatched by nothing, and are classified nowhere else:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryPendingProducerCell_IntendedContractIsCarriedByItsDocumentType()
    {
        // What makes a pending entry a CONTRACT rather than a comment: the token set
        // pinned here must actually appear in the contract the producing cell will be
        // handed — IDocumentType.RenderContract of the type that validates the reply.
        // A drifting type therefore fails HERE, before its binding story inherits a
        // token list that no longer matches the wire.
        var problems = new List<string>();
        var declaredBy = new Dictionary<string, (string Role, string Action)>(StringComparer.Ordinal);

        foreach (var ((role, action), pending) in PendingProducerCells)
        {
            var authority = pending.IntendedContract.Parser;

            if (!authority.EndsWith("DocumentType.Validate", StringComparison.Ordinal))
            {
                problems.Add(
                    $"  ({role}, {action}): intended authority '{authority}' is not a typed " +
                    "DocumentType.Validate — a document producer's shape authority is its validator (D7c)");
                continue;
            }

            // 41-1b D4: every document type declares EXACTLY ONE producing cell.
            if (declaredBy.TryGetValue(authority, out var owner))
                problems.Add(
                    $"  ({role}, {action}): '{authority}' is already the declared producing cell of " +
                    $"({owner.Role}, {owner.Action}) — each document type declares exactly one (41-1b D4)");
            else
                declaredBy[authority] = (role, action);

            var typeName = authority[..^".Validate".Length];
            var type = DocumentTypeRegistry.All.SingleOrDefault(t => t.GetType().Name == typeName);
            if (type is null)
            {
                problems.Add(
                    $"  ({role}, {action}): intended authority '{authority}' names no registered " +
                    $"IDocumentType (DocumentTypeRegistry.All has no '{typeName}') — the entry is " +
                    "premature, or the name is a typo");
                continue;
            }

            var contract = type.RenderContract();
            problems.AddRange(pending.IntendedContract.RequiredTokenGroups
                .Where(group => !group.Any(alt => contract.Contains(alt, StringComparison.Ordinal)))
                .Select(group =>
                    $"  ({role}, {action}): the '{type.Key}' contract does not carry " +
                    $"{string.Join(" | ", group)} — the token set pinned here must be one " +
                    $"{pending.OwningStory} can adopt verbatim when it binds the cell"));
        }

        problems.Should().BeEmpty(
            "every pending producer cell's intended contract must be carried by the registered document " +
            "type that will validate its replies:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems));
    }

    // ====================================================================
    // Universal pin (Story 39-15 D7c) — the platform invariant, now that every
    // DOCUMENT-PRODUCING llm-call pair rides a lifecycle binding.
    // ====================================================================

    /// <summary>
    /// The DOCUMENTED non-DocumentType residual (D7c): Bindings entries whose parser authority
    /// is an INLINE parser rather than a typed DocumentType.Validate, because the cell is NOT a
    /// document producer — assessment intake (questions / analysis feed the assessment loop, not a
    /// stored document) and deploy/rollback stage-status (a side-effect gate, not a document). These
    /// are the honest residual after the producer wave; recorded in
    /// .dev/findings/39-15-slice3-triage-migration.md.
    /// </summary>
    private static readonly IReadOnlySet<(string Role, string Action)> NonDocumentTypeResidual =
        new HashSet<(string, string)>
        {
            ("product_owner", "generate-assessment-questions"),
            ("product_owner", "analyze-assessment-response"),
            ("devops", "deploy"),
            ("devops", "rollback"),
        };

    [Test]
    public void UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual()
    {
        // (a) — every Bindings entry's parser authority ends in "DocumentType.Validate" (no
        // workflow-inline parser can re-enter the map), EXCEPT the documented non-document-producer
        // residual. This is what makes the graph type-check: a document producer's shape authority
        // is its typed validator, never an ad-hoc parser.
        var offenders = Bindings
            .Where(kv => !kv.Value.Parser.EndsWith("DocumentType.Validate", StringComparison.Ordinal))
            .Where(kv => !NonDocumentTypeResidual.Contains(kv.Key))
            .Select(kv => $"  ({kv.Key.Role}, {kv.Key.Action}) → {kv.Value.Parser}")
            .ToList();

        offenders.Should().BeEmpty(
            "every document-producing binding authority must be a typed DocumentType.Validate (D7c). " +
            "Non-DocumentType authorities must be in the documented NonDocumentTypeResidual (non-producer " +
            "inline parsers):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        // The residual must not rot: every listed pair must actually still be a Bindings entry with a
        // non-DocumentType authority (else delete it — it migrated or vanished).
        var staleResidual = NonDocumentTypeResidual
            .Where(k => !Bindings.ContainsKey(k) || Bindings[k].Parser.EndsWith("DocumentType.Validate", StringComparison.Ordinal))
            .Select(k => $"  ({k.Role}, {k.Action})")
            .ToList();
        staleResidual.Should().BeEmpty(
            "NonDocumentTypeResidual entries must still be non-DocumentType Bindings authorities:" +
            Environment.NewLine + string.Join(Environment.NewLine, staleResidual));
    }

    [Test]
    public void UniversalPin_EveryIntentionallyUnbound_IsProseOrCode()
    {
        // (b) — every remaining allowlist justification classifies as prose (free-text consumer) or
        // code (success-flag / file-format / lenient-degrade consumer). No document-producing cell may
        // hide here (those are all in Bindings now).
        string[] prose = { "free-text", "free text", "free-form", "prose", "verbatim", "raw text", "raw response", "stored as free text", "raw" };
        string[] code = { "success flag", "file-format", "code output", "patch", "lenient", "fallback", "default", "optional", "heuristic", "never fails closed", "clamp", "degrade" };

        var unclassified = IntentionallyUnbound
            .Where(kv =>
                !prose.Any(p => kv.Value.Contains(p, StringComparison.OrdinalIgnoreCase)) &&
                !code.Any(c => kv.Value.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => $"  ({kv.Key.Role}, {kv.Key.Action}): {kv.Value}")
            .ToList();

        unclassified.Should().BeEmpty(
            "every IntentionallyUnbound justification must read as prose (free-text) or code " +
            "(success-flag/file-format/lenient) — a document producer must be BOUND, never allowlisted (D7c):" +
            Environment.NewLine + string.Join(Environment.NewLine, unclassified));
    }

    // ====================================================================
    // Test 2 — coverage guard
    // ====================================================================

    [Test]
    public void EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted()
    {
        // The same enumeration TaxonomyDriftBuildTests uses for eligibility — the
        // reflection over every compiled llm-call dispatch site (plus its curated
        // supplement), so this guard sees the REAL runtime pairs, panel loops included.
        var discovered = TaxonomyDriftBuildTests.EnumerateAllDispatchPairs();

        discovered.Should().NotBeEmpty(
            "the dispatch enumeration returned nothing — the coverage guard would be a no-op " +
            "(TaxonomyDriftBuildTests' own tripwires should also be failing).");

        var discoveredPairs = discovered
            .Select(p => (Role: p.Role, Action: p.Action))
            .ToHashSet();

        // (a) No dispatched pair may be unclassified. THIS is the tripwire that
        // catches the next cell-reuse-with-a-different-shape the day it is written.
        var unclassified = discovered
            .Where(p => !Bindings.ContainsKey((p.Role, p.Action)) &&
                        !IntentionallyUnbound.ContainsKey((p.Role, p.Action)))
            .Select(p => $"  {p.Workflow}.{p.DispatchId}: ({p.Role}, {p.Action})")
            .Distinct()
            .ToList();

        unclassified.Should().BeEmpty(
            "every llm-call dispatch (role, action) pair must either be BOUND (add a ContractBindingTests " +
            "Bindings entry pinning the JSON fields your parser slices — do this if the caller parses " +
            "structured output) or explicitly ALLOWLISTED in IntentionallyUnbound with a written " +
            "justification (free-text / success-flag-only / lenient-degrade consumers). Unclassified " +
            "dispatch pairs:" + Environment.NewLine + string.Join(Environment.NewLine, unclassified));

        // (b) A pair must not be both bound and allowlisted — that is a contradiction.
        var contradictions = Bindings.Keys
            .Where(IntentionallyUnbound.ContainsKey)
            .Select(k => $"  ({k.Role}, {k.Action})")
            .ToList();

        contradictions.Should().BeEmpty(
            "a (role, action) pair cannot be both in Bindings and in IntentionallyUnbound:" +
            Environment.NewLine + string.Join(Environment.NewLine, contradictions));

        // (c) No stale classifications: an entry for a pair no dispatch site emits
        // any more is dead weight and hides drift (e.g. a binding that outlives its
        // caller would 'pass' forever). Remove entries when their dispatch goes away.
        var staleBindings = Bindings.Keys
            .Where(k => !discoveredPairs.Contains(k))
            .Select(k => $"  Bindings: ({k.Role}, {k.Action})")
            .ToList();
        var staleAllowlist = IntentionallyUnbound.Keys
            .Where(k => !discoveredPairs.Contains(k))
            .Select(k => $"  IntentionallyUnbound: ({k.Role}, {k.Action})")
            .ToList();

        staleBindings.Concat(staleAllowlist).ToList().Should().BeEmpty(
            "these classification entries refer to (role, action) pairs that no compiled llm-call " +
            "dispatch site emits any more — delete them (or restore the dispatch):" +
            Environment.NewLine + string.Join(Environment.NewLine, staleBindings.Concat(staleAllowlist)));
    }
}
