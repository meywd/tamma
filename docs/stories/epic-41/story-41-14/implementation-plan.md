# Implementation Plan — Story 41-14: Exploratory Test Charter Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `exploratory-charter` exists as a **thin binding over
`document-lifecycle`** (the 39-13/39-15 recipe), producing a typed **`Findings`** document — the charter
mission plus the session's evidence-cited observations — from the `(tester, exploratory-test)` producer
cell, optionally consuming an accepted `TestPlan` (41-13) and `AcceptanceCriteria` (41-2) as context. One
`DispatchWorkflow` targeting `document-lifecycle`, zero `Finish`, zero `llm-call`, zero parsing, no retry
plumbing, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, 39-10 gate green with no allowlist entry.

The story also **rewrites `Prompts/tester/exploratory-test.md` from file-format code output to the
`Findings` contract** (Correction C1 — the shipped cell today instructs the model to *write a test file*,
not to author a document), adds one `ContractBindingTests.Bindings` entry with authority
`FindingsDocumentType.Validate`, declares one `WorkflowDocumentInterface` row and bumps
`WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` by one, and emits `EXPLORATORY.*` alongside
`DOCUMENT.*`.

This is the **cheapest binding in the 41-12..41-16 set**: it mints no role, no action cell, and no
document type, and it reuses the landed `Findings` type verbatim. It is genuinely Wave-0-independent.

## Pre-Reading

