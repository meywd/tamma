# Implementation Plan — Story 41-13: Test-Plan / Strategy Authoring Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `test-plan-authoring` exists as a **thin binding over
`document-lifecycle`** (the 39-15 recipe), producing a typed **`TestPlan`** from the
`(tester, plan-test-strategy)` producer cell and consuming the accepted `AcceptanceCriteria` (41-2) for
the issue. It reads the consumed criteria through `FetchLatestAcceptedDocumentActivity`, folds them into
the declared producer carrier, forwards them as `validationContextJson` so the **strategy-line ⇒ criterion
traceability rule is a validator rule, not a binding branch** (the 39-15 D3 `CASE_UNKNOWN_TASK_ID`
precedent), dispatches `document-lifecycle` once, and routes the typed exit into a single `SetOutput`
terminal. Zero `Finish`, zero `llm-call`, zero parsing, no retry plumbing.

The story also **rewrites `Prompts/tester/plan-test-strategy.md` from its current `Plan` shape to the
`TestPlan` contract** (Correction C1 — the shipped cell today instructs tasks/files/testing, not risk
areas/coverage/entry-exit), owns the `(tester, plan-test-strategy)` `ContractBindingTests.Bindings` entry
(C2 — 41-1b cannot land it), declares one `WorkflowDocumentInterface` row and bumps
`WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` by one, declares
`[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, and emits `TEST_PLAN.*` alongside `DOCUMENT.*`.

## Pre-Reading

- `docs/stories/epic-41/story-41-13/41-13-test-plan-authoring.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — **the hard gate.** `TestPlan`'s domain
  rules table row, D1 (acceptance posture is chosen per type), D2 (no workflow edges), AC4 (the two
  vocabulary count pins move there, not here), AC6 + its shared-contract hazard note, AC7 (the edge pin is
  explicitly NOT touched by 41-1b)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs` — **THE reference binding
  for this story**: it is the one landed producer that consumes a sibling document AND forwards
  `validationContextJson` (`:146-148`). 41-13 is its structural twin one level up.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs:149-166` — the
  `FreshRun` + `FetchLatestAcceptedDocumentActivity` consumed-document read
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape the epic README names
- `apps/tamma-elsa/src/Tamma.Core/Documents/IDocumentType.cs:32-44` — `ValidateWithContext`, the additive
  default interface member; note the 39-15 finding that `TestSpecDocumentType` implements it as an
  **implicit interface method, not `override`** (a DIM is not virtual on the class)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TestSpec.cs:52-58` — `CaseUnknownTaskId`, the shape of a
  cross-document rule
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/CreationBindingHelper.cs:44-75`
  (`BuildTaskIdContext`) — the shape of a `validationContextJson` builder
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` — the shared
  fail-closed exit reader
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/plan-test-strategy.md` — the cell being rewritten
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/write-tests.md` — the **rewritten-to-contract** precedent
  (`version: 2`, "Return ONLY …", the "the downstream validator rejects …" closing line)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` (`:82`),
  the universal DocumentType-authority pin (`:626`), and **the clause-(c) staleness guard (`:725-737`)
  that makes C2 true**
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:460` / `:507` / `:125`
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174` +
  `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`, `:102-123`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:127-132` — `For` and its
  `_ => Rules` catch-all (a new type silently takes the single-`architect` unanimous row)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` — `GetReviewActionForRole`; tester ⇒
  `review-testability` **already**, so the story's review-lens claim needs no new code (C4)
- `.dev/findings/39-15-remaining-producers-migration.md` — the distilled recipe
- **NOT FOUND (planned by prerequisites, no code in tree):** `DocumentTypeKey.TestPlan` /
  `TestPlanDocumentType` / `Types/TestPlan.cs` (41-1b); `AcceptanceCriteria` type (41-1b); the
  `(product_owner, define-acceptance-criteria)` binding (41-2). See Blocks / Blocked by.

## Corrections to the story

