# Implementation Plan — Story 41-15: Acceptance Verification Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `acceptance-verification` exists as a **thin binding over
`document-lifecycle`** (the 39-15 recipe), producing a typed **`Review`** whose subject is the change
under test (`kind = "diff"`), from the `(tester, verify-acceptance)` producer cell, consuming the latest
accepted **`AcceptanceCriteria`** (41-2) for the issue. Every criterion in the consumed document maps to
exactly one pass/fail entry in the `Review` — and that completeness rule is enforced as a
**cross-document validator rule** (`CRITERION_UNMAPPED` / `CRITERION_UNKNOWN`) through the landed
`ValidateWithContext` seam, not as a branch in the binding. One `DispatchWorkflow`, zero `Finish`, zero
`llm-call`, zero parsing, no retry plumbing, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, 39-10
gate green with no allowlist entry.

The story also **rewrites `Prompts/tester/verify-acceptance.md` from its bespoke code-review JSON to the
canonical `Review` wire** (Correction C1), adds one `ContractBindingTests.Bindings` entry with authority
`ReviewDocumentType.Validate`, declares one `WorkflowDocumentInterface` row and bumps
`WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` by one, emits `ACCEPTANCE.VERIFY.*` alongside
`DOCUMENT.*`, and wires the accepted verdict into `SingleIssueCycleWorkflow`'s `merge-approval` gate
(AC4 — the one part of the story that touches a landed, live workflow).

## How this relates to the landed accept gate and `AcceptanceRules` — read this before designing anything

The story's title invites building something parallel to 39-8's accept gate. **It must not.** The two
"acceptances" are different objects and the distinction is load-bearing:

| | 39-8 accept gate (landed) | 41-15 acceptance verification (this story) |
|---|---|---|
| Question | "Should this **document** become the accepted revision?" | "Does this **code change** satisfy the issue's acceptance criteria?" |
| Mechanism | `PublishAcceptanceRequestActivity` → `WaitForDocumentDecisionActivity` suspend → orchestrator/human `AcceptanceDecision` | an `llm-call`-produced `Review` document, itself run through the lifecycle |
| Policy source | `AcceptanceRules` (`AutonomyLevel`, `AcceptorRequirement`, `AlwaysEscalate`, `ReviewerSelection`) | none — it is a *producer*, and its own output is gated by the accept gate like any document |
| Output | `accepted` / `rejected` / four typed `escalated` outcomes | a `Review` with `decision ∈ {approve, request-changes, needs-discussion}` |

So 41-15 **rides** the accept gate rather than competing with it: it produces a `Review`, the lifecycle
validates/reviews/accepts *that Review*, and the accepted `Review`'s `decision` is then a **gate input**
to `merge-approval` (AC4). There is exactly one already-existing overlap to respect:

- `AcceptanceDefaults`' shipped `DecisionGuidance` string already says *"Accept when the review approves
  with no blocking issues **and the document satisfies its acceptance criteria**"* (`:42-46`). That is
  operator prose the orchestrator reads — it is **not** executable and there is no criteria check behind
  it anywhere in the tree. 41-15 is what makes that sentence true, for the *code* rather than for the
  document. Do not restate it as a new rules knob.
- `ReviewDocumentType`'s `APPROVE_WITH_BLOCKING_ISSUES` (`:35`, enforced `:88-98`) is already the
  epic's flagship executable invariant. AC3 does not add it — it **inherits** it. The story's AC3 is
  therefore a set of fixture assertions over the landed validator, not new code.

Everything genuinely new in 41-15 is (a) the producer cell rewrite, (b) the criterion-completeness
cross-document rule, and (c) the `merge-approval` gate input.

## Pre-Reading

- `docs/stories/epic-41/story-41-15/41-15-acceptance-verification.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); rule 3 (the accept gate always publishes and
  suspends) and the Dependencies table showing 39-17/39-19/39-20 are stubbed
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/ReviewDocumentType.cs` — **the type this story
  produces.** All three AC3 codes verified: `SubjectIncomplete` `:23` (+ the diff-subject rule `:134-140`),
  `IssueMissingFix` `:32` (`:82-86`), `ApproveWithBlockingIssues` `:35` (`:88-98`). The story's cites
  `:35`, `:88-97`, `:23-32` are accurate. Also read `Contract` (`:160-180`) — the exact wire the rewritten
  template must instruct
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs` + `AcceptanceDefaults.cs:127-132`
  (`For(Review)` → the **7-role majority panel**) — see D6
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs` — **THE reference binding**:
  the one landed producer that forwards `validationContextJson` (`:146-148`), which is exactly what AC2
  needs; `TaskCreationWorkflow.cs:149-166` for the `FreshRun` + `FetchLatestAcceptedDocumentActivity`
  consumed-document read
