# Implementation Plan — Story 41-16: Regression & Flaky-Test Management Workflow

> **This plan is BLOCKED, and not only on the enabler set.** Three of its five ACs rest on machinery
> that does not exist and that **no story owns**: the tenant-aware scheduled-trigger seam (AC1), and a
> per-test CI result history (AC1, AC3). A fourth (AC2) is written against a document type whose closed
> vocabulary cannot express the classification the story requires. This plan says so plainly, sizes the
> missing pieces, and describes what can honestly ship without them — it does not invent a seam and then
> plan against it. See **Blocks / Blocked by**.

## Scope & Deliverable

**Target state (all blockers cleared).** A scheduled, tenant-scoped sweep dispatches
`regression-sweep` per window per tenant. For each suspect test it runs a **thin binding over
`document-lifecycle`** producing a typed classification document from the `(tester, manage-regression)`
producer cell (minted by 41-1a), and for a confirmed regression a follow-on binding producing a
**`TestSpec`** from `(tester, write-regression-test)`. Per-suspect fail-closed: a failed suspect emits
`REGRESSION.SWEEP.ITEM` with the failure and the sweep continues. `[ResumeBehavior(LatestStateReEntry)]`,
39-10 gate green with no allowlist entry, two new `WorkflowDocumentInterface` rows, the edge pin bumped.

**Shippable-today slice (Phase A, the only part this plan authorises starting).** The two bindings and
their prompt-cell rewrites, driven by an **explicit suspect list handed in as input** rather than mined
from CI history, dispatched **manually or by an existing caller** rather than on a cron. That slice is
real value (it turns "someone noticed a flaky test" into a typed, reviewed, accepted decision with an
audit trail) and it is testable end-to-end. Phase B — the sweep, the CI history read, and AC1/AC3 — waits
on the two missing pieces (the scheduler seam — now story 41-30 — and the per-test CI result store,
which no story schedules).

## Pre-Reading

