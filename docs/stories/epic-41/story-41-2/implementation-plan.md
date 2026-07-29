# Implementation Plan — Story 41-2: Acceptance-Criteria Authoring Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `AcceptanceCriteriaAuthoringWorkflow` (DefinitionId
`acceptance-criteria-authoring`) is a **thin binding** over `document-lifecycle` in exactly the shape the
landed producers already ship (`TaskCreationWorkflow` is the reference): it reads the issue context and the
latest accepted `clarification` / `findings` for the issue, dispatches `document-lifecycle` with
`documentType = "acceptance-criteria"` and the producer cell `(product_owner, define-acceptance-criteria)`,
routes the typed lifecycle exit, and exposes outputs. Zero `Finish`, zero `llm-call` dispatch, zero
validate/retry plumbing variables, exactly one `DispatchWorkflow` whose literal definition id is
`document-lifecycle`.

Alongside the binding this story ships: the **rewritten** `define-acceptance-criteria` prompt template (the
shipped one produces a task breakdown, not acceptance criteria — see Corrections); a new
`ACCEPTANCE_CRITERIA.*` DCB event family; a **shared** `EmitDomainLifecycleEventActivity` that 41-3/41-4/
41-5/41-6 reuse; the `WorkflowDocumentInterface` edge + its three pin edits; the `ContractBindingTests`
`Bindings` entry; and the structure/execution test suites. The `AcceptanceCriteria` **type itself is not
this story's** — 41-1b owns it (see Blocked by).

## Pre-Reading

- `docs/stories/epic-41/story-41-2/41-2-acceptance-criteria-authoring.md` — the story (ACs are source of truth, less the Corrections below)
- `docs/stories/epic-41/README.md` — rules 1–5, especially rule 1 clauses (a)–(f)
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — the `AcceptanceCriteria` type this binding produces (its AC1/AC2/AC4/AC5)
- **THE RECIPE — read all three, in order:**
  - `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the canonical thin binding (ReadInputs → ComputeReEntryPosition → ReadPositionStage → FreshRun → Fetch → DispatchLifecycle → ReadLifecycleExit → ExposeOutput; `[ResumeBehavior(LatestStateReEntry)]`)
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the structure-test set clause (a)–(f) is checked by
  - `docs/stories/epic-39/story-39-12/implementation-plan.md` — the binding rationale (D1 surface stability, D2 "no bespoke branch", D3 event mirroring, D5 drift-gate extension)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs` — the *consumes-an-upstream-accepted-document* variant (`FetchLatestAcceptedDocumentActivity` behind the `FreshRun` gate)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` — `LifecycleExit`, `ReadLifecycleResult` (fail-closed), `IsAccepted`; `CreationBindingHelper.cs` — `DeriveIssueId`, `ScopeIssueId`, `BuildFailureDetail`, `ProjectTasksArray`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:169-202` (the input contract) and `:811-822` (the output contract: `status`/`outcome`/`documentId`/`lifecycleResult`/`documentJson`/`decisionNotes`/`sessionId`)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs` — the 39-14 store read seam (fail-closed, `Found=false` never throws)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/EmitDocumentEventActivity.cs` + `apps/tamma-elsa/src/Tamma.Activities/Decomposition/EmitDecompositionEventActivity.cs` + `DecompositionEvents.cs` — the two emit shapes the shared activity generalises
- `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/define-acceptance-criteria.md` — the cell being rewritten (front matter `variables: role, workItemJson, contextFindings, conventions`)
- **The gates this story must move, all verified in tree:**
  - `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)`; `:96-132` the `reconciled` array (bidirectional)
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:82` `Bindings`; `:681` the classify-or-fail catch-all; `:725`/`:734` staleness
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:125` `ExpectedContributingWorkflows`; `:460` `ScanLifecycleBindingDispatches`
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs:108` (declare-or-allowlist), `:159` (b), `:238` (c), `:266` (no producer on the allowlist)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:128-133` — the `_ => Rules` catch-all 41-1b AC5 must give this type an arm on

## Corrections to the story