- `apps/tamma-elsa/src/Tamma.Core/Documents/IDocumentType.cs:32-44` — `ValidateWithContext`, the additive
  default interface member; note the 39-15 gotcha (a DIM is **not** virtual on the class — implement as an
  ordinary implicit-interface method, never `override`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TestSpec.cs:52-58` — `CaseUnknownTaskId`, the exact
  shape AC2's two codes copy
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape the epic README names
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/verify-acceptance.md` — the cell being rewritten
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:601` (the `code-review`
  dispatch), `:635-637` (`MergeApprovalGate` → `merge-approval`), `:652` (gate outcome
  `merge`|`reject`|`escalated`), `:1200` (the CI-passed → review + gate wiring) — AC4's insertion point
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeApprovalWorkflow.cs:61` (`DefinitionId`), `:148`
  (`WaitForMergeApprovalActivity`) — the gate this story feeds
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` +
  `Helpers/CreationBindingHelper.cs` (`DeriveIssueId`, `ScopeIssueId`, `BuildFailureDetail`) — shared
  fail-closed cores; do not fork
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs` — note it is
  **fail-closed** (`Found = false`, never throws, `:127-134`) — the reason AC1's "fails loud" is a typed
  routing outcome, not an exception (C5)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` (`:82`),
  `ReviewProducerDispatchablePairs` (`:505`) and its 16-pair pin (`:592-601`), the universal
  DocumentType-authority pin (`:626`), the clause-(c) staleness guard (`:725-737`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:460`/`:507`/`:125`
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:156-158` — `plan-review` /
  `task-review` / `code-review` all touch `review`; `:134-174` `BuildSeed`;
  `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`, `:102-123`
- `.dev/findings/39-15-remaining-producers-migration.md` — the distilled recipe **and the two-plans D2
  scoping decision that C4 reuses**
- **NOT FOUND (planned by prerequisites, no code in tree):** `DocumentTypeKey.AcceptanceCriteria` /
  `AcceptanceCriteriaDocumentType` / `Types/AcceptanceCriteria.cs` (**41-1b**); the
  `(product_owner, define-acceptance-criteria)` binding (**41-2**). See Blocks / Blocked by.

## Corrections to the story

- **C1 — `Prompts/tester/verify-acceptance.md` today instructs a bespoke code-review JSON, not the
  `Review` wire; rewriting it is in scope and the story does not say so.** The shipped cell asks for
  `{"issues":[{"file","line","severity":"critical|major|minor|style","category":"bug|security|performance|convention|test-coverage","issue","fix"}],
  "summary":{"decision":"APPROVE|REQUEST_CHANGES|COMMENT","text","filesReviewed","issuesBySeverity"}}`.
  `ReviewDocumentType` requires a top-level `subject`, a top-level `summary` **string**, a top-level
  `decision` from `approve|request-changes|needs-discussion`, and per-issue `description` +
  `suggestedFix` (`ReviewDocumentType.cs:160-180`). Nothing matches: `summary` is an object not a string;
  `decision` is nested and SCREAMING_CASE; `issue`/`fix` are not `description`/`suggestedFix`; there is
  no `subject` at all, so `SUBJECT_INCOMPLETE` fires on every reply. AC3 is unreachable until the cell is
  rewritten to `ReviewDocumentType.Contract`. Same class of edit 39-15 made for `debug-rootcause.md`.
- **C2 — the cell declares no carrier for the consumed criteria and no feedback carrier.** Front matter is
  `variables: role, prDescription, diff, conventions`, `enableTools: false`. The consumed
  `AcceptanceCriteria` has nowhere declared to land, and `feedbackVariableName` (39-6 D11) must name a
  DECLARED variable or repair/revise notes are silently dropped at render time — the "render-drop lesson"
  39-15 recorded. D5 adds `acceptanceCriteria` and `contextFindings` to the front matter as part of the
  rewrite.
- **C3 — AC2's "story-local rule" cannot be a story-local rule; it must be a cross-document validator rule
  on the SHARED `ReviewDocumentType`.** There is exactly one seam for a rule that cannot be seen
  payload-only — `IDocumentType.ValidateWithContext` + the lifecycle's `validationContextJson` input
  (39-15 D3) — and `Validate` is per **type**, not per producing cell. So `CRITERION_UNMAPPED` /
  `CRITERION_UNKNOWN` land on `ReviewDocumentType`, gated so they fire **only** when a criteria context is
  supplied. The context-free `Validate` must never emit them, or every landed `Review` producer
  (`task-review`, `code-review`, the 39-7 panel) breaks. Pinned by test (D4).
- **C4 — `review` is a CONTESTED type key and the story does not mention scoping.** Three declared edges
  produce `review` (`DocumentTypeRegistry.cs:156-158`: `plan-review`, `task-review`, `code-review`) and
  the 39-11 latest-accepted read scopes by `(issueId, documentType)` with **no producer filter** (the gap
  filed to 39-11 in `.dev/findings/39-15-remaining-producers-migration.md`). A verification binding keyed
  on the bare issue id would `ComputeReEntryPosition("review", issueId)` onto an accepted *task* review
  and short-circuit to `Complete` on every run — reporting success while never verifying anything. The
  binding **must** key on `CreationBindingHelper.ScopeIssueId(issueId, "acceptance-verification")`.
- **C5 — AC1's "fails loud with a distinct error code" is a typed routing outcome, not an exception.**
  `FetchLatestAcceptedDocumentActivity` is deliberately fail-closed: `Found = false` on absence *or* on
  any read failure, and it never throws out of the binding graph (`:127-134`). Realised as D3's
  `CriteriaResolved` `FlowDecision` → `ExposeOutput` with `status = "escalated"`,
  `outcome = "acceptance-criteria-missing"`, **no dispatch, no `Review` emitted** — which is exactly what
  AC1's second clause demands ("emits no `Review`", "an issue with no criteria never yields an `approve`
  verdict"). Zero `Finish`, one `DispatchWorkflow`: rule 1 clauses (a) and (c) hold.
- **C6 — `[ResumeBehavior]` mode: `LatestStateReEntry`, not `Both`.** AC5 says `Both`. The binding never
  suspends on a bookmark of its own; the accept-gate suspend is inside the dispatched `document-lifecycle`
  child while the parent waits on `WaitForCompletion = true`. `Both` fails
  `ResumableStandardStructuralTests` clause (b). Every landed producer binding declares
  `LatestStateReEntry` (`TaskCreationWorkflow.cs:47`, `TestCaseCreationWorkflow.cs:37`). AC5's real
  requirement — "39-10 structural test green without an allowlist entry" — is unchanged.
- **C7 — the autonomy rows (70–84 human verifies / 85–94 agent verifies, human confirms / 95–100
  self-accept) are not implementable and no AC claims them.** Nothing routes by autonomy today: 39-17
  (the orchestrator agent that *decides*) does not exist — `GetAcceptanceRulesTool` is deliberately
  unregistered and waits on the 39-17 host (`Program.cs:414-417`), `OrchestratorChannelHandler.cs:11`
  waits on 39-17 to mint the claim, `AgentOfflineChatRelay` refuses every message
  (`Program.cs:448-451`), and `InitiatorOnlyTaskAudienceResolver` admits only the issue initiator
  (`Program.cs:445-447`). The accept gate publishes and suspends; nothing answers. 41-15 claims the
  **produce + validate + review + persist + gate-input** half; the routing half is unreachable
  epic-wide. Say so in the ACs rather than implying day-one behaviour.
- **C8 — AC4's "an `approve` verdict releasing it" needs a decision about what happens when the
  verification has NOT run.** `SingleIssueCycleWorkflow`'s `MergeApprovalGate` (`:635-637`) is live today
  and every existing issue reaches it with no acceptance verdict in the store. A fail-**closed** gate
  ("no verdict ⇒ block") would break every in-flight cycle the day it merges. D8 makes it fail-**open on
  absence, closed on a negative verdict**, and pins both directions by test.

## Design Decisions

- **D1 — New workflow class + new `DefinitionId` `acceptance-verification`.** Inputs: `issueId`,
  `repository`, `prNumber?`, `commitSha?`, `prDescription`, `diff`, `tenantId`, `acceptanceRulesJson?`.
  Outputs: `status`, `outcome`, `documentId`, `reviewJson`, `verdict` (the `Review`'s `decision` wire,
  `""` on non-accept), `parentDocumentId` (the consumed criteria id), `error`.
  `builder.Version = WorkflowVersions.ComputedVersion`. **The binding verifies any diff, human- or
  agent-authored** (the story's Scope is right about this) — AC4's in-loop gating is the only Epic-40
  -dependent part.
- **D2 — Producer-scoped issue id (C4):** `anchor = CreationBindingHelper.ScopeIssueId(issueId,
  "acceptance-verification")`, threaded as both `issueId` and `correlationId` into the lifecycle and used
  for `ComputeReEntryPositionActivity`. Documented trade-off (same as 39-15 D2): this binding's
  `DOCUMENT.*` events carry the scoped anchor. The clean fix is the filed 39-11 producer-filter gap.
- **D3 — Consumed `AcceptanceCriteria` is read behind the `FreshRun` gate on the BARE issue id, and its
  absence is a typed escalation (C5).** `FetchLatestAcceptedDocumentActivity` with
  `DocumentTypeKey = "acceptance-criteria"`, `IssueId = <bare issueId>` (the criteria live on the issue,
  not on this producer's scope), gated on `positionStage == "produce"`. Then a `CriteriaResolved`
  `FlowDecision`: `Found == false` → `EmitVerifyFailed` → `ExposeOutput` (`status = "escalated"`,
  `outcome = "acceptance-criteria-missing"`); `Found == true` → `DispatchLifecycle`.
- **D4 — Criterion completeness is a cross-document VALIDATOR rule on `ReviewDocumentType` (C3).** The
  binding computes `validationContextJson =
  AcceptanceVerificationHelper.BuildCriteriaContext(criteriaJson)` — a projection of the criterion ids,
  mirroring `CreationBindingHelper.BuildTaskIdContext` (`:44-75`) — and forwards it.
  `ReviewDocumentType` gains an **implicit-interface** `ValidateWithContext(payload,
  validationContextJson)` (never `override` — a DIM is not virtual on the class) that, **only when the
  context is a non-empty criteria projection**, emits:
  - `CRITERION_UNMAPPED` — a criterion id in the context with no corresponding entry in the review;
  - `CRITERION_UNKNOWN` — a criterion id cited by the review that is not in the context.
  An empty/unreadable context is a no-op → payload-only validation, never a throw. The per-criterion
  mapping rides the existing `Review` shape: each criterion becomes an issue whose `category` is the
  criterion id for a FAIL, and passes are carried in an additive `criteriaResults` member on `Review`
  (a nullable list, absent for every other producer — additive-only, exactly like
  `Review.AggregatedFrom`). **This is a shared-type edit; coordinate with the 39-4 owner.**
- **D5 — The prompt cell is REWRITTEN to `ReviewDocumentType.Contract`'s shape, by hand (C1/C2).** No
  prompt file carries a 39-16 generated-region marker. Front matter becomes
  `variables: role, prDescription, diff, acceptanceCriteria, contextFindings, conventions`,
  `enableTools: false` (kept — verification reads the supplied diff, it does not run tools), `maxTokens:
  8192`, **`version: 1 → 2`**. `feedbackVariableName = "contextFindings"` so repair/revise notes land in a
  DECLARED carrier. Body instructs: the `diff` subject shape (`{"kind":"diff","repository":"…",
  "prNumber":12}`), the three-value lowercase `decision` vocabulary, one entry per supplied criterion,
  `category` = the criterion id, a concrete `suggestedFix` on every issue, and the approve/blocking
  invariant in the model's own words. `Bindings` token groups follow the rewritten body:
  `"subject"`, `"decision"`, `"summary"`, `"issues"`, `"severity"`, `"category"`, `"suggestedFix"`
  (the `ReviewDocumentType.Contract` set named at `:158-159`).
- **D6 — Acceptance policy: inherit the shipped `review` default, override per-run via
  `acceptanceRulesJson`.** `AcceptanceDefaults.For(Review)` is the 7-role **majority panel**
  (`:129`) — a seven-role panel reviewing a verification verdict is heavy, and for a tight merge loop the
  domain answer is a single `senior_developer` reviewer. This story changes **nothing shared** (that row
  is also `task-review`'s and `code-review`'s) and passes `acceptanceRulesJson` through. One integration
  test makes the default observable, one proves the override. The story's "any failing criterion always
  escalates" is an `EscalationClass` supplied through the same passthrough — policy, not code (39-5).
- **D7 — Pure helper `AcceptanceVerificationHelper` in `Workflows/Helpers/`, Elsa-free, total,
  fail-closed.** New: `BuildCriteriaContext(criteriaJson) → string` (`""` on empty/unreadable);
  `BuildVerificationVariables(criteriaJson, prDescription, diff) → string`;
  `ReadVerdict(reviewJson) → string` (`""` fail-closed — never "approve" on unreadable input);
  `CountFailingCriteria(reviewJson) → int`. Reuses `LifecycleBindingHelper.ReadLifecycleResult` /
  `IsAccepted` and `CreationBindingHelper.DeriveIssueId` / `ScopeIssueId` / `BuildFailureDetail`.
- **D8 — AC4's gate input: fail-OPEN on absence, fail-CLOSED on a negative verdict (C8).**
  `SingleIssueCycleWorkflow` gains one `FetchLatestAcceptedDocumentActivity`
  (`DocumentTypeKey = "review"`, `IssueId` = the verification-scoped anchor) immediately before
  `MergeApprovalGate` (`:635`), plus a `VerdictAllowsMerge` `FlowDecision` reading
  `AcceptanceVerificationHelper.ReadVerdict`:
  - not found → **proceed to the gate unchanged** (today's behaviour; no in-flight cycle breaks);
  - `approve` → proceed to the gate;
  - `request-changes` / `needs-discussion` / unreadable → route to the **existing** `emitStepFailed` /
    rejected sink (`:685`'s notify path), never a new terminal.
  This is additive and reversible: one fetch node, one `FlowDecision`, two edges, no `Finish`.
  `SingleIssueCycleWorkflow` is **also** rewritten by 40-2/40-4/40-5 and 41-29 — see the shared-edit
  register.
- **D9 — `ACCEPTANCE.VERIFY.*` gets its own emitter activity, house pattern.**
  `Tamma.Activities/Acceptance/AcceptanceVerifyEvents.cs` — `ACCEPTANCE.VERIFY.STARTED`,
  `ACCEPTANCE.VERIFY.VERDICT` (data `verdict`, `failingCriteria`, `consumedCriteriaId`),
  `ACCEPTANCE.VERIFY.FAILED` — plus `EmitAcceptanceVerifyEventActivity`, cloned from
  `Decomposition/EmitDecompositionEventActivity.cs`. Tags `issueId`, `prId`, `repository`, `tenantId`,
  `correlationId` (the story's tag set). Emissions gated on the re-entry position (39-12 D3) so re-entry
  cannot double-emit. *The story lists `.STARTED` → `.VERDICT`; `.FAILED` is added because every landed
  family has a failure member and the `rejected`/`escalated` exits need one.*
- **D10 — Drift-gate bookkeeping, enumerated (rule 1 clause (f)).** One `Bindings` entry for
  `(tester, verify-acceptance)`, authority `"ReviewDocumentType.Validate"`, tokens per D5; one `BuildSeed`
  row `("acceptance-verification", consumes [acceptance-criteria], produces review, false)`;
  `WorkflowInterfaceGraphTests.cs:45` `HaveCount(N) → HaveCount(N+1)` with the reason in the comment; the
  definition id appended to that file's `reconciled` list (`:102-123`);
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (`:125`) gains
  `"AcceptanceVerificationWorkflow"`. **No** `AgentAction`/`RolePhaseMap`/`SystemPrompts` count pin moves
  — `(tester, verify-acceptance)` already exists (`AgentAction.cs:82`, `RolePhaseMap.cs:121`) with a
  shipped template. **No** `DocumentTypeKey`/`DocumentTypeRegistry` count pin moves — `review` is
  registered. **No** change to `ReviewerSelectionHelper.AllDispatchablePairs` or its 16-pair pin
  (`ContractBindingTests.cs:592-601`) — `verify-acceptance` is a **producer** cell, not a reviewer lens.

## Implementation Steps

1. **Precondition gate (no code, a real gate).** Verify in tree and compiling:
   `DocumentTypeKey.AcceptanceCriteria` + `AcceptanceCriteriaDocumentType` registered (**41-1b**), and a
   workflow producing an accepted `AcceptanceCriteria` (**41-2**). Any gap blocks steps 6 and 10 — file
   it, do not work around it. Steps 2–5 and 9's pure tests can proceed against 41-1b's pinned shape with
   fakes. Also re-derive `SingleIssueCycleWorkflow` line numbers from the file: 40-2/40-4/40-5 and 41-29
   rewrite the same region and **all cites in this plan will have shifted**.

2. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/verify-acceptance.md`** (D5, C1/C2) to the
   `Review` wire; new front matter; bump `version` to 2.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Acceptance/AcceptanceVerifyEvents.cs` +
   `EmitAcceptanceVerifyEventActivity.cs`** (D9).

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/AcceptanceVerificationHelper.cs`** (D7).

5. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Review.cs` +
   `Types/ReviewDocumentType.cs`** (D4, C3) — the additive nullable `criteriaResults` member on `Review`,
   the two new violation constants, and the implicit-interface `ValidateWithContext`. **Lockstep with the
   39-4 owner**: this is a shared type used by `task-review`, `code-review`, `plan-review` and the 39-7
   panel producers. The context-free `Validate` path must be byte-identical afterwards — pinned by test.

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AcceptanceVerificationWorkflow.cs`** (D1–D5),
   copying `TestCaseCreationWorkflow`'s skeleton plus `TaskCreationWorkflow`'s consumed-document fetch:
   `ReadInputs` (deriving the scoped anchor, D2) → `ComputeReEntryPosition` (`DocumentType = "review"`,
   `IssueId = scopedAnchor`) → `ReadPositionStage` → `FreshRun` `FlowDecision`
   (True → `EmitVerifyStarted` → `FetchConsumedCriteria` → `CriteriaResolved` `FlowDecision`
   (False → `EmitVerifyFailed` → `ExposeOutput`; True → join); False → join)
   → `DispatchLifecycle` (the single `DispatchWorkflow`) → `ReadLifecycleExit` → `LifecycleAccepted`
   `FlowDecision` → `EmitVerifyVerdict` / `EmitVerifyFailed` → `ExposeOutput`.
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (C6). Dispatch input:
   `documentType = "review"`, `producerRole = AgentRole.Tester.ToWire()`,
   `producerAction = AgentAction.VerifyAcceptance.ToWire()`,
   `feedbackVariableName = "contextFindings"`,
   `validationContextJson = AcceptanceVerificationHelper.BuildCriteriaContext(...)`,
   `issueId`/`correlationId` = the scoped anchor, `acceptanceRulesJson` passthrough.

7. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** — the `BuildSeed` row
   (D10).

8. **MODIFY the drift/pin gates in ONE commit** (D10): `WorkflowInterfaceGraphTests.cs:45` + its
   `reconciled` list; `TaxonomyDriftBuildTests.cs:125`; `ContractBindingTests.cs` `Bindings` (`:82`).

9. **CREATE `AcceptanceVerificationWorkflowStructureTests.cs` + `AcceptanceVerificationHelperTests.cs`
   (`tests/Tamma.Activities.Tests/Workflows/`)**; extend
   `tests/Tamma.Core.Tests/Documents/Types/ReviewDocumentTypeTests.cs` — see Test Plan.

10. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`** (D8, AC4) —
    the fetch node + `VerdictAllowsMerge` `FlowDecision` ahead of `MergeApprovalGate`; add both to the
    flowchart's `Activities`/`Connections` lists. **Rebase onto the post-40 shape first.**

11. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/AcceptanceVerificationLifecycleExecutionTests.cs`**
    on the shared 39-6/39-10 Testcontainers fixture; extend
    `tests/Tamma.Activities.Tests/Workflows/SingleIssueCycleRoutingTests.cs` for the D8 gate. Scenarios in
    Test Plan. Finish with full `dotnet test` + `dotnet ef migrations has-pending-model-changes` (clean).

## Data & Migrations

None. `Review` documents persist to 39-11's `document_instances`; the additive `criteriaResults` member
lives inside the JSONB payload, not a column. `ACCEPTANCE.VERIFY.*` and `DOCUMENT.*` ride the existing
drain. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new family, D9):** `ACCEPTANCE.VERIFY.STARTED` (fresh runs only),
  `ACCEPTANCE.VERIFY.VERDICT` (on `accepted`; data `verdict`, `failingCriteria`, `consumedCriteriaId`),
  `ACCEPTANCE.VERIFY.FAILED` (on `rejected`/`escalated`, detail naming the typed outcome wire; also the
  D3 criteria-missing escalation). Tags `issueId`, `prId`, `repository`, `tenantId`, `correlationId`.
