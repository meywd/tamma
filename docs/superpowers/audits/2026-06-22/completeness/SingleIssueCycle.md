# Completeness Audit — `SingleIssueCycleWorkflow`

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` (DefinitionId `single-issue-cycle`)
**Maturity verdict:** **partial** (core happy-path orchestration is real and substantial; correctness/safety and several intended-scope phases are missing)
**Overall priority:** **P0** (silent-failure and false-success edges violate hard project rules)
**Effort to "complete":** **L**

---

## 1. Purpose & owner

The per-issue "roundabout": takes ONE pre-selected work item from the ADL Orchestrator (`AdlOrchestratorWorkflow` → `DispatchCycleActivity`) and drives it from validation through plan → review → tasks → branch → draft-PR → TDD-per-task → code review → approval → merge → close → deploy, reporting the outcome back to the engine.

- **Owning epic:** Epic 2 — Autonomous Development Loop (`docs/stories/epic-2/README.md`); the parent "14-step loop" is `docs/architecture.md` §"Base 14-Step Workflow" and PRD **FR-1**.
- **Cross-cutting owners:** Epic 13 (Workflow Decomposition — explicitly names this file and the TDD/CI debug-retry split), Epic 19 (`IAgentExecutor` mode abstraction — Story 19-5 AC-6 wired `ExecuteAgentActivity` here), Story 27-6 (per-account prompt propagation — AC3 requires `accountId` on **all** sub-workflow dispatches), Story 2-18 (git-workflow prompt/quality overhaul), Epic 32 redesign (LLM mediation — `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`).

---

## 2. Maturity: partial

This is **not** a thin stub like `PullRequestWorkflow` (which is literally `CreatePR → 3× SetOutput`). It is a ~918-line flowchart with ~40 activities, real branching (review outcomes: approved/needsModification/defer/split/needsHuman; task review: approved/needsChanges/needsHuman), bounded revision loops (max-3 with escalation), a per-task TDD loop, two bookmark waits (approval, merge), fire-and-forget issue notifications, and a `ReportCycleResult` exit on every modeled terminal branch. Sub-workflows handle context, plan, plan-review, tasks, task-review, branch, PR, test-cases, code-review, merge, deployment.

What keeps it from **complete** is a cluster of **correctness/safety holes** (unhandled fault edges, false-success on empty sub-results, TDD failure ignored) plus **missing intended-scope phases** (CI/build/security gates, ambiguity gate, the already-built debug-retry sub-workflows that are NOT wired in) and **multi-tenant/idempotency contract gaps**.

---

## 3. Current capabilities (what it actually does today)

- Reads workflow inputs (`workItemJson`, `repository`, `issueNumber`, `botAssignee`, `baseBranch`, `tenantId`) into variables; reads repo conventions via `ReadRepoConventionsActivity`.
- `ValidateWorkItemActivity` → `Valid`/`Invalid`; `Invalid` → notify + `reportError` → finish (the ONE wired error edge).
- Sub-workflow dispatches (all `WaitForCompletion=true` except notifications/code-review/merge): `context-gathering`, `plan-generation`, `plan-review`, `task-creation`, `task-review`, `branch-creation`, `pull-request` (draft), `test-case-creation`, `code-review` (fire&forget), `merge` (fire&forget), `deployment-pipeline`.
- Plan-review routing via `FlowSwitch`: approved / needsModification (bounded 3× revision loop → escalate) / defer (→ `create-issues` → reportDeferred) / split (→ `create-issues` → reportSplit) / needsHuman (→ reportNeedsHuman).
- Task-review routing: approved / needsChanges (bounded 3× → escalate) / needsHuman.
- Per-task TDD loop: `initTaskLoop` (count tasks) → `hasMoreTasks` → `extractCurrentTask` → `ExecuteAgentActivity` (mode-aware Local/GitHubActions executor per Story 19-5) → `incrementTask` → loop.
- After TDD: fire `code-review`, then `WaitForPRApprovalActivity` (bookmark) → fire `merge` + `WaitForPRMergedActivity` (bookmark) → close issue + `deployment-pipeline` → `reportSuccess` → finish.
- ~20 fire-and-forget `update-issue-status` notifications with label add/remove on milestones.
- DCB audit events: each composed activity inherits `TammaActivity`/`TammaOutcomeActivity`/`ITammaActivity` and emits Start/Success/Failure events (`CYCLE.WORKITEM.VALIDATE`, `AGENT.EXECUTION`, `CYCLE.PR.APPROVAL.WAIT`, `CYCLE.PR.MERGE.WAIT`, `CYCLE.RESULT.REPORT`, etc.). LLM/agent work is correctly **mediated** — steps do not call providers directly; LLM goes through the sub-workflows (`llm-call` → `/api/v1/llm/call`) and agent execution through `ExecuteAgentActivity`/`IAgentExecutor`.

---

## 4. Intended full scope (with citations)

1. **The 14-step loop** (`docs/architecture.md` §"Base 14-Step Workflow", PRD **FR-1**): issue assignment → **context gathering → research → ambiguity check** → code gen → **build validation → test validation → security scan** → code review → PR creation → **CI check** → approval gate → merge → deployment. The current workflow implements context/plan/review/tasks/TDD/PR/review/approval/merge/deploy but has **no research, ambiguity-check, build-validation, test-validation, security-scan, or CI-check step in the parent** — those are folded into the agent's opaque internal run or fire-and-forget merge.
2. **Clarifying-question / ambiguity gate** (PRD **FR-3**: "generate clarifying questions when encountering ambiguous specifications and wait for user approval before proceeding"). No needsHuman/question-back gate exists before plan generation.
3. **TDD with retry + debug** (Epic 13 README: extract `TddWithDebugRetryWorkflow` + `CiWithDebugRetryWorkflow`; PRD **FR-5**: red→green→refactor). Both sub-workflows **already exist** (`TddWithDebugRetryWorkflow.cs` `tdd-with-debug-retry`, `CiWithDebugRetryWorkflow.cs` `ci-with-debug-retry`) but are **not dispatched** anywhere — the parent uses a single `ExecuteAgentActivity` per task with no retry/debug and no CI loop.
4. **Per-account/tenant propagation** (Story 27-6 AC3: "`SingleIssueCycleWorkflow` propagates `accountId` to all sub-workflow dispatches"). Today `tenantId` reaches context/plan/review/tasks/test-cases/code-review but is **omitted** from branch-creation, pull-request, create-issues, merge, deployment, the wait activities, and the `NotifyIssue` helper.
5. **Robust git workflow** (Story 2-18 audit): structured PR description, derived labels, draft→ready transition, base-branch passthrough, conflict-aware branch naming — partially delegated to sub-workflows but the parent does not enforce/await their success.
6. **No silent-failure / no false-success / tenant→system→error / never empty-plain fallback** (CLAUDE.md hard rules; `feedback_resolution_no_empty_fallback`): every sub-result must be checked; a missing/empty critical output must route to error/needsHuman, never proceed with `""`.
7. **Mediation contract** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` rule #1/#2): steps never call external APIs directly; LLM via `POST /api/v1/llm/call`. Currently honored via sub-workflows — must stay honored for any new step added.

