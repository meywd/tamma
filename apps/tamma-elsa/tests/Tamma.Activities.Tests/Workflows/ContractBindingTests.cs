using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

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
            // ResearchWorkflow → ResearchParsing.ParseReport (Tamma.Activities/Research/ResearchParsing.cs):
            // fail-closed on missing "summary" / empty "findings"; per-finding slices
            // "title"/"summary", "relevance", "confidence", "citations"; reads "overallConfidence".
            [("product_owner", "research")] = new("ResearchParsing.ParseReport",
            [
                One("\"summary\""), One("\"findings\""), One("\"title\""),
                One("\"relevance\""), One("\"confidence\""), One("\"citations\""),
                One("\"overallConfidence\""),
            ]),

            // AmbiguityScoringWorkflow → AmbiguityParsing.ParseAssessment
            // (Tamma.Activities/Ambiguity/AmbiguityParsing.cs): fail-closed on missing/
            // out-of-range "score" and empty "rationale"; reads "confidence"; per-item
            // slices "type", "description" (item fail-closed), "severity", "recommendation".
            [("product_owner", "score-ambiguity")] = new("AmbiguityParsing.ParseAssessment",
            [
                One("\"score\""), One("\"confidence\""), One("\"rationale\""),
                One("\"ambiguities\""), One("\"type\""), One("\"description\""),
                One("\"severity\""), One("\"recommendation\""),
            ]),

            // IssueDecompositionWorkflow → DecompositionParsing.ParseDecomposition
            // (Tamma.Activities/Decomposition/DecompositionParsing.cs): fail-closed on
            // missing "summary" / no usable "subtasks"; per-subtask fail-closed on "id"
            // and title|description; slices "acceptanceCriteria", "estimateHours",
            // "complexity", "dependsOn".
            [("senior_developer", "decompose-issue")] = new("DecompositionParsing.ParseDecomposition",
            [
                One("\"summary\""), One("\"subtasks\""), One("\"id\""), One("\"title\""),
                One("\"description\""), One("\"acceptanceCriteria\""),
                One("\"estimateHours\""), One("\"complexity\""), One("\"dependsOn\""),
            ]),

            // ClarifyingQuestionsWorkflow → ClarifyParsing.ParseQuestions
            // (Tamma.Activities/Clarify/ClarifyParsing.cs): primary shape is a BARE
            // JSON array of question strings (empty parse → CLARIFY.*.FAILED terminal).
            // No field name to pin — assert the template instructs a JSON array.
            [("product_owner", "clarify-requirements")] = new("ClarifyParsing.ParseQuestions",
            [
                One("JSON array"),
            ]),

            // ClarifyingQuestionsWorkflow → ClarifyParsing.ParseClarification:
            // fail-closed on missing/empty "clarifiedRequirement"; slices
            // "remainingAmbiguities" and "resolved".
            [("product_owner", "incorporate-answers")] = new("ClarifyParsing.ParseClarification",
            [
                One("\"clarifiedRequirement\""), One("\"remainingAmbiguities\""), One("\"resolved\""),
            ]),

            // DesignProposalWorkflow → DesignParsing.ParseProposal
            // (Tamma.Activities/Design/DesignParsing.cs): fail-closed on missing
            // "summary"; slices "recommendation", "constraintEvaluation", and
            // "alternatives" items' "name"/"tradeoffs".
            [("architect", "propose-design")] = new("DesignParsing.ParseProposal",
            [
                One("\"summary\""), One("\"recommendation\""), One("\"constraintEvaluation\""),
                One("\"alternatives\""), One("\"name\""), One("\"tradeoffs\""),
            ]),

            // PlanGenerationWorkflow → PlanValidationHelper.ValidatePlan
            // (Tamma.ElsaServer/Workflows/Helpers/PlanValidationHelper.cs): invalid
            // unless the plan carries "tasks"|"steps" AND "fileMap"|"files"|"filesToModify".
            // The shipped template uses "tasks" + "files".
            [("architect", "plan-system-design")] = new("PlanValidationHelper.ValidatePlan",
            [
                AnyOf("\"tasks\"", "\"steps\""),
                AnyOf("\"fileMap\"", "\"files\"", "\"filesToModify\""),
            ]),

            // TaskCreationWorkflow inline ExtractValidate (TaskCreationWorkflow.cs
            // ~l.140-200): accepts a bare JSON array OR an object with a non-empty
            // "tasks" array; anything else → validation error → retry → error output.
            [("senior_developer", "create-tasks")] = new("TaskCreationWorkflow.ExtractValidate",
            [
                AnyOf("\"tasks\"", "JSON array"),
            ]),

            // TestCaseCreationWorkflow inline ExtractValidate (TestCaseCreationWorkflow.cs
            // ~l.123-193): accepts a bare JSON array OR an object with "testCases"|"tests";
            // anything else → validation error → retry → error output.
            [("tester", "write-tests")] = new("TestCaseCreationWorkflow.ExtractValidate",
            [
                AnyOf("\"testCases\"", "\"tests\"", "JSON array"),
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
            [("developer", "context-scan")] =
                "ContextGatheringWorkflow/TriageContextGatheringWorkflow store scan findings as free text " +
                "(TriageContextHelper wraps prose into a JSON envelope itself — no required reply fields)",
            [("tester", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",
            [("security", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",
            [("devops", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",
            [("architect", "context-scan")] = "free-text scan findings stored verbatim (ContextGatheringWorkflow.Extract)",

            // ---- lenient parsers with conservative degrade (not fail-closed) ---------
            [("senior_developer", "resolve-blocker")] =
                "ClassifyBlockerActivity.ParseAIDiagnosis treats every field (blocker_type, confidence, " +
                "root_cause…) as optional with defaults and falls back to heuristic signal-based " +
                "classification — never fails closed",
            [("product_owner", "triage-intake")] =
                "TriagePoDecisionHelper.ParseDecision is fail-safe: unparseable output becomes an explicit " +
                "needs-human-review decision and out-of-vocab fields are clamped to defaults with notes",

            // ---- review panels: missing fields degrade to a conservative verdict -----
            // TaskReviewWorkflow/PlanReviewWorkflow aggregate with TryGetProperty and
            // default a missing "verdict" to "concerns" (and the PO decision to
            // "needsHuman") — the panel can only get MORE cautious, never fail closed.
            [("architect", "plan-review")] = "panel review: lenient verdict parse, missing fields default to 'concerns'",
            [("senior_developer", "plan-review")] = "panel review: lenient verdict parse, missing fields default to 'concerns'",
            [("security", "plan-review-security")] = "panel review: lenient verdict parse, missing fields default to 'concerns'",
            [("developer", "review-feasibility")] = "panel review: lenient verdict parse, missing fields default to 'concerns'",
            [("tester", "review-testability")] = "panel review: lenient verdict parse, missing fields default to 'concerns'",
            [("devops", "review-operability")] = "panel review: lenient verdict parse, missing fields default to 'concerns'",
            [("product_owner", "review-scope")] =
                "panel review + PlanReviewWorkflow PO decision: lenient parse, unparseable output defaults " +
                "to 'needsHuman' (conservative degrade, not fail-closed)",

            // ---- triage panel: quorum-gated, per-role failure tolerated --------------
            // TriagePanelReviewWorkflow leaves an unparseable role review as the "{}"
            // sentinel (that role counts as FAILED) and TriagePanelAggregationHelper
            // aggregates whatever fields the surviving reviews carry — quorum decides,
            // no single reply field is load-bearing.
            [("security", "assess-vulnerability")] = "triage panel: quorum aggregation, unparseable review = failed role, no required fields",
            [("developer", "triage-defect")] = "triage panel: quorum aggregation, unparseable review = failed role, no required fields",
            [("tester", "triage-defect")] = "triage panel: quorum aggregation, unparseable review = failed role, no required fields",
            [("devops", "diagnose-incident")] = "triage panel: quorum aggregation, unparseable review = failed role, no required fields",
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