- **Emitted by the machinery this story wires in:** the full `DOCUMENT.*` family,
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes:** none at runtime; the `AcceptanceCriteria` arrives through the 39-11 store read.

## Test Plan

All NUnit + FluentAssertions (Moq; Testcontainers for step 11).

- **`AcceptanceVerificationWorkflowStructureTests`** — the clause set, cloned from
  `TaskCreationWorkflowStructureTests`: builds; `DefinitionId == "acceptance-verification"`; threads
  `TenantId`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables (d); exactly one
  `DispatchWorkflow`, literal def id `document-lifecycle`, zero targeting `llm-call` (a+b);
  `ScanLifecycleBindingDispatches()` contains `(AcceptanceVerificationWorkflow, DispatchLifecycle, tester,
  verify-acceptance)` and `MaterializeDispatchInput` shows `documentType == "review"` +
  `feedbackVariableName == "contextFindings"` (e); zero `Finish`, every leaf inside `ExposeOutput` (c);
  one `ComputeReEntryPositionActivity`, one `FetchLatestAcceptedDocumentActivity`;
  `[ResumeBehavior(LatestStateReEntry)]`; no `Wait*`. **Covers AC5.**
- **`AcceptanceVerificationHelperTests`** — `BuildCriteriaContext` on a valid criteria body /
  criteria-less body / garbage → `""`; `ReadVerdict` **fail-closed**: null / unreadable / missing
  `decision` → `""`, never `"approve"` (the single most important helper assertion in this story);
  `CountFailingCriteria`; `ScopeIssueId` yields an anchor distinct from the bare issue id (the C4 guard).