- **C1 — `Prompts/tester/plan-test-strategy.md` today instructs a `Plan`, not a `TestPlan`; rewriting it
  is in scope and the story does not say so.** The shipped cell asks for
  `{"tasks":[{"id","description","files","dependencies","complexity","testing"}],"totalComplexity",
  "estimatedDuration"}` — byte-for-byte the same block as `architect/plan-system-design.md`. There is no
  risk ranking, no coverage target, no entry/exit criteria. AC1 ("`TestPlan` validated (risk ranking,
  coverage mapping, entry/exit)") is unreachable until the cell is rewritten to
  `TestPlanDocumentType.RenderContract()`'s shape. *(It also carries the same `files`-as-objects /
  `dependencies`-not-`dependsOn` defect documented in 41-12's plan C1 — irrelevant once the block is
  replaced wholesale.)*
- **C2 — the `(tester, plan-test-strategy)` `Bindings` entry CANNOT land in 41-1b; it belongs here, and
  41-1b's AC6 is wrong about it.** 41-1b AC6 says "one `ContractBindingTests` entry per producing cell".
  But `ContractBindingTests.EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` clause (c)
  (`:725-737`) fails the build for any `Bindings` key that **no compiled dispatch site emits**
  (`staleBindings`). Until this story's binding exists, the pair is emitted nowhere — so a 41-1b-authored
  entry turns CI red. **Resolution:** 41-1b registers the *type* (+ `RenderContract`, examples, drift
  tests); 41-13 adds the `Bindings` entry in the same commit as the binding. This is a real cross-story
  correction, not a preference — file it back to 41-1b.
- **C3 — `[ResumeBehavior]` mode: `LatestStateReEntry`, not `Both`.** AC4 says "`[ResumeBehavior(Both)]`".
  The binding never suspends on a bookmark of its own (the accept-gate suspend is inside the dispatched
  `document-lifecycle` child; the parent waits on `WaitForCompletion = true`). `Both` fails
  `ResumableStandardStructuralTests` clause (b), which demands a graph node whose type is in both the
  declaration's `SuspendActivities` and `LifecycleBookmarks.CanonicalSuspendActivities`. Every landed
  producer binding declares `LatestStateReEntry` (`TaskCreationWorkflow.cs:47`,
  `TestCaseCreationWorkflow.cs:37`). AC4's real requirement — "39-10 structural test green without
  allowlist" — is unchanged.
- **C4 — the review-lens claim is already satisfied; nothing to build.** "Reviewed via
  `(tester, review-testability)` / architect lens" is exactly what
  `RolePhaseMap.GetPanelActionForRole(role, "test-plan")` → `GetReviewActionForRole` yields today
  (`:383` tester ⇒ `ReviewTestability`, `:378` architect ⇒ `PlanReview`). **But** it only *happens* if the
  resolved acceptance rules name tester/architect as reviewer(s) — and `AcceptanceDefaults.For` ends in
  `_ => Rules` (`:131`), the single-**architect** unanimous row. So a `test-plan` with no explicit rules
  gets an architect-only review, never a tester lens. Choosing that row is **41-1b's D1/AC5**, not this
  story's; 41-13 states the requirement and passes `acceptanceRulesJson` through for per-run override
  (D6).
- **C5 — AC2's "strategy lines trace to criteria" has no mechanism unless it is written as a
  cross-document validator rule.** There is exactly one seam for a rule that cannot be seen payload-only:
  `IDocumentType.ValidateWithContext` + the lifecycle's `validationContextJson` input. This plan makes it
  a `TestPlanDocumentType` rule (`STRATEGY_LINE_UNKNOWN_CRITERION`), owned here — **but the method it
  overrides lives on 41-1b's type**, so the two stories must land the member in lockstep (D4). Absent
  that, AC2 is prose with no check, exactly the failure mode 41-15 AC2's own Corrected note calls out.
- **C6 — AC3's "consumable by `test-case-creation` … via 39-11" is not automatic.**
  `TestCaseCreationWorkflow` reads no `TestPlan`: its consumed input is the bare `tasksJson` carrier
  (`:81`, `:138`) and its declared `consumes` is `[plan]`
  (`DocumentTypeRegistry.cs:172`). Making an accepted `TestPlan` actually *drive* `test-case-creation`
  would edit that landed binding — out of scope. AC3 is therefore scoped to **readability**: an
  integration test proves the accepted `TestPlan` is retrievable for the issue through the same
  `FetchLatestAcceptedDocumentActivity` seam `test-case-creation` would use, and the wiring is filed
  forward. State this in the AC rather than implying the consumer edit.

## Design Decisions

