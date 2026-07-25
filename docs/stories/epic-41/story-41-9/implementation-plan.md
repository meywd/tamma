# Implementation Plan — Story 41-9: ADR Authoring Workflow

## Scope & Deliverable

When this story is done, an Architecture Decision Record is a **document the platform produces, reviews,
accepts, persists and can query by issue** — not a markdown file someone remembers to commit. A new
`AdrAuthoringWorkflow` (`DefinitionId = "adr-authoring"` — free today, no workflow claims it) is a THIN
BINDING over `document-lifecycle` in exactly the 39-12/39-13/39-15 shape: it assembles the ADR context
(the issue work item, an optional accepted `Design` from 41-10, optional accepted `Findings`), dispatches
`document-lifecycle` with `documentType = "prose"` and the `(architect, write-adr)` producer cell, and
routes the typed exit. Zero `Finish`, zero `llm-call`, zero parsing, no validate/retry plumbing. The
review stage is the 39-7 producer over the prose (yielding a `Review` whose `ParentDocumentId` is the ADR),
the accept gate is 39-8's, and `AgentAction.WriteAdr` is already usable as an always-escalate
`EscalationClass` (`AcceptanceRulesModelTests.cs:84`, `AcceptanceGuardrailsTests.cs:45`). A new
`ADR.*` DCB family rides alongside `DOCUMENT.*`, and the workflow's `WorkflowDocumentInterface` edge is
declared with the edge pin bumped in the same change.

This story is the **reference implementation of the prose-on-lifecycle path** for 41-4, 41-5, 41-8's
narrative, 41-22, 41-24, 41-25 and 41-26 — which is precisely why it cannot precede 41-1c.

## Pre-Reading

- `docs/stories/epic-41/story-41-9/41-9-adr-authoring.md` — the story (ACs are source of truth, modulo the
  Corrections section below)