- **`ReviewDocumentTypeTests` additions (`Tamma.Core.Tests`)** —
  (i) **AC3 over the landed validator, one fixture per rule:** `decision = approve` with a critical issue
  ⇒ `APPROVE_WITH_BLOCKING_ISSUES`; an issue with no `suggestedFix` ⇒ `ISSUE_MISSING_FIX`; a
  `subject.kind = "diff"` with no `repository`, or with neither `prNumber` nor `commitSha` ⇒
  `SUBJECT_INCOMPLETE`.
  (ii) **AC2 / D4 cross-document rule:** a review mapping every context criterion validates; one omitting
  a criterion ⇒ `CRITERION_UNMAPPED` naming it; one citing an id absent from the context ⇒
  `CRITERION_UNKNOWN`; an **empty context** ⇒ neither fires and the payload-only result is returned
  unchanged; the context-free `Validate` **never** emits either code — plus a **regression pin that the
  existing `task-review` / `code-review` / panel fixtures are byte-identical before and after step 5**.
  **Covers AC2, AC3.**
- **Template-conformance test** — the JSON example embedded in the rewritten `verify-acceptance.md`
  deserializes to `Review` and validates clean, and the file contains no `APPROVE|REQUEST_CHANGES|COMMENT`
  string (a direct regression guard on C1). *This is what would have caught C1; the token-only
  `ContractBindingTests` cannot.*