- **D1 — New workflow class + new `DefinitionId` `test-plan-authoring`.** Nothing dispatches a test plan
  today, so there is no public surface to keep byte-stable. Inputs: `issueId`, `repository`,
  `issueNumber`, `workItemJson`, `contextIds`, `tenantId`, `acceptanceRulesJson?`. Outputs: `status`,
  `outcome`, `documentId`, `testPlanJson`, `parentDocumentId` (the consumed `AcceptanceCriteria` id),
  `error`. `builder.Version = WorkflowVersions.ComputedVersion`.
- **D2 — No issue-id scoping needed.** `test-plan` is a unique document type key, so unlike the two
  `plan` producers (39-15 D2) there is no same-type collision in the 39-11
  `(issueId, documentType)` read. The binding keys on the bare `issueId` —
  `TestCaseCreationWorkflow.cs:99-102` is the precedent.
- **D3 — Consumed `AcceptanceCriteria` is read through the existing seam behind the `FreshRun` gate.**
  `FetchLatestAcceptedDocumentActivity` with `DocumentTypeKey = "acceptance-criteria"`, guarded by the
  `positionStage == "produce"` `FlowDecision`, exactly as `TaskCreationWorkflow.cs:150-166`. `Found` /
  `DocumentId` / `DocumentJson` feed both the producer carrier (D5) and the validation context (D4).
  **Absent criteria is legal** (the story says "consumes … when present"): the run proceeds, the
  traceability rule cannot fire, and `parentDocumentId` is `""`.
- **D4 — Traceability is a cross-document VALIDATOR rule, never a binding branch (C5).** The binding
  computes `validationContextJson = TestPlanBindingHelper.BuildCriteriaContext(criteriaJson)` — a
  projection of the criterion ids, mirroring `CreationBindingHelper.BuildTaskIdContext` — and hands it to
  the lifecycle. `TestPlanDocumentType` implements
  `ValidateWithContext(payload, validationContextJson)` (an **implicit** interface method, not
  `override` — the 39-15 gotcha) emitting `STRATEGY_LINE_UNKNOWN_CRITERION` for any strategy line whose
  `criterionId` is absent from the consumed criteria. An empty/unreadable context is a no-op → payload-only
  validation, never a throw. **The member is authored in 41-1b's `Types/TestPlan.cs` file; this story owns
  its content and its tests.** Lockstep coordination is named in Blocks / Blocked by.
- **D5 — Producer variables use DECLARED carriers only; `feedbackVariableName = "contextFindings"`.** The
  rewritten cell (D7) keeps front matter `variables: role, workItemJson, contextFindings, conventions`
  so the change is contract-only. The consumed criteria + the plan + prior findings are folded into
  `contextFindings`; the issue into `workItemJson`. Repair/revise notes land in `contextFindings` — the
  `TaskCreationWorkflow.cs:190` pattern, and the reason the render-drop lesson is not re-learned.
- **D6 — Acceptance policy: this story sets none, and passes `acceptanceRulesJson` through (C4).**
  Choosing `test-plan`'s row in `AcceptanceDefaults.For` is 41-1b AC5. 41-13 records the *requirement*
  (a tester-or-architect reviewer, not the bare single-architect fall-through) as a lockstep note to
  41-1b and proves the passthrough with one integration test. The story's "a plan for a safety-critical
  area can be always-escalate" is an `EscalationClass(DocumentType, "test-plan")` supplied through the
  same passthrough — policy, not code (39-5 posture).
- **D7 — The prompt cell is REWRITTEN to the `TestPlan` contract, by hand (C1).** No prompt file carries a
  39-16 generated-region marker, so the rewrite reproduces `TestPlanDocumentType.RenderContract()`
  verbatim. Front matter: same four variables, `enableTools: true`, `maxTokens: 8192`, **`version: 1 → 2`**
  (`write-tests.md` precedent). The `Bindings` token groups are then chosen from the *rewritten* body —
  minimally `"riskAreas"`, `"strategy"` (or `"strategyLines"`), `"coverageTarget"`, `"entryCriteria"`,
  `"exitCriteria"` — and must be agreed with 41-1b's `RenderContract` **in the same commit window**, since
  the token check is literal.
