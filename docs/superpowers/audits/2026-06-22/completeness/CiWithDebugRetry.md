# Completeness Audit — CiWithDebugRetryWorkflow (`ci-with-debug-retry`)

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs`
**Maturity:** **partial**
**Overall priority:** P1
**Effort to complete:** M

---

## Purpose & owner

A CI sub-workflow that dispatches the `testing-pipeline` workflow and, on failure, runs the
`debugging` sub-workflow and re-runs CI up to `MaxRetries` (default 3) iterations before giving up.
Outputs `passed` (bool), `errorMessage` (string), `ciRetryCount` (int).

- **Owning story:** Epic 13, Story 13.2 — "CI Debug Retry Sub-Workflow"
  (`docs/stories/epic-13/13-2-ci-debug-retry-sub-workflow.md`). Epic 13 = workflow decomposition:
  extract the CI retry loop out of `SingleIssueCycleWorkflow` into a reusable, independently testable
  sub-workflow (`docs/stories/epic-13/README.md`).
- **Counter-reset follow-up:** Story 12-5e (`apps/wiki-site/public/content/stories/epic-12/12-5e-...`)
  — verified `ciRetryCount` resets to 0 on entry (the originally reported persistence bug was stale).

---

## Maturity: partial

The internal control flow is **complete and correct for its narrow extracted purpose** — it fully
implements the Story 13.2 shape (testsPassed → guard → increment → debug → loop, with terminal
pass/fail SetOutput sequences and a `Finish`). Build/structure tests pass
(`WorkflowStructureTests`, `CiRetryCounterResetTests`, `CiRetryCounterPersistenceTests`).

It is rated **partial — not complete** because, measured against its intended role in the live ADL
and the project's current architecture rules, it has material gaps:

1. **Orphaned / unwired.** Nothing in `apps/tamma-elsa/src` dispatches `ci-with-debug-retry`
   (grep finds no `new("ci-with-debug-retry")` caller; only its own definition + tests reference it).
   `SingleIssueCycleWorkflow` now runs CI inside `ExecuteAgentActivity` (the agent runs tests on the
   Actions runner) and never invokes this dedicated CI-retry loop. Story 13.2 AC-5/AC-6 (parent
   dispatches it; the 3 loop-back paths target it) were **not** wired into the current parent — its
   retry/guard/debug-loop logic is unreachable. This was already flagged STALE in the cluster audit
   `docs/superpowers/audits/2026-06-22/workflow-audit-cycle-git-cicd.md` (§ CiWithDebugRetry, and
   cross-cutting observation #3).
2. **No DCB audit events.** The workflow emits zero events — no `CI.RETRY.STARTED/SUCCESS/FAILED`,
   no per-attempt event. CLAUDE.md mandates 100% audit-trail coverage; for a loop that re-runs CI and
   auto-debugs, the lack of an audit envelope is a real gap.
3. **Broken correlation across retries.** Each `testing-pipeline` and `debugging` dispatch uses a
   fresh `Guid.NewGuid()` as `SessionId`/`sessionId` (lines 104, 156), so the three retry attempts
   cannot be stitched together in the audit trail.
4. **Transitive architecture-pivot dependency.** Its `debugging` dispatch reaches
   `AIDiagnosisActivity`, a §1.2 in-engine **direct-LLM fallback** caller (Epic 32/38 violator). Once
   wired, this workflow inherits the "steps never call external APIs directly" requirement
   transitively. (Its `testing-pipeline` → `TriggerCIActivity` is already compliant — internal
   callback, holds no CI credential.)

So: the skeleton is fully fleshed for what Story 13.2 narrowly asked, but it is not a
production-complete, wired, auditable CI phase. Hence **partial**, not **complete** and not **thin**.

---

## Current capabilities

- `initInputs` captures `repository`, `branchName`, `issueNumber`, `skillLevel`, optional
  `maxRetries`; always resets `ciRetryCount` to 0 on entry (full budget per invocation — verified
  correct, Story 12-5e).
- Dispatches `testing-pipeline` (`DispatchWorkflow`, `WaitForCompletion=true`), capturing
  `testResult`.
- `testsPassed` `FlowDecision` reads `passed` from the testing-pipeline result.
- On pass → `finishPassOutputs` sets `passed=true`, `errorMessage=""`, `ciRetryCount`.
- On fail → `ciRetryGuard` (`ciRetryCount < maxRetries`):
  - guard False → `finishFailOutputs` sets `passed=false`, `errorMessage="CI debug retry limit
    reached (N attempts)"`, `ciRetryCount`.
  - guard True → `incrementCiRetry` → `dispatchCiDebugging` (`debugging` workflow,
    `debugContextMode=RuntimeError`, error output extracted from the testing result) → loops back to
    `testingPipeline`.
- Single terminal `Finish` reached from both pass and fail sequences.
- Helper `GetTestErrorOutput` provides a non-empty default error string for the debug dispatch.

---