- **Drift-gate modifications (step 8, self-verifying)** — `ContractBindingTests` green with the new entry
  (non-stale via the lifecycle-binding walk), the universal DocumentType-authority pin (`:626`) green,
  the 16-pair `AllDispatchablePairs` pin **unchanged** (D10);
  `TaxonomyDriftBuildTests` contributor subset holds; `WorkflowInterfaceGraphTests` count +
  non-provisional assertion green.
- **`ResumableStandardStructuralTests`** — passes with no `LegacyResumeAllowlist` entry, for both the new
  workflow and (unchanged) `SingleIssueCycleWorkflow`. **Covers AC5.**
- **`SingleIssueCycleRoutingTests` additions (D8, AC4)** — graph assertions: the `VerdictAllowsMerge`
  `FlowDecision` sits between the fetch and `MergeApprovalGate`; a `request-changes` verdict reaches the
  rejected sink and **never** `MergeApprovalGate`; an `approve` verdict reaches `MergeApprovalGate`; a
  **not-found** verdict reaches `MergeApprovalGate` (fail-open, C8) — and a negative control that today's
  no-verdict path is byte-identical to pre-story. No new `Finish`. **Covers AC4 (structure half).**
- **`AcceptanceVerificationLifecycleExecutionTests` (Testcontainers)** —
  (a) **happy path:** seed an accepted `AcceptanceCriteria`; scripted valid `Review` draft mapping every
  criterion → review approve → `Accept` resume → `status=completed`, `verdict=approve`,
  `parentDocumentId` = the criteria id (**AC2, AC3**).
  (b) **no criteria (AC1):** empty store → `status=escalated`, `outcome=acceptance-criteria-missing`,
  **zero `DOCUMENT.PRODUCED.*`, zero `Review` persisted**, and the run **never** yields an `approve`
  verdict — the story's exact assertion.
  (c) **completeness ring (AC2):** first draft omits a criterion ⇒ `CRITERION_UNMAPPED` → repair/revise →
  corrected draft accepted; `DOCUMENT.REVISION_STARTED` present.
  (d) **approve-with-blocking (AC3):** a draft with `decision = approve` and a critical issue is rejected
  with `APPROVE_WITH_BLOCKING_ISSUES` and flows into repair/revise, never to acceptance.
  (e) **contested-type-key guard (C4):** seed an accepted `task-review` `Review` on the bare issue id,
  then dispatch this binding → it must still PRODUCE (not short-circuit to `Complete`).
  (f) **merge gating end-to-end (AC4):** a `request-changes` verdict blocks the merge-approval path; an
  `approve` verdict releases it. *The change under test is a **human-authored** PR fixture — see Blocks /
  Blocked by: `.github/workflows/tamma-agent.yml` does not exist, so an agent-authored change cannot be
  produced until Epic 40 lands.*
  (g) **reviewer-policy passthrough (D6):** default rules route the review through the 7-role majority
  panel; `acceptanceRulesJson` naming a single `senior_developer` routes through the single-reviewer
  branch.
  (h) **crash re-entry:** kill mid-review, fresh dispatch → resumes at review of the same revision,
  exactly one `ACCEPTANCE.VERIFY.STARTED` and one `DOCUMENT.ACCEPTED` on the stream.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — reads latest accepted `AcceptanceCriteria` via 39-11; none ⇒ fails loud with a distinct code, emits no `Review` *(as a typed exit, C5)* | 6 (D3) | ExecutionTests (b) |