- **D8 — Pure helper `TestPlanBindingHelper` in `Workflows/Helpers/`, Elsa-free, total, fail-closed.**
  New: `BuildCriteriaContext(criteriaJson) → string` (`""` on empty/unreadable),
  `ProjectStrategyLines(testPlanJson) → string` (`"[]"` fail-closed), `BuildFailureDetail(exit)`.
  Reuses `LifecycleBindingHelper.ReadLifecycleResult`/`IsAccepted` and
  `CreationBindingHelper.DeriveIssueId`. Nothing forked.
- **D9 — `TEST_PLAN.*` gets its own emitter activity, house pattern.**
  `Tamma.Activities/TestPlanning/TestPlanEvents.cs` (`TEST_PLAN.STARTED` / `.DRAFTED` / `.ACCEPTED` /
  `.FAILED`) + `EmitTestPlanEventActivity`, cloned from `Decomposition/EmitDecompositionEventActivity.cs`.
  Emissions are gated on the re-entry position (39-12 D3) so re-entry cannot double-emit.
  *Story-vs-code note: the story lists `TEST_PLAN.STARTED → .DRAFTED → .ACCEPTED` with no failure member;
  `.FAILED` is added because every landed family has one and `DEP_UPGRADE`/`DECOMPOSITION` both do.*
- **D10 — Drift-gate bookkeeping, enumerated (rule 1 clause (f)).** One `Bindings` entry (C2); one
  `BuildSeed` row `("test-plan-authoring", consumes [acceptance-criteria], produces test-plan, false)`;
  `WorkflowInterfaceGraphTests.cs:45` `HaveCount(N) → HaveCount(N+1)` with the reason in the comment;
  the definition id appended to that file's `reconciled` list (`:102-123`);
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (`:125`) gains
  `"TestPlanAuthoringWorkflow"`. **No** `AgentAction` / `RolePhaseMap` / `SystemPrompts` count pin moves —
  `(tester, plan-test-strategy)` already exists (`AgentAction.cs:78`, `RolePhaseMap.cs:116`) with a
  shipped template. **No** `DocumentTypeKey` / `DocumentTypeRegistry` count pin moves — those are 41-1b's
  AC4.

## Implementation Steps

1. **Precondition gate (no code, a real gate).** Verify in tree and compiling: `DocumentTypeKey.TestPlan`,
   `TestPlanDocumentType` registered, `DocumentTypeKey.AcceptanceCriteria` registered (all **41-1b**), and
   a workflow that produces an accepted `AcceptanceCriteria` (**41-2**). Any gap blocks the corresponding
   step — file it, do not work around it. Steps 3–5 and the helper tests can proceed against 41-1b's
   pinned shapes with fakes; steps 6–10 cannot.

2. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/plan-test-strategy.md`** (D7, C1) to the
   `TestPlan` contract; bump `version` to 2; keep the four declared variables.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/TestPlanning/TestPlanEvents.cs` +
   `EmitTestPlanEventActivity.cs`** (D9).

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/TestPlanBindingHelper.cs`** (D8).

5. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TestPlan.cs` (41-1b's file)** — add the
   `ValidateWithContext` implicit-interface member + the `StrategyLineUnknownCriterion` constant (D4,
   C5). **Lockstep with the 41-1b owner**; if 41-1b has already merged this is a pure addition.

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestPlanAuthoringWorkflow.cs`** (D1–D5),
   copying `TestCaseCreationWorkflow`'s skeleton plus `TaskCreationWorkflow`'s consumed-document fetch:
   `ReadInputs` → `ComputeReEntryPosition` (`DocumentType = "test-plan"`) → `ReadPositionStage` →
   `FreshRun` `FlowDecision` (True → `EmitStarted` → `FetchConsumedCriteria` → join; False → join) →
   `DispatchLifecycle` (the single `DispatchWorkflow`) → `ReadLifecycleExit` → `LifecycleAccepted`
   `FlowDecision` → `EmitAccepted`/`EmitFailed` → `ExposeOutput`.
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (C3). Dispatch input mirrors
   `TestCaseCreationWorkflow.cs:131-153` with `documentType = "test-plan"`,
   `producerRole = AgentRole.Tester.ToWire()`,
   `producerAction = AgentAction.PlanTestStrategy.ToWire()`,
   `feedbackVariableName = "contextFindings"`,
   `validationContextJson = TestPlanBindingHelper.BuildCriteriaContext(...)`,
   `acceptanceRulesJson` passthrough.

7. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** — the `BuildSeed` row
   (D10).

8. **MODIFY the drift/pin gates in ONE commit** (D10): `WorkflowInterfaceGraphTests.cs:45` + its
   `reconciled` list; `TaxonomyDriftBuildTests.cs:125`; `ContractBindingTests.cs` `Bindings` (`:82`) —
   the `(tester, plan-test-strategy)` entry, authority `"TestPlanDocumentType.Validate"`, token groups
   from the rewritten template (D7).

9. **CREATE `TestPlanAuthoringWorkflowStructureTests.cs` + `TestPlanBindingHelperTests.cs`
   (`tests/Tamma.Activities.Tests/Workflows/`) + `TestPlanCrossDocumentValidationTests.cs`
   (`tests/Tamma.Core.Tests/Documents/Types/`)** — see Test Plan.

10. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TestPlanLifecycleExecutionTests.cs`**
    on the shared 39-6/39-10 Testcontainers fixture. Scenarios in Test Plan. Finish with full
    `dotnet test` + `dotnet ef migrations has-pending-model-changes` (clean).

## Data & Migrations

None. `TestPlan` documents persist to 39-11's `document_instances` (41-1b introduces no new table);
`TEST_PLAN.*` and `DOCUMENT.*` ride the existing drain.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new family, D9):** `TEST_PLAN.STARTED` (fresh runs only), `.DRAFTED`, `.ACCEPTED`
  (data `strategyLineCount`, `consumedCriteriaId`), `.FAILED` (on `rejected`/`escalated`, detail naming
  the typed outcome wire). Tags `issueId`, `repository`, `tenantId`, `correlationId`.
- **Emitted by the machinery this story wires in:** the full `DOCUMENT.*` family,
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes:** none at runtime; the `AcceptanceCriteria` arrives through the 39-11 store read.

## Test Plan

All NUnit + FluentAssertions (Moq; Testcontainers for step 10).

- **`TestPlanAuthoringWorkflowStructureTests`** — the clause set, cloned from
  `TaskCreationWorkflowStructureTests`: builds; `DefinitionId == "test-plan-authoring"`; threads
  `TenantId`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables (d); exactly one
  `DispatchWorkflow` with literal def id `document-lifecycle`, zero targeting `llm-call` (a+b);
  `ScanLifecycleBindingDispatches()` contains `(TestPlanAuthoringWorkflow, DispatchLifecycle, tester,
  plan-test-strategy)` and `MaterializeDispatchInput` shows `documentType == "test-plan"` +
  `feedbackVariableName == "contextFindings"` (e); zero `Finish`, every leaf inside `ExposeOutput` (c);
  one `ComputeReEntryPositionActivity`, one `FetchLatestAcceptedDocumentActivity`;
  `[ResumeBehavior(LatestStateReEntry)]`; no `Wait*`. **Covers AC1 (structure), AC4.**
- **`TestPlanBindingHelperTests`** — `BuildCriteriaContext` on a valid criteria body / a criteria-less
  body / garbage → `""`; `ProjectStrategyLines` fail-closed `"[]"`; `BuildFailureDetail` names each
  reachable outcome wire. **Covers AC2 (helper half).**
- **`TestPlanCrossDocumentValidationTests` (`Tamma.Core.Tests`)** — the D4 rule: a `TestPlan` whose
  strategy line cites a criterion present in the context validates; one citing an absent criterion ⇒
  `STRATEGY_LINE_UNKNOWN_CRITERION`; an **empty context** ⇒ the rule does not fire and the payload-only
  result is returned unchanged (the additive-DIM guarantee); `Validate` (context-free) **never** emits
  the code. **Covers AC2 (the falsifiable half — this is what turns AC2 from prose into a check).**
- **`TestPlanDocumentType` payload-rule fixtures (41-1b's file, extended here if 41-1b's are thin)** —
  one rejecting + one accepting fixture per AC1 rule: unranked/duplicate-ranked risk areas; a strategy
  line with no coverage target; missing entry or exit criteria. Each asserts the **violation code**, not
  merely "invalid".
