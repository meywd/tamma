# Implementation Plan — Story 41-11: Tech-Debt & Technical-Risk Triage Workflow

> ## ⛔ BLOCKED — this story cannot start, and one of its blockers has no owner
>
> 41-11 is a **scheduled sweep** (its Scope, its AC1). The tenant-aware scheduled-trigger seam it needs
> **does not exist and no story in Epic 41 builds it** — the epic README lists it as the fourth Wave-0
> enabler with owner *"none — must be written"* (`epic-41/README.md:297`). Verified against
> `Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs`: it hardcodes its target workflow
> (`:197-198`), offers a single `int FireAtMinute` rather than a window/cron shape (`:34`), threads **no**
> `tenantId` into the dispatch (`:199-203`), keeps its last-fired window in a per-process field (`:83`),
> and its advisory-lock key `ComputeAdvisoryLockKey(year, dayOfYear, hour)` has **no tenant component**
> (`:241`) — so one tenant's leader suppresses every other tenant's fire. It is not reusable as a pattern
> and this plan does not invent a replacement.
>
> The story is **additionally** hard-blocked on **41-1a** (the `triage-tech-debt` cell does not exist in
> `AgentAction.cs`). Steps 1–11 below are the three workflows and are startable the day 41-1a lands;
> step 12 is the trigger and is `TODO(scheduler-seam)`.

## Scope & Deliverable

When this story is done (and the seam exists), accumulating technical debt is surfaced on a cadence
instead of at incident time. The story ships **three** workflows, not one (D1):

1. **`tech-debt-sweep`** — the scheduler-triggered orchestrator. Scans (via `context-gathering`) and reads
   the DCB/build window (via the `FetchEventWindowActivity` shared with 41-7), enumerates candidate items,
   and dispatches one per-item triage per candidate, then one risk assessment for the top-N. Produces no
   document itself.
2. **`tech-debt-item-triage`** — a THIN BINDING over `document-lifecycle` producing a typed
   `TriageDecision` per item, on the **new** `(architect, triage-tech-debt)` cell (41-1a).
3. **`technical-risk-assessment`** — a THIN BINDING producing a ranked, evidence-cited `Findings`, on the
   existing-but-never-dispatched `(architect, assess-technical-risk)` cell.

Both bindings are thin in the checkable sense (README rule 1 clauses (a)–(f)): one `DispatchWorkflow`
targeting `document-lifecycle`, zero `llm-call`, zero `Finish`, no validate/retry plumbing, a declared
feedback carrier, and a `WorkflowDocumentInterface` row with the edge pin bumped. A `TECH_DEBT.*` family
rides alongside `DOCUMENT.*`, tagged `repository`/`tenantId`.

## Pre-Reading

- `docs/stories/epic-41/story-41-11/41-11-tech-debt-and-technical-risk-triage.md` — the story
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); the scheduler bullet in Dependencies
- `docs/stories/epic-41/story-41-7/implementation-plan.md` — **D2's `FetchEventWindowActivity` and D3's
  window-as-issue-id idempotency are shared with this story**; read them before designing either
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 2 mints
  `(architect, triage-tech-debt)` + its `Prompts/architect/triage-tech-debt.md`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageItemCycleWorkflow.cs` — **the landed per-item
  sweep precedent**: `DefinitionId = "triage-item-cycle"` (`:51`) loops items and dispatches
  `triage-context-gathering` (`:165-168`) then `triage-po-decision` (`:215-218`). D1 copies this shape.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePODecisionWorkflow.cs` — the landed
  `TriageDecision` binding (`:38` `[ResumeBehavior(LatestStateReEntry)]`, `:184-198` the dispatch input,
  the empty-input SKIPPED short-circuit). **This is the file `tech-debt-item-triage` copies.**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ResearchWorkflow.cs` — the landed `Findings` binding
  `technical-risk-assessment` copies
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs` — the four closed vocabularies
  (`TriagePriority` urgent|high|normal|low; `TriageIssueType` bug|feature|chore|question|security|docs;
  `TriageComplexity` trivial|simple|medium|complex|epic; `TriageAutomation` tamma-auto|tamma-assist|
  needs-human) and the required `reasoning`. **Read this before assuming a `tech-debt` category exists —
  it does not** (Correction 3).
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs` — `EMPTY_FINDINGS`, `MISSING_EVIDENCE`,
  the [0,1] range rules
- `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/assess-technical-risk.md` — **read the body**: it
  instructs an `{issues:[{task,severity,category,issue,recommendation}], verdict:{decision,summary,
  blockingIssues}}` review-verdict JSON, NOT a `Findings` shape (Correction 2)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings`,
  `IntentionallyUnbound`, the coverage guard; note the `(product_owner, triage-intake)` entry
  (`TriageDecisionDocumentType.Validate`, five token groups) that this story's item binding mirrors
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`, `:102-123`;
  `TaxonomyDriftBuildTests.cs:110`, `:125-150`, `:460`; `ResumableStandardStructuralTests.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs:36`
  (`DefinitionId = "context-gathering"`)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:445-447` — `InitiatorOnlyTaskAudienceResolver`, why the
  "high-risk items assigned to the architect's Task View" promise is unreachable