1. **AC5's `[ResumeBehavior(Both)]` is wrong and would fail the build.** `Both` requires the workflow's
   **own** graph to contain a node whose type is in `LifecycleBookmarks.CanonicalSuspendActivities`
   (`WaitForDocumentDecisionActivity` / `WaitForDocumentInputActivity`) — `ResumableStandardStructuralTests`
   clause (b) at `:159`, plus the inverse declaration-honesty check at `:205`. A thin binding owns no
   bookmark: the accept gate suspends **inside the dispatched `document-lifecycle` child**, which the parent
   waits on with `WaitForCompletion = true`. Every landed producer declares
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (`TaskCreationWorkflow.cs:47`,
   `PlanGenerationWorkflow.cs:54`, `ResearchWorkflow.cs:35`, …) and
   `TaskCreationWorkflowStructureTests.Workflow_HasNoBookmarkSuspendActivity` pins the absence.
   **This plan declares `LatestStateReEntry`.** The story's *intent* — "resumable, passes 39-10 without an
   allowlist entry" — is fully satisfied; only the mode token changes.
2. **The shipped `define-acceptance-criteria.md` template does not produce acceptance criteria.** It
   instructs a **task breakdown** — `{"tasks":[{"id","description","files","dependencies","complexity",
   "testing"}],"totalComplexity","estimatedDuration"}` — i.e. the `Plan` wire, with criteria smuggled into
   each task's `testing` string. Binding it unchanged to an `AcceptanceCriteria` validator would fail every
   produce. **The template must be rewritten**, exactly as 39-15 D7 rewrote `(product_owner, triage-intake)`
   from the P0–P3/`ownerRole` vocabulary to the `TriageDecision` wire. That rewrite is in this story's scope
   and is the reason the estimate moves (below).
3. **AC1's "Rebuilt as a thin lifecycle binding" mis-states the starting point.** There is nothing to
   rebuild: `(product_owner, define-acceptance-criteria)` exists in the taxonomy (`AgentAction.cs:25`,
   `RolePhaseMap.cs:52`) with a prompt file, but **no workflow dispatches it** — repo-wide grep finds zero
   `.cs` references outside `AgentAction.cs`/`RolePhaseMap.cs`. This is a **greenfield** binding, not a
   migration. Consequence: there is no legacy event family to preserve (contrast 39-12's `DECOMPOSITION.*`),
   no parser to delete, and no `IntentionallyUnbound` entry to move — the `Bindings` entry is purely
   additive.
4. **The story does not name the epic's rule-1 clause (f) lockstep, and the epic README's version of it is
   incomplete.** Clause (f) names `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`,
   `HaveCount(16)`) — correct and verified. It omits the **stricter, bidirectional** sibling in the same
   file: `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:96`), whose hardcoded
   `reconciled` string array (`:102-123`, 12 ids today) asserts *everything in the list is `!Provisional`
   AND everything outside it is `Provisional`*. A non-provisional seed row that is not added there fails the
   build. Step 6 does both.
5. **AC3's lineage claim needs a caveat.** `DocumentInstance` carries a single `ParentDocumentId`
   (`DocumentInstance.cs:67`) — it cannot express "Issue → Clarification **and** Findings →
   AcceptanceCriteria" as two parents. D4 picks one parent and records the rest as consumed-document ids in
   the emitted event payload.
6. **AC-level reachability, per the epic README's Dependencies table.** The accept gate publishes an
   `AcceptanceRequest` and suspends, but **39-17 (orchestrator agent) has not landed**, so nothing decides;
   and **39-19** has not landed, so the story's line 36 ("accept in the Task View or by asking the
   orchestrator in chat") has no surface. Tests inject the decision through the 39-8
   `DocumentDecisionResumeEndpoint.Resume` statics — the same stand-in every landed Epic 39 producer test
   uses. This story claims the *workflow* half; it does not claim the routing half.

## Design Decisions

- **D1 — New DefinitionId `acceptance-criteria-authoring`; no existing call site moves.** Nothing dispatches
  this activity today, so there is no byte-stability obligation (contrast 39-12 D1 / 39-15 D1). The id is
  deliberately *not* `acceptance-criteria` so it never reads as the document-type wire; it is kebab
  (`Every_workflow_definition_id_is_non_empty_kebab`, `WorkflowInterfaceGraphTests.cs:49`) and unique
  (`:60`). Registration is by assembly scan (`elsa.AddWorkflowsFrom<LlmCallWorkflow>()`), so adding the
  class is enough. Inputs: `issueId`, `issueTitle`, `repository`, `issueNumber`, `workItemJson`, `tenantId`,
  `acceptanceRulesJson?`. Outputs: `status`, `outcome`, `documentId`, `parentDocumentId`,
  `acceptanceCriteriaJson`.