- **Template-conformance test** — the JSON example embedded in the rewritten
  `plan-test-strategy.md` deserializes to `TestPlan` and validates clean. *This is the test that would
  have caught C1, and the one that catches a future drift the token-only `ContractBindingTests` cannot.*
- **Drift-gate modifications (step 8, self-verifying)** — `ContractBindingTests` green with the new
  entry (non-stale via the lifecycle-binding walk) and the universal authority pin (`:626`) green;
  `TaxonomyDriftBuildTests` contributor subset holds; `WorkflowInterfaceGraphTests` count + the
  non-provisional assertion green.
- **`ResumableStandardStructuralTests`** — passes with no `LegacyResumeAllowlist` entry. **Covers AC4.**
- **`TestPlanLifecycleExecutionTests` (Testcontainers)** —
  (a) **happy path with criteria:** seed an accepted `AcceptanceCriteria`; scripted valid `TestPlan`
  draft → review approve → `Accept` resume → `status=completed`, `parentDocumentId` = the criteria id,
  store read-back by `(issueId, "test-plan")` succeeds (**AC1, AC2, AC3**).
  (b) **traceability ring:** first draft cites an unknown criterion ⇒ `STRATEGY_LINE_UNKNOWN_CRITERION`
  → repair/revise round → corrected draft accepted; `DOCUMENT.REVISION_STARTED` present (**AC2**).
  (c) **no criteria present:** empty store → the run proceeds, `parentDocumentId` `""`, the traceability
  rule never fires (**AC2** absent branch).
  (d) **downstream readability (C6):** after acceptance, the same
  `FetchLatestAcceptedDocumentActivity` read `test-case-creation` would use returns the accepted
  `TestPlan` (**AC3**, scoped per C6).
  (e) **reviewer-policy passthrough (D6):** `acceptanceRulesJson` naming a tester reviewer routes the
  review through `(tester, review-testability)`; a control run with the default rules routes through
  `(architect, plan-review)` — the observable difference C4 describes.
  (f) **validation exhaustion:** always-invalid stub → typed `ValidationExhausted` escalation with
  lineage; `TEST_PLAN.FAILED` detail names the outcome; no error terminal reached.
  (g) **crash re-entry:** kill mid-review, fresh dispatch → resumes at review of the same revision,
  exactly one `TEST_PLAN.STARTED` and one `DOCUMENT.ACCEPTED` on the stream.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `TestPlan` validated (risk ranking, coverage mapping, entry/exit) | 2, 6, 8 | StructureTests clauses (a)–(e); `TestPlanDocumentType` fixtures; template-conformance test |
| 2 — consumes `AcceptanceCriteria` when present; strategy lines trace to criteria | 4, 5, 6 (D3/D4) | `TestPlanCrossDocumentValidationTests`; ExecutionTests (b) + (c) |
| 3 — consumable by `test-case-creation` / 41-14 via 39-11 *(scoped to readability, C6)* | 6, 7 | ExecutionTests (d); the `BuildSeed` row |
| 4 — resume declaration *(as `LatestStateReEntry`, C3)*; 39-10 gate green without allowlist | 6 | `ResumableStandardStructuralTests`; StructureTests declaration assert |
| — (epic rule 1f) new `WorkflowDocumentInterface` row + edge pin bumped | 7, 8 | `WorkflowInterfaceGraphTests` count + non-provisional assertion |

## Risks & Mitigations

- **Wave-0 coupling is the schedule risk, not the code.** `TestPlan` and `AcceptanceCriteria` are both
  41-1b's; the traceability member is authored in 41-1b's file; the token groups depend on 41-1b's
  `RenderContract`. Mitigation: step 1 is a real gate; steps 2–5 + 9's pure tests are buildable against
  41-1b's pinned shapes with fakes; every consumed name is pinned in 41-1b's story so drift is a
  mechanical rename.
- **C2's `Bindings`-entry ownership is contested.** If 41-1b lands its AC6 entry first, CI goes red on
  the staleness guard before this story exists. Mitigation: the correction is filed back to 41-1b in
  writing; whoever merges first owns the removal. Cheapest fix: 41-1b drops the `Bindings` clause from
  AC6 and points at the per-binding stories.
- **The rewritten template drifts back to the `Plan` shape.** Mitigation: the template-conformance test
  parses the example out of the shipped `.md` and validates it — a shape regression fails the build,
  which the token-only gate cannot do.