## Corrections to the story

1. **"Thin binding over `document-lifecycle`" producing *two* document types and *N* documents is not a
   binding — it is three workflows.** The story's Scope says one workflow `produces: TriageDecision` per
   item **plus** a `Findings` for the top-N. That is structurally impossible in the shipped model:
   `WorkflowDocumentInterface` carries a single `Produces` key (`DocumentTypeRegistry.cs:137-173`), one
   `document-lifecycle` dispatch produces exactly one document, and `ContractBindingTests.Bindings` is
   keyed `(role, action) → ONE CellContract`, so one cell cannot carry two contracts. The landed
   precedent for exactly this shape is `TriageItemCycleWorkflow` → N × `triage-po-decision`. **D1 splits
   into the three workflows named in Scope & Deliverable**; the story's Scope should be reworded from
   "thin binding" to "a sweep orchestrator over two thin bindings".
2. **`Prompts/architect/assess-technical-risk.md` instructs the WRONG shape and this story owns rewriting
   it.** Its shipped body asks for a review verdict (`issues[]` + `verdict{decision,summary,
   blockingIssues}`), which `FindingsDocumentType.Validate` fails closed on — it requires `summary`,
   non-empty `findings[]`, per-finding `title`/`relevance`/`confidence`/`citations`, and
   `overallConfidence`. Binding it as a `Findings` producer without a rewrite fails VALIDATE on every
   draft. Precedent: 39-15 D7 rewrote `triage-intake.md` from the P0–P3 / `ownerRole` vocabulary to the
   `TriageDecision` wire for exactly this reason. The story does not mention this; it is ~0.5 d of real
   work and a `version: 1 → 2` front-matter bump.
3. **`TriageDecision` has no `tech-debt` category and this story must not add one.** `TriageIssueType` is
   a closed `[Wire]` enum (`bug|feature|chore|question|security|docs`) shared with `triage-po-decision`,
   whose prompt template and `ContractBindingTests` entry are pinned against it; adding a member is a
   lockstep vocabulary change that ripples into a landed producer. **Decision: a debt item classifies as
   `chore` (or `security` where the debt is a vulnerability class), with the debt nature carried in the
   REQUIRED `reasoning` field** — which satisfies AC2's "closed-enum classification with required
   reasoning" as written. Record the enum-extension option as considered and rejected. If a future story
   genuinely needs a `tech-debt` member, it is a `TriageDecision`-owning change with its own drift-test
   and prompt updates, not a side effect of this one.
4. **`(architect, assess-technical-risk)` is dispatched by nothing today** — grep-verified across
   `apps/tamma-elsa`: the only non-test references are `AgentAction.cs:47` and `RolePhaseMap.cs:76`. It is
   therefore in neither `ContractBindingTests.Bindings` nor `IntentionallyUnbound`, and the first dispatch
   trips `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`. This story adds the entry. The story treats
   the cell as if binding it were free; it is not.
5. **AC4's `[ResumeBehavior(LatestStateReEntry)]` applies to all THREE workflows, and is correct for each.**
   None of them holds a bookmark (the accept-gate suspend lives inside the dispatched `document-lifecycle`
   child), so `Both` would fail 39-10 clause (b). The sweep orchestrator also declares
   `LatestStateReEntry` — the same posture `TriageItemCycleWorkflow` took at its 39-15 burn-down.