- `docs/stories/epic-41/story-41-16/41-16-regression-and-flaky-test-management.md` — the story (ACs are
  source of truth)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); rule 5's "**Scheduled workflows have no
  reusable pattern yet**"; the Wave-0 table row "*Tenant-aware scheduled-trigger seam — owner: none —
  must be written*"; and the Dependencies bullet that calls it "**the one thing in Epic 41 that no story
  builds**"
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md:29` — `manage-regression` (tester) is
  one of its fifteen new cells
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` — the **non**-pattern.
  Verified: options section hardcoded (`:17`), single `FireAtMinute` int (`:34`, used `:169`), in-process
  `_lastFired` tuple (`:83`, set `:189`/`:213`), target workflow hardcoded (`:199`), advisory-lock key
  `(year, dayOfYear, hour)` with **no tenant component** (`:179`, `:241`), dispatch threads no `tenantId`
  (`:197-202`). Every one of the story's four criticisms checks out
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs` — **THE reference binding**
  (it already produces `TestSpec` from a consumed plan, with the `validationContextJson` ring at
  `:146-148`); `TaskCreationWorkflow.cs:149-166` for the consumed-document fetch
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape the epic README names
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs` — **read this before AC2.** The four
  closed vocabularies (`TriagePriority` `:14-20`, `TriageIssueType` `:22-30`, `TriageComplexity` `:32-40`,
  `TriageAutomation` `:42-48`), the record's five required members (`:118-129`), `OutOfVocabulary` `:146`,
  `ReasoningRequired` `:149`. The story's cite `:146-149` is accurate — **but see Correction C1**
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TestSpec.cs:40-58` — the four AC4 codes
  (`EmptyTestSpec` `:41`, `CaseMissingTaskId` `:44`, `CaseMissingBehavior` `:47`, `CaseUnknownTaskId`
  `:57`). The story's cite `TestSpec.cs:40-57` is accurate
- `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/write-regression-test.md` — the cell being rewritten (C3)
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Models/TestingModels.cs:24-52` — `CIResultsPayload` /
  `FailedTestDetail`; **and `apps/tamma-elsa/src/Tamma.Activities/LlmCall/GitMediationMapping.cs:46-61`,
  whose doc-comment is the load-bearing evidence for C2**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestingWorkflow.cs:174-186` — what
  `TEST.RESULTS_RECEIVED.SUCCESS` actually carries
- `.github/workflows/ci.yml:230-278` — the per-project test loop, the `PROJECT-FAILED` marker, and the
  `.trx` failure-recap parser (`tr '<' '\n' | grep 'UnitTestResult ' | grep 'outcome="Failed"' | grep -oE
  'testName="[^"]*"'`); `:281-289` — `TestResults/**` uploaded **only `if: failure()`**
- **Git history, not `.dev/findings/` — see C6.** `be35b89` *"fix(core): make UuidV7.NewGuid() monotonic
  within a millisecond"* (the resolution), `890502a` *"ci: print failed test names from the .trx"* (the
  diagnostic that finally named it), and the four human re-run commits `12c54a0`, `dd14c09`, `2179c23`,
  `dfdd8a7`. Also `d387454` (a *different* flake class: 4860/4860 passed but the job exited 1 —
  test-host teardown)
- `apps/tamma-elsa/src/Tamma.Core/Documents/UuidV7.cs:14-50` — the fix's doc-comment, which names the
  consumer class ("the `channel_outbox` FIFO replay, document lineage")
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs` +
  `Helpers/CreationBindingHelper.cs` — shared fail-closed cores; do not fork
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` (`:82`, `:286`, `:626`,
  `:725-737`) and `TaxonomyDriftBuildTests.cs` (`:110`, `:125`, `:460`, `:507`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174` +
  `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`, `:102-123`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentActionTests.cs:38` (`Be(80)`),
  `Agents/RolePhaseMapTests.cs:64` (`HaveCount(80)`),
  `PromptStore/SystemPromptsTests.cs:23`/`:30`/`:33` (cell count **derived** from the live taxonomy, both
  directions) and `src/Tamma.Api/Auth/PromptFileLoader.cs:106`/`:118` (`PROMPT.SEED.UNKNOWN_CELL`) /
  `:161-167` (`PROMPT.SEED.NO_BODY_FAMILY`) — the fail-loud-both-ways loader the new cell must satisfy
- **NOT FOUND (no code in tree):** `(tester, manage-regression)` (**41-1a**); any tenant-aware scheduler
  (**now story 41-30**); any per-test CI result store (**no story schedules it**). See Blocks / Blocked by.

## Corrections to the story

- **C1 — `TriageDecision` CANNOT express `regression | flaky | environmental`, and AC2 is written as if it
  can.** The landed type has **no** free "classification" member. It requires five fields — `priority`,
  `type`, `complexity`, `automation`, `reasoning` — of which the first four are validated against closed
  enums (`TriageDecision.cs:155-166`) whose vocabularies are `urgent|high|normal|low`,
  `bug|feature|chore|question|security|docs`, `trivial|simple|medium|complex|epic`,
  `tamma-auto|tamma-assist|needs-human`. "regression", "flaky" and "environmental" are in none of them.
  The story's AC2 quote — "an out-of-vocabulary value ⇒ `OUT_OF_VOCABULARY`" — is a *true statement about
  the landed type* attached to a classification the type does not have. Three options:
  (i) **shoehorn** — map regression→`bug`, flaky→`chore`, environmental→`chore`, and carry the real
  classification in `reasoning` prose. Rejected: it destroys the closed-enum property AC2 is trying to
  buy, and makes `flaky` and `environmental` indistinguishable to any consumer;
  (ii) **extend `TriageIssueType`** with `regression|flaky|environmental`. Rejected: it is a shared
  vocabulary consumed by `triage-po-decision`, `TriagePoDecisionHelper`, the 26-1 wire and
  `AcceptanceRules`' escalation-class parse — widening it changes what an *intake* triage may say;
  (iii) **a new `RegressionTriage` document type** with its own closed
  `RegressionClassification { regression, flaky, environmental }` vocabulary + `reasoning` required, in
  the 39-3/39-4 pattern. **This plan takes (iii)** and files it as a **seventh type for 41-1b** (or, if
  41-1b has closed, as a 41-16-owned type registration with its own count-pin bumps). It is the only
  option that keeps AC2 falsifiable. It also makes AC3 implementable: `FLAKY_WITHOUT_SPLIT_RESULT` is a
  rule on a type this story owns, not a story-local rule bolted onto a shared one.
  *Consequence: this story's `Produced documents` line changes from `TriageDecision` to
  `RegressionTriage`, and it acquires a `DocumentTypeKey`/`DocumentTypeRegistry` count-pin bump it did
  not have.*
- **C2 — THE BLOCKER THE STORY DOES NOT NAME: there is no per-test CI result anywhere in the platform, so
  "mine CI history for repeated failures" and AC3's same-commit-sha split have no data source.**
  Evidence, in order of decisiveness:
  1. `GitMediationMapping.ToTestRun` (`:46-61`) — the *only* projection from the CI-mediation wire —
     carries `RunId`/`Status`/`Total`/`Passed`/`Failed`/`Skipped`/`Coverage` and its doc-comment states
     outright: *"`TestRunResult.FailedTestDetails` is left empty — the CI-mediation endpoint returns
     aggregate counts only, not per-test detail."*
  2. `WaitForCIResultsActivity`'s field-by-field resume path (`:132-140`) reads `FailedTests` as an
     **int** and never populates `FailedTestDetails`.
  3. The DCB event AC1 would read, `TEST.RESULTS_RECEIVED.SUCCESS`, carries
     `sessionId`/`repository`/`branch`/`runId`/`attempt`/`maxAttempts` plus a free-text `ErrorDetail`
     built as `$"build={r.BuildPassed}, failedTests={r.FailedTests}/{r.TotalTests}"`
     (`TestingWorkflow.cs:180-185`). **No test names. No commit sha.** `GATE.*` carries less.
  4. The repo's own CI does produce per-test names — but only inside a `.trx` in the runner's workspace,
     parsed to stdout by a shell pipeline (`ci.yml:260-269`) and uploaded as an artifact **only on
     failure** (`:281-289`). Nothing ingests it; nothing persists it; a *passing* run's `.trx` is
     discarded, so "at least one pass **and** at least one failure of the same test at the same commit
     sha" (AC3) is unobservable even in principle from what CI retains today.
  **AC1 and AC3 are therefore unreachable, independently of the scheduler.** Someone must own a
  per-test-result ingest + store (a `test_results` table keyed `(tenantId, repository, commitSha,
  testName, outcome, runId, observedAt)`, fed either by parsing the `.trx`/JUnit artifact or by extending
  the CI-mediation wire + `CIResultsPayload.FailedTestDetails` end to end). Estimated separately below.
  This is a **second dependency that no story schedules** and the epic README does not list it.
- **C3 — `Prompts/tester/write-regression-test.md` produces a TEST FILE, not a `TestSpec`; rewriting it
  is in scope and the story does not say so.** The shipped cell declares
  `variables: role, testTarget, sourceCode, conventions`, `enableTools: true`, and closes with
  ```` File format: ```path/to/file // test contents ```` — it writes code.
  `TestSpecDocumentType.Validate` would reject that on every produce. Same class of edit 39-15 made for
  `debug-rootcause.md`. AC4 is unreachable without it. *(The story calls this cell "exists today,
  unbound" — true, and it is unbound precisely because it emits code, which is why it would otherwise
  belong in `ContractBindingTests.IntentionallyUnbound`'s "code/file-format output" class at `:303-311`.)*
- **C4 — AC5's `[ResumeBehavior(LatestStateReEntry)]` is right for the two bindings and wrong for the
  sweep.** A per-suspect sweep that must "fire at most once per window per tenant across a restart" with
  a **persisted** fired-window is not a `LatestStateReEntry` document producer at all — it has no
  document of its own to re-enter from. Its idempotency lives in the (unbuilt) scheduler seam's persisted
  last-fired row, not in `ILifecycleReEntryService`. Split the declaration: the bindings declare
  `LatestStateReEntry`; the sweep's resume story is part of the seam's design.
- **C5 — AC1's crash test ("kills the process mid-sweep … no suspect is double-triaged and none is
  dropped") tests the seam, not this story.** Per-suspect exactly-once across a restart is a property of
  the sweep's durable work-list, which does not exist. Written against Phase A (an input suspect list),
  the equivalent falsifiable test is per-**suspect** re-entry idempotency, which the landed
  `ComputeReEntryPositionActivity` already gives once each suspect is producer-scoped (D3).
- **C6 — the UUIDv7 outbox-ordering flake is NOT in `.dev/findings/`; it is in git history, and its
  lesson contradicts the story's high-autonomy row.** No file under `.dev/findings/` mentions it
  (verified). The record is `be35b89`. What it teaches, and what this story must encode:
  - The flake was `ChannelOutboxRepositoryTests.Enqueue_ListUnacked_OrderedByUuidV7Id`, 1 failure in
    5018, and it survived **four** human "re-run .NET Tests" commits before `890502a`'s `.trx`
    diagnostic named it. *That four-commit sequence is precisely the standing human chore this story
    exists to replace — cite it as the motivating evidence, not a hypothetical.*
  - Its evidence signature is **exactly** AC3's: same commit sha, sometimes pass, sometimes fail. It
    would have been classified `flaky` and, at autonomy 85–100, **auto-quarantined**.
  - That would have been the wrong action. The root cause was a real non-determinism in **production**
    code — `UuidV7.NewGuid()`'s sub-timestamp bytes were pure random, so ids minted within one
    millisecond did not sort by creation order, and *"the outbox FIFO replay (`ORDER BY id`) and any
    'read back in id order' consumer then occasionally came back out of enqueue order"*. Quarantining the
    test would have hidden a live ordering bug in the channel outbox.
  - **Design consequence (D6):** `flaky` is a *symptom* classification, never an *action*. A `flaky`
    verdict must open a root-cause step before any quarantine, and quarantine of a test covering
    production ordering/concurrency invariants belongs in the always-escalate class. Encode this rather
    than the story's "85–100: … quarantine + regression-test creation auto-assigned".
  - A second, distinct flake class is on record and is not the same thing: `d387454` (all tests passed,
    job exited 1 — test-host teardown) and the `--blame-hang` mitigation at `ci.yml:239`. A sweep reading
    only test outcomes cannot see it at all; `environmental` is the closest classification and the
    vocabulary should say so.
- **C7 — the story's "Blocking" list omits `41-1b`/type registration (a consequence of C1) and the
  per-test CI store (C2).** It correctly names `41-1a` and the scheduler seam.

## Design Decisions

- **D1 — Two-phase delivery; only Phase A is authorised to start.**
  **Phase A (unblocked once 41-1a + the C1 type land):** `regression-triage` binding + `regression-test-spec`
  binding, driven by an input suspect list; the prompt-cell rewrites; the drift/pin bookkeeping; ACs 2, 4,
  5 and the falsifiable half of 3.
  **Phase B (blocked):** the scheduled sweep + the CI-history read; ACs 1 and the "inside the sweep
  window" half of 3. Phase B does not start until both missing pieces are scheduled as stories.
- **D2 — Phase A entry point is `regression-triage`, a thin binding with an explicit suspect input.**
  `DefinitionId = "regression-triage"`. Inputs: `repository`, `testName`, `suspectEvidenceJson` (the
  observed run outcomes: `[{runId, commitSha, outcome, observedAt}]`), `issueId?`, `tenantId`,
  `acceptanceRulesJson?`. Outputs: `status`, `outcome`, `documentId`, `classification`, `error`. Where the
  evidence comes from is deliberately **outside** the binding — Phase B's sweep, or a human, or a future
  CI hook. This is what makes Phase A shippable.
- **D3 — Producer-scoped anchor per SUSPECT, not per issue.** Both new types are per-test, and multiple
  suspects share an issue/repository. The lifecycle anchor is
  `CreationBindingHelper.ScopeIssueId($"{repository}#{testName}", "regression-triage")` (and
  `"regression-test-spec"` for the follow-on). Without this, the second suspect's
  `ComputeReEntryPosition` short-circuits to `Complete` on the first suspect's accepted document — the
  39-15 D2 hazard, one level finer. **This also supplies C5's falsifiable replacement**: re-dispatching
  the same suspect after a crash re-enters at its own revision and never double-produces, while a
  different suspect on the same repository produces independently.
- **D4 — `RegressionTriage` document type (C1), registered in the 39-3/39-4 pattern.**
  `Types/RegressionTriage.cs`: closed `RegressionClassification { regression, flaky, environmental }`
  (`[Wire]`-mapped, alias-aware `TryParse` mirroring `TriageVocabulary`), plus `testName`, `reasoning`
  (required non-empty), `evidence[]` (`{runId, commitSha, outcome, observedAt}`), optional
  `proposedAction`. Violations: `OUT_OF_VOCABULARY` (classification not in the closed set),
  `REASONING_REQUIRED`, `EVIDENCE_REQUIRED`, and **`FLAKY_WITHOUT_SPLIT_RESULT`** (AC3, D5). Registered
  in `DocumentTypeRegistry.s_registrations` with its `RenderContract` + ≥1 valid / ≥1 invalid example.
- **D5 — AC3's split-result rule is a PAYLOAD rule on the new type, not a cross-document one.** Because
  the evidence rides *inside* the document (D4's `evidence[]`), `FLAKY_WITHOUT_SPLIT_RESULT` is
  computable by `Validate` alone: a payload whose `classification == "flaky"` and whose `evidence` does
  not contain **at least one `pass` and at least one `fail` sharing one `commitSha`** is rejected. No
  `ValidateWithContext`, no lifecycle plumbing. *(The story's "and the suspect is re-routed as
  `regression`/`environmental`" is not a validator behaviour — validators reject, they never re-route.
  Rejection drives the lifecycle's repair/revise ring, which re-produces with a corrected
  classification. That is the same intent, correctly located.)*
- **D6 — `flaky` opens a root-cause step; it never auto-quarantines (C6).** Encoded three ways, all
  cheap: (a) `RegressionTriage.proposedAction` has a closed vocabulary that includes
  `investigate-root-cause` and the type's `RenderContract` instructs that `flaky` ⇒
  `investigate-root-cause`, never `quarantine`, unless a root cause is already stated; (b) the shipped
  acceptance row for the type (D8) puts quarantine in `AlwaysEscalate`; (c) the rewritten prompt cell
  carries the `UuidV7` case as a one-paragraph worked example — a flake whose true cause was a production
  ordering bug. **No code path auto-quarantines anything in this story.**
- **D7 — The two prompt cells (C3).** `(tester, manage-regression)` is **new** — 41-1a mints the enum
  member and the eligibility entry; **this story authors `Prompts/tester/manage-regression.md`** to
  `RegressionTriageDocumentType.RenderContract()`. `Prompts/tester/write-regression-test.md` is
  **rewritten** from file-format output to `TestSpecDocumentType.RenderContract()` (the `write-tests.md`
  v2 precedent), front matter gaining a DECLARED feedback carrier, `version: 1 → 2`.
  **Loader lockstep, both directions:** `PromptFileLoader` throws `PROMPT.SEED.UNKNOWN_CELL`
  (`:106`, `:118`) for a file whose `{role}/{action}` is outside the taxonomy, and
  `PROMPT.SEED.NO_BODY_FAMILY` (`:161-167`) for a taxonomy cell with no file — so the enum member (41-1a)
  and the `.md` (this story) **must merge together or the app refuses to start**. The cell *count* pin is
  derived from the live taxonomy (`SystemPromptsTests.cs:23`) and both-directions keyset equality is
  already asserted (`:33` `CoversEveryTaxonomyCell_AndNothingExtra`), so no cell-count pin edit is needed
  — only 41-1a's `AgentActionTests.cs:38` `Be(80)` and `RolePhaseMapTests.cs:64` `HaveCount(80)`.
- **D8 — Acceptance policy for `RegressionTriage` is chosen, not inherited.** `AcceptanceDefaults.For`
  ends in `_ => Rules` (`:131`) — a new type silently takes single-`architect`-unanimous, which is the
  wrong reviewer for a test triage. This story adds an explicit arm: single **`tester`** reviewer
  (`GetReviewActionForRole(Tester)` ⇒ `review-testability`, already landed at `RolePhaseMap.cs:383` — no
  selector change needed), unanimous, with `AlwaysEscalate` carrying the quarantine class (D6b). Pinned by
  `AcceptanceDefaultsDriftTests`. *This is the 41-1b D1 discipline applied to this story's own type.*
- **D9 — `REGRESSION.*` / `FLAKY.*` emitter activity, house pattern.**
  `Tamma.Activities/Regression/RegressionEvents.cs` — `REGRESSION.SWEEP.STARTED`, `REGRESSION.SWEEP.ITEM`
  (data `testName`, `classification`, `failure`), `REGRESSION.SWEEP.COMPLETED`,
  `FLAKY.QUARANTINE.PROPOSED` (data `testName`, `rootCauseStated`) — plus
  `EmitRegressionEventActivity`. **Phase A emits `.ITEM` and `FLAKY.QUARANTINE.PROPOSED` only**;
  `.STARTED`/`.COMPLETED` are sweep-scoped and land with Phase B. Emissions gated on the re-entry
  position (39-12 D3).
- **D10 — Drift-gate bookkeeping, enumerated (rule 1 clause (f)).** Two `Bindings` entries —
  `(tester, manage-regression)` → `"RegressionTriageDocumentType.Validate"`, `(tester,
  write-regression-test)` → `"TestSpecDocumentType.Validate"`; two `BuildSeed` rows
  (`regression-triage` → `regression-triage`; `regression-test-spec` consumes `[regression-triage]`
  produces `test-spec`); `WorkflowInterfaceGraphTests.cs:45` `HaveCount(N) → HaveCount(N+2)` — **this
  story moves the pin by TWO, the only one of 41-12..41-16 that does**;
  both definition ids appended to that file's `reconciled` list; two names added to
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (`:125`); **plus** the C1 type's own
  `DocumentTypeKeyTests` `Be(N)` and `DocumentTypeRegistryTests` `HaveCount(N)` bumps if 41-1b has closed.
  41-1a owns `AgentActionTests.cs:38` / `RolePhaseMapTests.cs:64`.

## Implementation Steps — Phase A (authorised)

1. **Precondition gate (no code, a real gate).** Verify in tree and compiling: `(tester,
   manage-regression)` in `AgentAction` + `RolePhaseMap.EligibleActions` (**41-1a**); and agree with the
   41-1b owner whether `RegressionTriage` lands as their seventh type or as this story's own registration
   (C1). Any gap blocks steps 3–7 — file it, do not work around it.

2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Types/RegressionTriage.cs`** (D4/D5/C1) — the
   record, the closed `RegressionClassification` vocabulary with alias-aware parse (the
   `TriageVocabulary` shape at `TriageDecision.cs:50-106`), `RegressionTriageDocumentType` with
   `Validate` (incl. `FLAKY_WITHOUT_SPLIT_RESULT`), `RenderContract`, and ≥1 valid / ≥1 invalid example.
   **MODIFY `DocumentTypeKey.cs`** (+1 member) and **`DocumentTypeRegistry.s_registrations`**, with the
   two vocabulary count pins bumped in the same commit (D10).

3. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs`** (D8) — the explicit
   `RegressionTriage` arm + its `AcceptanceDefaultsDriftTests` pin.

4. **CREATE `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/manage-regression.md`** (D7) and **REWRITE
   `apps/tamma-elsa/src/Tamma.Api/Prompts/tester/write-regression-test.md`** (D7/C3) to
   `TestSpecDocumentType.RenderContract()`; `version: 1 → 2`; DECLARED feedback carrier in front matter;
   include D6's `UuidV7` worked example in the `manage-regression` body. **Merge in lockstep with
   41-1a's enum member** — the loader fails loud in both directions (D7).

5. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Regression/RegressionEvents.cs` +
   `EmitRegressionEventActivity.cs`** (D9).

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/RegressionBindingHelper.cs`** — pure,
   Elsa-free, total, fail-closed: `BuildSuspectContext(testName, suspectEvidenceJson)`,
   `ReadClassification(regressionTriageJson) → string` (`""` fail-closed — never `"flaky"` on unreadable
   input), `IsConfirmedRegression(json) → bool`, `BuildTaskIdContext(...)` reused from
   `CreationBindingHelper` for the follow-on `TestSpec`'s cross-document ring, `BuildFailureDetail`.
   Reuses `LifecycleBindingHelper` and `CreationBindingHelper.ScopeIssueId` (D3).

7. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RegressionTriageWorkflow.cs` and
   `RegressionTestSpecWorkflow.cs`** (D2/D3), each copying `TestCaseCreationWorkflow`'s skeleton:
   `ReadInputs` (scoped anchor) → `ComputeReEntryPosition` → `ReadPositionStage` → `FreshRun` →
   [`FetchConsumedRegressionTriage` for the follow-on] → `DispatchLifecycle` (the single
   `DispatchWorkflow`) → `ReadLifecycleExit` → `LifecycleAccepted` `FlowDecision` →
   `EmitSweepItem` / `EmitFlakyQuarantineProposed` / `EmitFailed` → `ExposeOutput`.
   Both `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (C4). Dispatch inputs mirror
   `TestCaseCreationWorkflow.cs:131-153` with `documentType` = `"regression-triage"` / `"test-spec"`,
   `producerRole = AgentRole.Tester.ToWire()`,
   `producerAction = AgentAction.ManageRegression.ToWire()` / `AgentAction.WriteRegressionTest.ToWire()`,
   a DECLARED `feedbackVariableName`, `validationContextJson` for the follow-on's task-ID ring, and the
   `acceptanceRulesJson` passthrough.

8. **MODIFY `DocumentTypeRegistry.BuildSeed`** (two rows) **and the drift/pin gates in ONE commit**
   (D10): `WorkflowInterfaceGraphTests.cs:45` **+2** + its `reconciled` list;
   `TaxonomyDriftBuildTests.cs:125` (two names); `ContractBindingTests.cs` `Bindings` (`:82`, two
   entries).

9. **CREATE the tests** — `RegressionTriageWorkflowStructureTests.cs`,
   `RegressionTestSpecWorkflowStructureTests.cs`, `RegressionBindingHelperTests.cs`
   (`tests/Tamma.Activities.Tests/Workflows/`) and
   `tests/Tamma.Core.Tests/Documents/Types/RegressionTriageDocumentTypeTests.cs`. See Test Plan.

10. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/RegressionLifecycleExecutionTests.cs`**
    on the shared 39-6/39-10 Testcontainers fixture. Scenarios in Test Plan. Finish with full
    `dotnet test` + `dotnet ef migrations has-pending-model-changes` (clean).

## Implementation Steps — Phase B (BLOCKED, do not start)

B1. **The tenant-aware scheduled-trigger seam — now story 41-30.** Minimum shape the epic README already
    specifies: a tenant component in the advisory-lock key; a `tenantId` threaded into the dispatch; a
    **persisted** last-fired window (a `scheduled_trigger_runs` row, not `_lastFired`); and a window/cron
    shape rather than a single `FireAtMinute`. Seven Epic 41 stories consume it (41-5, 41-7, 41-11,
    41-16, 41-17 PR-sweep, 41-20, 41-23) — it should be **one shared component**, not seven schedulers.
    Rough size: 4–6 days including the EF migration, the leader-lock generalisation and a
    multi-tenant/restart integration suite. **Not this story's to write.**

B2. **A per-test CI result store — no story schedules it (C2).** Minimum shape: a `test_results` table keyed
    `(tenantId, repository, commitSha, testName, outcome, runId, observedAt)`, plus an ingest. Two
    candidate ingests: parse the `.trx`/JUnit artifact CI already produces (`ci.yml:260-269` proves the
    parse is a five-line pipeline — but the artifact is uploaded **only on failure**, so passing runs
    must start being retained for AC3's split-result to be observable), or extend the CI-mediation wire
    end to end so `CIResultsPayload.FailedTestDetails` is actually populated (`GitMediationMapping.cs:46-61`
    is the single place that drops it). Rough size: 3–5 days. **Not this story's to write.**

B3. **`RegressionSweepWorkflow`** — reads B2's store for the window, builds the suspect list, and
    dispatches `regression-triage` per suspect with `WaitForCompletion = false`, emitting
    `REGRESSION.SWEEP.STARTED`/`.ITEM`/`.COMPLETED`. Fail-closed per suspect (a failed suspect emits
    `.ITEM` with the failure and the sweep continues). ~1.5 days **once B1 and B2 exist**.

## Data & Migrations

- **Phase A: none.** `RegressionTriage` and `TestSpec` documents persist to 39-11's `document_instances`
  (JSONB payload — a new document type needs no schema change);
  `REGRESSION.*`/`FLAKY.*`/`DOCUMENT.*` ride the existing drain.
  `dotnet ef migrations has-pending-model-changes` stays clean.
- **Phase B: two migrations, neither this story's** — the seam's persisted last-fired window (B1) and the
  `test_results` table (B2).

## Events

- **Emits (Phase A):** `REGRESSION.SWEEP.ITEM` (per suspect; data `testName`, `classification`,
  `failure`), `FLAKY.QUARANTINE.PROPOSED` (data `testName`, `rootCauseStated` — **proposed**, never
  applied, D6). Tags `repository`, `issueId`, `tenantId`, `correlationId`.
- **Emits (Phase B):** `REGRESSION.SWEEP.STARTED`, `REGRESSION.SWEEP.COMPLETED`.
- **Emitted by the machinery this story wires in:** the full `DOCUMENT.*` family,
  `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`, `DOCUMENT.REENTERED`.
- **Consumes (Phase B only):** B2's store. **Not** `GATE.*`/`TEST.*` from the DCB stream — per C2 those
  events carry no test names and no commit sha, so the story's stated `consumes: [GATE.*/TEST.* DCB
  events]` is not a usable source and should be corrected to name B2's store.

## Test Plan

All NUnit + FluentAssertions (Moq; Testcontainers for step 10). Phase A only.

- **`RegressionTriageWorkflowStructureTests` / `RegressionTestSpecWorkflowStructureTests`** — the clause
  set per binding, cloned from `TaskCreationWorkflowStructureTests`: builds; stable `DefinitionId`;
  threads `TenantId`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables (d); exactly one
  `DispatchWorkflow` with literal def id `document-lifecycle`, zero targeting `llm-call` (a+b);
  `ScanLifecycleBindingDispatches()` contains the canonical pair and `MaterializeDispatchInput` shows the
  expected `documentType` + declared `feedbackVariableName` (e); zero `Finish`, every leaf inside
  `ExposeOutput` (c); one `ComputeReEntryPositionActivity`; `[ResumeBehavior(LatestStateReEntry)]`; no
  `Wait*`. **Covers AC5 (structure half).**
- **`RegressionTriageDocumentTypeTests` (`Tamma.Core.Tests`)** —
  (i) **AC2, restated per C1:** a classification outside `{regression, flaky, environmental}` ⇒
  `OUT_OF_VOCABULARY` naming field + value; a classification with empty `reasoning` ⇒
  `REASONING_REQUIRED`; no evidence ⇒ `EVIDENCE_REQUIRED`; round-trip through the closed vocabulary.
  (ii) **AC3 (D5), the falsifiable form:** `classification = "flaky"` with one pass and one fail at the
  **same** `commitSha` ⇒ valid; `flaky` with failures only ⇒ `FLAKY_WITHOUT_SPLIT_RESULT`; `flaky` with a
  pass and a fail at **different** shas ⇒ `FLAKY_WITHOUT_SPLIT_RESULT`; `regression` /`environmental` with
  the same evidence ⇒ valid (the rule is flaky-specific).
  (iii) **D6:** a `flaky` payload proposing `quarantine` with no root cause stated is rejected /
  escalation-classed per the shipped rules; `investigate-root-cause` is accepted. **A named fixture
  reproduces the `UuidV7`/`ChannelOutboxRepositoryTests` case from `be35b89`** — same sha, split result,
  true cause a production ordering bug — and asserts the outcome is not an auto-quarantine. *This is the
  story's own regression test against its own worst failure mode.*
- **`TestSpecDocumentTypeTests` additions** — AC4's four codes against the **rewritten** template's shape:
  no cases ⇒ `EMPTY_TEST_SPEC`; case with no task id ⇒ `CASE_MISSING_TASK_ID`; no behavior ⇒
  `CASE_MISSING_BEHAVIOR`; a task id absent from the referenced plan ⇒ `CASE_UNKNOWN_TASK_ID` (through
  `ValidateWithContext`, never through the context-free `Validate`). **Covers AC4.**
- **`RegressionBindingHelperTests`** — `ReadClassification` fail-closed (`""`, never `"flaky"`, on
  null/garbage/missing); `IsConfirmedRegression` false on unreadable; `BuildSuspectContext` total;
  `ScopeIssueId` yields distinct anchors for two suspects on one repository (**the D3 guard**).
- **Template-conformance tests (both cells)** — the JSON example embedded in each shipped `.md`
  deserializes to its type and validates clean, and `write-regression-test.md` contains **no**
  ```` ```path/to/file ```` fence (a direct regression guard on C3). *This is what would have caught C3;
  the token-only `ContractBindingTests` cannot.*
