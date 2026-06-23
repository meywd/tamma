# Completeness Audit — `TddWorkflow` (tdd-cycle)

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWorkflow.cs`
**Wrapper:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs` (`tdd-with-debug-retry`)
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/TDD/*` (WriteTests, ValidateTestSyntax, CheckTestsFail, WriteImplementation, AnalyzeCode, ApplyRefactoring, RevertRefactoring, CommitChanges)

---

## Purpose & Owner

Drives the red-green-refactor TDD cycle for a single implementation task: write failing tests
(RED), write minimum implementation (GREEN), optionally refactor while keeping tests green
(REFACTOR), then commit an atomic test+impl change. Called in a loop from the main autonomous
loop's `START_IMPLEMENTATION` state (via `tdd-with-debug-retry`).

**Owner:** Epic 2 — Autonomous Development Loop (Core). Stories **2-5** (write failing tests / RED),
**2-6** (implementation / GREEN), **2-7** (refactoring pass / REFACTOR). Maps to the architecture's
14-step loop `CODE_GENERATION` + `TEST_VALIDATION` band (`docs/architecture.md` §"Base 14-Step
Workflow").

---

## Maturity: **partial**

This is NOT a thin happy-path skeleton — it is a genuine, well-structured flowchart with real
multi-phase control flow: a RED→GREEN→REFACTOR pipeline, a test-syntax pre-validation gate with a
dedicated failure sink, a RED rewrite loop (max 2) guarded by `CheckTestsFailActivity`, a GREEN
debug loop (max 3) with a failure terminal, a confidence-gated refactor branch with a
test-passes-after-refactor check and a revert path, atomic commit, and a fire-and-forget code-index
update. The wrapper adds an outer debug-retry loop dispatching the `debugging` sub-workflow.

It is rated **partial** (not complete) because three things that the story specs and the project's
core rules treat as mandatory are missing, and they undermine the workflow's actual correctness:

1. **Mediation violation (P0).** `WriteTestsActivity`, `WriteImplementationActivity`,
   `CommitChangesActivity`, and `RevertRefactoringActivity` call **external providers directly** from
   the Elsa engine — `httpClient.CreateClient("anthropic")` → `POST /v1/messages`, or an
   `Engine:CallbackUrl`. This is the exact pattern the agent-architecture pivot bans
   (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1: "A workflow STEP
   MUST NEVER call an external API/provider directly"; the LLM path must go through `POST
   /api/v1/llm/call`). The engine here holds/transits provider keys.

2. **No code is persisted, and the RED/GREEN/REFACTOR test runs do not test the generated code
   (P0).** `WriteTests`/`WriteImplementation` return code as strings in their result objects; nothing
   writes those files to the working tree/branch. The phase test runs dispatch `testing-pipeline`,
   which is CI-based (`TriggerCIActivity` → `WaitForCIResultsActivity`) and runs against the *branch
   as it exists on the remote* — but the commit happens only at the very end (`CommitChanges`), after
   all three dispatches. So the RED dispatch, GREEN dispatch, and post-refactor dispatch all run
   against code that was never written/committed. The RED guard is fed a **fabricated**
   `TestRunResult` (`FailureMessages = "Not yet implemented"`), so the RED phase "passes" by
   construction regardless of reality — a silent false-signal.

3. **Zero DCB audit events (P0 for this platform).** No TDD activity emits any event. Story 2-5 (AC7
   + `TESTS.GENERATED.SUCCESS/FAILED`), story 2-7 (`REFACTORING.ANALYSIS.*`,
   `REFACTORING.APPLIED.*`, `REFACTORING.ROLLED_BACK`), and the architecture ("every state transition
   must emit a corresponding DCB event") all require it. CLAUDE.md: "Every operation must emit events
   for audit trail."

There are also genuine silent-failure / false-success paths (see Missing Capabilities).

---

## Current Capabilities (what it actually does today)

- **Init:** captures `sessionId, storyId, taskDescription, taskFiles, repositoryUrl, branchName,
  skillLevel` from input; initializes rewrite/debug counters and flags.
- **RED phase:**
  - `WriteTestsActivity` — LLM-generates test code (skill-level-adapted prompt; rewrite-aware via
    `IsRewrite`/`PreviousTestCode`). Returns `{TestCode, TestFiles, TestCount, Success}`.
  - `ValidateTestSyntaxActivity` — best-effort compiler/parse check (writes to a temp dir; "skipped"
    when no compiler on PATH). On invalid syntax → `SetSyntaxInvalidOutputs` (`success=false`,
    `finishReason="test-syntax-invalid"`, serialized `syntaxErrors`) → `FinishSyntaxInvalid`.
  - Dispatches `testing-pipeline` (RED), extracts passed/failed counts via
    `ExtractPassed`/`ExtractPassedCount`/`ExtractFailedCount` (tolerant JSON parsing of
    `qualityReport`).
  - `CheckTestsFailActivity` — `TestsFail` → GREEN; `TestsPass` → max-rewrite check (≥2 → proceed to
    GREEN anyway with a warning; else increment and loop back to `WriteTests`).
- **GREEN phase:**
  - `WriteImplementationActivity` — LLM-generates minimum implementation. `TestFailureOutput` is
    hardcoded `null`.
  - Dispatches `testing-pipeline` (GREEN), captures all-passed/passed/failed.
  - `greenTestsPassCheck` True → REFACTOR; False → debug loop (`markDebug` → increment →
    `maxDebugCheck` ≥3 → `SetFailedOutputs`/`FinishFailed`; else loop back to `WriteImplementation`).
- **REFACTOR phase:**
  - `AnalyzeCodeActivity` (confidence threshold 0.6) → `refactoringNeededCheck` (HasSuggestions &&
    Confidence ≥ 0.6). False → commit; True → `ApplyRefactoringActivity` → re-dispatch
    `testing-pipeline` → `refactorTestsPassCheck`. True → commit; False → `RevertRefactoringActivity`
    → commit.
- **Commit & index:** `CommitChangesActivity` (atomic test+impl commit, message
  `feat({storyId}): {taskDescription} [TDD]`) → `UpdateCodeIndexActivity` (fire-and-forget).
- **Outputs:** success path sets `success/testCount/commitSha/filesChanged`; GREEN-fail path sets
  `success=false` + `errorMessage`; syntax-invalid path sets the dedicated reason payload.
- **Wrapper (`tdd-with-debug-retry`):** dispatches `tdd-cycle`; on failure dispatches `debugging` and
  retries up to `maxRetries` (default 3); finishes with `success`/`errorMessage`.

---

## Intended Full Scope (with citations)

From **Story 2-5** (`docs/stories/epic-2/story-2-5/...md`, ACs 1–8) the RED phase must:
generate tests from the plan; **write test files to the filesystem**; validate syntax/structure;
**execute tests and confirm they fail** with a tolerant pass-rate gate (`confirmTestsFail`, spec
allows ≤10% passing); and **log to the event trail** (`TESTS.GENERATED.SUCCESS/FAILED`, AC7). The
`ITestFirstGenerator` contract names `validateTestSyntax`, `executeTests`, `confirmTestsFail`,
`organizeTestFiles`.

From **Story 2-7** (`docs/stories/epic-2/story-2-7/...md`, ACs 1–8) the REFACTOR phase must:
analyze opportunities; apply only safe ones (risk/effort/priority filtered); **all tests must
continue to pass**; refactoring **optional/skippable**; **validate it didn't break functionality**;
emit `REFACTORING.ANALYSIS.SUCCESS/FAILED`, `REFACTORING.APPLIED.SUCCESS/FAILED`,
`REFACTORING.ROLLED_BACK`; and provide **rollback** on failure (the workflow has a revert path —
good — but no events).

From **`docs/architecture.md`** (14-step loop + "Logging Requirements" appended to both stories):
"Every state transition must emit a corresponding DCB event (see Epic 4)."

From **`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`** §1–§2: a step
must never call an external provider directly; LLM work routes through `POST /api/v1/llm/call`
(`{ tenantId, role, agentId/persona?, prompt, params }`); the engine holds no provider key.
`CallLlmActivity`/`CallLlmInlineActivity` are the canonical thin-client / inline mediation seams to
model after. Per-tenant scoping (single-user vs SaaS) per CLAUDE.md.

Domain best-practice for a production TDD cycle additionally requires: handling the LLM-returned-no-
code case (don't proceed to a green-on-empty), feeding real test-failure output into the GREEN debug
retry (otherwise the retry is blind), refusing to commit on a real test failure, and never
fabricating a "commit succeeded" SHA.

---

## Missing Capabilities

| # | Capability (gap to complete) | Priority | dependsOn |
|---|---|---|---|
| 1 | Route all LLM calls (WriteTests, WriteImplementation) through `POST /api/v1/llm/call` via the `TammaApiClient`/inline seam; delete the in-engine `"anthropic"`/`/v1/messages` and `Engine:CallbackUrl` direct paths. Engine holds no key. | P0 | 32-5 mediation |
| 2 | Route git effects (CommitChanges commit, RevertRefactoring checkout) through the mediated non-LLM internal-API seam; remove `SimulateCommit` fake-SHA fallback (false success). | P0 | Epic 38 (non-LLM mediation) |
| 3 | Persist generated test/impl files to the working tree/branch BEFORE the phase test runs, so `testing-pipeline` actually exercises the generated code. Today RED/GREEN/REFACTOR dispatches run against an un-committed branch. | P0 | Epic 38 (git apply seam) / none |
| 4 | Replace the fabricated RED `TestRunResult` (`FailureMessages="Not yet implemented"`) with the real parsed test-run result; implement `confirmTestsFail` pass-rate gate (≤10% passing) per story 2-5 AC6. | P0 | none |
| 5 | Emit DCB events: `TESTS.GENERATED.SUCCESS/FAILED`, `TESTS.RUN.*` (RED/GREEN/REFACTOR), `IMPLEMENTATION.GENERATED.SUCCESS/FAILED`, `REFACTORING.ANALYSIS.*`, `REFACTORING.APPLIED.*`, `REFACTORING.ROLLED_BACK`, `COMMIT.CREATED.SUCCESS/FAILED`, phase START/COMPLETE — tagged `{issueId, storyId, sessionId, tenantId}`. | P0 | none |
| 6 | Branch on `WriteTests` failure (`Success=false` / empty `TestCode`) → fail with reason instead of validating/running empty tests. | P0 | none |
| 7 | Branch on `WriteImplementation` failure (`Success=false` / empty `ImplementationCode`) → route to debug loop / fail, not silently to GREEN test run. | P0 | none |
| 8 | Feed REAL test-failure output into the GREEN debug retry: capture failure messages from the GREEN `testing-pipeline` result and pass them into `WriteImplementation.TestFailureOutput` (currently hardcoded `null`, so the retry is blind). | P1 | none |
| 9 | Branch on `CommitChanges` failure (`CommitResult.Success=false`, e.g. "No files to commit") → fail path; do not report workflow success on a no-op/failed commit. | P0 | none |
| 10 | Thread `tenantId` through `tdd-cycle` (the wrapper passes it but `TddWorkflow` Init never reads/forwards it) so mediated calls and DCB events are correctly tenant-scoped. | P0 | 32-5 / none |
| 11 | Honor refactoring config: enable/skip flag, risk/effort/priority filtering of suggestions (story 2-7 AC4 "optional and can be configured or skipped"; AC2 risk filtering). Today only a 0.6 confidence gate. | P1 | none |
| 12 | Surface refactor-revert as a tracked outcome (RefactoringResult success/reverted) in outputs + event, rather than the activity swallowing all errors and always logging "not a failure". | P1 | none |
| 13 | Distinguish "RED tests passed → task pre-implemented" terminal outcome (story 2-5 — when tests genuinely cannot be made to fail) from a normal proceed; currently it silently proceeds to GREEN with only a log. | P2 | none |
| 14 | Emit teaching/mentorship feedback signal (SessionId is plumbed everywhere but never used to record mentorship events) for the L1–L5 skill-adaptation loop. | P2 | Mentorship workflow |
| 15 | Per-phase timeouts / overall cycle timeout + cancellation handling (story perf targets: RED <2min, refactor pass <2min). | P3 | none |
| 16 | Idempotency: re-running a cycle for the same `(storyId, taskDescription)` should be safe (dedupe commit, avoid duplicate test files). | P3 | none |

---

## Ordered Build-Out Spec (to reach complete + robust)

Steps are ordered so structural correctness (persistence + real signals) lands before polish.

1. **Thread tenant + forward inputs.** In `TddWorkflow` Init, add `tenantId` variable and
   `setTenantId = Assign(tenantId, ctx => ctx.GetInput<string>("tenantId") ?? "")`. Pass `tenantId`
   into every activity and every `DispatchWorkflow("testing-pipeline")` input dict. (Gap #10.)

2. **Mediate the LLM steps (Gap #1).** Refactor `WriteTestsActivity` and
   `WriteImplementationActivity` to delegate to `POST /api/v1/llm/call` via `TammaApiClient` (model
   on `CallLlmActivity`/`CallLlmInlineActivity`), passing `{ tenantId, role: "tester"/"implementer",
   prompt, params }`. Delete `CallLlm`/`CallEngineCallback`/`Anthropic:UseMock` direct branches.
   Add workflow branches: after `writeTests`, a `writeTestsOkCheck` FlowDecision
   (`testGenResult.Success && !string.IsNullOrEmpty(TestCode)`); False →
   `SetFailedOutputs(reason="test-generation-failed")` → `FinishFailed`. (Gaps #1, #6.)
   Emit `TESTS.GENERATED.SUCCESS`/`FAILED` (Gap #5).

3. **Persist generated artifacts before running them (Gap #3).** Add a `WriteFilesToWorkspace` step
   (mediated git/filesystem apply seam — Epic 38) after `writeTests` (test files) and after
   `writeImplementation` (impl files). The phase test dispatch must run against a tree that contains
   the generated code. If the testing-pipeline requires a pushed branch, push/commit a WIP checkpoint
   per phase (or switch the RED/GREEN runs to a local-runner activity). Emit `FILES.WRITTEN.*`.

4. **Real RED signal + pass-rate gate (Gap #4).** Replace the hand-built `TestRunResult` feeding
   `CheckTestsFailActivity` with the parsed `testingPipelineResult` (use `ExtractPassedCount`/
   `ExtractFailedCount` and real failure messages). Add `confirmTestsFail` semantics: treat RED as
   correct when `passedCount/total <= 0.10`. On "tests cannot fail / pre-implemented" after max
   rewrites, route to a distinct `FinishPreImplemented` outcome with `finishReason="pre-implemented"`
   instead of silently proceeding (Gap #13). Emit `TESTS.RUN.RED`.

5. **GREEN failure context + impl-failure branch (Gaps #7, #8).** After `writeImplementation`, add
   `writeImplOkCheck` (Success && non-empty code); False → debug loop / fail. Capture GREEN
   `testing-pipeline` failure messages into a `greenFailureOutput` variable and bind
   `WriteImplementation.TestFailureOutput = greenFailureOutput` so the debug retry is informed.
   Emit `IMPLEMENTATION.GENERATED.*`, `TESTS.RUN.GREEN`.

6. **Refactor config + filtering (Gaps #11, #12).** Read a `refactoring.enabled` config; when
   disabled, skip straight to commit. Filter `AnalyzeCode` suggestions by risk/effort/priority before
   `ApplyRefactoring`. Bind `RefactoringResult` (applied/skipped/reverted) into outputs. Make
   `RevertRefactoringActivity` set a `reverted=true` result rather than swallowing errors silently.
   Emit `REFACTORING.ANALYSIS.SUCCESS/FAILED`, `REFACTORING.APPLIED.SUCCESS/FAILED`, and
   `REFACTORING.ROLLED_BACK` on the revert edge. Run `testing-pipeline` after revert too (the revert
   itself must leave tests green) — add a `revertTestsPassCheck`; on still-failing → `FinishFailed`.

7. **Mediate commit + fail-closed (Gaps #2, #9).** Route `CommitChangesActivity` through the
   mediated git seam; delete `SimulateCommit`. After `commitChanges`, add `commitOkCheck`
   (`CommitResult.Success`); False → `SetFailedOutputs(reason="commit-failed")` → `FinishFailed`.
   Emit `COMMIT.CREATED.SUCCESS`/`FAILED`. Likewise route `RevertRefactoring` through the mediated
   git-checkout seam.

8. **DCB events end-to-end (Gap #5).** Inject the event-append seam (a mediated `record-event`
   internal endpoint, since the engine holds no DB for cross-cutting DCB) and emit phase
   START/COMPLETE plus the success/failure events listed above, each tagged
   `{ issueId, storyId, sessionId, tenantId, provider }`, with `metadata.workflowVersion`. Verify the
   audit trail reconstructs the full red-green-refactor sequence.

9. **Robustness polish (Gaps #14, #15, #16).** Add per-phase + overall cycle timeouts with a clean
   `FinishFailed(reason="timeout")` edge; record mentorship/teaching feedback events keyed by
   `sessionId`; make commit + file-write idempotent (skip if branch already contains the change).

10. **Tests.** Unit-test each rewritten activity (mediated client mocked); integration-test the full
    flowchart for: normal RED→GREEN→REFACTOR→commit; RED-tests-pass rewrite loop + pre-implemented
    terminal; GREEN debug loop exhaustion → FinishFailed; refactor breaks tests → revert → commit;
    test-gen / impl-gen / commit failures → FinishFailed; syntax-invalid → FinishSyntaxInvalid.

---

## Bottom line

`TddWorkflow` is a real, multi-phase, branch-rich workflow — clearly past "thin" — but it is
**partial**: its LLM/git steps violate the no-direct-external-call mediation rule, it never persists
the code it generates (so the phase test runs and the RED guard operate on fabricated/empty signals),
it can report success on a simulated/failed commit, and it emits no DCB audit events. Closing the
seven P0 gaps (mediation, persistence, real RED signal, failure branching on gen/impl/commit, tenant
threading, and DCB events) is required before it can be called complete.