## Intended full scope (with citations)

- **Story 13.2 contract** (`docs/stories/epic-13/13-2-ci-debug-retry-sub-workflow.md`): the 5
  extracted activities, inputs `repository/branchName/issueNumber/skillLevel/ciRetryCount`, outputs
  `passed/errorMessage/ciRetryCount`, AND — critically — **AC-5/AC-6:** `SingleIssueCycleWorkflow`
  must dispatch `ci-with-debug-retry` (1 `DispatchWorkflow` + 1 `FlowDecision` on `passed`) and all
  **3 loop-back paths** in the parent (review-fix loop; merge-approval "test" signal; CI debug retry
  re-entry) must target the dispatch. The intended scope is a sub-workflow that is *actually called*
  by the cycle as the CI phase.
- **Story 13.2 logging requirements** (same file, "Logging Requirements"): a full table of INFO/DEBUG
  events is specified — sub-workflow started, testing pipeline dispatched/result, retry guard
  evaluated, counter incremented, debugging dispatched, completed-passed / completed-retry-limit, plus
  parent-side dispatch/result/loop-back-reason logs. The workflow currently emits **none** of these
  (no `ILogger`, no DCB event).
- **Agent-architecture pivot** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`):
  Rule 1 — a step must never call an external API directly. §1.2 table: `TriggerCIActivity` =
  **Compliant** (internal `/api/engine/trigger-ci` callback); `AIDiagnosisActivity` (reached via the
  `debugging` dispatch) = **VIOLATION (fallback)** — direct `/v1/messages` keyed fallback to be cut
  over to `call-LLM` under Epic 32. So once this workflow is wired, the auto-debug path must route LLM
  via `call-LLM`.
- **Cluster audit** (`docs/superpowers/audits/2026-06-22/workflow-audit-cycle-git-cicd.md`): rated
  STALE; the deliberate decision required is "re-wire it as the CI phase after the TDD loop (it adds
  the auto-debug retry the agent path lacks), or retire it"; if retained, fix `Guid.NewGuid()`
  session correlation and document the intended caller.
- **CLAUDE.md** project rules: DCB audit events for every operation; tenant→system→error (never
  empty/plain fallback); no silent-failure / false-success; ISO-8601 timestamps; `tenantId` threaded
  for per-mode (SaaS) resolution.

A **complete** version of this workflow is: a wired CI phase that the cycle invokes after the TDD
loop; threads `tenantId`; emits a DCB audit envelope + per-attempt events; correlates all retries
under a stable session id; routes its auto-debug LLM via the mediated `call-LLM`/`debugging` path
(no in-engine keyed fallback); and never silently advances on a missing/unparseable testing-pipeline
result.

---

## Missing capabilities

| # | Capability | Priority | Depends on |
|---|------------|----------|------------|
| 1 | **Wire as the CI phase** — `SingleIssueCycleWorkflow` (or the TDD-loop exit) dispatches `ci-with-debug-retry` and branches on `passed`; route the 3 loop-back paths (review-fix, merge-approval "test", CI re-entry) to it per Story 13.2 AC-5/AC-6. Or make a documented retire decision. | P1 | SingleIssueCycle wiring decision (this workflow) |
| 2 | **DCB audit events** — emit `CI.RETRY.STARTED`, per-attempt `CI.ATTEMPT.STARTED/RESULT`, and terminal `CI.RETRY.SUCCESS` / `CI.RETRY.FAILED`, tagged `issueId`, `tenantId`, `repo`, `branchName`, `attempt`, `ciRetryCount`. | P1 | none |
| 3 | **Stable session correlation** — derive `SessionId`/`sessionId` for the dispatched `testing-pipeline` and `debugging` from `issueNumber`+attempt (e.g. `adl-{issue}-ci-{attempt}`) instead of `Guid.NewGuid()`, so retries correlate in the audit trail. | P1 | none |
| 4 | **Result-shape / missing-result guard** — if `testResult` is null or missing `passed`, treat as an explicit failure with an error event (no silent advance); current `testsPassed` returns `false` on a missing result, which is fail-closed but emits no signal. | P1 | none |
| 5 | **Mediated auto-debug LLM** — the `debugging` dispatch must reach `AIDiagnosis` via `call-LLM` (no in-engine direct keyed fallback) before this loop is re-enabled. | P1 | Epic 32 (AIDiagnosis cutover) |
| 6 | **Thread `tenantId`** — accept `tenantId` as input and pass it into both `testing-pipeline` and `debugging` dispatches so per-tenant prompts/credentials/budget resolve under SaaS. | P2 | Epic 32 (call-LLM per-tenant) |
| 7 | **Structured logging** — add `ILogger`-based logs per the Story 13.2 logging table (attempt number, guard decision, counter, durations). | P2 | none |
| 8 | **Surface debug outcome** — `debugResult` is captured but never inspected; if the debug sub-workflow itself fails (no fix applied), the next CI run will just fail again and burn an attempt silently. Branch on the debug result and short-circuit / annotate `errorMessage` when debugging produced no fix. | P2 | none |
| 9 | **Aggregate failure context in `errorMessage`** — on retry-limit exhaustion, the failure message is generic ("limit reached"); include the last testing-pipeline `errorMessage`/quality summary so the parent can report a useful reason. | P3 | none |

> No P0: there is no correctness/safety defect in the live system because the workflow is currently
> unreachable. The P1s become live the moment it is wired (gap #1).

---

## Ordered build-out spec (to reach complete)

1. **Decision gate (do first): wire or retire.** Per the cluster audit, make the deliberate
   decision. The recommended path is **wire it** as the CI phase the agent TDD path lacks
   (auto-debug + bounded retry). If retiring, delete the workflow + its tests and remove the wiki
   entry; stop here. The rest of this spec assumes "wire it".

2. **Add inputs + correlation.** Add a `tenantId` workflow variable and capture it in `initInputs`
   (gap #6). Introduce an attempt-scoped, stable session id helper: in `testingPipeline` and
   `dispatchCiDebugging`, replace `Guid.NewGuid()` with a deterministic `SessionId` derived from
   `issueNumber` + current `ciRetryCount` (e.g. `$"adl-{issue}-ci-{ciRetryCount}"`), matching the
   `adl-{issue}-task-{n}` convention used in SingleIssueCycle (gap #3).

3. **Emit the entry DCB event.** After `initInputs`, add a Tamma event-emitting activity
   `CI.RETRY.STARTED` tagged `{ issueId, tenantId, repo, branchName }` (gap #2). Use a
   `TammaActivity`/`TammaAsyncActivity` base (the pattern used by `UpdateIssueStatusActivity` /
   `WaitForPRApprovalActivity`).

4. **Guard the testing-pipeline result.** Replace the inline `testsPassed` lambda's silent
   `return false` with an explicit branch: if `testResult` is null or lacks `passed`, route to a
   `CI.RESULTS.MISSING`-event step then to `finishFailOutputs` with an explicit
   `errorMessage="CI result unavailable"` — fail-closed WITH a signal, never an empty/plain fallback
   (gap #4). Keep the existing pass/fail decision for well-formed results.

5. **Per-attempt events.** Before each `testingPipeline` dispatch emit `CI.ATTEMPT.STARTED`
   (`attempt=ciRetryCount`); after it emit `CI.ATTEMPT.RESULT` (`passed`, durationMs). On the
   pass branch emit terminal `CI.RETRY.SUCCESS` inside `finishPassOutputs`; on guard-False emit
   `CI.RETRY.FAILED` (`ciRetryCount`, `maxRetries`, last error) inside `finishFailOutputs` (gap #2).
   Add the Story 13.2 `ILogger` logs alongside (gap #7).

6. **Inspect the debug outcome.** After `dispatchCiDebugging`, add a `FlowDecision` on
   `debugResult` (e.g. a `fixApplied`/`success` key). If debugging applied no fix, either
   short-circuit to `finishFailOutputs` (don't burn further CI runs that will deterministically fail)
   or annotate the carried error context; on success, loop back to `testingPipeline` as today
   (gap #8). Thread `tenantId` into the `debugging` dispatch input.

7. **Confirm the mediated LLM path.** Verify (do not re-enable before) that the `debugging`
   sub-workflow's `AIDiagnosis` routes through `call-LLM` (Epic 32) rather than the direct keyed
   `/v1/messages` fallback. The `testing-pipeline` side is already compliant (`TriggerCIActivity`
   internal callback). Gate re-wiring (step 8) on this (gap #5).

8. **Wire into SingleIssueCycle (Story 13.2 AC-5/AC-6).** Add a `DispatchWorkflow("ci-with-debug-retry")`
   as the CI phase after the TDD loop, `WaitForCompletion=true`, passing
   `repository/branchName/issueNumber/skillLevel/tenantId`. Add a `FlowDecision` on the sub-workflow's
   `passed`: True → continue to PR-approval/merge; False → `notifyError`/`reportError` (NOT a silent
   advance). Point the review-fix loop-back and the merge-approval "test" path at this dispatch
   (gap #1). Thread `tenantId` through the dispatch.

9. **Enrich the failure message.** In `finishFailOutputs`, build `errorMessage` from the last
   testing-pipeline `errorMessage`/quality summary plus the attempt count, instead of the generic
   "limit reached" string, so the parent surfaces an actionable reason (gap #9).

10. **Tests.** Add the Story 13.2 path-equivalence tests (first-try pass; fail→debug→pass;
    retry-limit-reached; review-fix re-dispatch; merge-approval "test" re-dispatch) plus new
    assertions for: stable session id across attempts, DCB events emitted on each terminal path,
    missing-result fail-closed branch, and `tenantId` propagation. A replay/structure test should
    assert the loop bounds at exactly `maxRetries`.