- **Prompt-loader lockstep test** — `SystemPrompts.GetRoleAction("tester", "manage-regression")` is
  non-null and `SystemPromptsTests.CoversEveryTaxonomyCell_AndNothingExtra` is green (D7's both-directions
  fail-loud).
- **Drift-gate modifications (step 8, self-verifying)** — `ContractBindingTests` green with both new
  entries (non-stale via the lifecycle-binding walk) and the universal DocumentType-authority pin
  (`:626`) green, with `(tester, write-regression-test)` **absent** from `IntentionallyUnbound`;
  `TaxonomyDriftBuildTests` contributor subset holds; `WorkflowInterfaceGraphTests` **+2** count +
  non-provisional assertions; `AcceptanceDefaultsDriftTests` pins the D8 row.
- **`ResumableStandardStructuralTests`** — both new workflows pass with no `LegacyResumeAllowlist` entry.
  **Covers AC5.**
- **`RegressionLifecycleExecutionTests` (Testcontainers)** —
  (a) **regression path:** suspect with a fail-only evidence set → scripted `regression` classification →
  review approve → `Accept` → `REGRESSION.SWEEP.ITEM` emitted; the follow-on `regression-test-spec`
  binding consumes the accepted triage and produces an accepted `TestSpec` (**AC2, AC4**).
  (b) **flaky path (AC3):** split-result evidence at one sha → `flaky` accepted; a `flaky` draft with
  failures only ⇒ `FLAKY_WITHOUT_SPLIT_RESULT` → repair/revise → re-produced as `regression` and accepted
  (**the story's "re-routed" intent, correctly located in the revise ring per D5**).
  (c) **D6/C6 pin:** the `flaky` acceptance emits `FLAKY.QUARANTINE.PROPOSED` and **nothing quarantines**;
  a quarantine proposal with no root cause hits the always-escalate class.
  (d) **multi-suspect isolation (D3/C5):** two suspects on one repository each produce independently;
  re-dispatching suspect #1 after a crash re-enters at its own revision, emits exactly one
  `REGRESSION.SWEEP.ITEM` for it, and does not touch suspect #2 — **the falsifiable replacement for AC1's
  process-kill test**.
  (e) **validation exhaustion:** always-invalid stub → typed `ValidationExhausted` escalation with
  lineage; no error terminal reached.

**Not testable in Phase A, and stated as such:** AC1 in full (tenant scoping, once-per-window-per-tenant
across a restart, process-kill mid-sweep) and the "inside the sweep window" clause of AC3.

## Definition of Done

| AC | Phase | Satisfied by step(s) | Verified by |
|---|---|---|---|
| 1 — tenant-scoped, once-per-window-per-restart, fail-closed per suspect | **B — BLOCKED** | B1, B3 | *Unreachable today. Partial substitute: ExecutionTests (d) proves per-suspect isolation + re-entry idempotency (C5).* |
| 2 — closed-enum classification; `OUT_OF_VOCABULARY` / `REASONING_REQUIRED` *(on `RegressionTriage`, not `TriageDecision` — C1)* | A | 2, 7 | `RegressionTriageDocumentTypeTests` (i); ExecutionTests (a) |
| 3 — `flaky` requires same-sha split-result evidence; `FLAKY_WITHOUT_SPLIT_RESULT` | A (rule) / **B** (sweep window) | 2 (D5) | `RegressionTriageDocumentTypeTests` (ii); ExecutionTests (b). *The "inside the sweep window" scoping needs B2.* |
| 4 — confirmed regression yields a validating `TestSpec` | A | 4, 7 | `TestSpecDocumentTypeTests` additions; ExecutionTests (a) |
| 5 — `[ResumeBehavior(LatestStateReEntry)]`; 39-10 gate green without allowlist; new `WorkflowDocumentInterface` rows; edge pin bumped **+2** | A *(bindings only — C4)* | 7, 8 | `ResumableStandardStructuralTests`; `WorkflowInterfaceGraphTests` |

## Risks & Mitigations

- **Planning against a seam that does not exist.** The primary risk this plan exists to avoid.
  Mitigation: Phase B is explicitly not authorised; ACs 1 and half of 3 are marked unreachable rather
  than designed around; B1/B2 are sized so someone can own them.
- **C2 discovered late would sink the story mid-build.** A team could build the sweep, wire it to the DCB
  stream, and only then find that `TEST.RESULTS_RECEIVED` carries `"failedTests=3/412"` and no names.
  Mitigation: C2 is the second correction, with three independent code cites and the decisive
  doc-comment quoted verbatim.
- **The C1 type decision is contested.** If 41-1b has closed, `RegressionTriage` becomes this story's own
  registration and it inherits two vocabulary count-pin bumps. Mitigation: step 1 forces the decision
  before code; D10 enumerates the pins for either outcome.
- **Auto-quarantine hides a production bug (C6) — the repo has already lived this.** Mitigation: D6's
  three encodings plus the named `be35b89` fixture in the test plan. No code path in this story
  quarantines anything.
- **Two rewritten prompt cells drift back.** Mitigation: the template-conformance tests parse the example
  out of each shipped `.md` and validate it, and assert the file-format fence is gone.
- **41-1a lockstep is a hard merge coupling, not a soft one.** The enum member without the `.md` throws
  `PROMPT.SEED.NO_BODY_FAMILY` at startup; the `.md` without the member throws
  `PROMPT.SEED.UNKNOWN_CELL`. Mitigation: D7 names both codes; the two changes merge in one commit or
  behind one branch.
- **The `+2` edge pin (D10) is the largest single-integer conflict surface in the epic.** Mitigation:
  sequence it last in the branch; rebase; coordinate with whichever of 41-12..41-15 is in flight.

## Est. Effort

**Phase A (authorised):**

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition gate + the C1 type-ownership decision with the 41-1b owner | 0.3 |
| 2 | `RegressionTriage` type + vocabulary + `FLAKY_WITHOUT_SPLIT_RESULT` + registration + count pins | 1.0 |
| 3 | `AcceptanceDefaults` arm + drift pin | 0.2 |
| 4 | New `manage-regression.md` + `write-regression-test.md` rewrite (C3, D6 worked example) | 0.7 |
| 5 | `REGRESSION.*`/`FLAKY.*` events + emitter | 0.35 |
| 6 | `RegressionBindingHelper` | 0.4 |
| 7 | The two binding workflows | 1.2 |
| 8 | Two registry rows + the drift/pin edits (**+2** edge pin) | 0.4 |
| 9 | Structure + helper + type + `TestSpec` + conformance + loader tests | 1.1 |
| 10 | Testcontainers scenarios (a)–(e) + full-suite green | 0.8 |
| — | 41-1a / 41-1b lockstep coordination, review polish | 0.3 |
| **Phase A total** | | **6.75** |

**Phase B (blocked, sized for whoever owns it — NOT part of this story's estimate):**

| Item | Owner | Days |
|---|---|---|
| B1 tenant-aware scheduled-trigger seam (shared by 7 stories) | **none — must be written** | 4–6 |
| B2 per-test CI result ingest + store | **none — must be written** | 3–5 |
| B3 `RegressionSweepWorkflow` (once B1+B2 exist) | 41-16 | 1.5 |

**Story estimate was 5–6 days for the whole thing.** Phase A alone is ~6.75 because the story did not
scope the new document type (C1), the two prompt rewrites (C3), or the acceptance-policy arm — and Phase
B is 8.5–12.5 days of unscheduled work on top. **Recommend re-estimating the story as Phase A only and
splitting Phase B out**, exactly as the epic README's Wave-0 table already implies for the seam.

## Blocks / Blocked by

- **Blocked by — HARD, Wave-0: `41-1a`.** `(tester, manage-regression)` is absent from `AgentAction.cs`
  and `RolePhaseMap.EligibleActions` today (verified — the tester set is `context-scan`,
  `plan-test-strategy`, `write-test-cases`, `write-tests`, `write-regression-test`, `exploratory-test`,
  `verify-acceptance`, `code-review-coverage`, `triage-defect`, `review-testability`,
  `RolePhaseMap.cs:114-124`). 41-1a lists it at `:29`. **This blocks the human path too** — a human
  assignee still needs a cell to bind. 41-1a owns `AgentActionTests.cs:38` `Be(80)` and
  `RolePhaseMapTests.cs:64` `HaveCount(80)`; this story owns the `.md` (D7), and the two must merge
  together or the app refuses to start.
- **Blocked by — HARD, and NOT in the story: the `RegressionTriage` document type (C1).** Either 41-1b
  adopts it as a seventh type or this story registers it with its own count-pin bumps. Decide in step 1.
- **Blocked by — HARD: the tenant-aware scheduled-trigger seam (story 41-30, not yet built).** At the time of writing no story built it;
  the README calls it "the one thing in Epic 41 that no story builds" and lists this story among its seven
  consumers. `HourlyAnalyticsRollupScheduler` is **not** a usable substitute — all four of the story's
  criticisms verified above. **AC1 is unreachable without it and this plan does not pretend otherwise.**
- **Blocked by — HARD, SCHEDULED BY NO STORY, and NOT NAMED ANYWHERE ELSE: a per-test CI result store (C2).** No component
  in the tree records which test failed, let alone at which commit sha.
  `GitMediationMapping.cs:46-61` drops per-test detail by design; `TEST.RESULTS_RECEIVED.SUCCESS` carries
  aggregate counts in a free-text string (`TestingWorkflow.cs:180-185`); CI's `.trx` is parsed to stdout
  and uploaded only on failure (`ci.yml:260-269`, `:281-289`). **AC1's "mines CI history" and AC3's
  same-commit-sha split are both unreachable without it.** This should be added to the epic README's
  Wave-0 enabler table alongside the scheduler.
- **Blocked by — for landing the regression test only (not for this workflow): `Epic 40`.**
  `.github/workflows/tamma-agent.yml` does not exist in this repo (verified: `.github/workflows/`
  contains `tamma-worker.yml`), so the coding step's dispatch fails loud with `WorkflowNotFound`.
  Producing and accepting the `TestSpec` has no Epic 40 dependency; committing the test does. The story's
  own Corrected note is right about this.
- **NOT blocked by:** `41-1c` (no prose) · `41-13`/`41-15` (independent).
- **Blocked in *substance* (not in code) by 39-17/39-19/39-20** — the accept gate publishes and suspends
  and nothing decides (`Program.cs:414-417`, `:445-451`); the story's autonomy rows and "assigned to a
  tester/dev role" have no Task View to land in. 41-16 claims the **document + lifecycle + validation +
  persistence + events** half.
- **Blocks:** nothing hard. Softly consumes `41-15` (a failed acceptance verification is a regression
  signal) and feeds Epic 40's coding step.
- **Shared-edit register:** `ContractBindingTests.Bindings` (two entries),
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (two names),
  `DocumentTypeRegistry.BuildSeed` (two rows) + `s_registrations` + `DocumentTypeKey`,
  `AcceptanceDefaults.For`, and the single-integer `WorkflowInterfaceGraphTests.cs:45` edge pin
  (**+2 here**) — plus 41-1a's taxonomy files and 41-1b's vocabulary count pins. This story touches more
  shared surface than any other in 41-12..41-16; land it after the lighter producer stories, not before.