- `docs/stories/epic-41/README.md` — rule 1 (the six checkable "thin" clauses (a)–(f)), rule 3/4 and the
  Dependencies table (what of Epic 39 has NOT landed)
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the hard blocker: the
  `prose` `DocumentTypeKey`, `ProseDocumentType`, `DocumentEnvelope.Audience` + `DocumentInstance.Audience`
  (+ EF config + migration), and the audience/kind vocabularies. **`kind=adr` and `audience=engineering`
  are 41-1c's vocabulary members, minted there, consumed here.**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DesignProposalWorkflow.cs` — the closest landed binding
  (single-document producer, `sessionId` threaded as the lifecycle's decision-session id, a pre-ACCEPT
  delivery hook). **This is the file to copy.**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the minimal binding skeleton
  (`ReadInputs` → `ComputeReEntryPosition` → `ReadPositionStage` → `FreshRun` → fetch consumed doc →
  `DispatchLifecycle` → `ReadLifecycleExit` → `ExposeOutput`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape the epic README names; every clause here is copied
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` — the shared
  fail-closed `ReadLifecycleResult` / `IsAccepted` reader every binding uses (do **not** write a new one)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:169-202` (the input
  contract: `documentType`, `producerRole`, `producerAction`, `producerVariablesJson`,
  `feedbackVariableName`, `issueId`, `correlationId`, `tenantId`, `acceptanceRulesJson`,
  `validationContextJson`, `reviewWorkflowDefinitionId`, `deliveryWorkflowDefinitionId`, `sessionId`) and
  `:814-822` (the outputs: `status`, `outcome`, `documentId`, `lifecycleResult`, `documentJson`,
  `decisionNotes`, `sessionId`)
- `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/write-adr.md` — **the produce cell already exists**;
  read its front matter (`variables: role, workItemJson, findings, audience`) and its current body
- `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs` — the fail-loud loader
  (`PROMPT.SEED.NO_BODY_FAMILY` / `PROMPT.SEED.UNKNOWN_CELL` / `MALFORMED_FILE`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings`,
  `IntentionallyUnbound`, `KnownContractViolations` (ratchet), and the coverage guard that fires the
  moment a new pair is dispatched
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:110`
  (`MinExpectedDispatchPairs = 21`), `:125-150` (`ExpectedContributingWorkflows`), `:460`
  (`ScanLifecycleBindingDispatches`)
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`
  (`HaveCount(16)`) and `:102-123` (the `reconciled` list)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs` — the
  39-10 gate; a NEW workflow gets **no** allowlist entry, it declares
- `apps/tamma-elsa/src/Tamma.Activities/Decomposition/DecompositionEvents.cs` +
  `EmitDecompositionEventActivity.cs` — the per-family event-catalogue + emitter template
- `.dev/decisions/` — the nine ADR/design markdown files that are today's "ADR store" (see Corrections)

## Corrections to the story

1. **AC4's `[ResumeBehavior(Both)]` is wrong and would FAIL the 39-10 build gate.** Clause (b) of
   `ResumableStandardStructuralTests` requires a `BookmarkSuspend`/`Both` workflow's own built graph to
   contain a node whose type is in both its declaration's `SuspendActivities` and
   `LifecycleBookmarks.CanonicalSuspendActivities`. A thin binding contains **no** suspend activity — the
   accept-gate bookmark lives inside the dispatched `document-lifecycle` child instance, which the parent
   awaits via `WaitForCompletion = true`. Every landed producer binding therefore declares
   `LatestStateReEntry`: `IssueDecompositionWorkflow.cs:47`-style,
   `TaskCreationWorkflow.cs:47`, `DesignProposalWorkflow.cs:38`, `TriagePODecisionWorkflow.cs:38`.
   **This plan declares `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`** and `TaskCreation`'s
   `Workflow_HasNoBookmarkSuspendActivity` pin is copied. The story's AC4 should read
   `LatestStateReEntry`. (Same correction applies to 41-8 AC3 and 41-10 AC5.)
2. **No `adr` document type exists and this story must not mint one.** `DocumentTypeKey.cs:22-33` has
   exactly ten members. ADRs live today as repo markdown under `.dev/decisions/` — nine files
   (`ADR-004-ai-benchmarking-service-evolution.md` plus eight design notes), with **no** registry entry,
   no persistence, no lineage and no code path of any kind. Adding an `adr` `DocumentTypeKey` would be a
   full lockstep vocabulary change (enum + `IDocumentType` + `s_registrations` + `AcceptanceDefaults.For`
   arm + the two count pins) for a body with no schema to validate — exactly what 41-1c's D1 rejects.
   **41-9 consumes 41-1c's `prose` type with `kind=adr`, `audience=engineering`.** The `.dev/decisions/`
   convention is untouched by this story; migrating those nine files into the store is explicitly out of
   scope.
3. **The produce cell and its prompt file ALREADY EXIST — but the template is the wrong shape.**
   `AgentAction.WriteAdr` (`AgentAction.cs:44`) is in `AgentRole.Architect`'s eligible set
   (`RolePhaseMap.cs:73`) and `Prompts/architect/write-adr.md` ships today, already declaring an
   `{{audience}}` variable. **However** the shipped body instructs a markdown report "to be posted as an
   issue comment" (`## Summary` / `### Key Findings` / `### Action Items`), not the
   `{kind, audience, title, body}` envelope `ProseDocumentType.Validate` will check. Binding it as-is
   would fail VALIDATE on every draft. **This story owns rewriting that template** (the 39-15 D7
   precedent, where `triage-intake.md` was rewritten from the P0–P3 vocabulary to the `TriageDecision`
   wire). So 41-9 is *not* "no new cell needed, zero taxonomy work" — it is "no new cell, one template
   rewrite". Reflected in the effort table.
4. **`(architect, write-adr)` is dispatched by nothing today** (grep-verified across `apps/tamma-elsa`:
   the only non-test references are the enum member and the `RolePhaseMap` set), so it appears in neither
   `ContractBindingTests.Bindings` nor `IntentionallyUnbound`. The instant this binding dispatches it,
   `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` fails the build. This story must add the entry
   (D4). The story does not mention this.
5. **Rule-3/rule-4 reachability.** Per the epic README's Dependencies table, 39-17 (the deciding
   orchestrator), 39-19 (Task View / chat) and 39-20 (role-addressed delivery) have not landed —
   `AgentOfflineChatRelay` refuses every chat message (`Program.cs:448-451`) and
   `InitiatorOnlyTaskAudienceResolver` admits only the issue initiator (`:445-447`). The accept gate
   publishes and suspends correctly; nothing decides on the other end except a test-side resume through
   `DocumentDecisionResumeEndpoint`. AC1–AC3 are fully claimable; the "architect accepts" half of the
   Autonomy-behavior section is **wired, not reachable end-to-end**, and the story's ACs should say so.

## Design Decisions

- **D1 — `DefinitionId = "adr-authoring"`; a new workflow, no incumbent rewired.** Nothing claims
  `adr-authoring` today (verified against the 50 files in `Tamma.ElsaServer/Workflows/`), so there is no
  byte-stability constraint of the 39-12 D1 kind and no call site to preserve. The workflow is dispatched
  by definition id (from `single-issue-cycle` once 41-29's `design`/`docs` routing lands, from 41-10's
  accept path as an ADR seed, or directly by the orchestrator). Inputs: `issueId`, `repository`,
  `issueNumber`, `workItemJson`, `decisionContext`, `tenantId`, plus additive `sessionId?`,
  `acceptanceRulesJson?`, `audience?` (defaulting to `engineering`). Outputs:
  `status`/`outcome`/`documentId`/`adrJson`/`sessionId` — the `TaskCreation`/`DesignProposal` output set.
- **D2 — the binding is a copy of `DesignProposalWorkflow`, not a new shape.** Graph:
  `ReadInputs` → `ComputeReEntryPosition` (`documentType = "prose"`, `IssueId = ScopedIssueId`) →
  `ReadPositionStage` → `FreshRun` `FlowDecision` → (True) `EmitAdrStarted` + `FetchConsumedDesign`
  (`FetchLatestAcceptedDocumentActivity`, type `design`, the 41-10/`design-proposal` seed) + optional
  `FetchConsumedFindings` → join → `DispatchLifecycle` → `ReadLifecycleExit`
  (`LifecycleBindingHelper.ReadLifecycleResult` — the SHARED reader, never a new one) →
  `AdrAccepted` `FlowDecision` → `EmitAdrDrafted`/`EmitAdrAccepted`/`EmitAdrFailed` → `ExposeOutput`
  (the single terminal `Sequence` of `SetOutput`s). Three `FlowDecision`s max, all routing TYPED values
  (39-12 D2's resolution of "no bespoke branch"); zero `Finish`; zero `llm-call` dispatch.
- **D3 — producer-scoped lifecycle issue id.** `plan-generation` and `task-creation` both produce
  documentType `plan` for one issue, and 39-15 solved the collision with
  `CreationBindingHelper.ScopeIssueId(baseIssueId, "task-creation")` → `"{issueId}#task-creation"`. The
  same collision is coming for `prose`: 41-9 (ADR), 41-4 (roadmap), 41-5 (stakeholder update), 41-22
  (postmortem), 41-24/25/26 can all produce `prose` for the same issue, and 39-11's latest-accepted read
  scopes by `(issueId, documentType)` with **no producer filter**. This binding therefore scopes on
  `ScopeIssueId(issueId, "adr")`. **Because 41-9 is the prose reference implementation, this decision is
  the one the other seven prose stories inherit** — record it as such, and file the general fix (a
  producer or `kind` filter on the 39-11 read) against 39-11 rather than solving it locally seven times.
- **D4 — the `ContractBindingTests` entry pins the prose ENVELOPE, not prose structure.** Adding
  `[("architect", "write-adr")] = new("ProseDocumentType.Validate", [One("\"kind\""), One("\"audience\""),
  One("\"title\""), One("\"body\"")])` binds the cell to exactly what 41-1c's validator checks — envelope
  facts only, never structure inside the markdown. The alternative (an `IntentionallyUnbound` entry
  reading "prose has no shape") is **wrong**: prose does have a wire shape; only its `body` is
  unvalidated, and an unbound entry would let a future template edit drop `audience` with no test
  noticing. The ADR *shape convention* (context / decision / consequences / alternatives-considered) stays
  guidance inside the rewritten template body, per 41-1c D3, and is deliberately NOT a token group.
- **D5 — the template rewrite keeps the existing variables, adds the contract block.** Front matter stays
  `variables: role, workItemJson, findings, audience` (so `feedbackVariableName = "findings"` lands in a
  **declared** carrier — the render-drop lesson from 39-15; a repair/revise note routed to an undeclared
  key is silently dropped). Body becomes: role framing → work item → seed design/findings → audience →
  the ADR shape convention as prose guidance → `ProseDocumentType.RenderContract()`'s JSON envelope
  instruction. `version` in the front matter bumps 1 → 2.
- **D6 — `ADR.*` is a four-member family with a LOUD terminal.** The story names three
  (`STARTED`/`DRAFTED`/`ACCEPTED`). Every landed family carries an error-status terminal so a degraded
  exit is never recorded as success (`DecompositionEvents.StatusForEvent`, `DocumentEvents.StatusForEvent`).
  Add `ADR.FAILED` (emitted on `rejected`/`escalated`, `Detail` naming the typed outcome wire).
  New `Tamma.Activities/Adr/AdrEvents.cs` + `EmitAdrEventActivity.cs`, copied from the decomposition pair.
- **D7 — acceptance policy is passed through, never hardcoded.** 41-1c D2 sets the prose default
  (`tech_writer` single reviewer). An ADR wants an architect reviewer; the binding does **not** edit
  `AcceptanceDefaults.For` (that is 41-1c's file and a per-type, not per-kind, decision) — it forwards a
  caller-supplied `acceptanceRulesJson`, and the story ships a documented default rules JSON for ADRs
  (architect reviewer, `AcceptorRequirement.Human` for the always-escalate class). The always-escalate
  mechanism already accepts `AgentAction.WriteAdr` as an `EscalationClass` value
  (`AcceptanceRulesModelTests.cs:84`) — no new machinery.
- **D8 — the lockstep set for this story is enumerated, not implied.** A new producing workflow moves
  exactly these, in one change: (i) `DocumentTypeRegistry.BuildSeed` += `new WorkflowDocumentInterface(
  "adr-authoring", empty, DocumentTypeKey.Prose, false)`; (ii)
  `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` `HaveCount(16)` → `HaveCount(17)` **or
  whatever the count is when this lands** (41-1c moves the *vocabulary* pins, not this one; 41-10/41-11
  move this one too — see the shared-edit note in Dependencies); (iii) that test's `reconciled` array
  += `"adr-authoring"`; (iv) `ContractBindingTests.Bindings` += D4's entry; (v)
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` += `"AdrAuthoringWorkflow"`; (vi) NO
  `ResumableStandardStructuralTests` allowlist entry (the workflow declares). The two vocabulary pins
  (`DocumentTypeKeyTests.cs:20`, `DocumentTypeRegistryTests.cs:37`) are **41-1c's**, not this story's —
  do not touch them.

## Implementation Steps

1. **Precondition gate (no code).** Verify in tree and compiling: `DocumentTypeKey.Prose`,
   `ProseDocumentType` registered, `DocumentEnvelope.Audience` + `DocumentInstance.Audience` + the
   migration, and the audience/kind vocabularies (all 41-1c). If any is missing, STOP — the produce step
   cannot persist (`DocumentTypeKeyExtensions.Parse` throws `DOCUMENT.TYPE.UNKNOWN`,
   `DocumentTypeRegistry.Resolve` throws `DOCUMENT.TYPE.NOT_REGISTERED`) on **either** execution path.
   This is a real gate, not a formality.
2. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/write-adr.md`** per D5. Verify
   `PromptFileLoader` still loads (front-matter keys exactly `variables`/`enableTools`/`maxTokens`/
   `version`, no extras — `RequireKeys` rejects unknown keys).
3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Adr/AdrEvents.cs` +
   `apps/tamma-elsa/src/Tamma.Activities/Adr/EmitAdrEventActivity.cs`** (D6) — constants
   `ADR.STARTED`/`.DRAFTED`/`.ACCEPTED`/`.FAILED`, `ParseTenantId`, `StatusForEvent`; the emitter appends
   a `TammaEvent` via `TammaEventEmitter.Emit` (no repository dependency — the drain persists).
4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/AdrBindingHelper.cs`** — pure,
   Elsa-free: `ProjectAdrBody(string documentJson)` (the accepted prose payload → the `adrJson` output),
   `BuildProducerVariables(...)`, `BuildFailureDetail(exit)`. `ReadLifecycleResult`/`IsAccepted` are
   **reused from `LifecycleBindingHelper`** — do not re-implement.
5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdrAuthoringWorkflow.cs`** per D1/D2/D3,
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, dispatch input:
   `documentType = "prose"`, `producerRole = AgentRole.Architect.ToWire()`,
   `producerAction = AgentAction.WriteAdr.ToWire()`, `feedbackVariableName = "findings"`,
   `producerVariablesJson = {workItemJson, findings, audience}`, `issueId = ScopedIssueId`,
   `correlationId`, `tenantId`, `acceptanceRulesJson`, `sessionId`.
6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** — D8(i).
7. **MODIFY the pins** — D8(ii)–(v): `WorkflowInterfaceGraphTests.cs`, `ContractBindingTests.cs`,
   `TaxonomyDriftBuildTests.cs`, each with a one-line reason in the comment naming this story.
8. **CREATE the tests** — `AdrAuthoringWorkflowStructureTests.cs`, `AdrBindingHelperTests.cs`,
   `AdrAuthoringLifecycleExecutionTests.cs` (see Test Plan).
9. **Green the suite** — full `dotnet test` + `dotnet ef migrations has-pending-model-changes` (must stay
   clean; this story adds no schema — the `Audience` column is 41-1c's migration).

## Data & Migrations

None. Prose rows land in 41-1c's `documents`/`document_instances` shape; `ADR.*` and `DOCUMENT.*` ride the
existing drain → `EventRepository` → `domain_events` path. `has-pending-model-changes` stays clean.

## Events

- **Emits (new family, this story's `AdrEvents.cs`):** `ADR.STARTED` (fresh runs only — a re-entry is not
  a new ADR), `ADR.DRAFTED` (lifecycle produced+validated), `ADR.ACCEPTED` (lifecycle `accepted`),
  `ADR.FAILED` (LOUD, on `rejected`/`escalated`, `Detail` = the typed outcome wire). Tags `issueId`,
  `tenantId`, `correlationId`, `documentId`, `audience`.
- **Emitted by the machinery this story wires in (not by this story's code):** the whole `DOCUMENT.*`
  family (39-6/39-10), `APPROVAL.REQUESTED`/`PROVIDED` and `ESCALATION.TRIGGERED` (39-8).
- **Consumes:** none at runtime.

## Test Plan

NUnit + FluentAssertions (+ Testcontainers for the execution suite).

- **`AdrAuthoringWorkflowStructureTests`** — a clause-for-clause copy of
  `TaskCreationWorkflowStructureTests`: builds; `DefinitionId == "adr-authoring"`; threads `TenantId`; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables; **exactly one `DispatchWorkflow`, whose
  literal definition id is `document-lifecycle`**; **zero** `DispatchWorkflow("llm-call")`; **zero
  `Finish`**; `ComputeReEntryPositionActivity` present; `FetchLatestAcceptedDocumentActivity` present;
  class declares `LatestStateReEntry`; no `Wait*` activity;
  `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains
  `(AdrAuthoringWorkflow, DispatchLifecycle, architect, write-adr)`;
  `MaterializeDispatchInput` yields `documentType == "prose"` and
  `feedbackVariableName == "findings"`. **Covers README rule-1 clauses (a)–(e), AC1 (structure half), AC2.**
- **`AdrBindingHelperTests`** — `ProjectAdrBody` over a valid prose payload / unreadable JSON → `""`;
  `BuildFailureDetail` names each reachable outcome wire + `rejected`; a `ProseDocumentType` payload
  round-trips through `DocumentJson.Options` with `kind`/`audience`/`title`/`body` intact (the consumer
  shape pin). **Covers AC3 (payload half).**
- **Prompt-contract tests (self-verifying)** — `ContractBindingTests` green with the new entry:
  the rewritten `write-adr.md` contains every token group; `KnownContractViolations` stays empty (a new
  violation is not baselined); the coverage guard passes without an `IntentionallyUnbound` entry.
  **Covers AC1 (contract half).**
- **Pin tests (self-verifying)** — `WorkflowInterfaceGraphTests` (bumped count, `adr-authoring` in
  `reconciled`, produces a registered key), `TaxonomyDriftBuildTests`
  (`EveryKnownContributingWorkflow_StillEmitsPairs` with the new contributor,
  `MinExpectedDispatchPairs` still cleared), `ResumableStandardStructuralTests` green **with no allowlist
  entry**. **Covers AC4.**
- **`AdrAuthoringLifecycleExecutionTests` (Testcontainers Postgres, on the shared 39-6/39-10 fixture)** —
  (a) happy path: scripted valid prose draft → review approve → `Accept` resume →
  `status=completed`, an accepted `prose` instance readable through `IDocumentInstanceRepository` by
  `issueId` with `Audience == "engineering"` and `kind == "adr"`; the `Review` row's `ParentDocumentId`
  is the ADR document id; stream carries both `ADR.*` and `DOCUMENT.*` with matching `issueId`.
  (b) empty-body draft → `ProseDocumentType` violation → repair/revise ring → accept (proves prose is
  validated on the envelope, not the structure). (c) always-escalate: rules naming
  `EscalationClass(AgentAction, "write-adr")` → the run escalates rather than self-accepting at autonomy
  100. (d) crash after acceptance → fresh dispatch short-circuits to `Complete`, exactly ONE
  `DOCUMENT.ACCEPTED` and exactly ONE `ADR.ACCEPTED` on the whole stream. (e) producer-scope isolation
  (D3): an accepted `prose` for the same issue written under a different scope is NOT returned as this
  binding's latest-accepted. **Covers AC1, AC2, AC3, AC4 (re-entry half).**

## Risks & Mitigations

- **41-1c is unlanded and this story is 100% gated on it.** No partial start is honest: without the
  `prose` key the produce step cannot persist on **either** path. Mitigation: steps 2–4 (template
  rewrite, event family, pure helper) depend only on names 41-1c pins, so they can be built against its
  plan; step 1 is a real gate. Coordinate `ProseDocumentType`'s exact wire field names
  (`kind`/`audience`/`title`/`body`) in lockstep — D4's token groups and D5's template both depend on
  them literally.
- **The prose token contract is thin by construction.** Four envelope tokens is a weak gate compared with
  `Findings`' seven. Mitigation: that is the honest contract (41-1c D1's "a type whose body is
  unvalidated"); the execution test's (b) scenario proves the validator actually bites on an empty body,
  so the gate is not vacuous.
- **D3's producer-scoped issue id becomes seven copies.** Mitigation: record it once here as the prose
  reference decision, and file the 39-11 read-filter gap (already FILED by 39-15 D2 for the two-plan
  case) so the general fix has one owner.
- **The rewritten `write-adr.md` breaks a consumer.** Mitigation: grep-verified — the cell has **no**
  dispatch site today, so there is no consumer to break. The rewrite is strictly additive risk.
- **Reviewer selection.** If the caller's rules name `tech_writer` (41-1c D2's default),
  `RolePhaseMap.GetReviewActionForRole` **throws** today (`:376-387`, no `TechWriter` arm) and
  `DocumentLifecycleWorkflow.cs:1199` calls it unguarded. Mitigation: this story's shipped default rules
  name `architect` (D7), which works today; the `tech_writer` path is 41-1a AC3's, not this story's.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1c precondition verification + rules-JSON design | 0.25 |
| 2 | `write-adr.md` rewrite to the prose envelope + contract block | 0.5 |
| 3 | `AdrEvents` + `EmitAdrEventActivity` | 0.25 |
| 4 | `AdrBindingHelper` (pure) | 0.25 |
| 5 | `AdrAuthoringWorkflow` binding | 0.75 |
| 6–7 | Registry edge + four pin bumps (lockstep) | 0.25 |
| 8 | Structure + helper + Testcontainers suites | 1.0 |
| 9 | Full-suite green, review polish | 0.25 |
| **Total** | | **3.5** (story estimate: 2–3 days — the delta is the template rewrite + `Bindings` entry the story does not name, Corrections 3 and 4) |

## Blocks / Blocked by

- **Blocked by — hard, cannot start:**
  - **41-1c** (`prose` `DocumentTypeKey` + `ProseDocumentType` + `Audience` on envelope **and**
    `DocumentInstance` + migration + the audience/kind vocabularies). Absolute gate on both the agent and
    the human-assigned path.
  - **Epic 39: 39-2/39-3/39-4** (registry), **39-6** (`DocumentLifecycleWorkflow`), **39-7**
    (`document-review` producers), **39-8** (accept gate + resume endpoint), **39-10**
    (`ResumeBehaviorAttribute`, `ComputeReEntryPositionActivity`, the structural gate), **39-11**
    (`IDocumentInstanceRepository` + lifecycle write wiring) — **all landed**, verified in tree.
- **Blocked by — for end-to-end claimability only (does not block shipping):** **39-17** (nothing decides
  at the accept gate), **39-19** (no Task View for a human acceptor), **39-20**
  (`InitiatorOnlyTaskAudienceResolver` is fail-closed). The story's ACs must state which half is claimed.
- **NOT blocked by:** 41-1a (no new role, no new cell — `(architect, write-adr)` exists) and **41-1b**
  (no new document type). 41-9 is in the 41-1c set only.
- **Blocks / feeds:** **41-4**, **41-5**, **41-8** (narrative half), **41-22**, **41-24**, **41-25**,
  **41-26** — all seven inherit D2's binding shape, D3's producer-scoped id, D4's envelope-contract
  posture and D6's event-family convention. 41-9 is the designated reference implementation; landing it
  first makes the other seven mechanical.
- **Consumes when present:** **41-10** (an accepted `Design` seeds the ADR — the `FetchConsumedDesign`
  node reads `documentType = design`, which `design-proposal` already produces today, so the node is
  useful before 41-10 lands).
- **Shared edit — `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`, `HaveCount(16)`
  today):** 41-9 (+1), 41-10 (+1) and 41-11 (+2) all move this single line, as do every other Epic 41
  producer story. Whoever lands second rebases the number; the pin is deliberately a conscious edit.
  Epic 41 has no `EXECUTION-PLAN.md` to register this in — see the README's "Planning artifacts this epic
  does not have".