| 2 — every criterion maps to exactly one pass/fail entry; `CRITERION_UNMAPPED` / `CRITERION_UNKNOWN` | 5, 6 (D4) | `ReviewDocumentTypeTests` (ii); ExecutionTests (c) |
| 3 — `APPROVE_WITH_BLOCKING_ISSUES` / `ISSUE_MISSING_FIX` / `SUBJECT_INCOMPLETE` | 2, 9 | `ReviewDocumentTypeTests` (i); template-conformance test; ExecutionTests (d) |
| 4 — verdict is a gate input to `single-issue-cycle`/`merge-approval` | 10 (D8) | `SingleIssueCycleRoutingTests` additions; ExecutionTests (f) |
| 5 — resume declaration *(as `LatestStateReEntry`, C6)*; 39-10 gate green without allowlist; new `WorkflowDocumentInterface` row; edge pin bumped | 6, 7, 8 | `ResumableStandardStructuralTests`; `WorkflowInterfaceGraphTests` count |

## Risks & Mitigations

- **Step 5 edits a SHARED type used by four landed producers (D4/C3).** A careless
  `ValidateWithContext` that fires payload-only, or a non-nullable `criteriaResults`, breaks
  `task-review`, `code-review`, `plan-review` and the 39-7 panel at once. Mitigation: the member is gated
  on a non-empty criteria context; `criteriaResults` is nullable and absent by default (the
  `AggregatedFrom` precedent); and the test plan carries an explicit **byte-identical-before-and-after**
  regression pin over the existing fixtures.
