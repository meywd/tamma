# Implementation Plan — Story 41-8: Retrospective Facilitation Workflow

## Scope & Deliverable

When this story is done, a sprint retrospective produces a **durable, tracked, evidence-cited artifact**
instead of evaporating after the meeting. A new `RetrospectiveWorkflow`
(`DefinitionId = "retrospective"` — free today) is a THIN BINDING over `document-lifecycle`: it assembles
the sprint context (accepted standup digests from 41-7, the sprint's DCB event window, blocker/escalation
events, and — once 41-6/41-1b land — the `SprintPlan`), dispatches `document-lifecycle` with
`documentType = "findings"` and the `(scrum_master, facilitate-retro)` producer cell (41-1a), and routes
the typed exit. Zero `Finish`, zero `llm-call`, zero parsing. A `RETRO.*` family rides alongside
`DOCUMENT.*`, tagged `tenantId`/sprint.

**The story is delivered in two phases** (D1, forced by the code — see Correction 1):

- **Phase A — the `Findings` retro (this plan's steps 1–9).** Gated on **41-1a only**. Retro items with
  cited sprint evidence, ranked and role-owned action items. Shippable as soon as 41-1a lands.
- **Phase B — the audience-tagged prose narrative (steps 10–12).** Gated on **41-1c** *and* on a
  **41-1a amendment** that 41-1a does not currently contain: a second scrum-master cell for the narrative.
  Phase B is deliberately deferred; Phase A is not blocked by it.

## Pre-Reading

- `docs/stories/epic-41/story-41-8/41-8-retrospective-facilitation.md` — the story
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); the Dependencies table (39-17/39-19/39-20
  have not landed); the "deliberately out of scope" note that the retro *meeting* is not automated — only
  the artifact around it