- **D2 — Graph shape is copied from `TaskCreationWorkflow`, node-for-node, with two fetches.**
  `ReadInputs` → `ComputeReEntryPosition` → `ReadPositionStage` → `FreshRun` `FlowDecision` →
  (`FetchClarification` → `FetchFindings`) → `DispatchLifecycle` → `ReadLifecycleExit` →
  `EmitAcceptanceCriteria*` → `ExposeOutput`. Exactly the pinned decision set
  `{FreshRun, LifecycleAccepted}` — no third gate. Both fetches sit behind `FreshRun` (a re-entry must not
  re-read context), both are `FetchLatestAcceptedDocumentActivity` (fail-closed: absent upstream ⇒
  `Found=false`, empty carrier — **never** a hard fail; acceptance criteria are authorable from the issue
  alone, unlike 41-6's `BacklogOrdering` prerequisite).
- **D3 — Consumed content rides the DECLARED `contextFindings` carrier, and `feedbackVariableName` is that
  same key.** The 39-15 render-drop lesson: a producer variable that the cell's front matter does not
  declare is silently dropped at render. `define-acceptance-criteria.md` declares
  `role, workItemJson, contextFindings, conventions`, so the Clarification + Findings bodies are
  concatenated into `contextFindings`, and `["feedbackVariableName"] = "contextFindings"` routes
  repair/revise notes into the same carrier. The rewritten template (D6) keeps that variable set unchanged
  — the front matter is **not** touched, only the body, so `ConventionSeedDriftTests`' three-way keyset
  equality and `PromptFileLoaderTests`' grid stay green with zero edits.
- **D4 — Single-parent lineage: `ParentDocumentId` = the accepted `Clarification` when one exists, else the
  accepted `Findings`, else null.** `DocumentInstance` has one parent slot (`:67`). Clarification is the
  closer ancestor (it resolves the ambiguity the criteria encode). The other consumed document ids ride the
  `ACCEPTANCE_CRITERIA.DRAFTED` event payload (`consumedDocumentIds`) so the full consumes-set is reachable
  from the DCB stream even though the row records one edge. Filed as a note to 39-11 rather than patched
  here.
- **D5 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, `ComputeReEntryPositionActivity` in the graph,
  no allowlist entry.** Per Correction 1. Clause (c) of `ResumableStandardStructuralTests` (`:238`) is
  satisfied by the node; clause (a) (`:108`) by the attribute; `:266` (no document producer on the legacy
  allowlist) by never adding one. The re-entry position also gates the fetches and the `STARTED` emission —
  a re-entry is not a new authoring run.
- **D6 — The prompt template is rewritten to the `AcceptanceCriteria` wire; front matter unchanged.**
  Precedent: 39-15 D7. The body instructs Given/When/Then **or** checklist form, one criterion per testable
  condition, each independently verifiable, bound to the issue, and forbids criteria referencing
  out-of-scope work. The exact JSON shape is `AcceptanceCriteriaDocumentType`'s — **41-1b owns the wire**,
  so this story's template edit is a *lockstep* with 41-1b's `Contract` const, and
  `ContractBindingTests.EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` (`:361`) is the
  enforcement. The `Bindings` entry's parser authority is `"AcceptanceCriteriaDocumentType.Validate"`.
- **D7 — ONE shared domain-event activity for the whole Epic 41 producer batch, created here.** Five stories
  (41-2/41-3/41-4/41-5/41-6) each name a domain event family. The house pattern is one
  `Emit{Family}EventActivity` per family (28 such classes exist) — five more near-identical copies is
  duplication with no upside, because none of the five families has a legacy consumer whose payload shape
  must be preserved (contrast `EmitDecompositionEventActivity`'s `SubtaskCount`). So this story creates
  `Tamma.Activities/Documents/EmitDomainLifecycleEventActivity.cs`: inputs `EventType`, `IssueId`,
  `CorrelationId`, `TenantId`, `DocumentId`, `Detail`, `DataJson`; status derived generically from the type
  suffix (`.FAILED`/`.REJECTED`/`.ESCALATED` ⇒ error, else success), emitted via `TammaEventEmitter.Emit`
  onto `tamma:events` like every other emitter (no repository dependency — an injected one is inert in the
  Elsa engine). Each consuming story ships only its `{Family}Events.cs` constants file. **This is a
  cross-story shared edit: 41-2 must land before 41-3/41-4/41-5/41-6, or they carry a local copy.**
- **D8 — Acceptance posture is 41-1b's to choose, not this story's.** `AcceptanceDefaults.For`
  (`AcceptanceDefaults.cs:128-133`) ends in `_ => Rules` — a newly registered type silently takes the
  single-`architect` unanimous row, which is wrong for acceptance criteria (the reviewer should be a second
  PO or a tester lens, per the story's "Produced document"). 41-1b AC5 owns writing the arm; this plan
  **states the required row** — `ReviewerSelection` single-reviewer `product_owner`, acceptor per autonomy —
  and its execution test asserts it, so a silent fall-through fails here as well as there. Both selector
  arms this needs already exist (`GetReviewActionForRole(ProductOwner) => ReviewScope`,
  `(Tester) => ReviewTestability`, `RolePhaseMap.cs:376-387`) — **no 41-1a dependency**, unlike the prose
  stories.

## Implementation Steps

1. **Precondition check (no code).** 41-1b merged and compiling: `DocumentTypeKey.AcceptanceCriteria`
   parses, `DocumentTypeRegistry.Resolve("acceptance-criteria")` returns an `IDocumentType`, its `Contract`
   const is final (D6 depends on the exact token set), and `AcceptanceDefaults.For` has the D8 arm. A gap
   blocks steps 5–7 — file it against 41-1b, do not work around it.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/EmitDomainLifecycleEventActivity.cs`** (D7) —
   the shared emitter, modelled on `EmitDocumentEventActivity` minus the `DocumentEvents.StatusForEvent`
   coupling. Ships with its own unit test (`Tamma.Activities.Tests/Documents/`).

   > **Note (2026-07-29, conformance round).** The activity landed; the unit test **did not**. Until
   > this note, the emitter's only executing coverage was three `StatusForEvent` assertions borrowed
   > inside `AcceptanceCriteriaAuthoringWorkflowStructureTests` and `AdrAuthoringWorkflowStructureTests`
   > — each pinning one binding's family constants, not the emitter's generic contract, even though
   > one activity now serves the whole Epic 41 batch. The gap was closed rather than merely recorded:
   > `tests/Tamma.Activities.Tests/Documents/EmitDomainLifecycleEventActivityTests.cs` (**14** cases)
   > now covers `StatusForEvent`'s suffix rule across families (including its Ordinal/suffix-position
   > sensitivity and the empty-type ⇒ `error` fail-loud), `ParseTenantId`, and `BuildTammaEvent`'s
   > tag/data mapping (every queryable tag present; blank tags omitted rather than written empty).

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/AcceptanceCriteriaEvents.cs`** — `Started`,
   `Drafted`, `Accepted`, `Failed` = `"ACCEPTANCE_CRITERIA.STARTED"` / `.DRAFTED` / `.ACCEPTED` / `.FAILED`.
   Tags on every emission: `issueId`, `repository`, `tenantId`, `correlationId`.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/AcceptanceCriteriaBindingHelper.cs`** —
   pure, Elsa-free, total, fail-closed (the `CreationBindingHelper` posture): `BuildContextFindings(string
   clarificationJson, string findingsJson)` (the D3 carrier composer), `ChooseParentDocumentId(string
   clarificationId, string findingsId)` (D4), `ProjectCriteria(string documentJson)` (the `criteria` array
   raw text, `"[]"` on unreadable), `BuildConsumedIdsJson(...)` for the event payload. Reuse
   `LifecycleBindingHelper.ReadLifecycleResult` / `IsAccepted` / `CreationBindingHelper.BuildFailureDetail`
   verbatim — do not fork them.

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AcceptanceCriteriaAuthoringWorkflow.cs`** (D1,
   D2, D5) — the binding. `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. Dispatch input:

   ```csharp
   ["documentType"]          = "acceptance-criteria",
   ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
   ["producerAction"]        = AgentAction.DefineAcceptanceCriteria.ToWire(),
   ["producerVariablesJson"] = /* { workItemJson, contextFindings, conventions:"" } */,
   ["feedbackVariableName"]  = "contextFindings",
   ["issueId"] / ["correlationId"] / ["tenantId"] / ["acceptanceRulesJson"]
   ```

   `WaitForCompletion = new(true)`. Emissions per D7/step 3, all gated on the re-entry position for
   `STARTED`.

6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed`, `:134-174`) —
   add the row `("acceptance-criteria-authoring", [Clarification, Findings], AcceptanceCriteria, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs`** — `:45`
   `HaveCount(16)` → `HaveCount(17)` with the reason in the comment (rule-1 clause (f): one conscious edit
   per producing workflow), **and** add `"acceptance-criteria-authoring"` to the `reconciled` array at
   `:102-123` (Correction 4 — bidirectional, so omitting it fails).

7. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/define-acceptance-criteria.md`** (D6,
   Correction 2) — body rewritten to the `AcceptanceCriteria` wire; front matter byte-identical.

8. **MODIFY the drift gates.**
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`: add the `Bindings`
   entry `[("product_owner","define-acceptance-criteria")] = new("AcceptanceCriteriaDocumentType.Validate",
   [ … token groups from 41-1b's Contract … ])` with a comment naming
   `Tamma.Core/Documents/Types/AcceptanceCriteria.cs` as the shape authority.
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`: add
   `"AcceptanceCriteriaAuthoringWorkflow"` to `ExpectedContributingWorkflows` (`:125`) with the
   lifecycle-binding-walk note. Verify — do not pre-edit — `MinExpectedDispatchPairs` (`:110`, a floor) and
   `EveryConcreteWorkflow_IsIntrospectableOrAllowListed` (`:397`).

9. **CREATE the test suites** —
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/AcceptanceCriteriaAuthoringWorkflowStructureTests.cs`,
   `.../Workflows/AcceptanceCriteriaBindingHelperTests.cs`,
   `.../Workflows/AcceptanceCriteriaLifecycleExecutionTests.cs`. See Test Plan.

10. **Full run.** `dotnet test` green; `dotnet ef migrations has-pending-model-changes` clean (this story
    adds no schema).

## Data & Migrations

None. `AcceptanceCriteria` rows are `document_instances` (39-11's table, 41-1b's registration);
`ACCEPTANCE_CRITERIA.*` and `DOCUMENT.*` ride the existing `TammaEventEmitter` → `EventPersistenceMiddleware`
→ `EventRepository` → `domain_events` drain. `has-pending-model-changes` stays clean.

## Events

- **Emits (new constants, this story):** `ACCEPTANCE_CRITERIA.STARTED` (fresh runs only),
  `.DRAFTED` (data `consumedDocumentIds`, `criteriaCount`), `.ACCEPTED` (on lifecycle `accepted`, data
  `documentId`), `.FAILED` (on `rejected`/`escalated`, detail names the typed outcome wire). Tags
  `issueId` / `repository` / `tenantId` / `correlationId` on all four.
- **Emitted by the machinery this binding wires in (not by this story's code):** the whole `DOCUMENT.*`
  family (39-6/39-10), `APPROVAL.REQUESTED`/`.PROVIDED` and `ESCALATION.TRIGGERED` (39-8) — asserted
  alongside in the replay test with matching `issueId` tags.
- **Consumes:** none at runtime.

## Test Plan

NUnit + FluentAssertions; Testcontainers for the execution suite (the shared 39-6/39-10 fixture).

- **`AcceptanceCriteriaAuthoringWorkflowStructureTests`** — the rule-1 clause (a)–(f) set, copied from
  `TaskCreationWorkflowStructureTests`: builds; DefinitionId `acceptance-criteria-authoring`; threads
  `TenantId`; **zero** `Finish`; **exactly one** `DispatchWorkflow`, literal def id `document-lifecycle`;
  **zero** `DispatchWorkflow` targeting `llm-call`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`
  variables; `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains the
  `(product_owner, define-acceptance-criteria)` pair attributed to this workflow;
  `MaterializeDispatchInput` yields `documentType == "acceptance-criteria"` and
  `feedbackVariableName == "contextFindings"`; two `FetchLatestAcceptedDocumentActivity` nodes and one
  `ComputeReEntryPositionActivity` node; `FlowDecision` id set exactly `{FreshRun, LifecycleAccepted}`; class
  carries `[ResumeBehavior(LatestStateReEntry)]`; **no `Wait*` activity** (Correction 1's pin). **Covers AC1,
  AC5.**
- **`AcceptanceCriteriaBindingHelperTests`** — `BuildContextFindings` over both-present / one-present /
  neither; `ChooseParentDocumentId` precedence + null; `ProjectCriteria` on a valid body and on unreadable
  JSON (`"[]"`, never throws); `BuildFailureDetail` names each reachable outcome wire. **Covers AC2 (exit
  mapping half).**
- **`AcceptanceCriteriaLifecycleExecutionTests`** (Testcontainers) —
  (a) **happy path:** scripted valid draft → review approve → `Accept` resume → `status=completed`, criteria
  projected; store asserts the accepted `AcceptanceCriteria` instance + its `Review` rows via
  `IDocumentInstanceRepository`, `ParentDocumentId` = the seeded `Clarification` (D4); replay asserts both
  event families with matching `issueId`. **Covers AC2, AC3.**
  (b) **repair/revise ring:** an invalid draft (a criterion referencing out-of-scope work) loops
  validate → repair → review-concerns → revise-with-notes → accept; asserts the revise notes reached the
  producer through `contextFindings` (D3) and `DOCUMENT.REVISION_STARTED` is present. **Covers AC2.**
  (c) **validation exhaustion:** always-invalid stub → typed `ValidationExhausted` escalation with lineage,
  `ACCEPTANCE_CRITERIA.FAILED` naming the outcome, `status=escalated`, **no error terminal reached**.
  **Covers AC2 (no-dead-end half).**
  (d) **41-15 consumer read:** after (a), a `FetchLatestAcceptedDocumentActivity` read for
  `(issueId, "acceptance-criteria")` returns the accepted body — the exact seam 41-15 will use. **Covers
  AC4.**
  (e) **re-entry:** crash after acceptance (39-10 D8 shape: dispose the host without resuming, fresh
  dispatch on the same store) → short-circuits with the SAME `documentId`, exactly one `DOCUMENT.ACCEPTED`
  and one `ACCEPTANCE_CRITERIA.ACCEPTED` on the stream; crash mid-review → resumes at review of the same
  revision, no second `DOCUMENT.PRODUCED.*`, no second `.STARTED`. **Covers AC5.**
  (f) **acceptance posture (D8):** `AcceptanceDefaults.For(DocumentTypeKey.AcceptanceCriteria)` returns the
  documented row, and a run with that row completes its review stage — so a silent `_ => Rules`
  fall-through fails here.
- **Drift gates (self-verifying, step 8)** — `ContractBindingTests` full suite green with the new entry and
  no new `IntentionallyUnbound`/residual; `TaxonomyDriftBuildTests` green;
  `WorkflowInterfaceGraphTests` green at 17 with the `reconciled` addition;
  `ResumableStandardStructuralTests` green with **no** allowlist entry for the new workflow. **Covers AC1
  (rule-1 clause (f)), AC5.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin lifecycle binding, no bespoke parse/terminal | 5, 6, 8 (D1/D2) | StructureTests clause (a)–(f); drift gates green |
| 2 — validated by the type; failure flows the rings, never a dead end | 5, 7 (D6) | ExecutionTests (b)(c); BindingHelperTests |
| 3 — persisted with lineage Issue → Clarification? → AC → Reviews | 5 (D4) | ExecutionTests (a) store + parent asserts (single-parent caveat recorded) |
| 4 — 41-15 reads the latest accepted AC via the 39-11 store | 5 | ExecutionTests (d) |
| 5 — resumable per the standard, no allowlist entry | 5 (D5) | StructureTests declaration + no-`Wait*` pins; ExecutionTests (e); `ResumableStandardStructuralTests` |

## Dependencies & Sequencing

- **Blocked by:** **41-1b** — hard. `DocumentTypeKey.AcceptanceCriteria` does not exist
  (`DocumentTypeKey.cs` has exactly 10 members, verified), so the type is unparsable
  (`DOCUMENT.TYPE.UNKNOWN`) and unpersistable on the **human path too**, not just the agent path. Also
  **Epic 39** (39-6 lifecycle, 39-7 review producers, 39-8 accept gate + resume endpoint, 39-10 re-entry,
  39-11 store) — all landed and verified in tree.
- **NOT blocked by 41-1a.** The `(product_owner, define-acceptance-criteria)` cell exists
  (`AgentAction.cs:25`, `RolePhaseMap.cs:52`, `Prompts/product_owner/define-acceptance-criteria.md`), and
  both candidate reviewer arms (`ProductOwner`, `Tester`) are already in
  `RolePhaseMap.GetReviewActionForRole`. This is the one Wave-1 producer with no taxonomy dependency.
- **Blocks:** **41-15** (acceptance verification reads the accepted `AcceptanceCriteria`), the merge-gate
  consumption named in the epic's Wave-1 rationale, and — via D7's shared emitter — **41-3, 41-4, 41-5,
  41-6** (each ships only a constants file if this lands first; otherwise each carries a local copy and one
  of them promotes it later).
- **Lockstep (same-commit or same-sprint coordination):** 41-1b's `AcceptanceCriteria` `Contract` const ↔
  step 7's template rewrite ↔ step 8's `Bindings` token groups — one wire shape, agreed once, or
  `EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` fails. 41-1b's `AcceptanceDefaults` arm ↔
  D8's assertion.
- **Stubbed, not pulled in:** 39-17 (the orchestrator that decides), 39-19 (Task View / chat), 39-20 (role
  routing) — all fail-closed stubs in tree; tests use the 39-8 resume statics.
- **Sequencing within the story:** 1 → 2/3 (parallel) → 4 → 5 → 6/7/8 (parallel) → 9 → 10.

## Risks & Mitigations

- **41-1b slips or its wire shape churns.** The template rewrite (step 7) and the `Bindings` token groups
  (step 8) are both downstream of a const this story does not own. Mitigation: steps 2–4 and the helper
  tests are 41-1b-independent and can be built first; agree the `criteria[]` wire in one review with the
  41-1b owner before step 7; token drift is a mechanical edit caught by a red build, never a silent pass.
- **Template rewrite regresses real output quality.** The shipped body is a *working* task-breakdown prompt;
  the new one is unproven. Mitigation: the type's `Examples` (41-1b AC2 — one accepting, one rejecting
  fixture per rule) are the contract the template is written against; a drifting draft drives a
  repair/revise turn rather than a silent normalisation, and the churn is exactly the telemetry 39-3 D6
  pre-authorised acting on.
- **D7's shared emitter becomes a cross-story bottleneck.** Four stories consume it. Mitigation: it is ~80
  lines with a stable input set and no story-specific behaviour; if 41-2 slips, any consumer can land a
  local copy and the promotion is a mechanical merge.
- **Rule-1 clause (f) is a two-edit lockstep, and the epic README names only one edit.** Mitigation:
  Correction 4 + step 6 name both; the `reconciled` array's bidirectional assertion fails loudly, so the
  omission cannot ship.
- **"Done" is narrower than the story's prose.** With 39-17/39-19 unlanded the accept gate parks. Mitigation:
  Correction 6 states the claim boundary; nothing in the ACs above depends on the orchestrator.
- **Story-vs-code tensions:** Corrections 1–5 are all resolved in favour of the code. None changes the
  story's intent; Correction 2 is the only one that changes the work (and the estimate).

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1b precondition verification + wire agreement | 0.25 |
| 2–3 | Shared `EmitDomainLifecycleEventActivity` (+ test) + `AcceptanceCriteriaEvents` | 0.5 |
| 4 | `AcceptanceCriteriaBindingHelper` | 0.5 |
| 5 | The binding workflow | 1.0 |
| 6 | Registry seed row + the two `WorkflowInterfaceGraphTests` edits | 0.25 |
| 7 | Prompt-template rewrite to the `AcceptanceCriteria` wire (Correction 2) | 0.5 |
| 8 | `ContractBindingTests` + `TaxonomyDriftBuildTests` edits | 0.5 |
| 9 | Structure + helper + Testcontainers suites (a)–(f) | 1.25 |
| 10 | Full-suite green, review polish | 0.25 |
| **Total** | | **5.0** |

**Est. Effort: 5 days.** The story file says 3–4 days; that estimate predates two facts this plan
verified — the prompt template must be rewritten (Correction 2, +0.5 d) and this story owns the shared
domain-event emitter for the whole five-story batch (D7, +0.5 d). Net of D7 the story-only cost is ~4.5 d.
The story's own `## Estimated Effort` section is left at 3–4 days and this plan is the record of the delta.

## Blocks / Blocked by

- **Blocked by:** 41-1b (hard — the `AcceptanceCriteria` document type); Epic 39 stories 39-6, 39-7, 39-8,
  39-10, 39-11 (all landed).
- **Blocks:** 41-15 (acceptance verification); the Epic 41 producer batch 41-3, 41-4, 41-5, 41-6 (D7's
  shared emitter, soft — each can carry a local copy).
- **Not blocked by:** 41-1a (cell and both reviewer arms already exist); 41-1c (this is a typed document,
  not prose); the tenant-aware scheduled-trigger seam (this workflow is issue-triggered, not scheduled).
