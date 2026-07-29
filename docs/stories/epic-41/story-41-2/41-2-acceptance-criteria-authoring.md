# Story 41-2: Acceptance-Criteria Authoring Workflow

Status: done (2026-07-29) — `AcceptanceCriteriaAuthoringWorkflow` (`DefinitionId = acceptance-criteria-authoring`) ships as a thin binding over `document-lifecycle`; the `define-acceptance-criteria` template was rewritten from the task-breakdown (Plan) wire to the `AcceptanceCriteria` wire (version 1 → 2) and its example validates with **zero** violations; the cell GRADUATED from `ContractBindingTests.PendingProducerCells` (6 → 5) into `Bindings` (16 → 17) with its intended contract adopted verbatim, and its `TemplateExampleConformanceTests.KnownNonConformingTemplates` baseline was deleted (pin 16 → 15). Claim boundary per the plan's Correction 6: this story ships the *workflow* half — 39-17/39-19 have not landed, so nothing decides at the accept gate end-to-end. See the dated amendments below.

## User Story

As a **product owner** (or an eligible role-holder at lower autonomy), I want a workflow that turns an
issue + its clarified requirements into a typed, testable **AcceptanceCriteria** document on the standard
lifecycle, so that "done" is defined once, reviewed, accepted, and then consumed by acceptance
verification (41-15) and the merge gate — instead of being implicit in a plan or a reviewer's head.

## Priority

P0 / Wave 1 — highest single-story leverage. It is the upstream anchor for 41-15 and gives the merge/accept
gates an explicit definition-of-done to check against.

## Scope

Thin binding over `document-lifecycle`. `consumes: [issue, Clarification?, Findings?]` /
`produces: AcceptanceCriteria`. Produce cell `(product_owner, define-acceptance-criteria)`.

The cell exists in the taxonomy today but **nothing dispatches it** — this is a greenfield binding, not a
migration, and there is no 41-1a work in it. What IS in scope is a **template rewrite**: the shipped
`Prompts/product_owner/define-acceptance-criteria.md` instructs a task breakdown (the `Plan` wire, with
criteria smuggled into each task's `testing` string), not acceptance criteria. Bound unchanged to the
`AcceptanceCriteria` validator it would fail every produce, so the body is rewritten to the
`AcceptanceCriteria` contract (39-15 D7 precedent; front matter unchanged).

## Produced document

`AcceptanceCriteria` (41-1): independently verifiable criteria in Given/When/Then or checklist form,
bound to `issueId`, no criterion referencing out-of-scope work. Reviewed via the unified `Review`
(single reviewer default: a second PO or tester lens; panel by policy).

## Events

`ACCEPTANCE_CRITERIA.STARTED` → `.DRAFTED` → `.ACCEPTED` / typed-escalation, alongside generic
`DOCUMENT.*`. All tagged `issueId`/`repository`/`tenantId`.

## Orchestrator / user interaction

Accept gate publishes `AcceptanceRequest`; orchestrator routes per rules + autonomy. A holder of the PO
role (or the initiator) can accept in the Task View or by asking the orchestrator in chat.

## Autonomy behavior

- **70–84:** produce is assigned to a human PO; accept is a human decision.
- **85–94:** agent drafts, human accepts.
- **95–100:** agent drafts and self-accepts unless an always-escalate class (e.g. contract-affecting
  criteria) is configured.

## Acceptance Criteria

1. Built as a greenfield thin lifecycle binding (nothing dispatches the cell today); no bespoke
   parse/terminal. Includes the `define-acceptance-criteria` template rewrite to the `AcceptanceCriteria`
   contract (see Scope).
2. Output validated by the `AcceptanceCriteria` type; validation failure flows the repair/review/escalation
   rings, never a dead end.
3. Accepted document persisted with lineage: Issue → Clarification? → AcceptanceCriteria → Reviews.
   `DocumentInstance` carries a single `ParentDocumentId`, so the parent is the accepted Clarification when
   one exists (else the Findings, else null); the other consumed document ids ride the
   `ACCEPTANCE_CRITERIA.DRAFTED` event payload.
4. 41-15 can read the latest accepted `AcceptanceCriteria` for an issue via the 39-11 store.
5. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched `document-lifecycle` child); passes the 39-10 structural test with no allowlist
   entry.

## Dependencies

- **Blocking:** **41-1b** (`AcceptanceCriteria` type — an unregistered type is unpersistable on the
  human path too), Epic 39 lifecycle/store/accept.
- **Unblocks:** 41-15, merge-gate consumption.

## Estimated Effort

3–4 days

## Amendments from the implementation pass (2026-07-29)