- `docs/stories/epic-41/story-41-7/implementation-plan.md` — 41-8 consumes 41-7's accepted digests; its
  D2 (`FetchEventWindowActivity`) and D4 (`Findings.ValidateWithContext` citation ring) are reused here
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 2's fifteen cells:
  `plan-sprint`, `synthesize-standup`, `facilitate-retro`, `track-impediments` for `scrum_master`.
  **Note what is NOT there: a narrative/prose cell** (Correction 1).
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — the `prose` type,
  `Audience`, and the `retro-narrative` **kind** it mints (Scope 3) with audience `team`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — the binding skeleton
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs` — `EMPTY_FINDINGS`, `MISSING_EVIDENCE`,
  `RELEVANCE_OUT_OF_RANGE` / `CONFIDENCE_OUT_OF_RANGE` (rejected, never clamped), the all-or-nothing rank
  rule (`PARTIAL_RANKS`, `DUPLICATE_RANK`) — the rules that make AC1's "cite concrete sprint evidence"
  free and its ranking requirement executable
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs`,
  `CreationBindingHelper.cs` (`ScopeIssueId`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174` — `BuildSeed`; a
  `WorkflowDocumentInterface` carries a **single** `Produces` key (Correction 1's mechanism)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` is keyed
  `(role, action) → ONE CellContract` (Correction 1's second mechanism)
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`, `:102-123`;
  `TaxonomyDriftBuildTests.cs:110`, `:125-150`, `:460`; `ResumableStandardStructuralTests.cs`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:445-451` — `InitiatorOnlyTaskAudienceResolver` and
  `AgentOfflineChatRelay`: why AC2 is not deliverable

## Corrections to the story

1. **"Produces `Findings` … plus a prose narrative summary" cannot be ONE binding, and the narrative's
   producer cell does not exist in 41-1a's list.** Three mechanisms in the shipped code force the split:
   (a) `WorkflowDocumentInterface` carries a single `Produces` key
   (`DocumentTypeRegistry.cs:137-173`); (b) one `document-lifecycle` dispatch produces exactly one
   document; (c) `ContractBindingTests.Bindings` is keyed `(role, action) → ONE CellContract`, so
   `(scrum_master, facilitate-retro)` cannot be both a `Findings` producer and a `prose` producer. A
   second document therefore needs a **second workflow on a second cell** — and 41-1a's fifteen-cell list
   (`41-1a…:25-29`) mints `plan-sprint`, `synthesize-standup`, `facilitate-retro`, `track-impediments` for
   `scrum_master` and **no narrative cell**. **This is an unrecorded lockstep gap between 41-8 and 41-1a.**
   Options considered:
   - fold the narrative into `Findings.Summary` — **rejected**: `summary` is validated as the report's
     overview, carries no `Audience`, and would silently drop the epic's audience-tagging promise;
   - reuse `(tech_writer, summarize-changes)` — **rejected**: it is already `IntentionallyUnbound` as
     free-text PR-description prose, and rebinding it as a document producer would break
     `PullRequestWorkflow`'s contract classification;
   - **chosen (D1): split into Phase A (`Findings`, 41-1a only) and Phase B (`prose`, 41-1c + a 41-1a
     amendment for `(scrum_master, write-retro-narrative)`).** File the amendment against 41-1a; do not
     mint the cell from this story (a cell without a `Prompts/scrum_master/…​.md` file fails
     `PromptFileLoader` at startup with `PROMPT.SEED.NO_BODY_FAMILY`, and a file without a taxonomy cell
     fails with `PROMPT.SEED.UNKNOWN_CELL` — the loader is fail-loud in both directions, so a half-change
     does not boot).
2. **AC3's `[ResumeBehavior(Both)]` would FAIL the 39-10 build gate — declare `LatestStateReEntry`.**
   Clause (b) of `ResumableStandardStructuralTests` requires a `Both`/`BookmarkSuspend` declaration to be
   backed by a canonical suspend-activity node in **this** workflow's graph. A thin binding has none — the
   accept gate suspends inside the dispatched `document-lifecycle` child, which the parent awaits with
   `WaitForCompletion = true`. Every landed binding declares `LatestStateReEntry`
   (`TaskCreationWorkflow.cs:47`, `DesignProposalWorkflow.cs:38`, `TriagePODecisionWorkflow.cs:38`). Same
   correction as 41-9 and 41-10.
3. **AC2 is not deliverable and must be re-scoped.** "Action items produce role-scoped Task View entries
   via 39-20" — 39-20 has not landed; `ITaskAudienceResolver` is stubbed fail-closed by
   `InitiatorOnlyTaskAudienceResolver` (`Program.cs:445-447`), admitting only the issue initiator, and
   39-19 ships no Task View (`AgentOfflineChatRelay` refuses every message, `:448-451`). The epic README
   names 41-8:46 as one of three ACs that fail *at the AC level*. Claim the half that exists: *"each
   accepted action item is emitted as a `RETRO.ACTION_ITEM` row carrying its owning role and evidence, and
   the accept gate publishes an `AcceptanceRequest`; role-scoped delivery is unreachable until
   39-19/39-20."*
4. **AC1's "items cite concrete sprint evidence" is already enforced by `FindingsDocumentType`**
   (`MISSING_EVIDENCE` per finding, `EMPTY_FINDINGS` for a zero-length list). The *new* work is making
   citations resolvable against the sprint's actual events — D5 reuses 41-7's
   `Findings.ValidateWithContext` ring for that rather than re-implementing it.
5. **An empty sprint is the same trap 41-7 hit.** `EMPTY_FINDINGS` makes a "nothing happened" retro
   invalid, so a sprint with no material events would loop the repair ring to exhaustion and exit
   `escalated`. D6 applies 41-7's Correction-1 fix: short-circuit to `RETRO.SKIPPED` before dispatching,
   with no document produced.
6. **The `SprintPlan` consumed edge is a 41-1b dependency and is deliberately dropped from Phase A.**
   The story's `consumes` list names `SprintPlan (41-6)`, which needs the `SprintPlan` type from **41-1b**.
   Declaring it in `WorkflowDocumentInterface.Consumes` would put 41-8 on 41-1b's critical path for no
   Phase-A benefit. D8 declares `Consumes = [Findings]` (the 41-7 digests, which exist) and leaves the
   `SprintPlan` edge as an additive follow-up. **Consequence: 41-8 Phase A is blocked on 41-1a only** —
   narrower than the story's Dependencies line, which also names 41-1c (true for Phase B only).

## Design Decisions

- **D1 — two phases, two workflows, one story.** Phase A: `RetrospectiveWorkflow`
  (`DefinitionId = "retrospective"`, produces `Findings`, cell `(scrum_master, facilitate-retro)`).
  Phase B: `RetroNarrativeWorkflow` (`DefinitionId = "retro-narrative"`, produces `prose` with
  `kind = retro-narrative`, `audience = team`, cell `(scrum_master, write-retro-narrative)` — the 41-1a
  amendment), dispatched by Phase A **after acceptance** with the accepted retro's `documentId` as its
  parent lineage. Two thin bindings, each independently satisfying rule 1's clauses (a)–(f). Phase B
  inherits every decision 41-9 records for prose (its D2 binding shape, D3 producer-scoped issue id, D4
  envelope-contract posture, D6 event-family convention) — **land 41-9 first and Phase B is mechanical.**
- **D2 — the sprint IS the lifecycle key.** A retro has no `issueId`. Generalising 39-15's
  `CreationBindingHelper.ScopeIssueId` and 41-7's D3: `issueId = "retro:{repository}:{sprintId}"`
  (normalised through the same segment transform). Consequence: a duplicate dispatch for the same sprint
  re-enters at `Complete` and short-circuits, emitting `DOCUMENT.REENTERED` and no second
  `DOCUMENT.ACCEPTED`. Phase B's narrative scopes on `"retronarrative:{repository}:{sprintId}"` so the two
  documents never share a slice.
- **D3 — trigger: sprint-close event OR direct dispatch, NOT a cron.** The story says "triggered at sprint
  close (or scheduled)". Sprint close is a domain event, not a clock tick, so this story takes the
  event/dispatch path and **is therefore NOT blocked by the scheduler seam (story 41-30)** — unlike 41-7 and
  41-11. Until 41-6 emits a sprint-close event, the workflow is dispatched by definition id with an
  explicit `sprintId` + window. Record this explicitly: it is the reason 41-8 is Wave-3-startable while
  its siblings are not.
- **D4 — context assembly reuses 41-7's activity, adds one store read.** `FetchEventWindowActivity`
  (41-7 D2) for the sprint's DCB window (prefixes `DOCUMENT.`, `STANDUP.`, `BLOCKER.`, `ESCALATION.`,
  `CYCLE.`, `DEPLOY.`), plus `FetchLatestAcceptedDocumentActivity` reads for the accepted standup
  `Findings` in the sprint range. If 41-7 has not landed, this story builds `FetchEventWindowActivity`
  per 41-7's pinned D2 signature — **register the shared edit before either starts.**
- **D5 — evidence is validated, not hoped for.** Reuse 41-7's `Findings.ValidateWithContext` citation
  ring: the binding forwards an evidence index built from the window + the consumed digests as
  `validationContextJson`, and an item citing an unresolvable id fails `CITATION_UNKNOWN_EVENT` into the
  repair/revise ring. If 41-7 has not landed the override, this story adds it (one place, shared) —
  **never a second, retro-local copy.**
- **D6 — the empty sprint short-circuits before the dispatch (Correction 5).** A `SprintHasMaterial`
  `FlowDecision` on `EventCount + digestCount > 0`: False → `EmitRetroSkipped` → `ExposeOutput` with
  `status = "skipped"`, no lifecycle dispatch, no document. A typed-value branch (39-12 D2's sanctioned
  kind); the structure test pins the `FlowDecision` id set so a parse gate cannot reappear.
- **D7 — `RETRO.*` is a five-member family.** `STARTED` / `SYNTHESIZED` / `ACCEPTED` (the story's three)
  plus `RETRO.ACTION_ITEM` (one per accepted action item, carrying `owningRole` + evidence — Correction
  3's claimable half) and `RETRO.FAILED` (LOUD, on `rejected`/`escalated`). Phase B adds
  `RETRO.NARRATIVE_ACCEPTED`. New `Tamma.Activities/Retro/RetroEvents.cs` +
  `EmitRetroEventActivity.cs`. Tagged `tenantId`, `repository`, `sprintId`, `correlationId`.
- **D8 — acceptance policy is passed through; `AcceptanceDefaults.cs` is not edited.**
  `For(DocumentTypeKey.Findings)` falls to the `_ => Rules` catch-all (single `architect`, unanimous),
  which is wrong for a retro — but the file is per document type and shared with `research`,
  `triage-context-gathering` and 41-7's digest. The binding forwards a caller-supplied
  `acceptanceRulesJson` (scrum-master reviewer at 70–84, self-accept at 85–100) as configuration.
- **D9 — the lockstep set, enumerated.** Phase A: (i) `DocumentTypeRegistry.BuildSeed` +=
  `new WorkflowDocumentInterface("retrospective", new[]{ DocumentTypeKey.Findings }, DocumentTypeKey.Findings, false)`
  — consumes the 41-7 digests, produces the retro (Correction 6: **no `SprintPlan` key**); (ii)
  `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` `HaveCount(16)` → `+1`; (iii) that test's
  `reconciled` array += `"retrospective"`; (iv) `ContractBindingTests.Bindings` +=
  `[("scrum_master","facilitate-retro")] = new("FindingsDocumentType.Validate", [the seven Findings token
  groups])`; (v) `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` += `"RetrospectiveWorkflow"`;
  (vi) NO `ResumableStandardStructuralTests` allowlist entry. Phase B repeats all six for
  `retro-narrative` / `(scrum_master, write-retro-narrative)` / `ProseDocumentType.Validate`.
  **The taxonomy count pins are 41-1a's and the document-type vocabulary pins are 41-1b/41-1c's — this
  story touches neither.**

## Implementation Steps

### Phase A — the `Findings` retro (41-1a only)

1. **Precondition gate (no code).** Verify `AgentRole.ScrumMaster` exists, `(scrum_master,
   facilitate-retro)` passes `RolePhaseMap.IsRoleEligibleForPhase`, and
   `Prompts/scrum_master/facilitate-retro.md` + `Prompts/scrum_master/_system.md` exist carrying D9(iv)'s
   seven `Findings` token groups and a declared `contextFindings` feedback carrier. Any gap is a 41-1a
   defect — file it there. Also check whether 41-7 has landed `FetchEventWindowActivity` and
   `Findings.ValidateWithContext` (D4/D5) — the answer changes steps 2 and 3.
2. **CREATE-OR-CONSUME `FetchEventWindowActivity`** (D4) — build per 41-7's pinned D2 signature if 41-7
   has not landed; otherwise consume unchanged.
3. **CREATE-OR-CONSUME `Findings.ValidateWithContext`** (D5) — same rule, one implementation.
4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Retro/RetroEvents.cs` + `EmitRetroEventActivity.cs`**
   (D7).
5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/RetroBindingHelper.cs`** — pure,
   Elsa-free: `BuildSprintIssueId(repository, sprintId)` and `BuildNarrativeIssueId(...)` (D2),
   `BuildEvidenceContext(windowJson, digestIds)`, `ExtractActionItems(documentJson)` (→ the
   `RETRO.ACTION_ITEM` rows, fail-closed to empty on unreadable JSON),
   `BuildFailureDetail(exit)`. `ReadLifecycleResult`/`IsAccepted` from `LifecycleBindingHelper`.
6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RetrospectiveWorkflow.cs`** —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (Correction 2); graph: `ReadInputs` →
   `ComputeReEntryPosition` (`documentType = "findings"`, `IssueId = SprintIssueId`) →
   `ReadPositionStage` → `FreshRun` → (True) `EmitRetroStarted` + `FetchEventWindow` +
   `FetchStandupDigests` → `SprintHasMaterial` (D6) → `DispatchLifecycle` → `ReadLifecycleExit` →
   `RetroAccepted` → `EmitRetroSynthesized` + `EmitActionItems` + `EmitRetroAccepted` /
   `EmitRetroFailed` → `ExposeOutput`. **Exactly one `DispatchWorkflow` in Phase A, literal id
   `document-lifecycle`.**
7. **MODIFY `DocumentTypeRegistry.cs` + the four pins** — D9(i)–(v).
8. **CREATE the Phase-A tests** — see Test Plan.
9. **Green the suite** — full `dotnet test` + `has-pending-model-changes` clean.

### Phase B — the audience-tagged narrative (41-1c + the 41-1a amendment)

10. **Precondition gate.** Verify `DocumentTypeKey.Prose` + `ProseDocumentType` + `DocumentEnvelope
    .Audience` + `DocumentInstance.Audience` + the `retro-narrative` kind and `team` audience (41-1c), AND
    `(scrum_master, write-retro-narrative)` + its prompt file (the 41-1a amendment). If either is
    missing, STOP — Phase B is not startable and Phase A is unaffected.
11. **CREATE `RetroNarrativeWorkflow.cs` + its `RetroNarrativeBindingHelper` additions**, copying 41-9's
    `AdrAuthoringWorkflow` verbatim in shape (D1). **MODIFY `RetrospectiveWorkflow.cs`** to add a second
    `DispatchWorkflow("retro-narrative")` on the accepted branch, threading the retro `documentId` as the
    narrative's parent lineage — and **update the Phase-A structure test's dispatch-count pin from one to
    two in the same change** (a conscious edit, not a silent relaxation).
12. **Repeat D9's six pin moves for `retro-narrative`.**

## Data & Migrations

None. `Findings` (Phase A) and `prose` (Phase B — 41-1c's `Audience` column and its migration) live in
39-11's tables; `RETRO.*`/`DOCUMENT.*` ride the existing drain → `EventRepository` → `domain_events`
path. `has-pending-model-changes` stays clean in both phases.

## Events

- **Emits (Phase A):** `RETRO.STARTED` (fresh runs only), `.SYNTHESIZED` (lifecycle produced+validated),
  `.ACCEPTED` (data: `itemCount`, `actionItemCount`, `documentId`), `.ACTION_ITEM` (one per accepted
  action item, data: `owningRole`, `evidence`, `rank`), `.SKIPPED` (empty sprint, D6), `.FAILED` (LOUD).
  **Phase B:** `.NARRATIVE_ACCEPTED` (data: `audience`, `kind`, `documentId`, `parentDocumentId`).
  Tags `tenantId`, `repository`, `sprintId`, `correlationId`.
- **Consumes (the window read):** `DOCUMENT.`, `STANDUP.`, `BLOCKER.`, `ESCALATION.`, `CYCLE.`, `DEPLOY.`
  prefixes via `IEventRepository.ListByTenantAsync`, plus the accepted 41-7 `Findings` through the 39-11
  store. Read-only.
- **Emitted by the machinery this story wires in:** `DOCUMENT.*`, `APPROVAL.*`, `ESCALATION.TRIGGERED`.

## Test Plan

- **`RetroBindingHelperTests` (pure)** — `BuildSprintIssueId`/`BuildNarrativeIssueId` are deterministic,
  tenant/repo/sprint-folded, mutually disjoint, and disjoint from 41-7's `standup:` and 41-11's
  `techdebt:` prefixes; `ExtractActionItems` on valid/unreadable/absent JSON (fail-closed to empty, never
  a throw out of a routing lambda); `BuildEvidenceContext` round-trips through the same parser
  `Findings.ValidateWithContext` uses (so the two halves cannot drift); `BuildFailureDetail` names each
  reachable outcome wire.
- **`RetrospectiveWorkflowStructureTests`** — the `TaskCreationWorkflowStructureTests` clause set:
  `DefinitionId == "retrospective"`; threads `TenantId`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/
  `*Valid` variables; **exactly one `DispatchWorkflow` in Phase A, literal id `document-lifecycle`**
  (two after Phase B, bumped consciously); **zero** `llm-call`; **zero `Finish`**;
  `ComputeReEntryPositionActivity`, `FetchEventWindowActivity` and `FetchLatestAcceptedDocumentActivity`
  present; declares `LatestStateReEntry` (Correction 2); no `Wait*` node; `FlowDecision` id set pinned to
  exactly `{FreshRun, SprintHasMaterial, RetroAccepted}`; `ScanLifecycleBindingDispatches()` contains
  `(RetrospectiveWorkflow, DispatchLifecycle, scrum_master, facilitate-retro)`;
  `MaterializeDispatchInput` yields `documentType == "findings"` and the declared
  `feedbackVariableName`. **Covers AC1 (structure half), AC3, rule-1 clauses (a)–(e).**
- **`FindingsCitationContextTests` (shared with 41-7)** — the evidence ring's matrix plus the regression
  pin that the SAME payload validates clean with an EMPTY context (`research` /
  `triage-context-gathering` unaffected). Only authored once, by whichever story lands first.
- **Pin tests (self-verifying)** — `WorkflowInterfaceGraphTests` (bumped count, `retrospective` in
  `reconciled`, produces a registered key); `ContractBindingTests` (the new entry satisfied by 41-1a's
  template; `KnownContractViolations` stays empty); `TaxonomyDriftBuildTests`;
  `ResumableStandardStructuralTests` green with **no** allowlist entry. **Covers AC3 (gate half).**
- **`RetrospectiveExecutionTests` (Testcontainers, shared 39-6/39-10 fixture)** — (a) happy path: seed a
  sprint window + two accepted standup digests → valid retro draft with ranked, cited items → review →
  accept → accepted `Findings` readable by the sprint issue id; `.ACTION_ITEM` rows carry the owning role;
  `.ACCEPTED` carries the counts. (b) **evidence ring (AC1):** a draft citing a fabricated event id →
  `CITATION_UNKNOWN_EVENT` → repair/revise → accept. (c) **ranking:** a draft with two items at the same
  explicit rank → `DUPLICATE_RANK`; a draft with ranks on some items only → `PARTIAL_RANKS` (both are the
  shipped `Findings` rules, exercised, not re-implemented). (d) **empty sprint (Correction 5):** no
  material → `RETRO.SKIPPED`, `status = "skipped"`, **zero** `document-lifecycle` instances started.
  (e) **idempotency (D2):** dispatch the same sprint twice → the second re-enters at `Complete`, emits
  `DOCUMENT.REENTERED`, exactly ONE `DOCUMENT.ACCEPTED` and ONE `RETRO.ACCEPTED` on the stream.
  (f) tenant isolation. **Phase B adds:** (g) the accepted retro triggers a `prose` narrative with
  `Audience == "team"`, `kind == "retro-narrative"` and `ParentDocumentId` = the retro's document id,
  retrievable through the 39-11 lineage API filtered by audience. **Covers AC1, AC3.**
- **Not tested, by design:** AC2's role-scoped Task View delivery (Correction 3 — the resolver is
  fail-closed). A test pins that the workflow performs **no** delivery side effect, so the gap is visible
  rather than implied.

## Risks & Mitigations

- **The narrative cell does not exist in 41-1a and nobody has noticed (Correction 1).** This is the
  story's biggest schedule risk and it is a coordination risk, not a technical one. Mitigation: Phase A is
  designed to be independently shippable on 41-1a alone, so the gap delays only Phase B; file the
  amendment against 41-1a immediately (it is one enum member, one `RolePhaseMap` entry and one prompt
  file — cheap if caught while 41-1a is still open, expensive as a follow-up because it re-moves 41-1a's
  count pins).
- **41-1a is a hard gate on both paths for Phase A.** A human assignee still needs a `(scrum_master,
  facilitate-retro)` cell to bind, and `PromptFileLoader` refuses to boot on a taxonomy cell with no file.
  Mitigation: step 1 is a real gate.
- **41-1a's `scrum_master` alias removal is a live behaviour change** (`RolePhaseMap.cs:239` maps
  `scrum_master → product_owner` today). Mitigation: 41-1a AC5 owns the migration; this story's execution
  tests must run after it lands so the resolved provider chain is the intended one.
- **Three stories independently need `FetchEventWindowActivity` and the `Findings` citation ring** (41-7,
  41-8, 41-11). Mitigation: register both as shared edits before any of the three starts; 41-7's D2/D4
  pin the signatures; whoever is second consumes. Two implementations of the same window read would be
  the second non-reusable scheduler mistake in a different costume.
- **41-7 must be LANDED, not merely scheduled, for the digest input to exist.** The consumed edge is a
  39-11 store read of accepted `Findings`, so 41-7's own blocker (the scheduler seam) does **not**
  transitively block 41-8 — a manually dispatched 41-7 run produces a readable digest. Mitigation: the
  empty-sprint short-circuit (D6) means 41-8 degrades gracefully to "no digests, window only" rather than
  failing.
- **Two `Findings` producers per repo** (41-7's digest, 41-8's retro, 41-11's risk assessment) share the
  type and the 39-11 read's `(issueId, documentType)` key. Mitigation: D2's distinct
  `retro:`/`standup:`/`techdebt:` scope prefixes; asserted disjoint in `RetroBindingHelperTests` and in
  execution scenario (f).
- **Edge-pin collision.** `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is moved by 41-7
  (+1), 41-8 Phase A (+1) and Phase B (+1), 41-9 (+1), 41-10 (+1) and 41-11 (+2). Mitigation: rebase the
  number, keep the comment.

## Est. Effort

| Phase | Step(s) | Work | Days |
|---|---|---|---|
| A | 1 | 41-1a precondition + 41-7 overlap check | 0.25 |
| A | 2–3 | `FetchEventWindowActivity` + `Findings.ValidateWithContext` (**0 if 41-7 landed first, 1.5 otherwise**) | 0–1.5 |
| A | 4 | `RetroEvents` + emitter | 0.25 |
| A | 5 | `RetroBindingHelper` (pure) | 0.5 |
| A | 6 | `RetrospectiveWorkflow` binding | 0.75 |
| A | 7 | Registry edge + four pin bumps | 0.25 |
| A | 8–9 | Helper + structure + Testcontainers suites (a)–(f), full-suite green | 1.25 |
| **A total** | | | **3.25** with 41-7 landed, **4.75** without |
| B | 10–12 | Prose narrative binding + pins + scenario (g) — **mechanical if 41-9 landed first** | 1.0 |
| **Total** | | | **4.25 / 5.75** (story estimate: 3–4 days — it did not account for the two-phase split Correction 1 forces) |

## Blocks / Blocked by

- **Blocked by — hard, Phase A:**
  - **41-1a** — `AgentRole.ScrumMaster`, the `facilitate-retro` cell, `Prompts/scrum_master/_system.md` +
    `facilitate-retro.md`, and the `scrum_master` alias removal. Blocking on **both** the agent and the
    human-assigned path.
  - **Epic 39: 39-2/39-3** (`Findings` registered), **39-6**, **39-7**, **39-8**, **39-10**, **39-11**,
    **39-15** (the `ValidateWithContext` seam D5 rides) — **all landed**, verified in tree.
- **Blocked by — hard, Phase B only:**
  - **41-1c** (`prose` + `Audience` on envelope and `DocumentInstance` + migration + the `retro-narrative`
    kind and `team` audience).
  - **a 41-1a AMENDMENT** minting `(scrum_master, write-retro-narrative)` + its prompt file — **not in
    41-1a's current fifteen-cell scope** (Correction 1). File it.
- **Blocked by — for AC-level claimability:** **39-19** + **39-20** (AC2 — Correction 3). The story must
  state which half it claims.
- **NOT blocked by:**
  - **the tenant-aware scheduled-trigger seam** — unlike 41-7, 41-11, 41-5, 41-16, 41-17 and 41-20, this
    story is triggered by sprint close or direct dispatch, not a cron (D3). 41-8 is one of the few Wave-3
    stories whose Phase A has no unbuilt blocker.
  - **41-1b** — Correction 6 drops the `SprintPlan` consumed edge from Phase A deliberately, keeping 41-8
    off 41-6/41-1b's critical path.
- **Soft / preferred ordering:**
  - **41-7** must be **landed** (not scheduled) for the digest input; landing it first also removes 1.5 d
    from Phase A by supplying `FetchEventWindowActivity` and the citation ring.
  - **41-9** should land before Phase B — it is the designated prose reference implementation, and Phase B
    is a near-verbatim copy of its binding.
  - **41-6** supplies the sprint-close trigger and the `SprintPlan` edge; both are additive follow-ups,
    neither blocks.
- **Blocks / feeds:** **41-3** (retro action items seed backlog candidates) and **41-11** (retro items
  seed debt candidates) — both via the 39-11 store, so this story must land *accepted documents* to
  unblock them.
- **Shared edits:** `FetchEventWindowActivity` and `Findings.ValidateWithContext` (with **41-7** and
  **41-11**); `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`, `HaveCount(16)` today).
  Epic 41 has no `EXECUTION-PLAN.md` to register these in (README, "Planning artifacts this epic does not
  have") — which is why they are listed per story.