---

## 5. Missing capabilities (gap to complete)

| # | Capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Fault/error edges on every awaited sub-workflow + activity.** Only `Validate→Invalid` routes to error. `context-gathering`, `plan-generation`, `plan-review`, `task-creation`, `task-review`, `branch-creation`, `pull-request`, `test-case-creation`, `deployment-pipeline`, `ReadRepoConventions`, `WaitForPRApproval`, `WaitForPRMerged` have **no failure edge** — a fault aborts the instance with no `reportError`/`notifyError`. `notifyError` is declared but **never connected**. | **P0** | none |
| 2 | **No-false-success result checks.** `extractPlan`/`extractTasks`/`extractContext`/`extractBranch`/`extractPR` fall back to `""`/`0` on missing keys and proceed. Empty `planJson` → review of nothing; `prNumber==0` → waits on a non-existent PR forever. Must validate-and-route-to-error (never empty/plain fallback). | **P0** | none |
| 3 | **TDD failure is ignored.** `tddForTask` `Completed` AND `Failed` both → `incrementTask`; a failed task still produces a "successful" PR. Must branch `Failed` → debug-retry → (still failing) escalate/abort, not advance silently. | **P0** | Epic 13 (`tdd-with-debug-retry`) |
| 4 | **Debug-retry sub-workflows not wired.** `tdd-with-debug-retry` and `ci-with-debug-retry` exist but are never dispatched. Parent has no CI gate at all between TDD and merge (CI is hidden inside fire-and-forget `merge`). | **P0** | Epic 13 (both already built) |
| 5 | **`accountId`/`tenantId` not propagated to all dispatches** (Story 27-6 AC3). Missing on branch-creation, pull-request, create-issues, merge, deployment-pipeline, `WaitFor*`, and `NotifyIssue`. Cross-tenant prompt/credential resolution + audit scoping breaks for those steps. | **P0** | Story 27-6 |
| 6 | **No bookmark timeout/SLA on the two waits.** `WaitForPRApproval`/`WaitForPRMerged` block forever — no timeout timer, no human-nudge, no stale-PR escalation, no cancel path. | **P1** | none |
| 7 | **Missing 14-step phases in the parent:** ambiguity/clarifying-question gate (FR-3), explicit security-scan, explicit build/test-validation gate, explicit CI-check step. | **P1** | Epic 38 mediation for any new external call; Epic 3 quality gates |
| 8 | **No idempotency / resume guard.** Re-dispatch for an issue with an existing branch/open PR re-runs from scratch (duplicate branches/PRs). No "already in progress / already has PR" short-circuit. | **P1** | none |
| 9 | **`extractTaskReview` dead-code bug.** `TryGetValue("tasksJson")` to load revised tasks sits **after** the `decision` `return`, so it is unreachable; the needsChanges loop re-reviews stale `tasksJson`. | **P1** | none |
| 10 | **Deferred/Split paths don't await/verify `create-issues`.** `subResult.GetValueOrDefault("deferred")` may be null → `"[]"`, silently creating no issues while reporting `deferred`/`split` success. | **P1** | none |
| 11 | **`reportSuccess` fires after `deployment-pipeline` regardless of its result.** Deployment failure still reports `success`. Must inspect deploy result and branch to error/partial. | **P1** | none |
| 12 | **Parallel close-issue + deploy after merge with no join/compensation.** `closeIssue` and `deploymentPipeline` race; if deploy fails post-close the issue is already marked completed. | **P2** | none |
| 13 | **No per-cycle correlation/run-id surfaced for time-travel.** Events emit per-activity but the parent doesn't stamp a single cycle correlation id / `issueId` tag onto every dispatch for clean replay. | **P2** | none |
| 14 | **No structured final summary output** (files changed, tokens, duration, deploy env) on the success report for analytics (Epic 36). `ReportCycleResultActivity` only carries reason + issueNumber. | **P3** | Epic 36 |