- `docs/stories/epic-41/story-41-14/41-14-exploratory-test-charter.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); the Epic 42 tool-governance caveat the story
  already carries
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ResearchWorkflow.cs` — **THE reference `Findings`
  producer.** 39-13 migrated it; `(product_owner, research)` → `documentType findings` is the exact shape
  41-14 clones, and its `ContractBindingTests` entry (`:91-96`) is the token set to copy
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriageContextGatheringWorkflow.cs` — the second landed
  `Findings` producer (39-15 D5, the split `(developer, triage-context-scan)` cell), and the precedent for
  **minting a document-producing cell out of a free-text one**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs` — the minimal binding
  skeleton (no consumed-document fetch); `TaskCreationWorkflow.cs:149-166` for the `FreshRun` +
  `FetchLatestAcceptedDocumentActivity` variant this story needs
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape the epic README names
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs` — the wire (`topic`/`summary`/`findings[
  {title,summary,relevance,confidence,citations,rank}]`/`overallConfidence`) and the nine violation codes,
  **especially `EmptyFindings` (`:58`, emitted at `:110-116`)** — the code that makes C2 true
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/exploratory-test.md` — the cell being rewritten
- `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/research.md` — the shape a `Findings`-producing
  template must land on
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` +
  `Helpers/CreationBindingHelper.cs` (`DeriveIssueId`, `BuildFailureDetail`) — shared fail-closed cores;
  do not fork
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` (`:82`),
  `IntentionallyUnbound` (`:286`) incl. the "code/file-format output, consumed only via the success flag"
  class this cell would otherwise join, the universal DocumentType-authority pin (`:626`), the
  prose-or-code classification pin (`:655`), the clause-(c) staleness guard (`:725-737`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:460`/`:507`/`:125`
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174` +
  `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`, `:102-123`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:127-132` — `For(Findings)` falls
  through `_ => Rules`: **single `architect`, unanimous** (see D6)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-764` — the six registered `IToolExecutor`s (the Epic 42
  ceiling the story's caveat already names)
- `.dev/findings/39-15-remaining-producers-migration.md` — the distilled recipe
- **All story-referenced paths exist and were verified in tree.** Nothing this story consumes is
  plan-only. `TestPlan` (41-13) is optional context, not a prerequisite.

## Corrections to the story

- **C1 — `Prompts/tester/exploratory-test.md` today produces a TEST FILE, not a document; rewriting it is
  in scope and the story does not say so.** The shipped cell declares
  `variables: role, testTarget, sourceCode, conventions`, `enableTools: true`, and closes with
  ```
  File format:
  ```path/to/file
  // test contents
  ```
  ```
  It instructs the model to *write exploratory test code*. `FindingsDocumentType.Validate` would reject
  that reply as `MALFORMED_PAYLOAD` on every produce. AC1 ("`Findings` cite concrete evidence") is
  unreachable until the cell is rewritten to `FindingsDocumentType.RenderContract()`'s shape. This is the
  same class of edit 39-15 made for `(senior_developer, debug-rootcause)` — "needs `debug-rootcause.md`
  rewritten to the Diagnosis contract", `.dev/findings/39-15-remaining-producers-migration.md`.
- **C2 — AC1's "empty session ⇒ valid empty findings" is impossible against the landed type, and must be
  restated.** `FindingsDocumentType` emits `EMPTY_FINDINGS` for a zero-length `findings` array by
  *deliberate* design — the doc-comment at `Findings.cs:53-57` says so: "an empty list is a violation,
  NOT a valid 'nothing found' (documented per AC3)", inherited from `ResearchParsing`'s fail-closed
  baseline. The three honest options are:
  (i) **restate the AC** — a session that found nothing emits one finding whose `title` is the charter
  mission and whose `summary` records "no anomalies observed", citing what was exercised. Evidence still
  required, `EMPTY_FINDINGS` still fires for a genuinely empty payload. **This plan takes (i)** — it is
  zero-cost, preserves the epic's evidence discipline, and touches no shared type;
  (ii) relax `EMPTY_FINDINGS` for this producer — impossible, `Validate` is per **type**, not per cell,
  and would silently loosen `research` / `triage-context-gathering`;
  (iii) a new document type — rejected by the epic's "reuse first" rule and by 41-1b's scope.
  Option (i) is recorded as D5 and pinned by a test.
- **C3 — `[ResumeBehavior]` mode: `LatestStateReEntry`, not `Both`.** AC3 says `Both`. The binding never
  suspends on a bookmark of its own — the accept-gate suspend is inside the dispatched
  `document-lifecycle` child while the parent waits on `WaitForCompletion = true`. `Both` fails
  `ResumableStandardStructuralTests` clause (b) (it requires a graph node whose type is in the
  declaration's `SuspendActivities` **and** in `LifecycleBookmarks.CanonicalSuspendActivities`). Every
  landed producer binding declares `LatestStateReEntry` (`TaskCreationWorkflow.cs:47`,
  `TestCaseCreationWorkflow.cs:37`). AC3's real requirement — "39-10 structural test green without
  allowlist" — is unchanged.
- **C4 — the story's Autonomy 70–84 row ("agent drafts the charter; a human runs the session and records
  findings") describes a TWO-DOCUMENT flow that one binding cannot express, and no AC covers it.** One
  `document-lifecycle` dispatch produces one document from one producer cell. A charter-then-session split
  is either (a) two runs of this binding on the same issue — impossible, `findings` is a single type key
  and the second run's `ComputeReEntryPosition` short-circuits to `Complete` on the first accepted
  document (the 39-15 D2 two-plans hazard, exactly); or (b) one document containing both, filled in two
  passes. **This plan takes (b)** (D3): the charter *mission* is the `topic` + `summary` of the same
  `Findings` document and the observations are its `findings[]`. At low autonomy the produce step is
  assigned to a human who fills the whole document; at high autonomy the agent does. This is what rule 4
  ("human-or-agent execution") actually permits. *(If a genuine two-document split is wanted later, it
  needs producer-scoped issue ids — `CreationBindingHelper.ScopeIssueId` — and is a separate story.)*
- **C5 — AC2 "defect findings integrate with triage/PR-triage" has no reachable consumer and must be
  scoped to readability.** 41-17 (PR triage) is unbuilt; `triage-po-decision` consumes `[findings]`
  (`DocumentTypeRegistry.cs:164`) but is dispatched from `TriageItemCycleWorkflow` on an *intake* item,
  not from an exploratory session, and nothing routes an accepted exploratory `Findings` into it. Wiring
  a route is a `TriageItemCycleWorkflow` edit — out of scope. AC2 is therefore scoped to: the accepted
  `Findings` is retrievable for the issue through the same `FetchLatestAcceptedDocumentActivity` seam
  `triage-po-decision` uses, and the `BuildSeed` row declares the produced edge. The route is filed
  forward to 41-17.
- **C6 — the story's `Events` line is a state list, not an event family.**
  `EXPLORATORY.CHARTER.STARTED` / `.SESSION` / `.FINDINGS` has no failure member; every landed family has
  one (`DECOMPOSITION.FAILED`, `DEPLOY.ROLLBACK.FAILED`, …) and the binding's `rejected`/`escalated` exits
  need somewhere to land. D7 adds `.FAILED`. `.SESSION` is also unemittable as written — nothing in the
  binding observes "a session happened" separately from the produce step; it is folded into `.STARTED`'s
  data or dropped.

## Design Decisions

- **D1 — New workflow class + new `DefinitionId` `exploratory-charter`.** Nothing dispatches an
  exploratory charter today. Inputs: `issueId`, `repository`, `issueNumber`, `workItemJson`, `testTarget`,
  `contextIds`, `tenantId`, `acceptanceRulesJson?`. Outputs: `status`, `outcome`, `documentId`,
  `findingsJson`, `parentDocumentId` (the consumed `TestPlan` id, `""` when absent), `error`.
  `builder.Version = WorkflowVersions.ComputedVersion`.
- **D2 — Producer-scoped issue id, because `findings` is a CONTESTED type key.** Unlike `test-spec` or
  `diagnosis`, `findings` already has two producers (`research` and `triage-context-gathering`,
  `DocumentTypeRegistry.cs:141`, `:163`) and the 39-11 latest-accepted read scopes by
  `(issueId, documentType)` with **no producer filter** (the gap filed to 39-11 in
  `.dev/findings/39-15-remaining-producers-migration.md`). An exploratory binding keyed on the bare issue
  id would `ComputeReEntryPosition("findings", issueId)` onto an accepted *research* report and
  short-circuit to `Complete` on every run. **The binding keys on
  `CreationBindingHelper.ScopeIssueId(issueId, "exploratory-charter")`** — the landed 39-15 D2 workaround.
  *The story does not mention this; it is the single highest-risk omission in it.*
- **D3 — One document carries charter + observations (C4).** `Findings.Topic` = the charter mission
  ("what we are exploring and why"); `Findings.Summary` = the session overview;
  `Findings.Items[]` = the observations, each with `title` / `summary` / `citations` (what was exercised)
  / `relevance` / `confidence`, `rank` optional-but-all-or-nothing. No second document, no second type.
- **D4 — Consumed `TestPlan` / `AcceptanceCriteria` are OPTIONAL context read behind the `FreshRun`
  gate.** Two `FetchLatestAcceptedDocumentActivity` nodes (or one, parameterised twice — two nodes is
  clearer and the structure test counts them), gated on `positionStage == "produce"`, exactly as
  `TaskCreationWorkflow.cs:150-166`. `Found = false` is the normal path, not an error: the charter is
  still authorable. **Neither type is a prerequisite for this story** — if 41-1b/41-13 have not landed,
  the `TestPlan` fetch is simply omitted and added later (a one-node addition), which is why 41-14 is
  Wave-0-independent.
- **D5 — "Nothing found" is a one-finding document, not an empty one (C2).** The rewritten template (D8)
  instructs it explicitly, and a `FindingsDocumentType` fixture pins that a genuinely empty array still
  yields `EMPTY_FINDINGS`. The AC text is restated in the story's own words rather than silently
  reinterpreted.
- **D6 — Acceptance policy: inherit `Findings`' shipped default; do not edit the shared row.**
  `AcceptanceDefaults.For(Findings)` falls through `_ => Rules` (`:131`) — single `architect`, unanimous,
  autonomy 70. That is a plausible-but-odd reviewer for an exploratory session (a `senior_developer` or
  `tester` lens is the domain answer). Changing it would silently change `research` and
  `triage-context-gathering`, so this story changes **nothing** and passes `acceptanceRulesJson` through
  for per-run override. One integration test makes the default observable and one proves the override.
  The domain preference is filed, not patched.
- **D7 — `EXPLORATORY.*` gets its own emitter activity, house pattern (C6).**
  `Tamma.Activities/Exploratory/ExploratoryEvents.cs` — `EXPLORATORY.CHARTER.STARTED`,
  `EXPLORATORY.CHARTER.FINDINGS` (on acceptance, data `observationCount`, `defectCount`,
  `consumedTestPlanId`), `EXPLORATORY.CHARTER.FAILED` — plus `EmitExploratoryEventActivity`, cloned from
  `Decomposition/EmitDecompositionEventActivity.cs`. `.SESSION` is dropped (C6). Emissions are gated on
  the re-entry position (39-12 D3) so re-entry cannot double-emit.
- **D8 — The prompt cell is REWRITTEN to `FindingsDocumentType.RenderContract()`'s shape, by hand (C1).**
  No prompt file carries a 39-16 generated-region marker. Front matter changes:
  `variables: role, testTarget, contextFindings, conventions` (dropping `sourceCode`, adding the
  DECLARED feedback carrier `contextFindings` — the render-drop lesson), `enableTools: true` **kept**
  (exploration wants tools; the Epic 42 caveat the story already carries explains that "tools" means the
  six coding executors at `Program.cs:753-764`), `maxTokens: 8192`, **`version: 1 → 2`**
  (`write-tests.md` precedent). Body: charter-mission framing + the `Findings` JSON contract + the
  explicit "nothing found is one finding, not zero" instruction (D5). Token groups for the `Bindings`
  entry are cloned from `(product_owner, research)` (`ContractBindingTests.cs:91-96`) unchanged:
  `"summary"`, `"findings"`, `"title"`, `"relevance"`, `"confidence"`, `"citations"`,
  `"overallConfidence"`.
- **D9 — Pure helper `ExploratoryBindingHelper` in `Workflows/Helpers/`, Elsa-free, total, fail-closed.**
  New: `BuildExplorationContext(testPlanJson, criteriaJson, testTarget) → string`,
  `CountObservations(findingsJson) → int` (0 on unreadable), `CountDefectObservations(findingsJson) → int`
  (observations whose `title`/`summary` carry the defect marker the template instructs — used only for the
  `.FINDINGS` event data, never for routing). Reuses `LifecycleBindingHelper.ReadLifecycleResult` /
  `IsAccepted` and `CreationBindingHelper.DeriveIssueId` / `ScopeIssueId` / `BuildFailureDetail`.
- **D10 — Drift-gate bookkeeping, enumerated (rule 1 clause (f)).** One `Bindings` entry for
  `(tester, exploratory-test)` with authority `"FindingsDocumentType.Validate"` and the research token
  groups; one `BuildSeed` row `("exploratory-charter", consumes [], produces findings, false)` —
  **`consumes` stays empty** because the `TestPlan` edge is optional and `WorkflowDocumentInterface`
  models a declared contract, not an opportunistic read *(add the `test-plan` key only once 41-13 has
  landed and the read is unconditional)*; `WorkflowInterfaceGraphTests.cs:45` `HaveCount(N) → HaveCount(N+1)`
  with the reason in the comment; the definition id appended to that file's `reconciled` list (`:102-123`);
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (`:125`) gains `"ExploratoryCharterWorkflow"`.
  **No** `AgentAction` / `RolePhaseMap` / `SystemPrompts` count pin moves — `(tester, exploratory-test)`
  already exists (`AgentAction.cs:81`, `RolePhaseMap.cs:120`) with a shipped template. **No**
  `DocumentTypeKey` / `DocumentTypeRegistry` count pin moves — `findings` is registered
  (`DocumentTypeRegistry.cs:30`).

## Implementation Steps

1. **Precondition check (no code).** Confirm in tree and compiling: `document-lifecycle`,
   `ComputeReEntryPositionActivity`, `FetchLatestAcceptedDocumentActivity`, `LifecycleBindingHelper`,
   `CreationBindingHelper`, `FindingsDocumentType`, `ResumableStandardStructuralTests` — all verified
   present at plan time. Decide with the 41-13 owner whether `TestPlan` will exist at merge time; if not,
   omit that fetch node (D4) and leave `consumes` empty (D10).

2. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/exploratory-test.md`** (D8, C1) to the
   `Findings` contract; bump `version` to 2; front matter per D8; include the D5 "nothing found is one
   finding" instruction verbatim.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Exploratory/ExploratoryEvents.cs` +
   `EmitExploratoryEventActivity.cs`** (D7).

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ExploratoryBindingHelper.cs`** (D9).

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ExploratoryCharterWorkflow.cs`** (D1–D4),
   copying `TaskCreationWorkflow`'s skeleton:
   `ReadInputs` (deriving the scoped anchor, D2) → `ComputeReEntryPosition` (`DocumentType = "findings"`,
   `IssueId = scopedIssueId`) → `ReadPositionStage` → `FreshRun` `FlowDecision`
   (True → `EmitCharterStarted` → `FetchConsumedTestPlan` → `FetchConsumedCriteria` → join; False → join)
   → `DispatchLifecycle` (the single `DispatchWorkflow`, `WorkflowDefinitionId = "document-lifecycle"`,
   `WaitForCompletion = true`) → `ReadLifecycleExit` → `LifecycleAccepted` `FlowDecision` →
   `EmitCharterFindings` / `EmitCharterFailed` → `ExposeOutput` (the single terminal `Sequence`).
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (C3). Dispatch input mirrors
   `TestCaseCreationWorkflow.cs:131-153` with `documentType = "findings"`,
   `producerRole = AgentRole.Tester.ToWire()`,
   `producerAction = AgentAction.ExploratoryTest.ToWire()`,
   `feedbackVariableName = "contextFindings"`, `issueId`/`correlationId` = the scoped anchor,
   `acceptanceRulesJson` passthrough.

6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** — the `BuildSeed` row
   (D10).

7. **MODIFY the drift/pin gates in ONE commit** (D10): `WorkflowInterfaceGraphTests.cs:45` + its
   `reconciled` list; `TaxonomyDriftBuildTests.cs:125`; `ContractBindingTests.cs` `Bindings` (`:82`) —
   the `(tester, exploratory-test)` entry with the research token groups.

8. **CREATE `ExploratoryCharterWorkflowStructureTests.cs` + `ExploratoryBindingHelperTests.cs`
   (`tests/Tamma.Activities.Tests/Workflows/`)** and extend
   `tests/Tamma.Core.Tests/Documents/Types/FindingsDocumentTypeTests.cs` — see Test Plan.

9. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ExploratoryCharterLifecycleExecutionTests.cs`**
   on the shared 39-6/39-10 Testcontainers fixture. Scenarios in Test Plan. Finish with full
   `dotnet test` + `dotnet ef migrations has-pending-model-changes` (clean).

## Data & Migrations

None. `Findings` documents persist to 39-11's `document_instances`; `EXPLORATORY.*` and `DOCUMENT.*` ride
the existing drain → `EventRepository` → `domain_events` path.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new family, D7/C6):** `EXPLORATORY.CHARTER.STARTED` (fresh runs only; data `testTarget`,
  `consumedTestPlanId`), `EXPLORATORY.CHARTER.FINDINGS` (on `accepted`; data `observationCount`,
  `defectCount`), `EXPLORATORY.CHARTER.FAILED` (on `rejected`/`escalated`, detail naming the typed outcome
  wire). Tags `issueId` (the **scoped** anchor — D2's documented trade-off), `repository`, `tenantId`,
  `correlationId`.
- **Emitted by the machinery this story wires in:** the full `DOCUMENT.*` family,
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes:** none at runtime; the optional `TestPlan`/`AcceptanceCriteria` arrive through the 39-11
  store read.

## Test Plan

All NUnit + FluentAssertions (Moq; Testcontainers for step 9).

- **`ExploratoryCharterWorkflowStructureTests`** — the clause set, cloned from
  `TaskCreationWorkflowStructureTests`: builds; `DefinitionId == "exploratory-charter"`; threads
  `TenantId`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables (d); exactly one
  `DispatchWorkflow`, literal def id `document-lifecycle`, zero targeting `llm-call` (a+b);
  `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains
  `(ExploratoryCharterWorkflow, DispatchLifecycle, tester, exploratory-test)` and
  `MaterializeDispatchInput` shows `documentType == "findings"` +
  `feedbackVariableName == "contextFindings"` (e); zero `Finish`, every leaf inside `ExposeOutput` (c);
  one `ComputeReEntryPositionActivity`, the expected count of `FetchLatestAcceptedDocumentActivity`
  nodes; `[ResumeBehavior(LatestStateReEntry)]`; no `Wait*`. **Covers AC1 (structure), AC3.**
- **`ExploratoryBindingHelperTests`** — `BuildExplorationContext` across all-present / plan-absent /
  garbage (never throws, never fabricates); `CountObservations` / `CountDefectObservations` on a valid
  body and on garbage → 0; `ScopeIssueId` produces a distinct anchor from the bare issue id (**the D2
  guard**). **Covers AC1 (helper half).**
- **`FindingsDocumentTypeTests` additions (`Tamma.Core.Tests`)** — the AC1 evidence rules exercised
  against the *charter-shaped* payload: an observation with no `citations` ⇒ `MISSING_EVIDENCE`; an
  observation with neither title nor summary ⇒ `FINDING_EMPTY_SHELL`; `relevance`/`confidence` outside
  [0,1] ⇒ `RELEVANCE_OUT_OF_RANGE`/`CONFIDENCE_OUT_OF_RANGE` (rejected, never clamped); mixed
  explicit/absent `rank` ⇒ `PARTIAL_RANKS`; **and the C2/D5 pin: a genuinely empty `findings` array ⇒
  `EMPTY_FINDINGS`, while the one-finding "no anomalies observed" charter validates clean.**
  **Covers AC1 — including the restated empty-session clause.**
- **Template-conformance test** — the JSON example embedded in the rewritten `exploratory-test.md`
  deserializes to `Findings` and validates clean; and the file contains **no** ```` ```path/to/file ````
  fence (a direct regression guard on C1). *This is the test that would have caught C1, and the one the
  token-only `ContractBindingTests` cannot do.*
- **Drift-gate modifications (step 7, self-verifying)** — `ContractBindingTests` green with the new entry
  (non-stale via the lifecycle-binding walk), the universal DocumentType-authority pin (`:626`) green,
  and `(tester, exploratory-test)` **absent** from `IntentionallyUnbound` (it must not be both — clause
  (b) of `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`); `TaxonomyDriftBuildTests` contributor
  subset holds; `WorkflowInterfaceGraphTests` count + non-provisional assertion green.
- **`ResumableStandardStructuralTests`** — passes with no `LegacyResumeAllowlist` entry. **Covers AC3.**
- **`ExploratoryCharterLifecycleExecutionTests` (Testcontainers)** —
  (a) **happy path:** scripted valid charter `Findings` draft → review approve → `Accept` resume →
  `status=completed`, `findingsJson` with the expected observation count, store read-back by
  `(scopedAnchor, "findings")` succeeds (**AC1**).
  (b) **contested-type-key guard (D2):** seed an accepted `research` `Findings` on the **bare** issue id,
  then dispatch this binding → it must still PRODUCE (not short-circuit to `Complete`), and the two
  accepted documents coexist. *This is the scenario that fails without the scoped anchor.*
  (c) **nothing-found session (C2/D5):** a one-finding "no anomalies observed" charter is accepted; a
  control run with a zero-length `findings` array is rejected by validation and flows into repair/revise
  (**AC1** restated clause).
  (d) **evidence ring:** a first draft with an uncited observation ⇒ `MISSING_EVIDENCE` → repair/revise →
  corrected draft accepted; `DOCUMENT.REVISION_STARTED` present.
  (e) **downstream readability (C5):** after acceptance, the same
  `FetchLatestAcceptedDocumentActivity` read `triage-po-decision` uses returns the accepted charter
  (**AC2**, scoped per C5).
  (f) **reviewer-policy passthrough (D6):** default rules route the review through
  `(architect, plan-review)`; `acceptanceRulesJson` naming a `senior_developer` reviewer routes through
  `(senior_developer, plan-review)` — making the D6 default observable.
  (g) **validation exhaustion:** always-invalid stub → typed `ValidationExhausted` escalation with
  lineage; `EXPLORATORY.CHARTER.FAILED` detail names the outcome; no error terminal reached.
  (h) **crash re-entry:** kill mid-review, fresh dispatch → resumes at review of the same revision,
  exactly one `EXPLORATORY.CHARTER.STARTED` and one `DOCUMENT.ACCEPTED` on the stream.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `Findings` cite concrete evidence; *empty session ⇒ a one-finding "nothing observed" charter, not an empty array (C2/D5)* | 2, 5, 7 | StructureTests clauses (a)–(e); `FindingsDocumentTypeTests` additions; template-conformance test; ExecutionTests (c)+(d) |
| 2 — defect findings integrate with triage *(scoped to readability + the declared edge, C5)* | 5, 6 | ExecutionTests (e); the `BuildSeed` row |
| 3 — resume declaration *(as `LatestStateReEntry`, C3)*; 39-10 gate green without allowlist | 5 | `ResumableStandardStructuralTests`; StructureTests declaration assert |
| — (epic rule 1f) new `WorkflowDocumentInterface` row + edge pin bumped | 6, 7 | `WorkflowInterfaceGraphTests` count + non-provisional assertion |

## Risks & Mitigations

- **The contested `findings` type key (D2) is the story's biggest latent bug.** Without the producer-scoped
  anchor, an issue that has already had a `research` run silently never produces a charter — and the
  workflow reports success (`status=completed`, re-entry `Complete`). Mitigation: D2's scoping plus
  ExecutionTests (b), which fails loudly on the unscoped variant.
- **The rewritten template drifts back to file-format output (C1).** Mitigation: the conformance test
  asserts both directions — the example validates as `Findings`, **and** no ```` ```path ```` fence
  remains.
- **`enableTools: true` invites an "exploration" that is really six coding tools (Epic 42).** Mitigation:
  the story already carries the caveat; this plan does not widen it, adds no new executor, and the
  template's instruction is scoped to what `FileRead`/`SearchCode`/`ShellExecute`/`RunTests` can honestly
  do. The governed-exploration gap stays filed to Epic 42.
- **C2's restatement changes an AC.** Mitigation: it is written down as a correction with the code cite
  (`Findings.cs:53-57`, `:110-116`) rather than quietly reinterpreted; the alternative options and why
  they were rejected are recorded, so a reviewer can overrule with full information.
- **An `architect`-only review of an exploratory session is domain-odd (D6).** Mitigation: nothing shared
  is changed; ExecutionTests (f) makes the default observable and the override cheap; the domain
  preference is filed rather than patched into `AcceptanceDefaults`.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check + 41-13 coordination | 0.1 |
| 2 | Template rewrite to the `Findings` contract (C1) | 0.4 |
| 3 | `EXPLORATORY.*` events + emitter | 0.35 |
| 4 | `ExploratoryBindingHelper` | 0.25 |
| 5 | The binding workflow (incl. D2 scoping) | 0.7 |
| 6–7 | Registry row + the four drift/pin edits | 0.3 |
| 8 | Structure + helper + `Findings` fixtures + conformance tests | 0.6 |
| 9 | Testcontainers scenarios (a)–(h) + full-suite green | 0.6 |
| **Total** | | **3.3** (story estimate: 3 days) |

## Blocks / Blocked by

- **Blocked by — hard: NOTHING that is unlanded.** This is the cheapest and least-gated story in the
  41-12..41-16 set. `(tester, exploratory-test)` exists today (`AgentAction.cs:81`,
  `RolePhaseMap.cs:120`) with a shipped template; `findings` is a registered document type
  (`DocumentTypeRegistry.cs:30`); the whole 39-2/39-4/39-6/39-7/39-8/39-10/39-11 substrate is in tree and
  verified. **It needs no part of 41-1a, 41-1b or 41-1c.** The story's Dependencies line ("Blocking:
  Epic 39") is accurate and complete — worth stating positively, because the epic README's blanket
  "twenty of twenty-nine wait on the enabler set" invites the opposite assumption.
- **Blocked by — for the optional consumed side only:** `41-13` (`TestPlan`) and, transitively, `41-1b`.
  **Soft**: D4 makes the fetch optional and the `consumes` edge is deliberately left empty until 41-13
  lands (D10). Ship without it; add one node later.
- **NOT blocked by:** `41-1a` · `41-1c` · **the tenant-aware scheduled-trigger seam** (issue-triggered,
  not cron — do not import the Wave-0 scheduler dependency) · **Epic 40** (no coding execution: this
  binding produces a document, it does not land code) · `41-17`.
- **Blocked in *substance* (not in code) by 39-17/39-19/39-20**, like every Epic 41 story: the accept gate
  publishes an `AcceptanceRequest` and suspends, and nothing on the other end decides
  (`Program.cs:414-417`, `:445-451`). The low-autonomy row ("a human runs the session") has no Task View
  to land in. Say which half is claimed: 41-14 claims the **document + lifecycle + persistence + events**
  half; the routing half is unreachable epic-wide.
- **Blocks:** nothing hard. Feeds `41-17` (defect findings → PR/defect triage; the route is filed forward
  per C5) and is a soft consumer of `41-13`.
- **Shared-edit register:** `ContractBindingTests.Bindings`,
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`, `DocumentTypeRegistry.BuildSeed`, and the
  single-integer `WorkflowInterfaceGraphTests.cs:45` edge pin are touched by every Epic 41 producer story.
  Sequence the pin bump last in the branch and rebase.
