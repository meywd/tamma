# Implementation Plan — Story 39-25: Ambiguity Threading — the Dead Score Leg, Wired

## Scope & Deliverable

When this story is done, `IsAmbiguityAboveThreshold`'s threaded leg is live: every downstream
`document-lifecycle` binding fetches the latest **accepted** `ambiguity-assessment` for its
`issueId` and passes its `score` as the `ambiguityScore` dispatch input — the input
`DocumentLifecycleWorkflow` already reads (`:179`) and `DocumentLifecycleHelper` already
threads (`:167,192`). No assessment ⇒ the key is **omitted** (never `0.0`). The comparison
helper is untouched. The family × signal coverage map ships as a test-readable fixture whose
structural test fails when a dispatcher, document type, signal, or lifecycle outcome changes
without a fixture edit.

Everything below was verified against the working tree on 2026-08-03.

## Pre-Reading

| Reference | Why it constrains the work |
|---|---|
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocumentLifecycleHelper.cs:72,167,192` | The state slot (`AmbiguityScore`), the `Init` parameter, and the assignment — the receiving side already exists end to end. |
| `DocumentLifecycleHelper.cs:363-376` | `IsAmbiguityAboveThreshold` — leg 1 (`inputScore`) at `:367-368`, leg 2 (self-read, `ambiguity-assessment` only) at `:370-373`. **AC6: not edited.** (Story cites `:363-377`; the method closes at `:376`.) |
| `DocumentLifecycleHelper.cs:378-398` | `TryReadAmbiguityScore` — the private payload reader whose semantics (root `score` number; malformed ⇒ no score) the new public reader mirrors. |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:179,198-200` | The `ambiguityScore` input read (`TryGetDouble(ctx.GetInput<object>(...))`) and its threading into `Init`. |
| `DocumentLifecycleWorkflow.cs:436-452, 901-903` | The ambiguity check/gate and its edges: `True → SeedAmbiguity`, `False → EmitReviewRequested` — "escalates **before REVIEW**" is these two connections. The single read of `state.AmbiguityScore` is `:445`. |
| `DocumentLifecycleWorkflow.cs:729-730, 733-734` | `SeedAmbiguity` / `SeedReviewUndecidable` — the two level-independent human pulls AC5 pins. |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AmbiguityScoringWorkflow.cs:39,44,70,149-176` | The producer: definition id `ambiguity-scoring`, self-scored type, `score` output variable, its own lifecycle dispatch (`Id = "DispatchLifecycle"`, `:149` region). **Not edited** (D4). |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs:237-262` | The canonical lifecycle-dispatch input dictionary every binding copies — where the conditional `ambiguityScore` key lands. |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs:166-174` | The existing `FetchLatestAcceptedDocumentActivity` node idiom (`FetchDecomposition`) the new fetch node copies verbatim. |
| `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs:37-135` | The fail-closed read seam: only a `Complete` (= accepted) position yields a body; any read failure ⇒ `Found=false`. This is what makes "null stays null" free. |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs:14-82` | The pure, Elsa-free shared binding core — where the new score reader lands. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:148-214` | 19 registry rows keyed by producing definition id — the spine the coverage map cross-checks. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs:24-49` | 17 members — the fixture's document-type dimension pin. |
| `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentLifecycleOutcome.cs:23-29` | 4 outcomes — the fixture's signal-vocabulary pin (a new escalation signal without a map edit must fail). |
| `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:42`; `AcceptanceRules.cs:85-86` | Default threshold `0.7`; dial validated `[70,100]` — so the AC1 "at dial 100" runs are legal today, and stay legal if a 43-batch story widens `Min`. |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:442,510` | `EnumerateAllDispatchPairs` / `MaterializeDispatchInput(workflow, dispatchId)` — the existing machinery the coverage-map test and the null-omission pin reuse. |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/StructureWalk.cs` | The graph walk + literal-input read idiom (`LiteralDefId`) the coverage-map derivation extends to the fetch node's `DocumentTypeKey` literal. |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleHelperTests.cs:91-105` | The helper's existing tests — AC6 requires they pass **unmodified**. `:101-105` already proves leg 1 works for any type when a score is passed; this story only creates callers. |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/IssueDecompositionLifecycleExecutionTests.cs:1-90` | The full-runtime harness (real `DocumentLifecycleWorkflow`, real `LifecycleReEntryService` over seedable fakes, scripted LLM/review stubs) the AC1/AC3/AC5 pins clone. Note its store-seeding pattern: the read fake is seeded directly. |
| Structure-test pins that move (see Count Pins): `PlanGenerationWorkflowStructureTests.cs:99`; `TaskCreationWorkflowStructureTests.cs:93`; `AdrAuthoringWorkflowStructureTests.cs:106-108`; `AcceptanceCriteriaAuthoringWorkflowStructureTests.cs:154-156`; `BacklogPrioritizationWorkflowStructureTests.cs:248-252` | Every one asserts a fetch-node count or id list that the new node changes. Updated in the same commit as the workflow edits. |
| `docs/stories/epic-39/story-39-24/39-24-acceptance-step-coverage.md` (AC2/AC4/AC10) | The fixture-plus-structural-test pattern this story's AC4 copies, and the dial-100 escape-hatch assertion AC5 references. **39-24 has NOT landed** — no fixture or AC4 test exists in the tree (verified: no `39-24` reference in `apps/tamma-elsa/tests`). |
| `docs/stories/epic-43/story-43-11/...md` Amendment 2 §F, Amendment 3, Amendment 4, caller-kind re-audit | The ruling model: the dial governs the LLM only; acceptance is always a step, the dial picks the approver; ambiguity + no-agreement are the only level-independent human pulls. This story widens the ambiguity signal; it adds no gate, no level, no approval scope. |

The 15 `document-lifecycle` dispatch sites, verified (14 workflows; `ClarifyingQuestionsWorkflow` has two):

| Workflow | Dispatch site | Threads after this story? |
|---|---|---|
| `AmbiguityScoringWorkflow.cs` | `:149` (`DispatchLifecycle`) | No — self-scored, leg 2 (D4) |
| `ClarifyingQuestionsWorkflow.cs` | `:171` (`DispatchRunA`), `:278` (`DispatchRunB`) | No — resolution path (D3) |
| `IssueDecompositionWorkflow.cs` | `:241` (`DispatchLifecycle`) | Yes |
| `PlanGenerationWorkflow.cs` | `:183` | Yes |
| `TaskCreationWorkflow.cs` | `:172` | Yes |
| `TestCaseCreationWorkflow.cs` | `:130` | Yes |
| `DebugDiagnosisWorkflow.cs` | `:122` | Yes |
| `ResearchWorkflow.cs` | `:197` | Yes |
| `DesignProposalWorkflow.cs` | `:140` | Yes |
| `AdrAuthoringWorkflow.cs` | `:224` | Yes |
| `AcceptanceCriteriaAuthoringWorkflow.cs` | `:228` | Yes |
| `BacklogPrioritizationWorkflow.cs` | `:353` | Yes |
| `TriageContextGatheringWorkflow.cs` | `:145` | Yes |
| `TriagePODecisionWorkflow.cs` | `:184` | Yes |

Every threading site already passes an issue anchor and tenant: `issueId` (or `scopedIssueId`
in `AdrAuthoringWorkflow.cs:243` / `TaskCreationWorkflow.cs:192`, `backlogAnchor` in
`BacklogPrioritizationWorkflow.cs:373`) plus `tenantId`. All twelve lifecycle dispatch nodes
carry `Id = "DispatchLifecycle"`.

Zero `["ambiguityScore"] =` call sites exist (re-verified). Zero dispatch sites for definition
id `"ambiguity-scoring"` exist anywhere in `src` (re-verified — see Blocked/contradictions #1).

## Design Decisions

- **D1 — The score is FETCHED from the accepted assessment payload, per binding, keyed by the
  binding's own `issueId` — not threaded through composite variables.** Each threading binding
  gains one `FetchLatestAcceptedDocumentActivity` node
  (`DocumentTypeKey = "ambiguity-assessment"`, `IssueId`/`TenantId` = the same variables its
  lifecycle dispatch already passes) and reads `score` from the fetched body.
  *Reasoning:* (i) it is what AC3 literally demands — "the threaded value is the latest
  accepted assessment for the run's `issueId`"; a fetch keyed by the dispatch's own anchor
  cannot pick up another issue's score, so interleaved runs are safe by construction; (ii) the
  composite-capture wiring the story's "Concretely:" sentence describes has no anchor in the
  tree — **nothing dispatches `ambiguity-scoring`**, so no composite ever holds a `score`
  variable to capture (Blocked/contradictions #1); (iii) it works identically when a binding is
  dispatched standalone, which is the only way anything runs today; (iv) the fetch is
  fail-closed — a store hiccup degrades to "no score", never to a fabricated value.
  *Rejected:* composite variable capture (unimplementable today; breaks standalone dispatch);
  a new `ambiguityScore` input on every binding forwarded by composites (same dead end, twice
  the plumbing); reading the score inside `DocumentLifecycleWorkflow` itself (moves policy into
  the lifecycle and violates AC6's "the fix is callers passing the input that exists").

- **D2 — Null is an omitted key, carried as a string wire.** Each threading binding holds two
  new variables (`assessmentFound: bool`, `assessmentJson: string`, bound to the fetch outputs)
  and the dispatch input lambda does:
  `if (LifecycleBindingHelper.TryReadAssessmentScore(assessmentFound.Get(ctx), assessmentJson.Get(ctx)) is double s) dict["ambiguityScore"] = s;`
  — the key is absent otherwise. The reader is total: not-found / malformed / non-numeric /
  missing `score` ⇒ `null`, mirroring `TryReadAmbiguityScore`'s semantics.
  *Rejected:* defaulting to `0.0` (reads as "measured unambiguous" — the exact lie the story
  forbids); a persisted `Variable<double?>` (no nullable workflow variable exists anywhere in
  the repo's persisted state — not a risk worth taking for a value derivable at dispatch time).

- **D3 — `ClarifyingQuestionsWorkflow` does NOT thread, at either site, and the fixture says
  so.** Clarification is the *resolution* of a high score: threading into `DispatchRunA` would
  escalate the questions-produce on the very score it exists to resolve — and because the
  ambiguity hatch is level-independent (43-11 Amendment 2-F), that wedge fires at every dial,
  not just 100. Threading into `DispatchRunB` (incorporate-answers) is worse: it would discard
  a human's already-given answers on a score that predates them. The coverage map carries an
  explicit row: `clarification — none (resolution path; threading would escalate the
  resolution itself)`. *Rejected:* thread everywhere (wedges resolution); thread Run A only
  (still wedges the questions produce). *Flagged for review:* the story's coverage table groups
  clarification under "documents downstream of an assessment"; this is a deliberate,
  fixture-recorded narrowing — see Blocked/contradictions #2.

- **D4 — `AmbiguityScoringWorkflow` does not thread.** It is the self-scored type (leg 2). A
  fetch there would pre-escalate every re-score on the *previous* run's score, making a high
  score permanently self-sealing. Fixture row: `ambiguity-assessment — leg 2 (payload read)`.

- **D5 — `IsAmbiguityAboveThreshold` and `DocumentLifecycleWorkflow` are untouched (AC6).**
  The entire diff on the receiving side is zero lines. `DocumentLifecycleHelperTests.cs:91-105`
  must pass byte-unmodified; a diff-scope check in review confirms neither file changed.

- **D6 — The coverage map is TWO fixtures plus one derivation test, all in the test assembly.**
  (a) *Dispatcher map*: 14 rows keyed by binding workflow type → declared signal
  `SelfScored | Threaded | None(reason)`. The test enumerates every `WorkflowBase` in
  `Tamma.ElsaServer` whose graph contains a `DispatchWorkflow` with literal definition id
  `document-lifecycle` (the `StructureWalk`/`LiteralDefId` idiom) and asserts set equality with
  the fixture keys, then derives each workflow's *actual* signal — `SelfScored` ⇔ the
  materialized dispatch input's `documentType` is `ambiguity-assessment`; `Threaded` ⇔ the
  graph contains a `FetchLatestAcceptedDocumentActivity` whose `DocumentTypeKey` literal is
  `ambiguity-assessment`; `None` ⇔ neither — and asserts derived == declared. A dispatcher
  gaining or losing the signal, appearing, or disappearing without a fixture edit fails.
  (b) *Honesty table*: one row per `DocumentTypeKey` (ambiguity signal × no-agreement signal —
  the story's table, flattened), key set asserted equal to `Enum.GetValues<DocumentTypeKey>()`
  (17), and each type produced by a fixture-(a) workflow (via `DocumentTypeRegistry`) must
  agree with that workflow's declared signal. Plus a vocabulary pin: `DocumentLifecycleOutcome`
  has exactly the 4 known members — a new escalation signal fails until the map is updated.
  The tool-call / effect row ("none — classification only") is a fixture constant, stated, per
  the story's honesty rule. *Rejected:* a markdown fixture parsed at test time (nothing else in
  the repo does this; C# fixtures get compile-checked keys); deriving "can thread" from
  "does its composite hold one" as the story words it (no composite holds one — D1).

- **D7 — AC5 ships SCOPED, because 39-24 AC4's test does not exist.** 39-24 is drafted, not
  landed, and not in this batch. This story pins its own two-hatch property at the width it
  changes: (i) a source-shape pin that `state.AmbiguityScore` is read at exactly one place in
  `DocumentLifecycleWorkflow` (the `AmbiguityCheck` value at `:445`) and feeds only the
  `AmbiguityGate → SeedAmbiguity` edge; (ii) an execution matrix at `AutonomyLevel = 100` over
  one threading binding: high score ⇒ escalated `ambiguity-above-threshold`; no score + clean
  panel ⇒ accepted with no human step; no score + undecidable review ⇒ escalated
  `review-undecidable`; and no other outcome pulls a person. When 39-24 lands, its AC4 test
  generalizes this pin; nothing here conflicts with it.

## Implementation Steps

1. **Score reader helper.** MODIFY
   `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` — add
   `public static double? TryReadAssessmentScore(bool found, string? documentJson)`: `null`
   unless `found`, JSON parses, root is an object, `score` exists and is a number; then the
   double. Total, never throws (mirrors `DocumentLifecycleHelper.TryReadAmbiguityScore`,
   `:378-398`). CREATE
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/LifecycleBindingHelperTests.cs`
   with the reader's unit tests. *Effort: 0.25 day.*