- **`ValidateWithContext` implemented as `override`** — it is a default interface member, not virtual on
  the class. This cost 39-15 a cycle (recorded in its findings). Mitigation: named in D4 and asserted by
  the "context-free `Validate` never emits the codes" test.
- **The contested `review` type key (C4)** silently turns the workflow into a no-op that *reports
  success*. Mitigation: D2's scoped anchor + ExecutionTests (e), which fails on the unscoped variant.
- **D8's `SingleIssueCycleWorkflow` edit collides with Epic 40 and 41-29.** 40-2 (`WaitForAgentRunActivity`),
  40-4 (`ComputeTaskResumeIndexActivity`), 40-5 (`[ResumeBehavior]`) and 41-29 (`FlowSwitchByKind`,
  `IssuePreRoute`) all rewrite that file; 41-29's plan already warns "all line cites will have shifted".
  Mitigation: D8 touches the **merge region** (`:635`), not the per-task loop those stories own — a
  different hunk — but the file-level conflict is real. Register the shared edit (below), rebase last,
  re-derive every line number.
- **AC4 could break every in-flight cycle (C8).** Mitigation: D8's fail-open-on-absence + the explicit
  negative-control test that the no-verdict path is byte-identical to pre-story.
- **Wave-0 coupling.** `AcceptanceCriteria` is 41-1b's and its producer is 41-2's. Mitigation: step 1 is
  a real gate; steps 2–5 + 9's pure tests build against 41-1b's pinned shape with fakes.
