# Implementation Plan — Story 41-18: Refactor Planning Workflow

## Scope & Deliverable

When this story is done, a refactor need — arriving from 41-11 tech-debt triage, from a review
concern, or dispatched directly — becomes a typed `Plan` on the Epic 39 lifecycle. A new thin
binding `DefinitionId = "refactor-plan"` assembles the refactor context (the consumed
`TriageDecision` or `Review`, a context scan, the target files), dispatches `document-lifecycle`
with `documentType = "plan"` and the existing, currently-unbound `(senior_developer, plan-refactor)`
producer cell, and routes typed exits. It contributes no parse, no branch that impersonates a
quality decision, no `Finish`. Behavior preservation is enforced **as structure** through a
story-local cross-document rule on `PlanDocumentType` reachable via the lifecycle's existing
`validationContextJson` seam — not as reviewer judgement. The accepted `Plan` is retrievable by
`issueId`/`repository` through 39-11 and readable by a coding-step dispatch.

**This story is genuinely small and genuinely unblocked at its produce step**: the cell exists, the
type exists, no taxonomy edit is needed. Its only real design work is AC3.

## Pre-Reading

- `docs/stories/epic-41/story-41-18/41-18-refactor-planning.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/README.md` — rule 1's six thinness clauses (a)–(f), especially (f)'s edge-pin bump
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — **the reference binding**; the graph skeleton to copy, incl. the D2 producer-scoped issue id (`ScopeIssueId`, `:112`) which this story needs for the same reason
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the reference structure-test set
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs:32-33`, `:148` — **the cross-document validation recipe** this story reuses: the binding builds a `validationContextJson` and the lifecycle forwards it to `IDocumentType.ValidateWithContext`
- `apps/tamma-elsa/src/Tamma.Core/Documents/IDocumentType.cs:32-44` — the `ValidateWithContext` default-interface-member seam (additive; context-free `Validate` is the fallback)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Plan.cs` — `PlanDocumentType` violation constants at `:47-71` (`EMPTY_PLAN` `:50`, `TASK_MISSING_FILE_MAP` `:53`, `TASK_MISSING_TESTING` `:56`, `DUPLICATE_TASK_ID` `:59`, `DANGLING_DEPENDS_ON` `:62`, `SELF_DEPENDS_ON` `:65`, `CYCLIC_DEPENDS_ON` `:68`, `NO_TOPOLOGICAL_ORDER` `:71`) and the shared `Contract` const at `:144`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:83-86`, `:198`, `:338-343` — where `validationContextJson` is read and forwarded
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` — `ReadLifecycleResult` / `IsAccepted` / the `LifecycleExit` record
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs` — the store read seam for the consumed document (AC4's lineage)
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs:54` (`[Wire("plan-refactor")] PlanRefactor`), `RolePhaseMap.cs:86` (in `SeniorDeveloper`'s set), `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/plan-refactor.md` — **all three exist; this story mints no cell**
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — where the new `Bindings` entry goes; note the universal-authority pin (`:626`) and the staleness guard (`:724-737`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174` + `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` (`HaveCount(16)`)
- `docs/stories/epic-39/story-39-12/implementation-plan.md` (D2 typed-routing-only, D3 event mirroring, D5 drift-guard migration, D7 resume declaration)

## Corrections to the story

1. **CONFIRMED — the cell exists and needs no 41-1a work.** `(senior_developer, plan-refactor)` is
   real: `AgentAction.cs:54`, `RolePhaseMap.cs:86` (`SeniorDeveloper`'s `FreezeSet`), and
   `src/Tamma.Api/Prompts/senior_developer/plan-refactor.md` ships. It is currently **unbound in
   both directions** — it appears in neither `Bindings` nor `IntentionallyUnbound` — which is legal
   only because no compiled dispatch site emits it (`EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`
   only classifies *discovered* pairs). The moment this binding lands, the pair becomes discovered
   and **must** be classified, and per
   `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` (`:626-652`) it
   must land in `Bindings` with a `*DocumentType.Validate` authority. AC1 is therefore not optional
   bookkeeping — it is what makes the build stay green.

2. **CONFIRMED — every violation code AC2 names exists, at the cited lines.** `Plan.cs:47-71` lists
   nine constants including all five AC2 names. AC2 requires **fixtures**, not validator code.

3. **CONFIRMED — AC3's premise.** `PlanDocumentType` has no behavior-preservation rule; the story's
   own "Corrected" note is accurate. `TASK_MISSING_TESTING` only requires the `testing` field to be
   non-empty — a step whose `testing` reads *"we'll figure it out"* passes today.

4. **NEW — AC3's `STEP_MISSING_CHARACTERIZATION_TEST` cannot be "story-local" in the naive sense, and
   the story does not say where it lives.** `PlanDocumentType.Validate` is shared by **three**
   producers: `(architect, plan-system-design)` (`ContractBindingTests.cs:160`),
   `(senior_developer, create-tasks)` (`:172`), and now `(senior_developer, plan-refactor)`. Adding
   an unconditional characterization-test rule to `Validate` would immediately invalidate every
   `plan-generation` and `task-creation` document in the tree. **The seam that makes it genuinely
   story-local already exists and is already used**: `IDocumentType.ValidateWithContext`
   (`IDocumentType.cs:32-44`, a default interface member) plus the lifecycle's
   `validationContextJson` input (`DocumentLifecycleWorkflow.cs:83-86`, `:198`, `:338-343`), which
   `TestCaseCreationWorkflow` already drives (`:148`). See **D3** — this is the plan of record and it
   requires no generic-layer behaviour change for the other two producers.

5. **NEW — AC3's "names a test that exists before the step runs" is not decidable from the document.**
   The plan document is text; whether `tests/Foo/BarCharacterizationTests.cs` exists in the repo is a
   filesystem fact. Enforcing existence would require the validator to do I/O, which
   `IDocumentType` forbids (pure, `JsonElement` in / `DocumentValidationResult` out). **Correction:**
   AC3 is enforced as *structure over supplied context* — the binding passes the known
   characterization/regression test inventory (from the context scan) as
   `validationContextJson`, and `ValidateWithContext` rejects a step whose `testing` field names no
   member of that inventory. Existence is asserted by the *context builder*, purity is preserved in
   the validator. Where no inventory is available the rule degrades to a shape rule (the `testing`
   field must name a test-like identifier, not prose) — recorded, not hidden.

6. **NEW — AC5's coding-step hand-off is weaker than "Epic 40 is only needed downstream" implies, but
   the story's own Corrected note already says the right thing.** Verified:
   `.github/workflows/tamma-agent.yml` does not exist in this repo, so the coding-step dispatch fails
   loud with `WorkflowNotFound`. AC5's integration test must therefore assert *"a coding-step
   dispatch **reads** the accepted `Plan` through 39-11"* — i.e. the read + the dispatch input shape —
   not that the coding step executes. Producing and accepting the `Plan` has zero Epic 40 dependency.

7. **NEW — the story does not name its resume mode consistently with the landed pattern.** AC6 says
   `[ResumeBehavior(Both)]`. Like every landed thin binding, this one never suspends on its own
   bookmark — the accept-gate suspend is inside the dispatched `document-lifecycle` child, waited on
   with `WaitForCompletion=true`. `ResumableStandardStructuralTests` clause (b) requires a
   `Both`-declaring workflow's graph to contain a canonical suspend activity; this graph will not.
   **Correction: `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`**, matching
   `TaskCreationWorkflow.cs:47`, `ResearchWorkflow.cs:35`, `IssueDecompositionWorkflow`, and 39-12's
   D7. See **D6**.

## Design Decisions

- **D1 — New `DefinitionId = "refactor-plan"`; nothing existing is rewired.** No incumbent workflow
  owns refactor planning, so there is no byte-stability constraint and no dispatch site to preserve.
  Inputs: `repository`, `issueId?`, `issueNumber?`, `tenantId`, `triageDocumentId?` (the 41-11
  `TriageDecision`), `reviewDocumentId?` (the alternative trigger), `targetPathsJson?`,
  `contextIds?`, `acceptanceRulesJson?`, `conventions?`. Outputs: `status`, `outcome`, `documentId`,
  `parentDocumentId`, `planJson`.

- **D2 — Producer-scoped resume anchor, because `plan` already has two producers per issue.**
  `TaskCreationWorkflow` D2 established this: 39-11's latest-accepted read scopes by
  `(issueId, documentType)` with **no producer filter**, so three `plan` producers on one issue would
  collide on re-entry and on "the accepted plan". The binding computes
  `scopedIssueId = CreationBindingHelper.ScopeIssueId(issueId, "refactor-plan")`
  (→ `{issueId}#refactor-plan`) and uses it for `ComputeReEntryPositionActivity`, the lifecycle's
  `issueId`, and `correlationId`. The **base** `issueId` is used only for the `FetchLatestAccepted*`
  reads of consumed documents. This is a reuse of a landed helper, not new machinery.

- **D3 — Behavior preservation is a cross-document rule on `PlanDocumentType`, reached only through
  `validationContextJson` (Corrections 4 + 5).** Concretely:
  - Add `public const string StepMissingCharacterizationTest = "STEP_MISSING_CHARACTERIZATION_TEST";`
    to `PlanDocumentType` and **override `ValidateWithContext`** so that: with an **empty** context it
    is byte-identical to `Validate` (so `plan-generation` and `task-creation` are untouched — they
    pass no context); with a non-empty context of shape
    `{"requireCharacterizationTests":true,"knownTests":["…"]}` it runs `Validate` first, then rejects
    any task whose `testing` field names no member of `knownTests` (or, when `knownTests` is empty,
    whose `testing` does not match the test-identifier shape).
  - The binding builds that context from the refactor context scan
    (`RefactorBindingHelper.BuildCharacterizationContext`) and passes it as
    `validationContextJson` — exactly `TestCaseCreationWorkflow.cs:148`'s move.
  - **Why this and not a new document type:** a refactor plan *is* a `Plan` (the README's reuse-first
    rule names `Plan` for refactor plans). Forking the type to carry one extra rule would add a
    vocabulary member, two count-pin bumps and an `AcceptanceDefaults` arm for no domain gain.
  - **Why this and not a reviewer instruction:** the story's own note ("enforced as structure, not as
    judgement") and the epic's flagship precedent (`APPROVE_WITH_BLOCKING_ISSUES` makes a bad state
    *unrepresentable*) both point at the validator.

- **D4 — Consumed-document lineage is a store read, fail-loud (AC4).** The binding uses
  `FetchLatestAcceptedDocumentActivity` (or a by-id read where `triageDocumentId` is supplied
  explicitly) and records the resolved `documentId` — or `null` when triggered from a `Review`
  concern instead — into the output `parentDocumentId` and the `REFACTOR.PLAN.STARTED` event data.
  A supplied-but-unreadable id is **not** silently treated as absent: the read seam's not-found
  result routes to the loud failure edge with a typed detail. AC4's "fails loud if a referenced id is
  unreadable" is thus a routing property of the binding, testable without an LLM.

- **D5 — Zero parse, zero `Finish`, exactly two typed `FlowDecision`s.** Following 39-12 D2's
  resolution of "no bespoke branch": the binding contains `FreshRun` (re-entry position == produce —
  gates the STARTED emission and the context reads so a re-entry is not a new plan) and
  `LifecycleAccepted` (typed lifecycle `status`). Nothing else branches; no branch reads model
  output. The structure test pins the exact `FlowDecision` id set so a parse gate cannot reappear.

- **D6 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, no allowlist entry (Correction 7).**
  With one `ComputeReEntryPositionActivity` node in the graph (clause (c)) and no `Wait*` activity.
  AC6's substance — "39-10 structural test green without an allowlist entry" — is fully met; only the
  mode literal changes, and it changes to the honest one.

- **D7 — New event family `REFACTOR.*`.** New file
  `apps/tamma-elsa/src/Tamma.Activities/Refactor/RefactorEvents.cs` in the `ResearchEvents.cs` shape:
  `Started` = `REFACTOR.PLAN.STARTED`, `Drafted` = `REFACTOR.PLAN.DRAFTED`,
  `Accepted` = `REFACTOR.PLAN.ACCEPTED`, plus a LOUD `Failed` = `REFACTOR.PLAN.FAILED` for the
  `rejected`/`escalated` exits — the story's three-event list has no failure member, and every landed
  family has one (`RESEARCH.FAILED`, `DECOMPOSITION.FAILED`); a typed escalation must not exit
  silently. `ParseTenantId` + `StatusForEvent` per house convention.

- **D8 — The API-affecting always-escalate class is configuration, not code.** The story's
  "A refactor touching a public API can be an always-escalate class" is satisfiable today **only** at
  the granularity the mechanism supports: `AcceptanceRules.AlwaysEscalate` holds
  `EscalationClass(Kind, Key)` where `EscalationClassKind` is `document-type` **or** `agent-action`
  (`AcceptanceRules.cs:200-210`; matched in `AcceptanceGuardrails.TryPreGate`, `:50-68`). So
  `{"kind":"agent-action","key":"plan-refactor"}` escalates **every** refactor plan; there is no
  payload-conditional escalation class. Making "touches a public API" escalate is therefore **not
  implementable as a rule** in this story. Record it: the AC-level promise is met by making the
  *class* configurable and testing that the `agent-action` class escalates; a per-payload predicate
  would be a new `EscalationClassKind` in the generic layer and belongs to a 39-5 follow-up, not
  here. (41-19, 41-20 and 41-21 hit the same wall — see their plans' matching decision.)

## Implementation Steps

1. **Precondition check (no code).** `dotnet build` green. Confirm in tree: `PlanDocumentType`
   registered (`DocumentTypeRegistry.cs:34`), `IDocumentType.ValidateWithContext` present, the
   lifecycle's `validationContextJson` forwarding present (`:338-343`),
   `FetchLatestAcceptedDocumentActivity` present, `(senior_developer, plan-refactor)` present in the
   taxonomy and its prompt file present. **All verified present at plan time.**

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Plan.cs`** (D3, AC3) — add the
   `StepMissingCharacterizationTest` constant and override `ValidateWithContext(JsonElement, string)`:
   empty/whitespace context ⇒ `Validate(payload)` verbatim (byte-identical behaviour for the other
   two producers); non-empty ⇒ `Validate` then the characterization rule, with a domain-phrased
   violation naming the offending task id and its `testing` value. Extend the `Contract` const
   (`:144`) with **one** additional sentence describing the `testing` expectation — **shared by all
   three plan producers**, so word it as guidance that a plan-generation/task-creation template can
   also honour without becoming invalid.

3. **HAND-EDIT `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/plan-refactor.md`** — bring
   the body onto the canonical `Plan` wire (`"tasks"`/`"steps"` + `"fileMap"`/`"files"`/
   `"filesToModify"` + `"testing"` + `"dependsOn"`), and instruct that each step's `testing` names an
   existing characterization/regression test. Bump `version` in the front matter. **No 39-16
   generated-region marker exists in any prompt file** (verified — same finding 41-29's plan records
   for the two plan templates), so this is a hand edit; if 39-16 lands first, replace it with its
   output. Do **not** touch `Prompts/architect/plan-system-design.md` or
   `Prompts/senior_developer/create-tasks.md`.

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Refactor/RefactorEvents.cs`** (+ an
   `EmitRefactorEventActivity` if the house per-family emitter pattern applies) — D7.

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/RefactorBindingHelper.cs`** —
   pure, Elsa-free, total, fail-closed:

   ```csharp
   public static class RefactorBindingHelper
   {
       // D3 — the validationContextJson the lifecycle forwards to PlanDocumentType.ValidateWithContext.
       public static string BuildCharacterizationContext(string? knownTestsJson);
       // AC4 — the consumed-document reference recorded on the run (null when Review-triggered).
       public static string? ResolveParentDocumentId(bool triageFound, string? triageDocId,
                                                     bool reviewFound, string? reviewDocId);
       public static int CountSteps(string documentJson);           // 0 on unreadable
       public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit);
   }
   ```

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RefactorPlanWorkflow.cs`** — the binding.
   Copy `TaskCreationWorkflow.cs`'s skeleton. `builder.DefinitionId = "refactor-plan"`,
   `builder.Version = WorkflowVersions.ComputedVersion`,
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (D6). Graph:
   `ReadInputs` (computes `scopedIssueId` per D2) `→ ComputeReEntryPosition(scopedIssueId, "plan")
   → ReadPositionStage → FreshRun(FlowDecision)`
   → *(True)* `EmitRefactorStarted` → `FetchConsumedTriage` (`FetchLatestAcceptedDocumentActivity`,
   documentType `triage-decision`, on the **base** issue id) → `FetchConsumedReview` (optional,
   documentType `review`) → `BuildValidationContext` (`SetVariable`) → join;
   → *(False)* join
   → `DispatchLifecycle` (`document-lifecycle`, `WaitForCompletion=true`) with
   `documentType = "plan"`, `producerRole = AgentRole.SeniorDeveloper.ToWire()`,
   `producerAction = AgentAction.PlanRefactor.ToWire()`, `producerVariablesJson` (target paths,
   consumed triage/review payloads, conventions, known tests), a **declared**
   `feedbackVariableName` naming a variable `plan-refactor.md` actually declares (clause (e) — check
   the front matter; this is the render-drop lesson), `validationContextJson` (D3), `issueId` =
   `scopedIssueId`, `correlationId`, `tenantId`, `acceptanceRulesJson`
   → `ReadLifecycleExit` → `LifecycleAccepted(FlowDecision)` → `EmitRefactorDrafted`/`EmitRefactorAccepted`
   vs `EmitRefactorFailed` → `ExposeOutput` (the single terminal `Sequence` of `SetOutput`s).
   **Zero `Finish`; zero `DispatchWorkflow("llm-call")`; exactly one `DispatchWorkflow`, literal
   definition id `document-lifecycle`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`
   variables.**

7. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**
   (AC1, Correction 1) — add to `Bindings`:

   ```csharp
   // Story 41-18 — RefactorPlanWorkflow binds (senior_developer, plan-refactor) as the produce
   // step of its document-lifecycle binding; shape authority is
   // Tamma.Core/Documents/Types/Plan.cs (PlanDocumentType.Validate / ValidateWithContext for the
   // characterization-test ring). Token groups mirror the two sibling plan producers.
   [("senior_developer", "plan-refactor")] = new("PlanDocumentType.Validate",
   [
       AnyOf("\"tasks\"", "\"steps\""),
       AnyOf("\"fileMap\"", "\"files\"", "\"filesToModify\""),
       One("\"testing\""),
   ]),
   ```

   Re-run the whole fixture: the pair must now be *discovered* (via
   `TaxonomyDriftBuildTests`' lifecycle-binding walk), so clause (c)'s staleness guard passes; the
   universal-authority pin passes (`PlanDocumentType.Validate`); no `IntentionallyUnbound` entry
   exists so the both-classified guard is trivially satisfied.

8. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (AC6, rule 1(f)) —
   add to `BuildSeed`:
   `new WorkflowDocumentInterface("refactor-plan", new[] { DocumentTypeKey.TriageDecision }, DocumentTypeKey.Plan, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`** —
   `HaveCount(16)` → `HaveCount(17)`, with a comment naming Story 41-18 and the added edge (the pin
   is a deliberate conscious edit, one per new producing workflow — coordinate with whichever Epic 41
   story lands first).

9. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`** —
   add `"RefactorPlanWorkflow"` to `ExpectedContributingWorkflows` with the standard
   lifecycle-binding-walk comment. `MinExpectedDispatchPairs` (`:110`, 21) needs no change (the count
   rises).

10. **CREATE the tests** — see Test Plan. Finish with full `dotnet test` and
    `dotnet ef migrations has-pending-model-changes` (must stay clean).

## Data & Migrations

None. The `Plan` document persists through 39-11's existing `document_instances` table; `REFACTOR.*`
rides the existing `TammaEventEmitter` → `EventPersistenceMiddleware` → `EventRepository` →
`domain_events` drain. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new constants, `Tamma.Activities/Refactor/RefactorEvents.cs`):**
  `REFACTOR.PLAN.STARTED` (fresh runs only; data `parentDocumentId`, `trigger` = `triage`|`review`|`direct`),
  `REFACTOR.PLAN.DRAFTED` (data `stepCount`), `REFACTOR.PLAN.ACCEPTED` (data `documentId`,
  `stepCount`), `REFACTOR.PLAN.FAILED` (LOUD, on `rejected`/`escalated`, detail names the typed
  outcome wire — D7). Tags: `issueId`, `repository`, `tenantId`, `correlationId`.
- **Emitted by the machinery this binding wires in (not by this story's code):** the `DOCUMENT.*`
  family, `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`.
- **Consumes:** none at runtime; tests read both families back from the captured stream.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`PlanDocumentTypeCharacterizationTests`** (`Tamma.Core.Tests`, AC3, D3) — **the heart of this
  story.** (i) `ValidateWithContext(payload, "")` is byte-identical to `Validate(payload)` for a
  `plan-generation`-shaped and a `task-creation`-shaped fixture — the no-regression proof for the
  other two producers; (ii) with a `requireCharacterizationTests` context, a step whose `testing`
  names a known test **passes**, a step whose `testing` is prose **fails** with
  `STEP_MISSING_CHARACTERIZATION_TEST` naming the task id, and a step whose `testing` is empty still
  fails with `TASK_MISSING_TESTING` (the two codes are distinct and both reachable); (iii) an
  empty `knownTests` inventory degrades to the shape rule (Correction 5) — documented in the test
  name. **Covers AC3.**
- **`PlanDocumentType` AC2 fixture sweep** (`Tamma.Core.Tests`) — one rejecting fixture per rule
  asserting the **code**: no steps ⇒ `EMPTY_PLAN`; step with no file map ⇒ `TASK_MISSING_FILE_MAP`;
  empty `testing` ⇒ `TASK_MISSING_TESTING`; self-dependent step ⇒ `SELF_DEPENDS_ON`; cyclic pair ⇒
  `CYCLIC_DEPENDS_ON`. Reuse existing fixtures where they already exist; add only what is missing.
  **Covers AC2.**
- **`RefactorPlanWorkflowStructureTests`** (modelled on `TaskCreationWorkflowStructureTests`) — the
  six thinness clauses executable: exactly one `DispatchWorkflow` with literal def id
  `document-lifecycle`; zero `llm-call` dispatches; `OfType<Finish>()` empty; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables;
  `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains
  `(RefactorPlanWorkflow, DispatchLifecycle, senior_developer, plan-refactor)` and
  `MaterializeDispatchInput` yields `documentType == "plan"` plus a declared
  `feedbackVariableName`; `DefinitionId == "refactor-plan"`, threads `TenantId`, one
  `ComputeReEntryPositionActivity`, no `Wait*` activity,
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. **Plus** a pin on the exact `FlowDecision` id
  set `{FreshRun, LifecycleAccepted}` (D5). **Covers AC1 (structure), AC6.**
- **`RefactorBindingHelperTests`** — `BuildCharacterizationContext` round-trips and produces the
  exact shape `ValidateWithContext` reads (pin both sides in one test so they cannot drift);
  `ResolveParentDocumentId` across the four found/not-found combinations, including the
  supplied-but-unreadable case → loud, never silently `null`; `CountSteps` on valid/unreadable;
  `BuildFailureDetail` names each reachable `DocumentLifecycleOutcome` wire + `rejected`.
  **Covers AC4 (unit half).**
- **Drift-guard runs (steps 7–9, self-verifying)** — full `ContractBindingTests` fixture green;
  `ResumableStandardStructuralTests` green with **no** `RefactorPlanWorkflow` allowlist entry;
  `WorkflowInterfaceGraphTests` green at 17. **Covers AC1, AC6.**
- **`RefactorPlanLifecycleExecutionTests`** (Testcontainers, on the shared 39-6/39-10 fixture) —
  (a) **happy path from a 41-11-shaped trigger:** seed an accepted `TriageDecision` in the store,
  dispatch `refactor-plan`, scripted valid `Plan` draft → review approve → orchestrator `Accept`
  resume → outputs carry `parentDocumentId` = the seeded triage document id;
  `REFACTOR.PLAN.STARTED`/`.DRAFTED`/`.ACCEPTED` present with matching `issueId` tags.
  **Covers AC4.**
  (b) **Review-triggered variant:** no triage document; `parentDocumentId` is `null` and the run
  still completes. **Covers AC4.**
  (c) **unreadable reference:** a supplied `triageDocumentId` that resolves to nothing → the loud
  failure edge, `REFACTOR.PLAN.FAILED` with a typed detail, `status = escalated`, **no** `Finish`
  reached. **Covers AC4 ("fails loud").**
  (d) **AC5:** the accepted `Plan` is retrievable by `issueId`/`repository` through the 39-11
  repository read, and a coding-step dispatch input is constructed from it — assert the **read and
  the dispatch input shape**, not execution (Correction 6).
  (e) **AC3 end-to-end:** a draft whose steps name no characterization test is rejected by VALIDATE
  with `STEP_MISSING_CHARACTERIZATION_TEST` and drives a repair/revise round, then a corrected draft
  is accepted. This is the proof the rule is reachable through the lifecycle, not just callable.
  (f) **re-entry:** crash after acceptance → fresh `refactor-plan` dispatch for the same scoped issue
  re-enters at `Complete`; exactly one `DOCUMENT.ACCEPTED` and one `REFACTOR.PLAN.ACCEPTED` on the
  stream; and the sibling `plan-generation` document for the same base issue is **untouched** (the D2
  scoping proof).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding, one `Bindings` entry, `PlanDocumentType.Validate` authority | 6, 7 (D1/D5) | `RefactorPlanWorkflowStructureTests`; full `ContractBindingTests` fixture |
| 2 — one fixture per `Plan` rule, asserting codes | 10 | `PlanDocumentType` AC2 fixture sweep |
| 3 — behavior preservation as structure | 2, 3, 5, 6 (D3) | `PlanDocumentTypeCharacterizationTests`; execution scenario (e) |
| 4 — consumed `documentId` recorded, unreadable id fails loud | 5, 6 (D4) | `RefactorBindingHelperTests`; execution scenarios (a)(b)(c) |
| 5 — accepted `Plan` retrievable via 39-11, read by a coding-step dispatch | 6 | execution scenario (d) |
| 6 — resume declared, 39-10 green without allowlist, edge pin bumped | 6, 8 (D6) | `ResumableStandardStructuralTests`; `WorkflowInterfaceGraphTests` at 17 |

## Risks & Mitigations

- **`ValidateWithContext` is a shared-type edit and the other two `plan` producers are live (D3).**
  The single biggest risk in this story. Mitigation: the empty-context branch is **byte-identical to
  `Validate`** and test (i) asserts exactly that against a `plan-generation`-shaped and a
  `task-creation`-shaped fixture; neither sibling binding passes a `validationContextJson`
  (verified — only `TestCaseCreationWorkflow` does, at `:148`).
- **The shared `Plan.Contract` sentence (step 2) reaches three prompt templates.** Mitigation: word
  it as guidance, not as a new required field; the two sibling `ContractBindingTests` entries
  (`:160`, `:172`) must stay green unchanged — run them in the same commit.
- **AC3 can degenerate into a keyword grep.** A validator that accepts any string containing "Test"
  proves nothing. Mitigation: the rule matches against a **supplied inventory** first; the shape-only
  degrade is explicitly a documented fallback (Correction 5) named in the test, so nobody reads it as
  the primary path.
- **The `Plan` type now has a producer-specific rule, inviting more.** Mitigation: the rule is
  context-gated, so it is opt-in per producer by construction; record the pattern in
  `.dev/findings/` so the next story reaches for the same seam rather than forking the type.
- **Edge-pin collision with sibling Epic 41 stories.** `WorkflowInterfaceGraphTests.cs:45` is bumped
  by every producing-workflow story in the epic. Mitigation: rebase-and-bump-last; the comment names
  the story so the history stays readable.
- **Story-vs-code tensions:** D6 (resume mode `LatestStateReEntry`, not the story's `Both`) and D8
  (the API-affecting escalation class is not expressible as a payload predicate) both deviate from
  story text with reasons recorded. D8 is a real, un-closable gap in this story's scope and is stated
  as such rather than claimed.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check | 0.1 |
| 2 | `PlanDocumentType.ValidateWithContext` + constant + `Contract` sentence | 0.5 |
| 3 | `plan-refactor.md` rewrite onto the canonical `Plan` wire | 0.25 |
| 4–5 | `RefactorEvents` + `RefactorBindingHelper` | 0.4 |
| 6 | `RefactorPlanWorkflow` binding | 0.75 |
| 7–9 | Contract entry + registry row + edge-pin bump + drift-guard | 0.25 |
| 10 | `Tamma.Core.Tests` (characterization + AC2 sweep) | 0.5 |
| 10 | Structure + helper tests | 0.4 |
| 10 | Testcontainers scenarios (a)–(f) | 0.75 |
| **Total** | | **3.9** (story estimate: 3–4 days — confirmed) |

## Blocks / Blocked by

- **Blocked by:** Epic 39 only — `Plan` type + `PlanDocumentType` (39-4), `document-lifecycle` +
  `validationContextJson` forwarding (39-6/39-15), `document-review`/`review-panel` (39-7), the
  accept gate (39-8), the resume standard (39-10), the document store + lineage API (39-11). **All
  landed and verified in tree.** This story needs **nothing from 41-1a, 41-1b or 41-1c** — the cell,
  the type and the prompt file all exist — so it is one of the few Wave-3 stories that could in
  principle be pulled forward.
- **NOT blocked by Epic 40 for its own deliverable.** Epic 40 (and the missing
  `.github/workflows/tamma-agent.yml`) gates only what happens *after* an accepted plan is worked.
  AC5 is scoped to the read + dispatch-input shape (Correction 6), which is testable today.
- **Blocked by (soft, for the intended trigger):** **41-11** (Tech-Debt & Technical-Risk Triage)
  produces the `TriageDecision` this workflow is designed to consume. 41-11 is itself blocked on
  41-1a's `triage-tech-debt` cell **and** the scheduled-trigger seam (story 41-30). This story does **not**
  inherit either blocker — scenario (b) proves the `Review`-triggered and direct-dispatch paths work
  with no 41-11 document present, and scenario (a) seeds the triage document directly into the store.
- **Blocks:** nothing hard. Its accepted `Plan` is a consumer of **41-11** and a producer for the
  Epic 40 coding step.
- **Shared-file register (coordinate before editing):** `Tamma.Core/Documents/Types/Plan.cs`
  (also touched by **41-29** Phase 1, which adds `TaskKind` + `TASK_KIND_OUT_OF_VOCABULARY` and edits
  the same `Contract` const — these two stories must not land the `Contract` edit blind of each
  other); `ContractBindingTests.cs`; `DocumentTypeRegistry.BuildSeed` +
  `WorkflowInterfaceGraphTests.cs:45`; `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`.