2. **Wire the 12 threading bindings.** MODIFY (one mechanical edit each, copying the
   `PlanGenerationWorkflow.cs:166-174` idiom):
   `IssueDecompositionWorkflow.cs`, `PlanGenerationWorkflow.cs`, `TaskCreationWorkflow.cs`,
   `TestCaseCreationWorkflow.cs`, `DebugDiagnosisWorkflow.cs`, `ResearchWorkflow.cs`,
   `DesignProposalWorkflow.cs`, `AdrAuthoringWorkflow.cs`,
   `AcceptanceCriteriaAuthoringWorkflow.cs`, `BacklogPrioritizationWorkflow.cs`,
   `TriageContextGatheringWorkflow.cs`, `TriagePODecisionWorkflow.cs`
   (all under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`). Per file: two variables
   (`assessmentFound`, `assessmentJson`); one
   `FetchLatestAcceptedDocumentActivity { Id = "FetchAmbiguityAssessment", DocumentTypeKey =
   new("ambiguity-assessment"), IssueId/TenantId = the same variables the lifecycle dispatch
   already reads }` inserted on the single inbound edge of the `DispatchLifecycle` node (so it
   runs only on the path that actually dispatches — after any re-entry short-circuit gate);
   the conditional key-add from D2 inside the existing `Input` lambda. In
   `BacklogPrioritizationWorkflow.cs` the node goes OUTSIDE the bounded per-item loop (it is
   run-scoped, keyed on `backlogAnchor`). No dispatch nodes, no decisions, no outputs change.
   *Effort: 1 day.*

3. **Move the five structure-test pins, same commit as step 2.** MODIFY
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/`:
   `PlanGenerationWorkflowStructureTests.cs:99` (`ContainSingle` → id list
   `{FetchDecomposition, FetchAmbiguityAssessment}`); `TaskCreationWorkflowStructureTests.cs:93`
   (same shape, its existing fetch id + the new one); `AdrAuthoringWorkflowStructureTests.cs:106-108`
   (id list + `FetchAmbiguityAssessment`); `AcceptanceCriteriaAuthoringWorkflowStructureTests.cs:154-156`
   (same); `BacklogPrioritizationWorkflowStructureTests.cs:248-252` (the in-loop identity
   becomes: every fetch except the one ambiguity fetch lives inside the loop — filter on the
   `DocumentTypeKey` literal, preserving the pin's intent). *Effort: 0.25 day.*

4. **Coverage-map fixture + structural test (AC4).** CREATE
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/AmbiguitySignalCoverageMapTests.cs`
   containing fixture (a), fixture (b), and the derivation/pin tests per D6 (14 dispatcher
   rows; 17 type rows; outcome-vocabulary pin at 4; registry cross-check via
   `DocumentTypeRegistry`; reuses `TaxonomyDriftBuildTests.MaterializeDispatchInput` and
   `StructureWalk`). Includes the AC2 structural half: for each `Threaded` row, the
   materialized `DispatchLifecycle` input at default variable state **omits** the
   `ambiguityScore` key. *Effort: 0.5 day.*

5. **End-to-end pins (AC1, AC2 behavioral, AC3, AC5).** CREATE
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/AmbiguityThreadingExecutionTests.cs`
   — clone of the `IssueDecompositionLifecycleExecutionTests` harness (CI-only, `[Explicit]`,
   real lifecycle + seedable `LifecycleReEntryService` fakes), driving
   `IssueDecompositionWorkflow` as the representative threading binding, rules JSON pinned at
   `AutonomyLevel = 100`, threshold `0.7`. Scenarios in the Test Plan. Also the D7 source-shape
   pin (single `state.AmbiguityScore` read feeding the gate) — this one lives in
   `AmbiguitySignalCoverageMapTests.cs` since it is structural. *Effort: 0.75 day.*

6. **AC6/AC7 close-out.** Diff-scope check: `DocumentLifecycleHelper.cs` and
   `DocumentLifecycleWorkflow.cs` have zero changes; `DocumentLifecycleHelperTests.cs`
   unmodified. Run `dotnet test`. No migrations exist in this story (no schema change to
   verify beyond the test run). *Effort: 0.1 day.*

Total: ~2.85 days, matching the story's 2–3 day estimate.

## Test Plan — fail-first

Every new test's red state against today's tree, stated:

| Test | Red state today |
|---|---|
| `AmbiguitySignalCoverageMapTests.DispatcherMap_MatchesDerivedSignals` | **RED** — the fixture declares 12 `Threaded` rows; derivation finds no `FetchAmbiguityAssessment` node in any binding, so all 12 rows mismatch (`None` derived vs `Threaded` declared). Goes green only when step 2 lands. |
| `...DispatcherMap_KeySetEqualsLifecycleDispatchers` | Green today (14 == 14) — it is the drift guard. Its red state: any workflow gains/loses a `document-lifecycle` dispatch without a fixture row (pinned by construction: set equality). |
| `...HonestyTable_CoversEveryDocumentTypeKey` / `...OutcomeVocabulary_IsExactlyFour` | Green today — drift guards. Red state: an 18th `DocumentTypeKey` or a 5th `DocumentLifecycleOutcome` member lands without a map edit. This is the task's "new document type or signal without updating the map fails" requirement, mechanically. |
| `...ThreadedSites_OmitAmbiguityScoreAtDefault` (AC2 structural) | Cannot fail today (the key exists nowhere). Its red state is against a WRONG implementation: any site that adds the key unconditionally (e.g. `score ?? 0.0`) materializes the key at default state and fails. This is the "never 0.0" pin. |
| `LifecycleBindingHelperTests.TryReadAssessmentScore_*` | **RED** — does not compile until step 1 adds the method. Cases: not-found ⇒ null; malformed JSON ⇒ null; non-object root ⇒ null; missing/non-numeric `score` ⇒ null; `0.0` payload ⇒ `0.0` (a *measured* zero threads — distinct from absent); `0.95` ⇒ `0.95`. |
| `AmbiguityThreadingExecutionTests.AcceptedHighScore_EscalatesNextDispatch_BeforeReview_AtDial100` (AC1) | **RED** — seed an accepted `ambiguity-assessment` (score `0.95`) for the issue, run decomposition with a valid draft + approve-review scripted: today it completes `accepted` because nothing threads; the assertions (exit `escalated`, outcome `ambiguity-above-threshold`, review stub never consumed) fail. |
| `...AcceptedLowScore_DoesNotEscalateOnThreadedLeg` (AC1 complement) | Green today trivially (nothing threads, nothing escalates). Red state: an implementation that escalates on mere assessment *presence* rather than `score >= threshold`. Paired with the row above; only the pair is evidence. |
| `...ScoreFollowsIssueId_TwoInterleavedRuns` (AC3) | **RED on the A-half** — issue A seeded at `0.95`, issue B unseeded; run B (asserts accepted + no `ambiguityScore` input: green today), then run A (asserts escalated: fails today). Red state after landing for a WRONG implementation: a fetch ignoring `issueId` escalates B. |
| `...AtDial100_OnlyTwoOutcomesPullAHuman` (AC5, D7 matrix) | **RED on the high-score leg** (same mechanism as AC1); the no-score legs are green today and guard against this story introducing a third pull. |
| `AmbiguitySignalCoverageMapTests.ThreadedInput_FeedsOnlyTheAmbiguityGate` (AC5, D7 source-shape) | Green today (one read at `DocumentLifecycleWorkflow.cs:445`). Red state: a future edit adds a second `state.AmbiguityScore` consumer — the score becoming an input to anything but the gate is a policy change this story promises not to make. |
| Existing `DocumentLifecycleHelperTests.cs:91-105` (AC6) | Must pass **unmodified**. Any edit to them in this story's diff is a review reject. |
| The five moved structure pins (step 3) | Each is **RED between step 2 and step 3** if split — which is why they land in the same commit. Their permanent value: a binding losing its ambiguity fetch node fails its own suite *and* the coverage map. |

Execution tests are `[Explicit]`/CI-only, matching the harness convention
(`FullyQualifiedName!~Execution` local filter).

## Count pins moved (before → after, read from the tree)

| Pin | Before | After |
|---|---|---|
| `PlanGenerationWorkflowStructureTests.cs:99` — fetch nodes in `PlanGenerationWorkflow` | `ContainSingle` (1) | 2 (id list `{FetchDecomposition, FetchAmbiguityAssessment}`) |
| `TaskCreationWorkflowStructureTests.cs:93` — fetch nodes in `TaskCreationWorkflow` | `ContainSingle` (1) | 2 |
| `AdrAuthoringWorkflowStructureTests.cs:106-108` — fetch id list | `{FetchConsumedDesign, FetchConsumedFindings}` (2) | 3 (+ `FetchAmbiguityAssessment`) |
| `AcceptanceCriteriaAuthoringWorkflowStructureTests.cs:154-156` — fetch id list | `{FetchConsumedClarification, FetchConsumedFindings}` (2) | 3 (+ `FetchAmbiguityAssessment`) |
| `BacklogPrioritizationWorkflowStructureTests.cs:248-252` — "every fetch is inside the loop" | all == in-loop | all *minus the one ambiguity fetch* == in-loop (filter on the `DocumentTypeKey` literal) |

New pins this story mints (not moves): dispatcher-map row count **14**; honesty-table row
count **17** (`DocumentTypeKey`); outcome vocabulary **4** (`DocumentLifecycleOutcome`);
threading-site count **12**.

Pins that must NOT move, named so review can check: per-binding dispatch-count pins (e.g.
`IssueDecompositionWorkflowStructureTests.cs:104-118` — the fetch is an `Activity`, not a
`DispatchWorkflow`); `PlanReviewShimStructureTests.cs:55` (`PlanReviewWorkflow` is not a
lifecycle dispatcher and is untouched); every `TaxonomyDriftBuildTests` pair enumeration (no
new `(role, action)` pairs, no new dispatches); `ActionCatalog` counts (no catalog change);
`DocumentLifecycleHelperTests` (AC6).

## Blocked / contradictions

1. **The story's "Concretely:" wiring sentence contradicts the tree.** It says the
   orchestrating composites (triage/intake path, `SingleIssueCycleWorkflow`'s planning chain)
   "capture `score` when the assessment completes and pass it in each subsequent dispatch's
   input dictionary". Verified: **no workflow, endpoint, or service dispatches
   `ambiguity-scoring`** (zero sites for the definition id across `src`;
   `SingleIssueCycleWorkflow`'s chain is context-gathering → plan-generation → plan-review →
   task-creation → …; `TriageItemCycleWorkflow`'s is triage-context-gathering →
   triage-po-decision). There is no "when the assessment completes" moment inside any
   composite, so composite capture is unimplementable as written. **Not treated as a blocker**:
   the same story's AC3 specifies the observable rule ("latest accepted assessment for the
   run's `issueId`"), and the batch brief confirms the source is "the accepted
   AmbiguityAssessment payload" — D1's per-binding fetch implements exactly that and satisfies
   every AC. Recorded here so the story's Architectural Context can be amended rather than
   silently diverged from. Consequence worth stating: until something dispatches
   `ambiguity-scoring` inside a run (a product decision, out of scope per the story), leg 1
   fires only for issues whose assessment was run out-of-band — the wiring is live, honest,
   and idle-by-default, which is exactly what the coverage map's third row ("no upstream
   assessment — none, stated") admits.
2. **Clarification's row in the coverage table is narrowed, deliberately (D3).** The story's
   family table implies every document downstream of an assessment gets leg 1; threading the
   clarification lifecycle would escalate the resolution of ambiguity on the score it exists
   to resolve (and, at Run B, discard human answers) — at every dial, since the hatch is
   level-independent. The fixture records `clarification: none (resolution path)`. If the
   product owner overrules, the change is one row in the fixture plus one step-2-shaped edit
   in `ClarifyingQuestionsWorkflow.cs`.
3. **AC5's "39-24 AC4's assertion is re-run" cannot be executed literally** — 39-24 has not
   landed and its AC4 test does not exist in the tree. D7 ships the scoped equivalent; the
   generalization stays with 39-24. No AC is unpassable; AC5 is satisfied at the width this
   story changes.
4. Minor citation drift, recorded: the story cites `IsAmbiguityAboveThreshold` as `:363-377`;
   the method spans `:363-376`. All other story citations verified exact.

## Dependencies on the batch (43-12..16, 42-10, 40-8, 31-13)

**Nothing in the batch must land first.** Verified file-level disjointness:

- **43-12 (per-target keys), 43-13 (caller-kind predicate), 43-14 (approval scopes/grants),
  43-15 (toggles/dial UI), 42-10 (shell sandbox/secret.read), 31-13 (PR ops)** — no shared
  files. The story states, and the tree confirms, this escalation path is the lifecycle's own
  `escalated` exit (`SeedAmbiguity → EmitEscalated`), not the gate ledger's: no
  `CheckActionGateActivity`, no `action_authorizations`, no catalog key is touched, so 43-13/
  43-14's approval-scope machinery is irrelevant here by construction.
- **43-16 (acceptance unification form α)** — rewires acceptor floors in
  `AcceptanceDefaults`/`AcceptanceFloors`; it does not touch `AmbiguityEscalationThreshold`.
  This story's execution tests pass explicit `acceptanceRulesJson` (dial 100, threshold 0.7),
  so they are insulated from 43-16's derivation change. No ordering constraint either way.
- **40-8 (create-issues workflow)** — edits `SingleIssueCycleWorkflow`'s neighborhood and adds
  `CreateIssuesWorkflow`; this story touches neither (D1 removed the composite edits the story
  text implied — that is what makes the two lanes disjoint). One forward interaction, by
  design: if 40-8's new workflow (or any future one) ever dispatches `document-lifecycle`, the
  coverage map's set-equality pin forces a fixture row from that author.
- **39-24** (not in the batch, not landed) — soft, one-directional: 39-24's AC4/AC10 tests,
  when they land, generalize this story's D7 pin and can consume this story's fixture; nothing
  here waits for it.

If a batch story widens `AutonomyDial.Min`/`AcceptanceRules` bounds below 70, the dial-100
tests here are unaffected (100 remains valid; the hatch is level-independent).

## Risks

- **The threaded score never goes stale within an issue.** After a clarification resolves the
  ambiguity, the old high-scoring assessment is still "the latest accepted" for that issue —
  every later dispatch keeps escalating until a NEW assessment is accepted, and nothing in the
  tree re-runs assessments (Out of Scope in the story). Honest statement: at high dials this
  is "a person stays in the loop for an issue that measured ambiguous until it is re-measured"
  — safe-side, but a friction cost. Carried as the follow-up candidate the story itself names
  (auto-assessment per producer); the coverage map's third row keeps it visible.
- **`BacklogPrioritizationWorkflow` threads mechanically but its anchor is a batch id**, so
  the fetch finds nothing in practice; the row is honest (`Threaded`, normally null). Cost: one
  extra store read per run, fail-closed.
- **One extra store read per binding run** (12 sites). `FetchLatestAcceptedDocumentActivity`
  is the same seam six bindings already use per run (some in loops); a read failure degrades to
  "no score" — today's behavior — never a fault.
- **Escalation volume at high dials.** Every downstream produce for a high-scoring issue exits
  `escalated` instead of `accepted`, and the bindings' fail-closed exits surface it as their
  typed failure paths. This is the product rule working as specified, but operators see more
  escalated exits the day this lands on issues with out-of-band assessments. Mitigation: none
  needed in code; release note it.
- **Harness seam mismatch** (known, inherited): the execution harness's persist hop and read
  fake are decoupled, so AC1/AC3 seed the read fake directly with the accepted assessment —
  the same pattern `IssueDecompositionLifecycleExecutionTests` documents at its header. The
  structural tests carry the burden the harness cannot.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — leg 1 live, end to end, dial 100 | 1, 2 | `AcceptedHighScore_EscalatesNextDispatch_BeforeReview_AtDial100` + low-score complement |
| 2 — null is honest, key omitted | 1, 2 | `ThreadedSites_OmitAmbiguityScoreAtDefault` + `TryReadAssessmentScore_*` |
| 3 — score follows the run's issueId | 2 | `ScoreFollowsIssueId_TwoInterleavedRuns` |
| 4 — coverage map fixture, drift-proof | 4 | `AmbiguitySignalCoverageMapTests` (set equality, signal derivation, 17/14/4 pins) |
| 5 — only two level-independent pulls | 5 | `AtDial100_OnlyTwoOutcomesPullAHuman` + `ThreadedInput_FeedsOnlyTheAmbiguityGate` (scoped per D7) |
| 6 — helper unchanged | 6 | zero-diff on `DocumentLifecycleHelper.cs` / `DocumentLifecycleWorkflow.cs`; `DocumentLifecycleHelperTests` unmodified |
| 7 — green, no schema change | 6 | `dotnet test` |

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-03 | 1.0.0   | Initial plan. Verified against the tree: 15 dispatch sites / 14 binding workflows enumerated with lines; zero `ambiguity-scoring` dispatchers (composite-capture wiring recorded as contradicting the tree; fetch-based design adopted per AC3 + batch brief); clarification excluded from threading (D3, flagged); AC5 scoped because 39-24 has not landed; five structure-test pins named with before→after. | Claude |
