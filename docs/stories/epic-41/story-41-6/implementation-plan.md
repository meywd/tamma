# Implementation Plan — Story 41-6: Sprint Planning Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `SprintPlanningWorkflow` (DefinitionId `sprint-planning`) is a
**thin binding** over `document-lifecycle` in the landed producer shape: it reads the accepted
`BacklogOrdering` (41-3) plus stated capacity and prior-sprint carry-over, dispatches `document-lifecycle`
with `documentType = "sprint-plan"` and the producer cell `(scrum_master, plan-sprint)`, routes the typed
exit, and exposes the accepted commitment. Zero `Finish`, zero `llm-call` dispatch, zero validate/retry
plumbing, exactly one `DispatchWorkflow` targeting `document-lifecycle`.

Alongside the binding: a `SPRINT.*` DCB event family; the sprint lineage anchor (shared normalisation with
41-3/41-4/41-5); a **typed loud exit** when no accepted `BacklogOrdering` exists (AC2's "hard-fails loud"
reconciled with rule 1's zero-`Finish` requirement); the `WorkflowDocumentInterface` edge + its three pin
edits; the `ContractBindingTests` `Bindings` entry; and the structure/execution suites. The `SprintPlan`
**type is 41-1b's** and the `scrum_master` role + `plan-sprint` cell are **41-1a's** — neither is this
story's.

## Pre-Reading

- `docs/stories/epic-41/story-41-6/41-6-sprint-planning.md` — the story (ACs are source of truth, less the Corrections below)
- `docs/stories/epic-41/README.md` — rules 1–5; the Dependencies table row naming **41-6:45** as an AC that fails at the AC level today
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — mints `AgentRole.ScrumMaster` + `(scrum_master, plan-sprint)`; its **D2** (scrum_master on *neither* review panel) and **D3** (`LegacyRoleAliases` removal polarity) are load-bearing here
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — the `SprintPlan` type + its **D1** (acceptance posture: "plausibly wrong for at least `SprintPlan` — a scrum_master/product_owner acceptor")
- `docs/stories/epic-41/story-41-3/implementation-plan.md` — the upstream producer; **its D2 anchor helper is a shared contract this story consumes, not re-derives**
- `docs/stories/epic-41/story-41-2/implementation-plan.md` — D7's shared `EmitDomainLifecycleEventActivity`; the `[ResumeBehavior]` correction; the rule-1 clause (f) two-edit lockstep
- **THE RECIPE:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`; `PlanGenerationWorkflow.cs` for the consumes-an-accepted-document variant
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs` — **fail-closed by design**: absent upstream ⇒ `Found=false`, it never throws. This is why D4 exists.
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:230-242` — `LegacyRoleAliases`, incl. `scrum_master → product_owner` (`:239`), the entry 41-1a removes; `:376-387` — `GetReviewActionForRole`, 7 arms, **throws for any role not listed**
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:128-133` — the `_ => Rules` catch-all a new type silently falls through
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:445-447` — `ITaskAudienceResolver` is stubbed fail-closed by `InitiatorOnlyTaskAudienceResolver`. **This is why AC3 cannot be claimed** (Correction 3).
- **The gates this story must move:** `WorkflowInterfaceGraphTests.cs:45` + the `reconciled` array `:102-123`; `ContractBindingTests.cs:82`; `TaxonomyDriftBuildTests.cs:125`, `:460`; `ResumableStandardStructuralTests.cs:108/:159/:238/:266`

## Corrections to the story

1. **AC4's `[ResumeBehavior(Both)]` is wrong and would fail the build.** As in 41-2/41-3/41-4/41-5: `Both`
   requires a canonical suspend node from `LifecycleBookmarks.CanonicalSuspendActivities` in the binding's
   **own** graph (`ResumableStandardStructuralTests.cs:159` + the inverse honesty check at `:205`); a thin
   binding owns none, because the accept gate suspends inside the dispatched child. **Declare
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.**
2. **AC2's "hard-fails loud if none exists" collides with rule 1 clause (c) (zero `Finish`) and with the
   read seam's fail-closed contract.** `FetchLatestAcceptedDocumentActivity` **never throws** — a missing
   upstream document yields `Found=false`, `DocumentJson="{}"`. So "hard-fail" cannot be an exception, and
   it cannot be a `Finish` terminal (the structure test forbids one). D4 resolves it as a **typed loud
   exit**: a `FlowDecision` on the fail-closed `Found` flag routing to a `SPRINT.PLANNING.FAILED` emission
   plus the single `ExposeOutput` region with `status="failed"` and a named detail — loud in the DCB stream
   and in the outputs, structurally identical to every other non-accept exit. This is an *additional*
   `FlowDecision` beyond the `{FreshRun, LifecycleAccepted}` pair the siblings pin; it routes a typed value,
   never raw LLM output, which is exactly what 39-12 D2 sanctions.
3. **AC3 ("Committed items produce role-scoped Task View entries via 39-20") is NOT achievable and must not
   be claimed.** `ITaskAudienceResolver` is registered as `InitiatorOnlyTaskAudienceResolver`
   (`Tamma.Api/Program.cs:445-447`) — a fail-closed stub that admits only the issue initiator — and 39-19
   (the Task View itself) has not landed either (`AgentOfflineChatRelay` is the registered
   `IOrchestratorChatRelay`, `:448-451`, and refuses every message). The epic README names **41-6:45**
   explicitly as one of three ACs that "fail at the AC level, not merely in prose". **This plan does not
   plan AC3.** What it *does* deliver is the precondition: the accepted `SprintPlan` payload carries an
   `ownerRole` per committed item (41-1b's domain rule) and the acceptance publishes the standard
   `AcceptanceRequest` — so when 39-19/39-20 land, routing is a consumer of this document, not an edit to
   this binding.
4. **A `SprintPlan` is not issue-scoped, so it needs its own lineage anchor.** `DocumentInstance.IssueId` is
   a required non-null string (`:37`) and the only store read key (`IDocumentInstanceRepository.cs:40-50`).
   D2 defines `sprint:{repository}:{sprintKey}`. The upstream `BacklogOrdering` is likewise read through
   **41-3's** `BacklogBindingHelper.BuildAnchor(repository, backlogScope)` — called, never re-derived.
5. **The reviewer cannot be a `scrum_master`.** 41-1a **D2**'s default position is that `scrum_master` and
   `project_manager` join **neither** panel ("they produce and accept, they do not critique documents"), and
   `RolePhaseMap.GetReviewActionForRole` throws for any unlisted role (`:376-387`), called unguarded at
   `DocumentLifecycleWorkflow.cs:1199`. D6 pins a **`product_owner`** reviewer, which already resolves
   (`ProductOwner => ReviewScope`) and is the right lens for a commitment. Recorded because "the scrum
   master's plan is reviewed by the scrum master" is the intuitive-but-broken default.
6. **The `scrum_master` alias removal is a behaviour change this story inherits.** `LegacyRoleAliases`
   currently maps `scrum_master → product_owner` (`RolePhaseMap.cs:239`), so **today**
   `AgentRoleExtensions.Parse("scrum_master")` returns `ProductOwner`. 41-1a AC1/AC5/D3 own the removal and
   its data migration; this story must not ship against the alias (a binding that resolved
   `scrum_master` to `product_owner` would silently dispatch a PO cell). Step 1 gates on 41-1a's AC1 being
   true.
7. **Rule-1 clause (f) is a two-edit lockstep and the epic README names only one.** Besides
   `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)`, the same file's
   `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:96`) asserts bidirectionally against
   the hardcoded `reconciled` array (`:102-123`).
8. **The story's "the accepted plan seeds Task View assignments per committed item's owner-role" and
   "Over-commit beyond capacity is a validator rejection" split cleanly.** The second **is** achievable —
   it is 41-1b's `SprintPlanDocumentType.Validate` domain rule and this story proves it end-to-end. The
   first is Correction 3's unachievable half. They are stated together in the story; they are not equally
   deliverable.

## Design Decisions

- **D1 — New DefinitionId `sprint-planning`; produce cell `(scrum_master, plan-sprint)` from 41-1a.**
  Greenfield: repo-wide grep for `plan-sprint` / `PlanSprint` returns **zero** hits in `.cs` and `.md`, and
  `project_manager`/`scrum_master` are not `AgentRole` members (the enum has exactly 8). So the `Bindings`
  entry is purely additive and no `IntentionallyUnbound` entry moves. Inputs: `repository`, `tenantId`,
  `sprintKey` (e.g. `2026-S14`), `backlogScope` (to locate the upstream ordering), `capacityJson` (stated
  capacity per owner-role), `priorSprintKey?` (carry-over source), `acceptanceRulesJson?`. Outputs:
  `status`, `outcome`, `documentId`, `sprintPlanJson`, `sprintAnchor`.
- **D2 — Lineage anchor `sprint:{repository}:{sprintKey}`, shared normalisation.** Correction 4.
  Deterministic, so 39-10 re-entry, the carry-over read and any consumer (41-5's `SprintPlan` read)
  recompute it from inputs alone. Written into the existing required `IssueId` column — no schema change.
  Implemented in `SprintBindingHelper.BuildAnchor` delegating to the same segment transform 41-3's helper
  uses, and unit-asserted against it.
- **D3 — Two upstream reads, both behind `FreshRun`, with opposite failure postures.** (i) the accepted
  `BacklogOrdering` under **41-3's** anchor — **required** (D4's loud exit when absent); (ii) the prior
  sprint's accepted `SprintPlan` under `BuildAnchor(repository, priorSprintKey)` — **optional** (absent ⇒
  no carry-over, which is the correct semantics for sprint 1). Both are
  `FetchLatestAcceptedDocumentActivity`; the difference is entirely in the routing, not in the read.
- **D4 — The missing-prerequisite exit is a typed loud exit, not a `Finish` and not an exception.**
  Correction 2. A third `FlowDecision` (`BacklogAvailable`) on the fail-closed `Found` flag: `False` →
  `EmitSprintPlanningFailed` (detail `missing-backlog-ordering`, data naming the anchor that was searched)
  → `ExposeOutput` with `status="failed"`, `outcome="missing-prerequisite"`. `True` → `DispatchLifecycle`.
  Loud in the DCB stream, loud in the outputs, structurally a typed exit like every other. The pinned
  `FlowDecision` id set for this workflow is therefore
  `{FreshRun, BacklogAvailable, LifecycleAccepted}` — one more than the siblings, justified here per the
  epic's rule "any story that cannot meet (a)–(f) must name the deviation" (this does not violate (a)–(f);
  it is a documented extra typed gate, the 39-12 D2 allowance).
- **D5 — `feedbackVariableName` is a DECLARED carrier on the new cell.** 41-1a mints
  `Prompts/scrum_master/plan-sprint.md`; this plan specifies its front matter as
  `variables: role, backlogOrderingJson, capacityJson, carryOverJson, revisionNotes` / `enableTools: false`
  / `maxTokens: 8192` / `version: 1`, and the dispatch sets `["feedbackVariableName"] = "revisionNotes"` —
  which is also `DocumentLifecycleHelper.DefaultFeedbackVariable` (`:32`), so the carrier is the canonical
  one rather than a repurposed content variable. An undeclared producer variable is silently dropped at
  render (the 39-15 render-drop lesson). **Lockstep with 41-1a:** the file is created *there*, its contents
  are specified *here*. 41-1a must also create `Prompts/scrum_master/_system.md` and a
  `context-scan.md` (every role's eligible set includes `ContextScan`), or `PromptFileLoader` refuses to
  start (`PROMPT.SEED.NO_BODY_FAMILY`).
- **D6 — Reviewer pinned to `product_owner`; acceptor per autonomy.** Correction 5. 41-1b **AC5/D1** owns
  writing the `AcceptanceDefaults.For(DocumentTypeKey.SprintPlan)` arm; this plan **states the required
  row** — `ReviewerSelection` single reviewer `product_owner`, acceptor per the autonomy dial, with the
  story's "commitment beyond a configured capacity band always escalates" expressed as a per-document-type
  always-escalate class in the resolved rules, **not** as a branch in the binding (rule 3). The execution
  test asserts the row, so a silent `_ => Rules` fall-through (single-`architect` unanimous — wrong for a
  sprint commitment) fails here as well as in 41-1b.
- **D7 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` + `ComputeReEntryPositionActivity` keyed on the
  D2 anchor, no allowlist entry.** Correction 1. The position gates both upstream reads and the
  `SPRINT.PLANNING.STARTED` emission.