6. **"high-risk items assigned to the architect/senior-dev role's Task View" is unreachable.** 39-19 ships
   no Task View and `ITaskAudienceResolver` is stubbed fail-closed by `InitiatorOnlyTaskAudienceResolver`
   (`Program.cs:445-447`); 39-20 has not landed. AC3's "output consumable by 41-3/41-6 as backlog
   candidates" IS claimable — it is a 39-11 store read, not a routing hop. Claim the store half; state
   the routing half as wired-but-unreachable.
7. **`context-gathering` is a real dependency and it exists** (`ContextGatheringWorkflow.cs:36`). The
   story's Dependencies line is accurate here.

## Design Decisions

- **D1 — three workflows: `tech-debt-sweep` → N × `tech-debt-item-triage` → 1 ×
  `technical-risk-assessment`.** Definition ids all free today. The sweep is a composite (the
  `TriageItemCycleWorkflow` shape: `hasMoreItems` loop → `extractCurrentItem` → `DispatchWorkflow` with
  `WaitForCompletion` → `incrementItem`), declares **no** `Produces` edge, and holds the per-item loop and
  the ranking. Each binding is thin and independently testable. This is also what makes AC1's "fail-closed
  per item" real: one item's escalation does not abort the sweep, because each item is its own lifecycle
  instance with its own typed exit.
- **D2 — the sweep's inputs come from two sources, one shared with 41-7.** (i) `context-gathering`
  (`DispatchWorkflow("context-gathering")`, `WaitForCompletion = true`) for the codebase scan; (ii)
  `FetchEventWindowActivity` — **the activity 41-7's D2 designs** — for the DCB/build-signal window
  (prefixes `CI.`, `DEPLOY.`, `TEST.`, `DOCUMENT.`, `BLOCKER.`). Whichever of 41-7/41-11 lands first
  builds it; the other consumes it unchanged. Register this as a shared edit before either starts.
- **D3 — the sweep window is the lifecycle key, per item.** Generalising 39-15's
  `CreationBindingHelper.ScopeIssueId` and 41-7's D3: each item binding runs on
  `issueId = "techdebt:{repository}:{windowStartUtc:yyyy-MM-dd}:{itemKey}"` and the risk assessment on
  `"techrisk:{repository}:{windowStartUtc:yyyy-MM-dd}"`, where `itemKey` is a stable, content-derived
  identifier for the debt item (normalised `path#symbol` or the rule id, NOT an ordinal — an ordinal
  would re-key every item when one is added). Consequence: **a duplicate sweep for the same window
  re-enters each item at `Complete` and short-circuits**, so AC1's tenant-scoped idempotency is delivered
  by the existing 39-10 machinery. The scheduler seam still needs its own durable fire-once record.