- **`ValidateWithContext` implemented as `override`** — it is a default interface member and is **not**
  virtual on the class; `override` does not compile and a plain method silently shadows nothing. This
  cost 39-15 a debugging cycle (recorded in its findings). Mitigation: named in D4 and asserted by the
  context-free-`Validate`-never-emits test.
- **A tester lens is assumed but not configured (C4).** Mitigation: ExecutionTests (e) makes the default
  observable; the requirement is filed to 41-1b AC5 rather than silently patched into the shared
  `AcceptanceDefaults`.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate + 41-1b/41-2 shape reconciliation | 0.25 |
| 2 | Template rewrite to the `TestPlan` contract (C1) | 0.4 |
| 3 | `TEST_PLAN.*` events + emitter | 0.35 |
| 4 | `TestPlanBindingHelper` | 0.3 |
| 5 | `ValidateWithContext` + `STRATEGY_LINE_UNKNOWN_CRITERION` (lockstep with 41-1b) | 0.4 |
| 6 | The binding workflow | 0.8 |
| 7–8 | Registry row + the four drift/pin edits | 0.35 |
| 9 | Structure + helper + cross-doc + conformance tests | 0.75 |
| 10 | Testcontainers scenarios (a)–(g) + full-suite green | 0.6 |
| — | 41-1b lockstep coordination, review polish | 0.2 |
| **Total** | | **4.4** (story estimate: 3–4 days — the extra ~0.5 d is C1's template rewrite and C5's validator member, neither of which the story scoped) |

## Blocks / Blocked by

- **Blocked by — HARD, Wave-0: `41-1b`.** `TestPlan` is one of 41-1b's six types.
  `DocumentTypeKeyExtensions.Parse("test-plan")` throws `DOCUMENT.TYPE.UNKNOWN` today
  (`DocumentTypeKey.cs:49-59`, ten members at `:22-34`) and `DocumentTypeRegistry.Resolve` throws
  `DOCUMENT.TYPE.NOT_REGISTERED` (`:85-91`). **This blocks the human path too** — a `DocumentInstance`
  row cannot be written for an unregistered type, so "a human authors it instead" is not an escape.
  41-1b also owns the two vocabulary count pins (`DocumentTypeKeyTests.cs:20` `Be(10)`,
  `DocumentTypeRegistryTests.cs:37` `HaveCount(10)`) — this story moves **neither**.
- **Blocked by — HARD: `41-2`** (Acceptance-Criteria Authoring), and transitively 41-1b again for the
  `AcceptanceCriteria` type. Without it AC2's consumed side has no producer. AC1/AC4 and the D3 "absent
  criteria" path are testable without it (ExecutionTests (c)); AC2's traceability ring is not.
- **Blocked by — lockstep, not sequential:** the `ValidateWithContext` member (D4) is authored in
  41-1b's `Types/TestPlan.cs`; the `Bindings` token groups depend on 41-1b's `RenderContract`; the
  `test-plan` acceptance row is 41-1b AC5. Agree all three in one signature/shape review with the 41-1b
  owner before either branch merges.
- **NOT blocked by:** `41-1a` (no new role, no new cell — `(tester, plan-test-strategy)` exists at
  `AgentAction.cs:78` / `RolePhaseMap.cs:116` with a shipped template) · `41-1c` (no prose) · **the
  tenant-aware scheduled-trigger seam** (this workflow is issue-triggered, not cron) · **Epic 40** (no
  coding execution) · 39-17/39-19/39-20 for the *shape* of the story, though rules 3 and 4 remain
  unreachable end-to-end epic-wide — the accept gate publishes and suspends, and nothing decides.
- **Blocks:** `41-14` (Exploratory Test Charter — its `consumes: [TestPlan?]`) as a soft edge; nothing
  hard.
- **Shared-edit register:** `ContractBindingTests.Bindings`,
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`, `DocumentTypeRegistry.BuildSeed`, and the
  single-integer `WorkflowInterfaceGraphTests.cs:45` edge pin are touched by every Epic 41 producer
  story (41-12, 41-14, 41-15, 41-16, 41-2, 41-3, 41-6, 41-19, 41-27 …). Two producer branches in the
  same window WILL conflict on the pin. Sequence the pin bump last and rebase.