- **The autonomy rows read as delivered (C7).** Mitigation: the correction states which half is claimed;
  no AC asserts routing behaviour.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate + 41-1b/41-2 shape reconciliation + post-40 re-derivation | 0.3 |
| 2 | Template rewrite to the `Review` wire (C1/C2) | 0.5 |
| 3 | `ACCEPTANCE.VERIFY.*` events + emitter | 0.35 |
| 4 | `AcceptanceVerificationHelper` | 0.35 |
| 5 | Shared `Review` / `ReviewDocumentType` edit (lockstep with 39-4) | 0.7 |
| 6 | The binding workflow | 0.8 |
| 7–8 | Registry row + the four drift/pin edits | 0.3 |
| 9 | Structure + helper + `Review` fixtures + regression pin + conformance test | 0.9 |
| 10 | `SingleIssueCycleWorkflow` gate wiring (post-40 rebase) | 0.5 |
| 11 | Testcontainers scenarios (a)–(h) + routing tests + full-suite green | 0.9 |
| — | 41-1b / 41-2 / 39-4 lockstep coordination, review polish | 0.3 |
| **Total** | | **5.9** (story estimate: 4–5 days — the overrun is C1's template rewrite, C3's shared-type edit and C8's fail-open design, none of which the story scoped) |

## Blocks / Blocked by

- **Blocked by — HARD, Wave-0: `41-1b`.** `AcceptanceCriteria` is one of its six types.
  `DocumentTypeKeyExtensions.Parse("acceptance-criteria")` throws `DOCUMENT.TYPE.UNKNOWN` today
  (ten members at `DocumentTypeKey.cs:22-34`) and `DocumentTypeRegistry.Resolve` throws
  `DOCUMENT.TYPE.NOT_REGISTERED` (`:85-91`). **This blocks the human path too** — an unregistered type
  cannot be persisted, so "a human authors the criteria instead" is not an escape. 41-1b owns the two
  vocabulary count pins; this story moves neither.
- **Blocked by — HARD: `41-2`** (Acceptance-Criteria Authoring). AC1's consumed side has no producer
  without it. The story's own Dependencies line is correct on this.
- **Blocked by — for AC4 only: `Epic 40`.** `.github/workflows/tamma-agent.yml` does not exist in this
  repo (verified: `.github/workflows/` contains `tamma-worker.yml`, not `tamma-agent.yml`), so the coding
  step's dispatch fails loud with `WorkflowNotFound` and there is no *agent-authored* change to gate. The
  story's own Corrected note is right: AC1–AC3 and AC5 verify a human-authored PR and have no Epic 40
  dependency. ExecutionTests (f) therefore uses a human-authored PR fixture.
- **Blocked by — file-level, not story-level: `40-2 → 40-4 → 40-5` and `41-29`** all rewrite
  `SingleIssueCycleWorkflow.cs`. D8's edit is in the merge region rather than the per-task loop, but this
  story must land **after** the Epic 40 sequence and coordinate with 41-29. Registered in Epic 40's
  `EXECUTION-PLAN.md`; Epic 41 has no execution plan to register it in (README, "Planning artifacts this
  epic does not have") — which is itself a gap.
- **Blocked by — lockstep, not sequential:** the `Review` / `ReviewDocumentType` edit (D4/C3) is a shared
  39-4 type; the `Bindings` token groups depend on the rewritten template. Agree both in one shape review.
- **NOT blocked by:** `41-1a` (no new role, no new cell — `(tester, verify-acceptance)` exists at
  `AgentAction.cs:82` / `RolePhaseMap.cs:121` with a shipped template) · `41-1c` (no prose) · **the
  tenant-aware scheduled-trigger seam** (PR/issue-triggered, not cron).
- **Blocked in *substance* (not in code) by 39-17/39-19/39-20** — see C7. 41-15 claims the produce +
  validate + review + persist + gate-input half; the autonomy-routing half is unreachable epic-wide.
- **Blocks:** requirement-complete merge gating (the story's own "Unblocks" line) and, softly, `41-16`
  (a failed verification is a regression signal).
- **Shared-edit register:** `ContractBindingTests.Bindings`,
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`, `DocumentTypeRegistry.BuildSeed`, the
  single-integer `WorkflowInterfaceGraphTests.cs:45` edge pin (every Epic 41 producer story), the shared
  `Types/Review.cs` + `ReviewDocumentType.cs` (39-4 / 39-7 / 41-17), and `SingleIssueCycleWorkflow.cs`
  (40-2, 40-4, 40-5, 41-29). This is the most edit-contended story of the five.