---

## 6. Ordered build-out spec (to reach complete + robust)

Honor project rules throughout: **tenant→system→error (never empty/plain fallback)**, **no silent-failure / no false-success**, **steps never call external providers directly (route LLM via `/api/v1/llm/call`, external integrations via Epic-38 mediation)**, **emit DCB audit events on every new edge**.

### Phase A — Correctness & safety (P0; the user's core complaint)

1. **Add a global error sink.** Wire `notifyError` (already declared) + `reportError` and connect a `Failed`/fault outcome from **every** awaited step to them:
   - For each `DispatchWorkflow` that must succeed (`gatherContext`, `generatePlan`, `reviewPlan`, `createTasks`, `reviewTasks`, `createBranch`, `createPR`, `createTestCases`, `deploymentPipeline`, `createDeferredIssues`, `createSplitIssues`): after each `extract*`, add a `FlowDecision` `<step>Ok?` that checks the extracted critical output is present/valid; `False` → `notifyError` (label `tamma-error`, remove `tamma-processing`) + `reportError` → `finish`. Emit a DCB event `CYCLE.STEP.FAILED` (tags: `issueId`, `tenantId`, `stepId`).
   - For `readConventions`, `waitForApproval`, `waitForMerged`: add a `Faulted`/error edge (or a surrounding `Try`/fault handler) → `notifyError` + `reportError`.
2. **Replace empty-string fallbacks with validated routing.** In `extractPlan`/`extractTasks`/`extractContext`/`extractBranch`/`extractPR`: if the required key is absent or blank/`0`, do NOT set `""`/`0` and continue — set an `ExitReason="error"` and route to the Phase-A error sink. (e.g. `planJson==""` → error; `prNumber<=0` → error before any `WaitFor*`).
3. **Branch the TDD `Failed` outcome.** Replace `ConnectOutcome(tddForTask,"Failed",incrementTask)` with: `Failed` → dispatch `tdd-with-debug-retry` (def already exists) with the one-task slice + `tenantId`; on its `Success` → `incrementTask`; on its `Failed` → set `ExitReason="needsHuman"` (or `error`), `notifyNeedsHuman`, `reportNeedsHuman`/`reportError` → `finish`. Add a per-task failure counter to cap retries. Emit `CYCLE.TDD.TASK.FAILED` / `CYCLE.TDD.TASK.RETRIED`.
4. **Insert an explicit CI gate after the TDD loop, before approval.** On `hasMoreTasks==False`: dispatch `ci-with-debug-retry` (def already exists) for the PR branch (`WaitForCompletion=true`, pass `tenantId`); `Success` → `dispatchCodeReview` + `waitForApproval`; `Failed` → `notifyError` + `reportError` → `finish`. Emit `CYCLE.CI.PASSED` / `CYCLE.CI.FAILED`.
5. **Propagate `tenantId`/`accountId` everywhere** (Story 27-6 AC3): add `["tenantId"]=tenantId.Get(ctx)` to `createBranch`, `createPR`, `createDeferredIssues`, `createSplitIssues`, `dispatchMerge`, `deploymentPipeline` inputs; add a `tenantId` param to the `NotifyIssue` helper and to `WaitForPRApproval`/`WaitForPRMerged` inputs so their events + downstream resolution are tenant-scoped.
6. **Fix the `extractTaskReview` ordering bug.** Move the revised-`tasksJson` capture **before** the decision `return` so the needsChanges loop re-reviews the revised tasks.