- **D4 — `tech-debt-item-triage` is `TriagePODecisionWorkflow` with a different cell.** Dispatch input:
  `documentType = "triage-decision"`, `producerRole = AgentRole.Architect.ToWire()`,
  `producerAction = AgentAction.TriageTechDebt.ToWire()`, `feedbackVariableName = "contextFindings"`
  (must be a variable 41-1a's template DECLARES — the render-drop lesson; named lockstep with 41-1a),
  plus the `issueId`/`correlationId`/`tenantId`/`acceptanceRulesJson` passthroughs. The review stage is
  the doc-type-aware panel: `RolePhaseMap.GetPanelActionForRole(role, "triage-decision")` selects the
  TRIAGE lens (security → `assess-vulnerability`, developer/tester → `triage-defect`, devops →
  `diagnose-incident`, `RolePhaseMap.cs:404-436`) — **no new selector arm is needed and none is added.**
- **D5 — `technical-risk-assessment` is `ResearchWorkflow` with a different cell, plus the 41-7 evidence
  ring.** Dispatch input: `documentType = "findings"`,
  `producerAction = AgentAction.AssessTechnicalRisk.ToWire()`, and — if 41-7's
  `Findings.ValidateWithContext` citation ring has landed — a `validationContextJson` evidence index
  built from the sweep's window + the accepted item decisions, so "risk `Findings` cite concrete
  evidence" (AC2) is executable rather than aspirational. If 41-7 has not landed, ship without the ring
  and file the follow-up; do NOT duplicate the override.
- **D6 — the rewritten `assess-technical-risk.md` (Correction 2).** Front matter keeps
  `variables: role, workItemJson, planJson, conventions` and **adds `contextFindings`** as the feedback
  carrier; body becomes role framing → the sweep's scan + window + accepted item decisions → ranking
  instruction → `FindingsDocumentType.RenderContract()`'s JSON shape. `version: 1 → 2`.
- **D7 — `TECH_DEBT.*` is a five-member family.** `SWEEP.STARTED` / `.ITEM` / `.COMPLETED` (the story's
  three) plus `.ITEM_FAILED` (LOUD, per-item typed exit — AC1's fail-closed-per-item audit row) and
  `.SWEEP_FAILED` (LOUD). New `Tamma.Activities/TechDebt/TechDebtEvents.cs` +
  `EmitTechDebtEventActivity.cs`. Tagged `repository`, `tenantId`, `windowStartUtc`, `itemKey`.
- **D8 — acceptance policy is passed through, twice.**
  `AcceptanceDefaults.For(DocumentTypeKey.TriageDecision)` falls to the `_ => Rules` catch-all (single
  `architect`, unanimous) — which is actually a reasonable default here — and `For(Findings)` likewise.
  **This story edits `AcceptanceDefaults.cs` not at all** (both types are shared with landed producers);
  the "risk above a configured threshold always escalates" behaviour is a caller-supplied
  `acceptanceRulesJson` with an `EscalationClass`, not code.
- **D9 — the lockstep set, enumerated.** (i) `DocumentTypeRegistry.BuildSeed` += **two** rows:
  `("tech-debt-item-triage", empty, DocumentTypeKey.TriageDecision, false)` and
  `("technical-risk-assessment", new[]{ DocumentTypeKey.TriageDecision }, DocumentTypeKey.Findings, false)`
  — the sweep gets **no** row (it produces nothing); (ii)
  `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` `HaveCount(16)` → **`+2`**; (iii) that
  test's `reconciled` array += both ids; (iv) `ContractBindingTests.Bindings` += **two** entries —
  `[("architect","triage-tech-debt")] = new("TriageDecisionDocumentType.Validate", ["priority","type",
  "complexity","automation","reasoning"])` (mirroring the `triage-intake` entry) and
  `[("architect","assess-technical-risk")] = new("FindingsDocumentType.Validate", [the seven Findings
  groups])`; (v) `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` += both binding class names (the
  sweep contributes no `(role, action)` pair and must NOT be listed); (vi) NO
  `ResumableStandardStructuralTests` allowlist entries. **The taxonomy count pins are 41-1a's — this
  story must not touch them.**

## Implementation Steps

1. **Precondition gate (no code).** Verify `AgentAction.TriageTechDebt` exists, `(architect,
   triage-tech-debt)` passes `RolePhaseMap.IsRoleEligibleForPhase`, and
   `Prompts/architect/triage-tech-debt.md` exists carrying D9(iv)'s five token groups and a declared
   `contextFindings` carrier. Any gap is a 41-1a defect — file it there, do not patch the taxonomy here.
   Also check whether 41-7 has landed `FetchEventWindowActivity` and `Findings.ValidateWithContext`
   (D2/D5) — the answer changes steps 3 and 8.
2. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/assess-technical-risk.md`** per D6
   (Correction 2). Verify `PromptFileLoader` still loads (front-matter keys exactly
   `variables`/`enableTools`/`maxTokens`/`version`).
3. **CREATE-OR-CONSUME `FetchEventWindowActivity`** (D2) — build it per 41-7's D2 if 41-7 has not landed;
   otherwise consume unchanged.
4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/TechDebt/TechDebtEvents.cs` +
   `EmitTechDebtEventActivity.cs`** (D7).
5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/TechDebtBindingHelper.cs`** — pure,
   Elsa-free: `BuildItemKey(candidate)` (stable, content-derived — D3), `BuildItemIssueId(...)`,
   `BuildRiskIssueId(...)`, `EnumerateCandidates(scanJson, windowJson)` (the deterministic candidate
   extraction — **the only place a heuristic lives, and it is pure and unit-tested**),
   `RankTopN(acceptedDecisions, n)`, `BuildEvidenceContext(...)`, `BuildFailureDetail(exit)`.
   `ReadLifecycleResult`/`IsAccepted` come from `LifecycleBindingHelper`.
6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TechDebtItemTriageWorkflow.cs`** (D4) —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, the `TriagePODecisionWorkflow` skeleton with an
   empty-input SKIPPED short-circuit.
7. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TechnicalRiskAssessmentWorkflow.cs`** (D5) —
   same posture, `documentType = "findings"`.
8. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TechDebtSweepWorkflow.cs`** (D1/D2/D3) —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`; graph: `ReadInputs` → `ComputeReEntryPosition` →
   `FreshRun` → `EmitSweepStarted` → `GatherContext` (`context-gathering`) → `FetchEventWindow` →
   `ExtractCandidates` → `HasMoreItems` loop → `DispatchItemTriage`
   (`DispatchWorkflow("tech-debt-item-triage")`, `WaitForCompletion = true`) → `RecordItemOutcome`
   (`EmitTechDebtEvent` `.ITEM` / `.ITEM_FAILED`, **never abort the loop** — AC1) → `IncrementItem` →
   (loop exit) `DispatchRiskAssessment` (`DispatchWorkflow("technical-risk-assessment")`) →
   `EmitSweepCompleted` → `ExposeOutput`. **Zero `Finish`; three `DispatchWorkflow` sites, none targeting
   `llm-call`.**
9. **MODIFY `DocumentTypeRegistry.cs` + the pins** — D9(i)–(v).
10. **CREATE the tests** — see Test Plan.
11. **Green the suite** — full `dotnet test` + `has-pending-model-changes` clean.
12. **`TODO(scheduler-seam)` — the trigger.** NOT buildable today. Required contract, identical to 41-7's:
    a tenant component in the advisory-lock key; `tenantId` + `repository` + window bounds threaded into
    the dispatch input; a **persisted** last-fired window; a window/cron shape. Record it as this story's
    consumer requirement for whoever writes the seam.

## Data & Migrations

None **for this story**. `TriageDecision`/`Findings` payloads are JSONB in 39-11's tables; `TECH_DEBT.*`
and `DOCUMENT.*` ride the existing drain → `EventRepository` → `domain_events` path.
`has-pending-model-changes` stays clean. *The scheduler seam's persisted last-fired table is the seam
story's migration.*

## Events

- **Emits:** `TECH_DEBT.SWEEP.STARTED` (fresh sweeps only), `.ITEM` (per accepted item, data: `itemKey`,
  `priority`, `complexity`, `documentId`), `.ITEM_FAILED` (LOUD, per non-accept item exit, `Detail` naming
  the typed outcome wire), `.COMPLETED` (data: `candidateCount`, `acceptedCount`, `riskDocumentId`),
  `.SWEEP_FAILED` (LOUD). Tags `repository`, `tenantId`, `windowStartUtc`, `itemKey`, `correlationId`.
- **Consumes (the window read):** `CI.`, `DEPLOY.`, `TEST.`, `DOCUMENT.`, `BLOCKER.` prefixes via
  `IEventRepository.ListByTenantAsync`. Read-only.
- **Emitted by the machinery this story wires in:** `DOCUMENT.*` incl. the panel markers,
  `APPROVAL.*`, `ESCALATION.TRIGGERED`.

## Test Plan

- **`TechDebtBindingHelperTests` (pure, the largest unit suite)** — `BuildItemKey` is stable and
  content-derived (inserting a candidate does not re-key the others — the D3 invariant, and the one that
  makes idempotency work); `BuildItemIssueId`/`BuildRiskIssueId` are deterministic and
  tenant/repo/window-folded, and the `techdebt:`/`techrisk:` prefixes never collide with 41-7's
  `standup:`; `EnumerateCandidates` on empty/valid/malformed scan+window input (fail-closed to zero
  candidates, never a throw out of a routing lambda); `RankTopN` is a total order with no ties;
  `BuildFailureDetail` names each reachable outcome wire.
- **`TechDebtItemTriageWorkflowStructureTests` + `TechnicalRiskAssessmentWorkflowStructureTests`** — the
  `TaskCreationWorkflowStructureTests` clause set applied to each: stable `DefinitionId`; threads
  `TenantId`; no retry-plumbing variables; **exactly one `DispatchWorkflow`, literal id
  `document-lifecycle`**; zero `llm-call`; **zero `Finish`**; `ComputeReEntryPositionActivity` present;
  declares `LatestStateReEntry`; no `Wait*` node; `ScanLifecycleBindingDispatches()` contains
  `(…, architect, triage-tech-debt)` and `(…, architect, assess-technical-risk)` respectively;
  `MaterializeDispatchInput` yields `documentType == "triage-decision"` / `"findings"` and the declared
  `feedbackVariableName`. **Covers AC4 (structure half), rule-1 clauses (a)–(e) for both bindings.**
- **`TechDebtSweepWorkflowStructureTests`** — three `DispatchWorkflow` sites with literal ids
  `context-gathering`, `tech-debt-item-triage`, `technical-risk-assessment`; **zero** `llm-call`; **zero
  `Finish`**; `FetchEventWindowActivity` present; declares `LatestStateReEntry`; the per-item failure edge
  routes to `IncrementItem`, **not** to a terminal (the AC1 fail-closed-per-item structural proof, walked
  on the built graph).
- **Pin tests (self-verifying)** — `WorkflowInterfaceGraphTests` (**+2**, both ids in `reconciled`, the
  sweep absent); `ContractBindingTests` (both new entries satisfied by 41-1a's template and the rewritten
  `assess-technical-risk.md`; `KnownContractViolations` stays empty); `TaxonomyDriftBuildTests` (both
  binding classes in `ExpectedContributingWorkflows`, the sweep NOT);
  `ResumableStandardStructuralTests` green with **no** allowlist entries for any of the three.
- **`TechDebtSweepExecutionTests` (Testcontainers, shared 39-6/39-10 fixture)** — (a) happy path: seeded
  scan + window → 3 candidates → 3 accepted `TriageDecision`s + 1 accepted risk `Findings`;
  `.ITEM` × 3 + `.COMPLETED` on the stream; all four documents readable by their scoped ids through
  `IDocumentInstanceRepository`. (b) **AC1 fail-closed per item:** candidate #2's lifecycle exits
  `escalated` → `.ITEM_FAILED` for #2, `.ITEM` for #1 and #3, sweep still `.COMPLETED`, risk assessment
  still runs over the accepted subset. (c) **AC1 idempotency (D3):** re-run the same window → every item
  re-enters at `Complete`, zero new documents, exactly one `DOCUMENT.ACCEPTED` per item across the whole
  stream. (d) **AC2 closed enums:** a draft with `type: "tech-debt"` is rejected with
  `OUT_OF_VOCABULARY` → repair/revise → accepted as `chore` with the debt nature in `reasoning`
  (Correction 3's decision, proven). (e) **AC3:** the accepted `TriageDecision`s are retrievable by
  `(tenant, issueId, documentType)` in the shape 41-3/41-6 would read — a store-read assertion, not a
  routing one. (f) tenant isolation: two tenants' sweeps produce disjoint document sets.
- **Not tested, by design:** the Task View routing half of the story's Orchestrator section (Correction
  6). A test pins that the workflows perform no delivery side effect, so the gap is visible.

## Risks & Mitigations

- **The scheduler seam is unowned.** Mitigation: steps 1–11 are seam-independent and the three workflows
  are dispatchable by API, so the story delivers an on-demand debt sweep; step 12's contract is written
  down for the seam's author. Do NOT build a 41-11-local scheduler — six other stories need the same seam.
- **41-1a is a hard gate on both paths.** `triage-tech-debt` does not exist in `AgentAction.cs`; a human
  assignee still needs a cell to bind, and `PromptFileLoader` refuses to boot on a taxonomy cell with no
  file. Mitigation: step 1 is a real gate.
- **`EnumerateCandidates` is the one heuristic in the story and heuristics rot.** Mitigation: it is a
  pure, total, fail-closed function in `Tamma.Core`-adjacent helper code with its own unit matrix; it
  never throws out of a routing lambda; quality of the candidate set is explicitly the review panel's job
  and the accept gate's, not an AC (the same posture 41-10's story takes for design depth).
- **N lifecycle instances per sweep is an unbounded fan-out.** A first sweep on a large repo could
  enumerate hundreds of candidates, each a full produce→validate→review→accept ring with LLM cost.
  Mitigation: a `MaxItemsPerSweep` input (default modest, e.g. 20) with the truncation recorded in
  `.COMPLETED`'s data and in the risk `Findings` summary — visible truncation, never a silently partial
  sweep. This is the same posture as 41-7's window `MaxEvents`.
- **Rewriting `assess-technical-risk.md` breaks a consumer.** Mitigation: grep-verified — no dispatch site
  exists, so there is no consumer. Strictly additive risk.
- **Edge-pin collision.** This story moves `Declared_edge_count_is_pinned` by **+2**, while 41-9 and 41-10
  each move it by +1. Mitigation: rebase the number, keep the comment; the pin is deliberately a conscious
  edit, one per producing workflow.
- **`FetchEventWindowActivity` gets built twice.** Mitigation: register the shared edit with 41-7 before
  either starts; whoever is second consumes, and the activity's inputs/outputs are pinned in 41-7's D2.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1a precondition + 41-7 overlap check | 0.25 |
| 2 | `assess-technical-risk.md` rewrite to the `Findings` wire | 0.5 |
| 3 | `FetchEventWindowActivity` (**1.0 if this story builds it, 0 if 41-7 landed first**) | 0–1.0 |
| 4 | `TechDebtEvents` + emitter | 0.25 |
| 5 | `TechDebtBindingHelper` (item keys, candidates, ranking) | 1.0 |
| 6–7 | The two thin bindings | 1.0 |
| 8 | `TechDebtSweepWorkflow` (the per-item loop) | 1.0 |
| 9 | Registry edges (+2) + pin bumps | 0.25 |
| 10 | Helper + three structure suites + Testcontainers scenarios (a)–(f) | 1.75 |
| 11 | Full-suite green, review polish | 0.25 |
| 12 | Scheduler trigger | **not estimable — the seam does not exist** |
| **Total (steps 1–11)** | | **6.25** with the shared activity, **5.25** without (story estimate: 4–5 days — the delta is Correction 1's three-workflow split and Correction 2's prompt rewrite) |

## Blocks / Blocked by

- **Blocked by — hard, no owner, cannot be worked around:**
  - **The tenant-aware scheduled-trigger seam.** No Epic 41 story builds it
    (`epic-41/README.md:297`, `:454-472`). AC1's "scheduled, tenant-scoped" half is unreachable without
    it. Shared with **41-5**, **41-7**, **41-16**, **41-17** (PR sweep), **41-20**, **41-23**.
- **Blocked by — hard, owned:**
  - **41-1a** — mints `(architect, triage-tech-debt)` + `Prompts/architect/triage-tech-debt.md`. Blocking
    on **both** execution paths.
  - **Epic 39: 39-2/39-3/39-4** (`TriageDecision` + `Findings` registered), **39-6**, **39-7** (the
    doc-type-aware triage panel), **39-8**, **39-10**, **39-11**, **39-15** (the panel-as-review-stage
    semantics D4 relies on) — **all landed**, verified in tree.
  - **`context-gathering`** — landed (`ContextGatheringWorkflow.cs:36`).
- **Blocked by — for AC-level claimability:** **39-17**, **39-19**, **39-20** (the Task View routing half
  — Correction 6).
- **Soft / preferred ordering:** **41-7** — it designs `FetchEventWindowActivity` (D2) and
  `Findings.ValidateWithContext` (D5's evidence ring). Landing 41-7 first removes 1.0 d from this story
  and avoids two implementations of the same activity. Neither strictly blocks the other.
- **NOT blocked by:** **41-1b** (reuses `TriageDecision` + `Findings`) and **41-1c** (produces typed
  documents, not prose). 41-11 appears in neither README table — correctly.
- **Blocks / feeds:** **41-3** (backlog ordering consumes the accepted `TriageDecision`s as candidates)
  and **41-6** (sprint planning) — both via the 39-11 store, so this story must land *accepted documents*,
  not a routing hop, to unblock them. Also seeds **41-18** (refactor planning) with ranked debt items.
- **Shared edits:** `FetchEventWindowActivity` (with **41-7**);
  `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`, `HaveCount(16)` today) — this story
  **+2**, 41-9 +1, 41-10 +1, 41-7 +1, plus every other Epic 41 producer. Epic 41 has no `EXECUTION-PLAN.md`
  to register these in (README, "Planning artifacts this epic does not have") — that gap is why these
  collisions are listed per story.