- **D8 — The `SPRINT.*` family rides 41-2's shared `EmitDomainLifecycleEventActivity`.** This story ships
  only `SprintEvents.cs`. Note the story names a `.CLOSED` transition alongside `.ACCEPTED`; **sprint
  closure is not this workflow's** (it happens at the end of the time-box, not at planning time), so the
  constant is defined but emitted by nothing here, and that is recorded in the file's comment rather than
  faked with a spurious emission.
- **D9 — Capacity is data, not code.** `capacityJson` is an input, the ≤-capacity rule lives in
  `SprintPlanDocumentType.Validate` (41-1b), and the binding neither computes nor enforces it. This keeps
  the "over-commit is a validator rejection, not an accept-time surprise" promise (story, Orchestrator
  section) structural rather than aspirational — and it is why Correction 8's second half *is*
  deliverable.

## Implementation Steps

1. **Precondition check (no code). Two hard gates.** (i) **41-1a**: `AgentRoleExtensions.Parse("scrum_master")`
   returns `AgentRole.ScrumMaster` — **not** `ProductOwner` (Correction 6 — the alias at
   `RolePhaseMap.cs:239` must be gone); `AgentAction.PlanSprint` exists and is eligible for `ScrumMaster` in
   `RolePhaseMap.EligibleActions`; `Prompts/scrum_master/_system.md`, `context-scan.md` and `plan-sprint.md`
   all exist (D5's front matter) or `PromptFileLoader` refuses to start; and 41-1a **D2**'s panel decision
   for `scrum_master` is recorded (D6 assumes "neither panel"). (ii) **41-1b**: `Parse("sprint-plan")`
   succeeds, `Resolve("sprint-plan")` returns `SprintPlanDocumentType`, its `Contract` const is final, and
   `AcceptanceDefaults.For` carries D6's arm. Also confirm **41-3** has landed (its `BuildAnchor` is D3's
   input) and 41-2's shared emitter is in tree (else D8's fallback). Any gap blocks steps 4–7 — file it, do
   not work around it.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/SprintEvents.cs`** — `SPRINT.PLANNING.STARTED` /
   `.PLANNED` / `.ACCEPTED` / `.FAILED` / `.CLOSED` (the last defined-not-emitted, D8). Tags `repository` /
   `tenantId` / `correlationId` (= the D2 anchor) / `sprintKey`.

3. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/SprintBindingHelper.cs`** — pure,
   Elsa-free, total, fail-closed: `BuildAnchor(repository, sprintKey)` (D2, shared normalisation),
   `BuildProducerVariables(backlogOrderingJson, capacityJson, carryOverJson)` (D5's carrier set),
   `ProjectCommittedItems(documentJson)` (the accepted `committed` array raw text, `"[]"` on unreadable),
   `BuildMissingPrerequisiteDetail(searchedAnchor)` (D4's loud detail). Reuse
   `LifecycleBindingHelper.ReadLifecycleResult`/`IsAccepted` and `CreationBindingHelper.BuildFailureDetail`
   verbatim; call — do not copy — `BacklogBindingHelper.BuildAnchor` for the upstream read key.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SprintPlanningWorkflow.cs`** (D1/D2/D3/D4/D7) —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. Graph: `ReadInputs` → `ComputeReEntryPosition` →
   `ReadPositionStage` → `FreshRun` → `EmitSprintPlanningStarted` → `FetchBacklogOrdering` →
   `FetchPriorSprintPlan` → `BacklogAvailable` `FlowDecision` → (`False` → `EmitSprintPlanningFailed` →
   `ExposeOutput`) | (`True` → `DispatchLifecycle` → `ReadLifecycleExit` → `LifecycleAccepted` →
   `EmitPlanned`/`EmitAccepted` | `EmitFailed` → `ExposeOutput`). Single terminal region; **no `Finish`**.
   Dispatch input:

   ```csharp
   ["documentType"]          = "sprint-plan",
   ["producerRole"]          = AgentRole.ScrumMaster.ToWire(),   // 41-1a
   ["producerAction"]        = AgentAction.PlanSprint.ToWire(),  // 41-1a
   ["producerVariablesJson"] = /* { backlogOrderingJson, capacityJson, carryOverJson } */,
   ["feedbackVariableName"]  = "revisionNotes",                  // D5 — the canonical declared carrier
   ["issueId"] = anchor, ["correlationId"] = anchor,             // D2
   ["tenantId"] / ["acceptanceRulesJson"]                        // D6's default rules
   ```

   `WaitForCompletion = new(true)`. `FlowDecision` id set exactly
   `{FreshRun, BacklogAvailable, LifecycleAccepted}` (D4's documented deviation).

5. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed`) — add
   `("sprint-planning", [BacklogOrdering, SprintPlan], SprintPlan, false)` (it consumes its own type as
   carry-over — legal, and `WorkflowInterfaceGraphTests` constrains only that produced keys are registered
   or pending).
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs`** — bump `:45`
   by one with the reason in the comment, **and** add `"sprint-planning"` to the `reconciled` array
   `:102-123` (Correction 7).

6. **MODIFY the drift gates.** `ContractBindingTests.cs`: add
   `[("scrum_master","plan-sprint")] = new("SprintPlanDocumentType.Validate", [ … 41-1b token groups … ])`
   with a comment naming `Tamma.Core/Documents/Types/SprintPlan.cs` as the shape authority.
   `TaxonomyDriftBuildTests.cs`: add `"SprintPlanningWorkflow"` to `ExpectedContributingWorkflows` (`:125`).
   Verify (do not pre-edit) `MinExpectedDispatchPairs` (`:110`) and
   `EveryConcreteWorkflow_IsIntrospectableOrAllowListed` (`:397`).

7. **CREATE the test suites** — `SprintPlanningWorkflowStructureTests.cs`, `SprintBindingHelperTests.cs`,
   `SprintPlanningLifecycleExecutionTests.cs`. See Test Plan.

8. **Full run.** `dotnet test` green; `dotnet ef migrations has-pending-model-changes` clean (this story
   adds no schema).

## Data & Migrations

None. `SprintPlan` rows are `document_instances` (39-11's table, 41-1b's registration); `SPRINT.*` and
`DOCUMENT.*` ride the existing emitter → drain → `domain_events` path. `has-pending-model-changes` stays
clean. **Note:** 41-1a's `scrum_master` alias removal (its D3) may carry a one-shot data migration for
stored agent configs keyed `scrum_master` — that is 41-1a's, not this story's, but it must have run before
this workflow dispatches (Correction 6).

## Events

- **Emits (new constants, this story):** `SPRINT.PLANNING.STARTED` (fresh runs only, data `sprintKey`),
  `.PLANNED` (data `committedCount`, `carryOverCount`, `consumedOrderingId`), `.ACCEPTED` (data
  `documentId`), `.FAILED` — emitted on **two** distinct paths: the D4 missing-prerequisite exit (detail
  `missing-backlog-ordering`, data `searchedAnchor`) and a `rejected`/`escalated` lifecycle exit (detail
  names the typed outcome wire). `.CLOSED` is defined but not emitted here (D8). Tags `repository` /
  `tenantId` / `correlationId` (= the D2 anchor) / `sprintKey`.
- **Emitted by the machinery this binding wires in:** the `DOCUMENT.*` family, `APPROVAL.*`,
  `ESCALATION.TRIGGERED`.
- **Consumes:** none at runtime (both upstream documents are store reads, not event reads).

## Test Plan

NUnit + FluentAssertions; Testcontainers for the execution suite (the shared 39-6/39-10 fixture).

- **`SprintPlanningWorkflowStructureTests`** — the rule-1 clause (a)–(f) set, `TaskCreation`-shaped: builds;
  DefinitionId `sprint-planning`; threads `TenantId`; **zero** `Finish` (the D4 loud exit is a typed route,
  not a terminal — this is the pin that proves Correction 2's reconciliation); **exactly one**
  `DispatchWorkflow`, literal id `document-lifecycle`; **zero** targeting `llm-call`; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables; `ScanLifecycleBindingDispatches()`
  contains `(scrum_master, plan-sprint)` attributed to this workflow; `MaterializeDispatchInput` yields
  `documentType == "sprint-plan"` and `feedbackVariableName == "revisionNotes"`; one
  `ComputeReEntryPositionActivity` and **two** `FetchLatestAcceptedDocumentActivity` nodes (D3);
  `FlowDecision` id set exactly `{FreshRun, BacklogAvailable, LifecycleAccepted}` (D4's documented
  deviation, pinned so a fourth gate cannot appear unnoticed); `[ResumeBehavior(LatestStateReEntry)]`; **no
  `Wait*` activity** (Correction 1); **every graph leaf is inside the single `ExposeOutput` region** (the
  no-dead-end proof, both exits included). **Covers AC1, AC4.**
- **`SprintBindingHelperTests`** — `BuildAnchor` determinism, folding, hostile-character normalisation, and
  **agreement with `BacklogBindingHelper.BuildAnchor`'s transform**; `BuildProducerVariables` with
  ordering/capacity/carry-over present and absent; `ProjectCommittedItems` on a valid body and on unreadable
  JSON (`"[]"`, never throws); `BuildMissingPrerequisiteDetail` names the searched anchor;
  `BuildFailureDetail` names each reachable outcome wire. **Covers AC2 (detail half).**
- **`SprintPlanningLifecycleExecutionTests`** (Testcontainers) —
  (a) **happy path:** a seeded accepted `BacklogOrdering` under 41-3's anchor + a capacity input → scripted
  valid draft → review approve → `Accept` resume → `status=completed`, committed items projected; store
  asserts the accepted `SprintPlan` + its `Review` rows; replay asserts both event families and that
  `SPRINT.PLANNING.PLANNED` carries `committedCount`. **Covers AC1.**
  (b) **capacity rejection (Correction 8's deliverable half, D9):** a draft whose committed estimates exceed
  the stated capacity is rejected by `SprintPlanDocumentType.Validate` with the **named** violation code,
  loops repair/revise (notes arriving through `revisionNotes` — D5), and accepts on the second round. This
  is the "over-commit is a validator rejection, not an accept-time surprise" promise, proven. **Covers AC1.**
  (c) **missing prerequisite (AC2, D4):** no accepted `BacklogOrdering` for the anchor → **no lifecycle
  dispatch at all**, `SPRINT.PLANNING.FAILED` emitted with detail `missing-backlog-ordering` and the
  searched anchor, outputs `status="failed"`/`outcome="missing-prerequisite"`, and **no `SprintPlan` row
  written**. Asserted loudly on all three surfaces (events, outputs, store). **Covers AC2.**
  (d) **carry-over:** a seeded prior `SprintPlan` under `BuildAnchor(repository, priorSprintKey)` is read
  and reaches the producer variables; absent prior sprint ⇒ empty carry-over and a successful plan (D3's
  optional posture).
  (e) **validation exhaustion:** always-over-capacity stub → typed `ValidationExhausted` escalation with
  lineage, `SPRINT.PLANNING.FAILED` naming the outcome, `status=escalated`, no error terminal.
  (f) **re-entry:** crash after acceptance → short-circuits with the SAME `documentId`, exactly one
  `DOCUMENT.ACCEPTED` and one `SPRINT.PLANNING.ACCEPTED`, zero extra upstream reads; crash mid-review →
  resumes at review of the same revision. **Covers AC4.**
  (g) **acceptance posture (D6):** `AcceptanceDefaults.For(DocumentTypeKey.SprintPlan)` returns the
  documented row, and a run with a `product_owner` reviewer completes its review stage; **control case**:
  configuring a `scrum_master` reviewer throws at `RolePhaseMap.GetReviewActionForRole`, pinning Correction
  5 / 41-1a D2 so the "neither panel" decision is asserted rather than assumed.
  (h) **41-5 consumer read:** after (a), `FetchLatestAcceptedDocumentActivity` for
  `(BuildAnchor(repository, sprintKey), "sprint-plan")` returns the accepted body — the seam 41-5 uses,
  proving the D2 anchor is recomputable from inputs alone.
- **AC3 is NOT tested** — Correction 3. `InitiatorOnlyTaskAudienceResolver` admits only the initiator, so
  there is nothing to assert. A single explicit test documents the gap: the accepted payload carries an
  `ownerRole` per committed item (the precondition 39-20 will consume) and the resolver is the stub.
- **Drift gates (self-verifying, steps 5/6)** — `ContractBindingTests`, `TaxonomyDriftBuildTests`,
  `WorkflowInterfaceGraphTests` (count **and** `reconciled`) and `ResumableStandardStructuralTests`
  (declares, **no** allowlist entry) green in the same commit.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `SprintPlan` validated (capacity, owner+estimate, carry-over) | 4, 6 (D1/D9) | StructureTests clause (a)–(f); ExecutionTests (a)(b)(d) |
| 2 — consumes accepted `BacklogOrdering`; hard-fails loud if none | 3, 4 (D3/D4) | ExecutionTests (c) on events + outputs + store; HelperTests detail |
| 3 — role-scoped Task View entries via 39-20 | **NOT CLAIMABLE** | **Correction 3.** `ITaskAudienceResolver` is the fail-closed `InitiatorOnlyTaskAudienceResolver` stub (`Program.cs:445-447`) and 39-19 has not landed. The precondition (per-item `ownerRole` in the accepted payload) is delivered and asserted; the routing is not. |
| 4 — resumable per the standard, no allowlist entry | 4 (D7) | StructureTests declaration + no-`Wait*`; ExecutionTests (f); `ResumableStandardStructuralTests` |

## Dependencies & Sequencing

- **Blocked by (hard):**
  - **41-1a** — `AgentRole.ScrumMaster`, `AgentAction.PlanSprint`, `Prompts/scrum_master/{_system,
    context-scan, plan-sprint}.md`, **and the `scrum_master → product_owner` alias removal**
    (`RolePhaseMap.cs:239`). Verified: `plan-sprint`/`PlanSprint` has **zero** repo-wide hits and the role
    enum has exactly 8 members. Without it the produce cell does not exist on the **human path either** — a
    human assignee still needs a cell to bind.
  - **41-1b** — `DocumentTypeKey.SprintPlan` + `SprintPlanDocumentType` + the `AcceptanceDefaults` arm.
    Verified: the vocabulary has exactly 10 members and `sprint-plan` is not one, so the document is
    unparsable (`DOCUMENT.TYPE.UNKNOWN`) and unpersistable.
  - **41-3** — the consumed accepted `BacklogOrdering` and its `BuildAnchor` helper (a shared string
    contract, not a copy).
  - **Epic 39** — 39-6/39-7/39-8/39-10/39-11 (all landed and verified in tree).
- **Soft-blocked by 41-2** — D8's shared emitter only.
- **Blocks:** **41-5** (its `consumes: SprintPlan`, read through this story's D2 anchor — optional there, so
  soft); **41-7** and **41-8** (both named `Related:` in the story; both read the sprint's commitment as the
  baseline their digest/retro is measured against).
- **Not achievable without:** **39-19** + **39-20** for AC3 (Correction 3). Both are fail-closed stubs;
  neither is in this epic.
- **Lockstep:** 41-1a's `plan-sprint.md` front matter ↔ D5's variable set (specified here, created there);
  41-1a's D2 panel decision ↔ D6/test (g); 41-1b's `SprintPlan` `Contract` const ↔ step 6's `Bindings`
  token groups; 41-1b's `AcceptanceDefaults` arm ↔ D6/test (g); 41-3's `BuildAnchor` ↔ D3.
- **Sequencing within the story:** 1 → 2/3 (parallel) → 4 → 5/6 (parallel) → 7 → 8.

## Risks & Mitigations

- **This is the most-blocked story in the batch: 41-1a AND 41-1b AND 41-3, all three hard.** Mitigation: it
  is Wave 3 in the epic's own ordering, so the enablers should be long landed; steps 2–3 and the helper
  tests are enabler-independent; step 1 is a real gate, not a formality.
- **The `scrum_master` alias removal is a live behaviour change (Correction 6).** If this story ships while
  the alias survives, `AgentRole.Parse("scrum_master")` silently returns `ProductOwner` and the binding
  dispatches a **PO** cell with no error anywhere. Mitigation: step 1 gates on 41-1a AC1 explicitly; the
  structure test asserts the materialised pair is `(scrum_master, plan-sprint)` via
  `ScanLifecycleBindingDispatches`, which reads the compiled `ToWire()` values — an aliased resolution would
  not change those, so the *execution* test (a) is the real guard and asserts the dispatched producer role
  on the child instance.
- **D4's extra `FlowDecision` invites re-litigation of "thin".** Mitigation: it routes a typed value
  (39-12 D2's explicit allowance), the id set is pinned so a fourth gate fails the build, and the deviation
  is named here per the epic's own rule.
- **AC3 will read as delivered by anyone skimming.** Mitigation: it is marked NOT CLAIMABLE in the DoD
  table, excluded from the test plan with a documenting test, and the epic README already names 41-6:45 as
  an AC-level failure.
- **Anchor drift across four helpers** (41-3/41-4/41-5/41-6). Mitigation: one shared normalisation;
  `SprintBindingHelperTests` asserts agreement with `BacklogBindingHelper` directly.
- **Story-vs-code tensions:** Corrections 1–8 all resolve in favour of the code. Correction 3 removes an AC;
  Corrections 2 and 5 change the design; 4 and 6 add constraints; 1, 7 and 8 are mechanical or
  clarifications.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1a/41-1b/41-3 precondition verification + front-matter and contract agreement | 0.5 |
| 2 | `SprintEvents` (+ 41-2 emitter reuse or local copy) | 0.25 |
| 3 | `SprintBindingHelper` (anchor, carrier composer, projections, loud detail) | 0.75 |
| 4 | The binding workflow incl. the D4 typed loud exit | 1.25 |
| 5 | Registry seed row + the two `WorkflowInterfaceGraphTests` edits | 0.25 |
| 6 | `ContractBindingTests` + `TaxonomyDriftBuildTests` edits | 0.5 |
| 7 | Structure + helper + Testcontainers suites (a)–(h) + the AC3-gap documenting test | 1.5 |
| 8 | Full-suite green, review polish | 0.25 |
| **Total** | | **5.25** |

**Est. Effort: 5.25 days.** The story file says 4–5 days, which is close: the extra ~0.25–1 d is the D4 loud
exit (a route the siblings do not have, +0.5 d) and the larger execution matrix (two upstream reads with
opposite failure postures, plus the panel-throw control case). Note the estimate **excludes AC3**, which is
not achievable (Correction 3) — if 39-19/39-20 landed, routing the accepted commitment would be additional
work not costed here. The story's `## Estimated Effort` section is left at 4–5 days and this plan is the
record.

## Blocks / Blocked by

- **Blocked by (hard):** 41-1a (`scrum_master` role + `plan-sprint` cell + its prompt files + the alias
  removal); 41-1b (the `SprintPlan` document type + its acceptance row); 41-3 (the consumed
  `BacklogOrdering` + its anchor helper); Epic 39 stories 39-6, 39-7, 39-8, 39-10, 39-11 (all landed).
- **Blocked by (soft):** 41-2 (shared emitter).
- **Blocks:** 41-5 (consumes the accepted `SprintPlan` — soft, optional there); 41-7 and 41-8 (both read the
  sprint commitment as their baseline).
- **AC3 additionally requires:** 39-19 (Task View) and 39-20 (teams/roles/repo access + task routing) —
  both fail-closed stubs in tree, neither in this epic. AC3 is excluded from this plan's scope.
- **Not blocked by:** 41-1c (this is a typed document, not prose); the tenant-aware scheduled-trigger seam
  (sprint planning is time-box-triggered by a human or an API call, not a cron sweep — unlike 41-5/41-7).