1. **AC5's `[ResumeBehavior(Both)]` reads `LatestStateReEntry` in the tree** — the plan's Correction 1,
   applied. A thin binding owns no canonical suspend node, so `Both` fails clause (b) of
   `ResumableStandardStructuralTests`; the accept gate suspends inside the dispatched
   `document-lifecycle` child, which the parent awaits with `WaitForCompletion = true`. The story text
   above already reads `LatestStateReEntry`. No allowlist entry was added — the workflow declares.

2. **AC1's "greenfield binding" is what landed.** Nothing dispatched
   `(product_owner, define-acceptance-criteria)` before this story, so there is no legacy event family,
   no parser to delete and no `IntentionallyUnbound` entry to move. The `Bindings` entry is purely
   additive, and the `PendingProducerCells` entry 41-1b minted for this cell graduated on schedule —
   its `IntendedContract` (10 token groups) moved into `Bindings` **verbatim**, exactly as that table
   promised.

3. **The template rewrite was load-bearing, not cosmetic.** The shipped body carried **1 of its 10**
   required tokens (it instructed `{"tasks":[{id, description, files, dependencies, complexity,
   testing}], totalComplexity, estimatedDuration}` — the `Plan` wire with criteria smuggled into each
   task's `testing` string). Bound unchanged it would have failed VALIDATE on every produce. Front
   matter `variables`/`enableTools`/`maxTokens` are byte-identical (so the convention-seed keyset and
   the `PromptFileLoader` grid are untouched); only `version` moved 1 → 2, matching the 39-15 D7 /
   41-1b `threat-model` rewrite precedent.

4. **Acceptance posture: 41-1b chose the 7-role PANEL, not the plan's single product_owner reviewer.**
   The implementation plan's D8 *stated the required row* as a single-reviewer `product_owner` arm.
   41-1b had already landed a different, deliberate answer —
   `AcceptanceDefaults.For(AcceptanceCriteria) => s_panelRules`, with the recorded reason "it is the
   merge gate's definition of done and 41-15 verifies against it — the same breadth plan/review get".
   That arm was **not** touched: it is 41-1b's file and 41-1b's D1 decision. The executing assertion
   moved with it — the structure suite pins the LANDED panel row and, more importantly, pins that the
   type does **not** reach the `_ => Rules` catch-all, which is the failure mode D8 exists to prevent.

5. **A third `FlowDecision` exists (`DocumentDrafted`), against the plan's "exactly
   `{FreshRun, LifecycleAccepted}` — no third gate".** The `.DRAFTED` member of the event family the
   story names has to fire when — and only when — the lifecycle actually minted a document; the
   binding knows that from the typed exit's `documentId`. Routing it is a decision over a TYPED
   lifecycle value (39-12 D2's rule), not a parse-derived branch, and the epic README's checkable
   "thin" clauses (a)–(f) place no constraint on `FlowDecision` count. The structure suite pins the
   decision set exactly, so the shape is still a build gate rather than a free hand.

6. **Test-coverage boundary — no new Testcontainers execution suite.** The plan's
   `AcceptanceCriteriaLifecycleExecutionTests` (a)–(f) was NOT written. Every landed lifecycle
   execution fixture in the tree is `[Explicit]`, no CI job passes a filter that selects `[Explicit]`
   fixtures, and 41-1c's follow-up F1 established that under the bare-provider harness such a fixture
   fails deterministically (the lifecycle suspends forever on its first `ActivityKind.Task` node with
   no bookmark to resume). Adding a sixth fixture that nothing runs would have recorded coverage that
   does not exist. Following F1's precedent, the executing coverage is
   `AcceptanceCriteriaAuthoringWorkflowStructureTests` (13 tests) +
   `AcceptanceCriteriaBindingHelperTests` (19 cases) + the two drift gates. **What that leaves
   unproven by THIS story:** AC3's persisted-lineage assertion and AC4's 41-15 read-back are carried
   by 41-1b's `NewDocumentTypeStoreRoundTripTests` — a real Postgres 17 Testcontainer sweep that
   already takes `acceptance-criteria` through envelope → `DocumentInstanceRepository.InsertAsync` →
   `ListByIssueAsync` + the production lineage handler — not by a run of this workflow. The
   repair/revise ring and the crash re-entry short-circuit are proven generically by the 39-6/39-10
   suites over `DocumentLifecycleWorkflow`, which this binding dispatches unmodified.

7. **Shared emitter (plan D7) landed here:**
   `Tamma.Activities/Documents/EmitDomainLifecycleEventActivity.cs` — one activity for the whole Epic
   41 producer batch, with the event family as an input and the status derived from the type suffix
   (`.FAILED`/`.REJECTED`/`.ESCALATED` ⇒ error, `.STARTED` ⇒ started, else success). 41-3/41-4/41-5/
   41-6 now ship only a constants file. 41-9 consumed it in the same wave rather than carrying the
   near-identical `EmitAdrEventActivity` copy its own plan's D6 called for.
