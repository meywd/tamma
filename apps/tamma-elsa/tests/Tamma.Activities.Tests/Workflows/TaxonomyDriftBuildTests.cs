using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 27-17 — the taxonomy DRIFT build test (SPEC §3.4 + §7,
/// <c>docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md</c>).
///
/// <para><b>Drift = test failure.</b> This test makes it impossible to ship a
/// workflow dispatch site that emits a <c>(role, action)</c> pair the taxonomy
/// doesn't allow. It enumerates EVERY <c>(role, action)</c> emitted by every
/// compiled <c>llm-call</c> dispatch site across <c>Tamma.ElsaServer</c> (AC1),
/// and asserts each pair is in the taxonomy AND the role is eligible for the
/// action per <see cref="RolePhaseMap"/> (AC2). It runs in the normal
/// <c>dotnet test</c> gate (the existing <c>dotnet-tests</c> CI job), so a
/// drifted pair breaks the build (AC5). On failure the message names the exact
/// workflow + dispatch activity + drifted pair (AC6).</para>
///
/// <para><b>Why action ∉ taxonomy is impossible by construction:</b> Story 27-19
/// migrated every dispatch site to TYPED <see cref="AgentRole"/> /
/// <see cref="AgentAction"/> enum members (<c>AgentAction.X.ToWire()</c>). The
/// enums ARE the taxonomy union, so an out-of-vocabulary action cannot be
/// expressed. The REAL drift this test catches is a <c>(role, action)</c> PAIR
/// where the role is NOT eligible for the action per the §4 per-role sets — e.g.
/// <c>developer + deploy</c> (deploy is devops-only). That eligibility check is
/// the load-bearing assertion.</para>
///
/// <para><b>Enumeration approach — reflection over the BUILT workflow graphs
/// (SPEC §7 "reflect over the compiled workflow assembly"):</b> each
/// <c>DispatchWorkflow.Input</c> is an <c>Input&lt;IDictionary&gt;</c> backed by
/// a <c>ctx =&gt; new Dictionary{…}</c> DELEGATE (NOT a literal), so the
/// <c>["role"]/["action"]</c> values are only materialised when the delegate
/// runs. We invoke that delegate against a minimal
/// <see cref="ExpressionExecutionContext"/> whose <see cref="MemoryRegister"/>
/// pre-declares every <c>Variable</c> the closure captured — so the constant
/// <c>role/action</c> entries resolve and the variable-backed entries
/// (<c>planJson.Get(ctx)</c> …) return their declared defaults. This reads the
/// ACTUAL runtime pairs, INCLUDING the panel-loop / helper-computed ones (e.g.
/// <see cref="RolePhaseMap.GetReviewActionForRole"/> applied to each role on a
/// review panel) that pure source/Roslyn analysis cannot resolve statically.
/// Self-discovering over the whole assembly: a NEW workflow with an ineligible
/// pair is checked automatically, no audit list to maintain.</para>
///
/// <para><b>The one non-materialisable exception:</b> a handful of dispatch
/// lambdas read workflow INPUTS via <c>ctx.GetInput&lt;T&gt;(…)</c>, which throws
/// outside a full <c>WorkflowExecutionContext</c> we cannot fabricate without a
/// DI container. Those dispatches are listed in
/// <see cref="NonMaterializableSupplement"/> with their (known-constant) pair,
/// and a COVERAGE GUARD asserts that every llm-call dispatch which fails to
/// materialise IS in that supplement — so a new <c>GetInput</c>-using dispatch
/// can't silently escape the drift check (it fails the guard until listed).</para>
///
/// <para><b>Related AC coverage (NOT duplicated here):</b>
/// AC3 (Parse/ToWire round-trip for every <see cref="AgentRole"/> /
/// <see cref="AgentAction"/>) already lives in
/// <c>Tamma.Api.Tests/Agents/AgentRoleTests.Roundtrip_holds_for_every_role</c>
/// and <c>AgentActionTests.Roundtrip_holds_for_every_action</c> (Story 27-15).
/// AC4 (prompt seed keyset == convention seed keyset == taxonomy) already lives
/// in <c>Tamma.Api.Tests/Conventions/ConventionSeedDriftTests</c> (Story 27-16).
/// Both are referenced, not re-implemented.</para>
/// </summary>
[TestFixture]
public class TaxonomyDriftBuildTests
{
    private const string LlmCallDefinitionId = "llm-call";

    /// <summary>
    /// Story 39-12 (Design Decision D5) — the generic lifecycle binding target. A
    /// workflow that dispatches <c>document-lifecycle</c> (IssueDecompositionWorkflow and
    /// the 39-13/14/15 family) rides its <c>(producerRole, producerAction)</c> INTO the
    /// lifecycle inputs rather than a compiled <c>llm-call</c> site, so the drift
    /// enumeration must see THROUGH the binding to keep the producer pair discovered.
    /// </summary>
    private const string LifecycleDefinitionId = "document-lifecycle";

    /// <summary>
    /// Lower bound on the number of dispatch-site <c>(role, action)</c> pairs the
    /// enumeration must find. A COVERAGE TRIPWIRE: if the reflection ever silently
    /// stops resolving dispatch inputs (e.g. an Elsa upgrade changes the Input
    /// delegate shape), the count collapses and this guard fails instead of the
    /// test degrading into a green no-op. Observed at authoring time: 44 runtime
    /// pairs across the assembly (panels expand to one dispatch per role). Set to
    /// 40 — just 4 below observed — so a legitimate single-site removal (or one
    /// review panel losing a role) doesn't trip it, while a wholesale collapse
    /// does. NOTE: this floor catches BULK breakage; it does NOT catch one whole
    /// workflow's dispatches vanishing within the slack — that is the job of
    /// <see cref="ExpectedContributingWorkflows"/> +
    /// <see cref="EveryKnownContributingWorkflow_StillEmitsPairs"/>.
    ///
    /// <para>Story 39-14 recount: PlanReviewWorkflow's 15 compiled llm-call dispatch sites
    /// (7 role reviews + 7 rebuttals + the PO-decision phase) vanished when it became a
    /// zero-dispatch read-through shim, and PlanGenerationWorkflow's single llm-call site
    /// became a document-lifecycle binding pair (still counted, via the lifecycle-binding
    /// walk) — so the observed pair count dropped by ~15 (from the 44 authoring-time figure
    /// to ~29). Lowered 40 → 25, keeping the same "a few below observed" slack discipline.</para>
    ///
    /// <para>Story 39-15 recount: TriagePanelReviewWorkflow's 4 compiled llm-call dispatch sites (the
    /// 4-role triage panel) vanished when it was DELETED (the panel is now the lifecycle REVIEW stage);
    /// TriageContextGathering's context-scan and TriagePODecision's triage-intake llm-call sites became
    /// document-lifecycle binding pairs (still counted, via the lifecycle-binding walk). Net: observed
    /// drops by ~4 (to ~25). Lowered 25 → 21, keeping the "a few below observed" slack discipline.</para>
    /// </summary>
    private const int MinExpectedDispatchPairs = 21;

    /// <summary>
    /// The set of workflow type-names that each contribute ≥1 <c>(role, action)</c>
    /// dispatch pair today (materialised OR supplemented). Derived by running the
    /// enumeration at authoring time (44 pairs across these 14 workflows). This is
    /// a documented EXPECTED-SUBSET floor: a known contributor silently dropping to
    /// zero dispatch pairs (e.g. an Elsa upgrade changing a container property so
    /// the activity-tree walk misses that workflow, or a panel collapsing) would
    /// still clear <see cref="MinExpectedDispatchPairs"/> — but it FAILS
    /// <see cref="EveryKnownContributingWorkflow_StillEmitsPairs"/>. The check is a
    /// SUBSET assertion (known ⊆ discovered), not exact-equality: adding a NEW
    /// dispatch-bearing workflow must NOT fail this test, but losing a known one
    /// MUST. If a removal is intentional, delete its name here with a note.
    /// </summary>
    private static readonly IReadOnlySet<string> ExpectedContributingWorkflows = new HashSet<string>
    {
        "AcceptanceCriteriaAuthoringWorkflow", // Story 41-2: the (product_owner, define-acceptance-criteria) pair is dispatched via its document-lifecycle binding, discovered by the lifecycle-binding walk (39-12 D5)
        "AdrAuthoringWorkflow",         // Story 41-9: the (architect, write-adr) pair is dispatched via its document-lifecycle prose binding, discovered by the lifecycle-binding walk (39-12 D5)
        "AmbiguityScoringWorkflow",     // Story 3.6: dispatches the dedicated (product_owner, score-ambiguity) scoring pair
        "BacklogPrioritizationWorkflow", // Story 41-3: the (product_owner, prioritize-backlog) pair is dispatched via its document-lifecycle BacklogOrdering binding, discovered by the lifecycle-binding walk (39-12 D5)
        "AssessmentWorkflow",           // P0 fix 2026-06-30: dispatches generate-assessment-questions + analyze-assessment-response
        "BlockerDiagnosisWorkflow",
        "ClarifyingQuestionsWorkflow",  // Story 3.5: dispatches clarify-requirements (generate questions) + incorporate-answers
        "ContextGatheringWorkflow",
        "DebugDiagnosisWorkflow",        // Story 39-15: the (senior_developer, debug-rootcause) pair is dispatched via its document-lifecycle binding, discovered by the lifecycle-binding walk (D4)
        "DebuggingWorkflow",             // still dispatches (developer, debug) via applyFix; AIDiagnosis migrated to the debug-diagnosis binding
        "DeploymentPipelineWorkflow",
        "IssueDecompositionWorkflow",   // Story 39-12: the pair is now dispatched via its document-lifecycle binding (producerRole/producerAction inputs), discovered by the lifecycle-binding walk (D5)
        "MentorshipWorkflow",
        "PlanGenerationWorkflow",       // Story 39-14: the (architect, plan-system-design) pair is now dispatched via its document-lifecycle binding, discovered by the lifecycle-binding walk (D5)
        // PlanReviewWorkflow removed (Story 39-14): it became a zero-dispatch read-through shim over the store — it emits NO (role, action) dispatch pair, so it is no longer a contributor.
        "PullRequestWorkflow",
        "ResearchWorkflow",             // Story 3.4: dispatches the dedicated (product_owner, research) synthesis pair
        "ReviewFixWorkflow",
        "TaskCreationWorkflow",
        "TaskReviewWorkflow",
        "TestCaseCreationWorkflow",
        "TriageContextGatheringWorkflow", // Story 39-15: the (developer, triage-context-scan) pair is now dispatched via its document-lifecycle Findings binding, discovered by the lifecycle-binding walk (D5)
        // TriagePanelReviewWorkflow removed (Story 39-15): DELETED — the 4-role panel is now the lifecycle REVIEW stage over a triage-decision draft (39-7 config), so it emits NO (role, action) dispatch pair.
        "TriagePODecisionWorkflow",       // Story 39-15: the (product_owner, triage-intake) pair is now dispatched via its document-lifecycle TriageDecision binding, discovered by the lifecycle-binding walk (D5)
    };

    /// <summary>
    /// Concrete <see cref="WorkflowBase"/> subclasses that the discovery path
    /// CANNOT instantiate for drift introspection (no parameterless ctor / throws
    /// on construction) and are deliberately exempted from
    /// <see cref="EveryConcreteWorkflow_IsIntrospectableOrAllowListed"/>. EMPTY
    /// today — every concrete workflow has a parameterless ctor. RULE: anything
    /// added here MUST carry a written reason (e.g. "DI-only ctor, has no llm-call
    /// dispatch — verified manually on YYYY-MM-DD"). An entry without a reason is a
    /// silent hole in the drift coverage and defeats the guard's purpose.
    /// </summary>
    private static readonly IReadOnlySet<string> NonIntrospectableAllowList = new HashSet<string>();

    /// <summary>
    /// Dispatch sites whose Input delegate cannot be materialised in an
    /// expression-only context (they call <c>ctx.GetInput&lt;T&gt;</c>), keyed by
    /// <c>(WorkflowTypeName, DispatchActivityId)</c> → the (role, action) pair the
    /// site emits as compile-time constants. Each pair is still asserted eligible
    /// in <see cref="EveryDispatchSitePair_IsEligibleInTaxonomy"/>. The coverage
    /// guard ensures this list stays in sync with the actually-non-materialisable
    /// set (no stale entries, no unlisted escapees).
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Workflow, string DispatchId), (string Role, string Action)>
        NonMaterializableSupplement = new Dictionary<(string, string), (string, string)>
        {
            // (empty) — every dispatched (role, action) pair is now reflectable from the
            // workflow source. ReviewFixWorkflow.DispatchFixGeneration was previously listed
            // here (its sessionId/dispatch constants weren't reflectable), but the ReviewFix
            // build-out made it materialise normally, so the manual supplement is no longer
            // needed. The NonMaterializableSupplement_StaysInSyncWithReality guard enforces
            // that this stays empty until a genuinely non-reflectable dispatch reappears.
        };

    /// <summary>
    /// Story 39-6 (Design Decision D3) — dispatch sites whose <c>(role, action)</c>
    /// is DATA-DRIVEN: the Input delegate materialises (it does not throw) but reads
    /// the role/action from workflow VARIABLES that default to <c>""</c>, so no
    /// compile-time constant pair can be extracted. Keyed by
    /// <c>(WorkflowTypeName, DispatchActivityId)</c>. Each entry documents that the
    /// pair is input-driven AND runtime-validated fail-loud at the workflow's Init
    /// (per D2) — cross-checked by <c>DocumentLifecycleWorkflowStructureTests</c>,
    /// which asserts the lifecycle's Init calls
    /// <c>DocumentLifecycleHelper.ValidateProducerSpec</c>. The coverage guard
    /// <see cref="DataDrivenDispatchAllowList_StaysInSyncWithReality"/> keeps this list
    /// in exact sync with reality so a NEW data-driven dispatch cannot silently escape
    /// the drift check (it would neither materialise a pair NOR be non-materialisable).
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Workflow, string DispatchId), string>
        DataDrivenDispatchAllowList = new Dictionary<(string, string), string>
        {
            [("DocumentLifecycleWorkflow", "DispatchProduce")] =
                "input-driven (producerRole/producerAction workflow variables default to \"\"); the pair is " +
                "validated fail-loud at Init via DocumentLifecycleHelper.ValidateProducerSpec (39-6 D2/D3).",
            [("DocumentLifecycleWorkflow", "DispatchRepair")] =
                "input-driven repair re-dispatch of the same producer spec; validated at Init (39-6 D2/D3).",
            [("DocumentLifecycleWorkflow", "DispatchRevise")] =
                "input-driven revise re-dispatch of the same producer spec; validated at Init (39-6 D2/D3).",
            // Story 39-7 (D9) — the single-reviewer producer's one llm-call reads its
            // (role, action) from the ReviewerRole/ReviewerAction workflow variables
            // (default ""), resolved + validated fail-loud at Init via
            // ReviewerSelectionHelper.Resolve. The panel + router workflows dispatch only
            // the review-single-reviewer / review-panel sub-workflows (not llm-call), so
            // they contribute ZERO llm-call dispatch pairs and appear nowhere here.
            [("SingleReviewerWorkflow", "DispatchReviewerCall")] =
                "input-driven (ReviewerRole/ReviewerAction workflow variables default to \"\"); the pair is " +
                "resolved + validated fail-loud at Init via ReviewerSelectionHelper.Resolve (39-7 D3/D9).",
        };

    // Internal (not private): ContractBindingTests reuses the same enumeration so
    // its coverage guard sees EXACTLY the dispatch pairs this drift test checks.
    internal sealed record DispatchPair(string Workflow, string DispatchId, string Role, string Action);

    // ====================================================================
    // AC1 + AC2 + AC6 — every emitted (role, action) is eligible
    // ====================================================================

    [Test]
    public void EveryDispatchSitePair_IsEligibleInTaxonomy()
    {
        var pairs = EnumerateAllDispatchPairs();

        // AC6 — name the exact site(s) + pair(s) that drifted.
        var violations = pairs
            .Where(p => !RolePhaseMap.IsRoleEligibleForPhase(p.Action, p.Role))
            .Select(p =>
                $"  {p.Workflow}.{p.DispatchId}: (role='{p.Role}', action='{p.Action}') — " +
                Diagnose(p.Role, p.Action))
            .ToList();

        violations.Should().BeEmpty(
            "every (role, action) emitted by a compiled llm-call dispatch site must be a " +
            "taxonomy-eligible pair per RolePhaseMap (SPEC §4). Drifted dispatch sites:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void EveryDispatchPair_UsesCanonicalWireStrings()
    {
        // Defence in depth: a site must emit the canonical AgentRole/AgentAction
        // wire forms (so it round-trips through Parse without relying on the
        // legacy-alias normaliser). Catches a site that hand-writes a
        // non-canonical string instead of X.ToWire().
        var pairs = EnumerateAllDispatchPairs();

        var nonCanonical = pairs
            .Where(p => !RolePhaseMap.ValidRoles.Contains(p.Role) ||
                        !RolePhaseMap.ValidActions.Contains(p.Action))
            .Select(p => $"  {p.Workflow}.{p.DispatchId}: role='{p.Role}', action='{p.Action}'")
            .ToList();

        nonCanonical.Should().BeEmpty(
            "dispatch sites must emit canonical AgentRole/AgentAction wire strings. " +
            "Non-canonical emitters:" + Environment.NewLine +
            string.Join(Environment.NewLine, nonCanonical));
    }

    // ====================================================================
    // Coverage tripwires — keep the test from degrading into a no-op
    // ====================================================================

    [Test]
    public void Enumeration_FindsDispatchPairs_NotANoOp()
    {
        var pairs = EnumerateAllDispatchPairs();

        pairs.Should().NotBeEmpty(
            "the reflection must actually resolve dispatch-site (role, action) pairs — " +
            "an empty set would make EveryDispatchSitePair_IsEligibleInTaxonomy a no-op.");

        pairs.Count.Should().BeGreaterThanOrEqualTo(MinExpectedDispatchPairs,
            $"expected at least {MinExpectedDispatchPairs} dispatch-site (role, action) pairs " +
            $"across Tamma.ElsaServer; found {pairs.Count}. A sharp drop means the reflection " +
            "stopped resolving dispatch inputs (e.g. an Elsa Input-delegate shape change).");
    }

    [Test]
    public void LifecycleBindingWalk_FindsPairs_NotANoOp()
    {
        // Story 39-12 (D5) — the lifecycle-binding walk must actually resolve
        // (producerRole, producerAction) pairs. A walk that silently finds nothing while a
        // document-lifecycle binding exists (IssueDecompositionWorkflow) would hide the
        // producer pair from the drift + contract gates. This tripwire fails on that
        // collapse (same posture as Enumeration_FindsDispatchPairs_NotANoOp).
        var pairs = ScanLifecycleBindingDispatches();

        pairs.Should().NotBeEmpty(
            "the document-lifecycle binding walk must resolve at least one (producerRole, producerAction) " +
            "pair — IssueDecompositionWorkflow (39-12) binds (senior_developer, decompose-issue).");
        pairs.Should().Contain(p => p.Workflow == "IssueDecompositionWorkflow",
            "the 39-12 pilot binding must be discovered by the lifecycle-binding walk (D5).");
    }

    [Test]
    public void DataDrivenDispatchAllowList_StaysInSyncWithReality()
    {
        // Story 39-6 (D3). A DATA-DRIVEN dispatch (Input materialises but role/action
        // resolve to the "" workflow-variable defaults) contributes NO constant pair to
        // the drift check AND is not "non-materialisable" — so without this guard it
        // would silently escape both checks. The allowlist must list EXACTLY the
        // data-driven llm-call dispatches: no stale entries, no unlisted escapees.
        var (_, _, dataDriven) = ScanLlmCallDispatches();
        var actual = dataDriven.Select(d => (d.Workflow, d.DispatchId)).ToHashSet();
        var listed = DataDrivenDispatchAllowList.Keys.ToHashSet();

        var unlistedEscapees = actual.Except(listed)
            .Select(k => $"  {k.Workflow}.{k.DispatchId}")
            .ToList();
        var staleEntries = listed.Except(actual)
            .Select(k => $"  {k.Workflow}.{k.DispatchId}")
            .ToList();

        unlistedEscapees.Should().BeEmpty(
            "these llm-call dispatch sites read their (role, action) from workflow variables that " +
            "default to \"\" and are NOT in DataDrivenDispatchAllowList, so their pair escapes the drift " +
            "check entirely. Add each to the allowlist with a justification AND ensure the workflow's Init " +
            "validates role/action fail-loud (39-6 D2/D3):" + Environment.NewLine +
            string.Join(Environment.NewLine, unlistedEscapees));

        staleEntries.Should().BeEmpty(
            "these DataDrivenDispatchAllowList entries no longer correspond to a data-driven llm-call " +
            "dispatch and should be removed:" + Environment.NewLine +
            string.Join(Environment.NewLine, staleEntries));
    }

    [Test]
    public void NonMaterializableSupplement_StaysInSyncWithReality()
    {
        // The supplement must list EXACTLY the llm-call dispatches that fail to
        // materialise — no stale entries (a site that became materialisable),
        // and crucially no unlisted escapees (a new GetInput-using dispatch that
        // would otherwise slip past the eligibility check entirely).
        var (_, nonMaterializable, _) = ScanLlmCallDispatches();
        var actual = nonMaterializable.Select(d => (d.Workflow, d.DispatchId)).ToHashSet();
        var listed = NonMaterializableSupplement.Keys.ToHashSet();

        var unlistedEscapees = actual.Except(listed)
            .Select(k => $"  {k.Workflow}.{k.DispatchId}")
            .ToList();
        var staleEntries = listed.Except(actual)
            .Select(k => $"  {k.Workflow}.{k.DispatchId}")
            .ToList();

        unlistedEscapees.Should().BeEmpty(
            "these llm-call dispatch sites cannot be materialised AND are not in " +
            "NonMaterializableSupplement, so their (role, action) escapes the drift check. " +
            "Add each to the supplement with its emitted pair:" + Environment.NewLine +
            string.Join(Environment.NewLine, unlistedEscapees));

        staleEntries.Should().BeEmpty(
            "these NonMaterializableSupplement entries now materialise normally and should be " +
            "removed (the reflection already covers them):" + Environment.NewLine +
            string.Join(Environment.NewLine, staleEntries));
    }

    [Test]
    public void EveryKnownContributingWorkflow_StillEmitsPairs()
    {
        // PER-WORKFLOW coverage guard (complements MinExpectedDispatchPairs, which
        // only catches BULK collapse). A single workflow's dispatches can vanish
        // entirely — e.g. an Elsa upgrade renames a container property so the
        // activity-tree walk misses that workflow, or a review panel collapses —
        // while the total pair count still clears the floor inside its slack. That
        // would silently blind the drift test to every dispatch in that workflow.
        // Assert: every KNOWN contributor still emits ≥1 (role, action) pair, i.e.
        // ExpectedContributingWorkflows ⊆ discovered. Subset (not exact) so adding
        // a NEW dispatch-bearing workflow doesn't fail — but losing a known one does.
        var discovered = EnumerateAllDispatchPairs()
            .Select(p => p.Workflow)
            .ToHashSet();

        var vanished = ExpectedContributingWorkflows
            .Where(w => !discovered.Contains(w))
            .OrderBy(w => w)
            .Select(w =>
                $"  {w}: previously emitted llm-call dispatch pairs but now contributes none " +
                "— discovery may be broken or the dispatch was removed; update " +
                "ExpectedContributingWorkflows if intentional.")
            .ToList();

        vanished.Should().BeEmpty(
            "every workflow in the known contributing set must still emit at least one " +
            "(role, action) dispatch pair; a contributor dropping to zero hides ALL of its " +
            "dispatch sites from the drift check while the pair-count floor's slack absorbs " +
            "the loss. Vanished contributors:" + Environment.NewLine +
            string.Join(Environment.NewLine, vanished));
    }

    [Test]
    public void EveryConcreteWorkflow_IsIntrospectableOrAllowListed()
    {
        // DiscoverWorkflows() silently skips a concrete WorkflowBase it can't
        // default-construct (no parameterless ctor, or Activator.CreateInstance
        // throws). Today every concrete workflow has a parameterless ctor, but a
        // future DI-ctor workflow carrying an ineligible dispatch pair would vanish
        // from the drift test with NO guard tripping. Convert "silently skipped"
        // into a LOUD failure: every concrete (non-abstract) WorkflowBase subclass
        // in Tamma.ElsaServer must EITHER be successfully instantiated by the
        // discovery path OR be on the documented NonIntrospectableAllowList.
        var assembly = typeof(LlmCallWorkflow).Assembly;
        var introspectable = DiscoverWorkflows()
            .Select(w => w.GetType())
            .ToHashSet();

        var invisible = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(WorkflowBase).IsAssignableFrom(t))
            .Where(t => !introspectable.Contains(t) && !NonIntrospectableAllowList.Contains(t.Name))
            .OrderBy(t => t.Name)
            .Select(t =>
                $"  {t.Name}: cannot be instantiated for drift introspection and is not " +
                "allow-listed — its dispatch sites are invisible to the drift test; add a " +
                "parameterless test ctor or allow-list it (with a written reason) in " +
                "NonIntrospectableAllowList.")
            .ToList();

        invisible.Should().BeEmpty(
            "every concrete WorkflowBase subclass must be instantiable by DiscoverWorkflows() " +
            "(so its llm-call dispatch sites are checked for drift) or explicitly allow-listed. " +
            "Non-introspectable, non-allow-listed workflows:" + Environment.NewLine +
            string.Join(Environment.NewLine, invisible));
    }

    // ====================================================================
    // Enumeration
    // ====================================================================

    /// <summary>
    /// The full set of dispatch-site (role, action) pairs: those resolved by
    /// materialising the Input delegate, PLUS the curated supplement for the
    /// dispatches that can't be materialised.
    /// </summary>
    internal static IReadOnlyList<DispatchPair> EnumerateAllDispatchPairs()
    {
        var (materialized, _, _) = ScanLlmCallDispatches();

        var supplemented = NonMaterializableSupplement
            .Select(kv => new DispatchPair(kv.Key.Workflow, kv.Key.DispatchId, kv.Value.Role, kv.Value.Action));

        // Story 39-12 (D5) — also see THROUGH document-lifecycle bindings: a binding rides
        // its producer (role, action) into the lifecycle inputs, so the pair is discovered
        // here (keeping the ContractBindingTests entry non-stale and the contributor visible).
        return materialized.Concat(supplemented).Concat(ScanLifecycleBindingDispatches()).ToList();
    }

    /// <summary>
    /// Story 39-12 (D5) — walk every workflow, find every <c>document-lifecycle</c>
    /// <see cref="DispatchWorkflow"/>, materialise its Input delegate, and read the
    /// <c>producerRole</c>/<c>producerAction</c> the binding hands the generic lifecycle.
    /// Attributed to the BINDING workflow (its ExpectedContributingWorkflows entry). The
    /// generic <c>DocumentLifecycleWorkflow</c> itself dispatches only <c>llm-call</c>
    /// (data-driven), not <c>document-lifecycle</c>, so it contributes nothing here.
    /// </summary>
    internal static IReadOnlyList<DispatchPair> ScanLifecycleBindingDispatches()
    {
        var pairs = new List<DispatchPair>();

        foreach (var workflow in DiscoverWorkflows())
        {
            var workflowName = workflow.GetType().Name;
            var builder = WorkflowTestHelper.BuildWorkflow(workflow);
            var root = builder.Object.Root;
            if (root == null) continue;

            foreach (var dispatch in CollectDispatchWorkflows(root))
            {
                if (ReadWorkflowDefinitionId(dispatch) != LifecycleDefinitionId)
                    continue;
                if (!TryMaterializeInputDictionary(dispatch, out var input))
                    continue;

                var role = ReadString(input!, "producerRole");
                var action = ReadString(input!, "producerAction");
                if (!string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(action))
                    pairs.Add(new DispatchPair(workflowName, dispatch.Id ?? "<no-id>", role!, action!));
            }
        }

        return pairs;
    }

    /// <summary>
    /// Story 39-6 (D3) — the data-driven llm-call dispatch sites (materialise but
    /// resolve empty role/action from workflow variables). Exposed so
    /// <c>ContractBindingTests</c> can cross-check its justified allowlist against the
    /// SAME discovery this drift test uses.
    /// </summary>
    internal static IReadOnlyList<(string Workflow, string DispatchId)> EnumerateDataDrivenDispatches()
    {
        var (_, _, dataDriven) = ScanLlmCallDispatches();
        return dataDriven.Select(d => (d.Workflow, d.DispatchId)).ToList();
    }

    /// <summary>
    /// Story 39-12 — materialise the Input dictionary of a specific dispatch site
    /// (<paramref name="workflowName"/>.<paramref name="dispatchId"/>) so a structure test
    /// can pin the constant inputs it hands its sub-workflow (e.g. <c>documentType</c>).
    /// Reuses the same delegate-invocation seam the drift enumeration relies on. Returns
    /// <c>null</c> if the site is not found or its Input cannot be materialised.
    /// </summary>
    internal static IDictionary<string, object>? MaterializeDispatchInput(string workflowName, string dispatchId)
    {
        foreach (var workflow in DiscoverWorkflows())
        {
            if (workflow.GetType().Name != workflowName) continue;
            var builder = WorkflowTestHelper.BuildWorkflow(workflow);
            var root = builder.Object.Root;
            if (root == null) continue;

            foreach (var dispatch in CollectDispatchWorkflows(root))
            {
                if (dispatch.Id != dispatchId) continue;
                return TryMaterializeInputDictionary(dispatch, out var input) ? input : null;
            }
        }
        return null;
    }

    private sealed record DispatchRef(string Workflow, string DispatchId);

    /// <summary>
    /// Walk every workflow in the assembly, find every <c>llm-call</c>
    /// <see cref="DispatchWorkflow"/> that carries a role + action, and split
    /// them into (materialised pairs, non-materialisable refs, data-driven refs).
    /// </summary>
    private static (List<DispatchPair> Materialized, List<DispatchRef> NonMaterializable, List<DispatchRef> DataDriven) ScanLlmCallDispatches()
    {
        var materialized = new List<DispatchPair>();
        var nonMaterializable = new List<DispatchRef>();
        var dataDriven = new List<DispatchRef>();

        foreach (var workflow in DiscoverWorkflows())
        {
            var workflowName = workflow.GetType().Name;
            var builder = WorkflowTestHelper.BuildWorkflow(workflow);
            var root = builder.Object.Root;
            if (root == null) continue;

            foreach (var dispatch in CollectDispatchWorkflows(root))
            {
                // Only dispatches to the llm-call sub-workflow carry a (role,
                // action) pair. Dispatches to other sub-workflows (plan-review,
                // tdd-cycle, …) are pure orchestration — their pairs are checked
                // when THAT sub-workflow is built.
                if (ReadWorkflowDefinitionId(dispatch) != LlmCallDefinitionId)
                    continue;

                var dispatchId = dispatch.Id ?? "<no-id>";

                if (TryMaterializeInputDictionary(dispatch, out var input))
                {
                    var role = ReadString(input!, "role") ?? ReadString(input!, "agentRole");
                    var action = ReadString(input!, "action");
                    if (!string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(action))
                        materialized.Add(new DispatchPair(workflowName, dispatchId, role!, action!));
                    else
                        // Materialised but role/action resolved to the "" variable defaults —
                        // a DATA-DRIVEN dispatch (39-6 D3). Tracked separately so it can't escape.
                        dataDriven.Add(new DispatchRef(workflowName, dispatchId));
                }
                else
                {
                    nonMaterializable.Add(new DispatchRef(workflowName, dispatchId));
                }
            }
        }

        return (materialized, nonMaterializable, dataDriven);
    }

    /// <summary>
    /// Every instantiable <see cref="WorkflowBase"/> in the Tamma.ElsaServer
    /// assembly. Self-discovering so a new dispatch-bearing workflow is covered
    /// automatically.
    /// </summary>
    private static IEnumerable<WorkflowBase> DiscoverWorkflows()
    {
        var assembly = typeof(LlmCallWorkflow).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(WorkflowBase).IsAssignableFrom(type))
                continue;
            if (type.GetConstructor(Type.EmptyTypes) == null)
                continue;

            WorkflowBase? instance = null;
            try
            {
                instance = (WorkflowBase)Activator.CreateInstance(type)!;
            }
            catch
            {
                // A workflow that can't be default-constructed isn't a dispatch
                // site we can introspect; skip rather than fail the whole gate.
            }

            if (instance != null)
                yield return instance;
        }
    }

    /// <summary>
    /// Recursively collect every <see cref="DispatchWorkflow"/> reachable from
    /// <paramref name="root"/>. A generic reflection walk over IActivity-typed
    /// members handles every container (Flowchart, Sequence, If, FlowDecision,
    /// labelled wrappers, …) without enumerating each type by hand.
    /// </summary>
    private static IEnumerable<DispatchWorkflow> CollectDispatchWorkflows(IActivity root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<IActivity>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var activity = stack.Pop();
            if (activity == null || !seen.Add(activity))
                continue;

            if (activity is DispatchWorkflow dispatch)
                yield return dispatch;

            foreach (var child in ChildActivities(activity))
                stack.Push(child);
        }
    }

    private static IEnumerable<IActivity> ChildActivities(IActivity activity)
    {
        var type = activity.GetType();
        var members = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        foreach (var member in members)
        {
            object? value;
            try
            {
                value = member switch
                {
                    PropertyInfo p when p.CanRead && p.GetIndexParameters().Length == 0 => p.GetValue(activity),
                    FieldInfo f => f.GetValue(activity),
                    _ => null,
                };
            }
            catch
            {
                continue;
            }

            switch (value)
            {
                case null:
                    break;
                case IActivity child:
                    yield return child;
                    break;
                case System.Collections.IEnumerable enumerable and not string:
                    foreach (var item in enumerable)
                        if (item is IActivity nested)
                            yield return nested;
                    break;
            }
        }
    }

    // ====================================================================
    // Input-delegate materialisation
    // ====================================================================

    private static string? ReadWorkflowDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expression?.Value?.ToString();
    }

    /// <summary>
    /// Invoke a <see cref="DispatchWorkflow"/>'s <c>Input</c> dictionary delegate
    /// against a minimal expression context. Returns <c>true</c> with the
    /// materialised dictionary, or <c>false</c> if the delegate threw (e.g. it
    /// reads <c>ctx.GetInput</c>) — the caller then routes the dispatch through
    /// the curated supplement instead.
    /// </summary>
    private static bool TryMaterializeInputDictionary(
        DispatchWorkflow dispatch, out IDictionary<string, object>? input)
    {
        input = null;

        var inputProp = typeof(DispatchWorkflow).GetProperty("Input");
        var inputValue = inputProp?.GetValue(dispatch);
        if (inputValue == null) return false;

        var expression = inputValue.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(inputValue) as Expression;

        if (expression?.Value is not Delegate del)
            return false;

        var memory = new MemoryRegister(new Dictionary<string, MemoryBlock>());
        var counter = 0;
        foreach (var reference in CollectCapturedMemoryReferences(del))
        {
            // Variables built via the mock builder share a blank auto-Id, so they
            // collide in the register keyed by Id. Give each captured reference a
            // unique Id (the lambda holds the SAME instance, so its .Get(ctx) then
            // resolves the block we declare here) before declaring it.
            EnsureUniqueId(reference, ref counter);
            try { memory.Declare(reference); }
            catch { /* a reference we can't declare just yields its default below */ }
        }

        var ctx = new ExpressionExecutionContext(
            NullServiceProvider.Instance, memory, null, null, null, default);

        object? raw;
        try
        {
            raw = del.DynamicInvoke(ctx);
        }
        catch
        {
            // A delegate that needs a full WorkflowExecutionContext (ctx.GetInput)
            // is handled via NonMaterializableSupplement.
            return false;
        }

        input = Unwrap(raw) as IDictionary<string, object>;
        return input != null;
    }

    /// <summary>
    /// The Input delegate is <c>Func&lt;ExpressionExecutionContext,
    /// ValueTask&lt;object&gt;&gt;</c>. Unwrap the ValueTask to its result.
    /// ASSUMPTION: the dispatch Input delegates complete SYNCHRONOUSLY (they build a
    /// plain dictionary from constants + variable .Get(ctx) reads — no awaits), so
    /// reading <c>.Result</c> / <c>AsTask().Result</c> never blocks or deadlocks. If
    /// a future dispatch ever does real async work here, switch the result reads to
    /// <c>.GetAwaiter().GetResult()</c> for a cleaner failure than a blocked .Result.
    /// </summary>
    private static object? Unwrap(object? raw)
    {
        if (raw == null) return null;
        var type = raw.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = type.GetMethod("AsTask")!.Invoke(raw, null);
            return asTask!.GetType().GetProperty("Result")!.GetValue(asTask);
        }
        return raw;
    }

    /// <summary>
    /// Walk the delegate's closure to find every captured
    /// <see cref="MemoryBlockReference"/> (the <c>Variable&lt;T&gt;</c> instances
    /// the lambda calls <c>.Get(ctx)</c> on), descending into nested objects and
    /// collections (e.g. a per-role review-variable map captured by a panel
    /// lambda). Declaring these lets the delegate run: constant role/action
    /// entries resolve; variable entries return their declared default.
    /// </summary>
    private static IEnumerable<MemoryBlockReference> CollectCapturedMemoryReferences(Delegate del)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        if (del.Target != null) stack.Push(del.Target);

        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (obj == null || !seen.Add(obj)) continue;
            if (obj is string || obj.GetType().IsPrimitive) continue;

            if (obj is MemoryBlockReference reference)
            {
                yield return reference;
                continue;
            }

            if (obj is System.Collections.IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (item is MemoryBlockReference er) yield return er;
                    else if (item.GetType().IsClass && item is not string) stack.Push(item);
                    else PushKeyValuePairHalves(item, stack);
                }
                continue;
            }

            foreach (var field in obj.GetType()
                         .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value;
                try { value = field.GetValue(obj); }
                catch { continue; }
                if (value == null) continue;

                if (value is MemoryBlockReference r)
                    yield return r;
                else if (value is string || value.GetType().IsPrimitive)
                    continue;
                else if (value.GetType().IsClass || value is System.Collections.IEnumerable)
                    stack.Push(value);
            }
        }
    }

    /// <summary>Push both halves of a KeyValuePair&lt;,&gt; onto the walk stack.</summary>
    private static void PushKeyValuePairHalves(object item, Stack<object> stack)
    {
        var t = item.GetType();
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(KeyValuePair<,>)) return;
        var key = t.GetProperty("Key")?.GetValue(item);
        var val = t.GetProperty("Value")?.GetValue(item);
        if (key != null && key.GetType().IsClass && key is not string) stack.Push(key);
        if (val != null && val.GetType().IsClass && val is not string) stack.Push(val);
    }

    /// <summary>
    /// Assign a unique Id to a <see cref="MemoryBlockReference"/> that has a blank
    /// one (the mock-built variables all share an empty auto-Id and would collide
    /// in the register). The <c>Id</c> setter is non-public, so set it via
    /// reflection.
    /// </summary>
    private static void EnsureUniqueId(MemoryBlockReference reference, ref int counter)
    {
        try
        {
            if (!string.IsNullOrEmpty(reference.Id)) return;
        }
        catch { return; }
        // SAFETY BACKSTOP: if reading/assigning the Id throws and the variable ends
        // up undeclared, the dispatch Input delegate's .Get(ctx) throws → the site
        // falls into the non-materialisable set → NonMaterializableSupplement_StaysInSync
        // flags it as an unlisted escapee. So a failure here can't silently pass: it
        // surfaces as a coverage-guard failure, not a dropped dispatch pair.

        var idProp = typeof(MemoryBlockReference).GetProperty("Id",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (idProp?.CanWrite == true)
        {
            try { idProp.SetValue(reference, $"__drift_{counter++}"); }
            catch { /* leave as-is; declaration may still collide harmlessly */ }
        }
    }

    private static string? ReadString(IDictionary<string, object> dict, string key)
        => dict.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>Human-readable reason a pair is ineligible, for the AC6 message.</summary>
    private static string Diagnose(string role, string action)
    {
        if (!RolePhaseMap.ValidRoles.Contains(role))
            return $"role '{role}' is not a known AgentRole.";
        if (!RolePhaseMap.ValidActions.Contains(action))
            return $"action '{action}' is not a known AgentAction.";
        return $"role '{role}' is not eligible for action '{action}' " +
               "(not in that role's §4 action set).";
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