### Phase B — Intended-scope phases (P1)

7. **Ambiguity / clarifying-question gate (FR-3).** Between `extractContext` and `generatePlan`, dispatch an `ambiguity-check` sub-workflow (LLM via `llm-call`/`/api/v1/llm/call`, role/action keyed, tenant→system→error). Outcome `clear` → continue; `ambiguous` → post questions (notify) + `WaitForHumanAnswerActivity` bookmark (with timeout, see #11) → resume into `generatePlan`. Emit `CYCLE.AMBIGUITY.DETECTED` / `CYCLE.QUESTION.ANSWERED`.
8. **Security-scan + build/test-validation gate.** Add explicit steps mapping the architecture's `SECURITY_SCAN` / `BUILD_VALIDATION` / `TEST_VALIDATION` (Epic 3 quality gates) — either as new sub-workflows or assertions inside the CI gate (#4) — each with pass/fail edges to the error sink. Emit `GATE.SECURITY.*`, `GATE.BUILD.*`, `GATE.TEST.*`.
9. **Idempotency / resume guard.** First thing after `validateItem`: a `CheckCycleStateActivity` that queries existing branch/open-PR/in-progress label for this issue. Outcomes: `fresh` → continue; `hasOpenPr` → jump to `waitForApproval`; `alreadyCompleted`/`alreadyProcessing` → `reportSuccess`/no-op → finish. Emit `CYCLE.RESUMED` / `CYCLE.DUPLICATE_SKIPPED`.
10. **Verify deferred/split issue creation.** After `createDeferredIssues`/`createSplitIssues`, check the result reports `createdCount>0` (and that the input array was non-empty); empty input or failure → error sink instead of reporting `deferred`/`split` success.

### Phase C — Robustness & observability (P1→P3)

11. **Bookmark SLA timers.** Give `WaitForPRApproval`/`WaitForPRMerged` (and any human-answer bookmark) a parallel `Delay`/timeout branch: on timeout → escalation notify + `WaitForHuman`/re-nudge, with a hard cap → `reportNeedsHuman`. Emit `CYCLE.PR.APPROVAL.TIMEOUT` / `CYCLE.PR.MERGE.TIMEOUT`.
12. **Inspect deployment result.** Capture `deployment-pipeline` result; `success` → `notifyMerged` + `reportSuccess`; `failed`/`partial` → `notifyError` + a new `reportPartial` (reason `deployFailed`) so the cycle does not report false success.
13. **Join close-issue/deploy or order them.** Either run `closeIssue` only after `deploymentPipeline` succeeds, or add a compensation edge that reopens/labels the issue if deploy fails.
14. **Stamp a cycle correlation id.** In `initInputs`, generate/propagate a `cycleId` and include it in every dispatch input + as a DCB tag on all emitted events for clean time-travel replay (DCB pattern).
15. **Rich success summary.** Extend `reportSuccess` (and `ReportCycleResultActivity`) to carry filesChanged/commits/tokens/duration/deployEnv (from `ExecuteAgentActivity` + deploy result) for Epic 36 analytics.

---

## 7. Notes / non-issues

- **Mediation is already correct.** No step in this workflow calls an external provider directly — LLM work flows through sub-workflows that terminate at `llm-call`/`/api/v1/llm/call`, and agent execution through `ExecuteAgentActivity`/`IAgentExecutor` (Story 19-5). New steps added above must preserve this (route external calls through tamma-api / Epic-38 mediation).
- **Epic 13 is the right home** for items #3/#4 — the `tdd-with-debug-retry` and `ci-with-debug-retry` sub-workflows are built; this workflow simply never dispatches them. Wiring them is mostly connection-graph work, not new activities.
- This is **partial, not thin** — unlike `PullRequestWorkflow` (genuinely thin). The build-out is hardening + filling intended phases, not building from a skeleton.
