# Completeness Audit — TddWithDebugRetryWorkflow

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs`
**Child workflows it composes:** `tdd-cycle` (`TddWorkflow.cs`), `debugging` (`DebuggingWorkflow.cs`)
**Scope source of truth:** `docs/stories/epic-13/13-1-tdd-debug-retry-sub-workflow.md` (Story 13.1, Epic 13 — Workflow Decomposition)
**Companion structural audit:** `docs/superpowers/audits/2026-06-22/workflow-audit-agent-llm.md` (lines 100–113, rated **GOOD**)
**Related completeness audits:** `Debugging.md` (the child it dispatches), `SingleIssueCycle.md` (the parent that superseded the inline path)

---

## Purpose & owner

One-line purpose: a thin, reusable orchestrator sub-workflow that wraps the `tdd-cycle` red-green-refactor workflow in a bounded debug-retry loop — on TDD failure it dispatches the `debugging` workflow and re-runs the cycle, up to `maxRetries` (default 3), then emits a `success`/`errorMessage` contract.

Owner: **Epic 13 — Workflow Decomposition**, Story **13.1** (status in structural audit: `done`). It was created by extracting the TDD retry loop out of `SingleIssueCycleWorkflow` so the parent shrinks and the loop is independently testable/versionable. It is registered via ELSA assembly scan (`AddWorkflowsFrom<...>()`) with `DefinitionId: "tdd-with-debug-retry"`.

**Critical context — the workflow is currently orphaned.** A repo-wide search shows the only file that references `tdd-with-debug-retry` / `TddWithDebugRetry` is the workflow itself; `SingleIssueCycleWorkflow` no longer dispatches it. Per Story **19-5 AC-6** (`docs/stories/epic-19/story-19-5/19-5-agent-executor-abstraction.md`, cited in `SingleIssueCycleWorkflow.cs:472-512`), the inline TDD step was replaced with a mode-aware `ExecuteAgentActivity` (`LocalExecutor` / `GitHubActionsExecutor` via `AgentExecutorFactory`). So the autonomous loop no longer drives TDD through `tdd-cycle` at all — it hands the whole per-task TDD to an external CLI/Actions agent. `tdd-with-debug-retry` (and `tdd-cycle`) survive only as standalone / re-usable sub-workflows with no production caller today.

---

## Maturity: **thin**

This is a faithful, correct extraction of exactly the 5 activities Story 13.1 asked for (`tddCycle`, `tddSuccess`, `tddDebugGuard`, `incrementTddDebug`, `dispatchTddDebugging`), and as a *pure orchestrator* it is clean: no activity touches a provider, the loop terminates via `maxRetries`, and both finish branches are wired (the structural audit rated it GOOD on those axes, with one cosmetic P2). **It fully satisfies Story 13.1's narrow "extract the loop" scope.**

It is rated **thin** (not partial/complete) for the *completeness* lens because the story it implements was deliberately a mechanical refactor — it inherited the original happy-path skeleton verbatim and added nothing beyond it. Measured against what a production-complete "TDD-with-retry mediator" must do (the autonomous-loop contract, the pivot rules, DCB audit, and parity with its CI sibling), it has real gaps:

- **No DCB audit events.** The workflow emits zero `TDD.*` events at graph boundaries (cycle dispatched, passed, failed, debug attempted, retry-exhausted). The Story 13.1 "Logging Requirements" table mandates structured logging at every one of these points; CLAUDE.md mandates DCB audit events for every operation. Neither is present — there is no `ILogger<T>`, no event-emit step.
- **The `tenantId` it threads is silently dropped downstream.** It forwards `tenantId` into both the `tdd-cycle` and `debugging` dispatch inputs — but `TddWorkflow` declares/reads **no** `tenantId` (verified: its init block reads only sessionId/storyId/taskDescription/taskFiles/repositoryUrl/branchName/skillLevel), and `DebuggingWorkflow` declares **no** `tenantId` variable at all. So in SaaS mode the children resolve **system** prompts/conventions/credentials, not the tenant's — a silent wrong-scope, contrary to the tenant→system→error rule. This workflow does its half correctly; the contract is broken on the receiving side.
- **No real error propagation.** On retry-exhaustion it emits a hardcoded `errorMessage = "TDD debug retry limit reached (N attempts)"` and throws away the actual failure detail (`GetTddErrorOutput(tddResult)` is used only to feed the debugger, never surfaced on the failure output). The caller and audit trail never learn *why* TDD failed.
- **No idempotency / session continuity.** Each `tddCycle` and each `dispatchTddDebugging` mints a fresh `Guid.NewGuid()` `sessionId`, so retries are not correlated into one TDD session and a resumed/re-dispatched run cannot dedupe. The CI sibling has the same issue but at least exposes `ciRetryCount`.
- **Output-contract drift vs. its sibling.** `CiWithDebugRetryWorkflow` (the near-identical CI variant) exposes a `ciRetryCount` output and resets its counter on entry for re-entrancy. This workflow does **not** reset `tddDebugAttempt` on entry (only `maxRetries` is overridable) and exposes no `tddDebugAttempt`/`attempts` output — so a re-dispatched instance can carry a stale counter, and callers can't see how many retries were burned.

This is the user's "thin = happy-path skeleton" category: it does the one thing the extraction story asked, lacks audit/observability, leaks no diagnostic data, and has a tenant-scoping contract that's only half-honored.

---

## Current capabilities (what it does today)

- **Init (`InitTddRetryInputs`)** — captures `storyId`, `planJson`, `repositoryUrl`, `branchName`, `skillLevel`, `issueNumber`, `tenantId`, and optional `maxRetries` from workflow input (conditional, non-empty-only). Does **not** reset `tddDebugAttempt`.
- **TDD cycle (`DispatchTddCycle`)** — `DispatchWorkflow("tdd-cycle", WaitForCompletion=true)`, passing a fresh `sessionId`, `storyId`, `taskDescription=planJson`, empty `taskFiles`, repo/branch/skill/tenant. Result captured into `tddResult`.
- **TDD success check (`TddSuccess`)** — `FlowDecision` reading `tddResult["success"]` (tolerant: `bool true` or string `"True"`). True → finish-success.
- **Debug guard (`TddDebugGuard`)** — `FlowDecision(tddDebugAttempt < maxRetries)`. False → finish-failure.
- **Increment + debug (`IncrTddDebug` → `DispatchTddDebugging`)** — bumps the counter, then `DispatchWorkflow("debugging", WaitForCompletion=true)` with `debugContextMode="TddFailure"`, `errorOutput=GetTddErrorOutput(tddResult)`, repo/branch/skill/tenant. Loops back to `DispatchTddCycle`.
- **Finish-success (`TddRetryFinishSuccess`)** — `SetOutput(success=true)`, `SetOutput(errorMessage="")`.
- **Finish-failure (`TddRetryFinishFailure`)** — `SetOutput(success=false)`, `SetOutput(errorMessage="TDD debug retry limit reached (N attempts)")`.
- **Terminal `Finish`** — both finish sequences converge.
- **No provider calls** — composes only `DispatchWorkflow`/`SetVariable`/`FlowDecision`/`SetOutput` (pivot-clean by construction).

---

## Intended full scope (with citations)

**Story 13.1** (`docs/stories/epic-13/13-1-tdd-debug-retry-sub-workflow.md`):
- AC2–AC6 (the extraction shape): file exists, `DefinitionId "tdd-with-debug-retry"`, the 5 activities, inputs `{storyId, planJson, repositoryUrl, branchName, skillLevel}`, outputs `{success, errorMessage}`. **All met.**
- AC9 "path equivalence" with the original inline section. **Met** (it is a verbatim extraction).
- **Logging Requirements (mandatory section):** the workflow "should use `ILogger<T>`" and log a defined table of events — *Sub-workflow started, TDD cycle dispatched, TDD cycle result received, TDD debug retry guard evaluated, TDD debug counter incremented, Debugging workflow dispatched, Sub-workflow completed (success), Sub-workflow completed (retry-limit, WARN)* — each with structured properties and a `{ParentWorkflowInstanceId}` correlation id passed in by the parent, and explicit redaction (do NOT log `planJson`). **None of this is implemented** — there is no logger, no parent-instance correlation input, no event lines.

**Architecture pivot** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`, and the structural-audit README rule 7): "a workflow step must never call an external provider directly; route via the `call-LLM` mediation"; and `tenantId` must be threaded end-to-end for BYOK→platform credential resolution. This workflow holds no provider step (compliant), and it *does* forward `tenantId` — but its compliance is "inherited from the children" (`tdd-cycle`, `debugging`), and those children currently **drop** the `tenantId` (see Maturity). The structural audit notes this workflow depends transitively on **32-5** via its children, and rule-5 (line 288) flags the inconsistent `tenantId` threading: `TddWithDebugRetry` threads it, `TddWorkflow` does not.

**DCB / CLAUDE.md:** every operation emits an audit event (`AGGREGATE.ACTION.STATUS`). A retry-orchestrating workflow should emit `TDD.CYCLE.STARTED`, `TDD.CYCLE.PASSED/FAILED`, `TDD.DEBUG.ATTEMPTED`, `TDD.RETRY.EXHAUSTED`, `TDD.COMPLETED.SUCCESS` so the audit trail and time-travel debugging can reconstruct why an issue cycle stalled in TDD. None are emitted.

**Sibling parity** (`CiWithDebugRetryWorkflow.cs`): the near-identical CI variant resets its retry counter on entry (for re-entrancy) and exposes a `ciRetryCount` output. A complete TDD variant should mirror that (reset `tddDebugAttempt`, expose `tddDebugAttempt`/`attempts`).

**Domain best-practice for a retry mediator:** propagate the real underlying failure (not a generic "limit reached" string), correlate retries under one stable session id, and gate `success` strictly on the child's `success` (no silent false-success — currently OK since it does read `tddResult["success"]`).

**Reachability / lifecycle:** Per Story 19-5 the main loop no longer calls this workflow; a complete state is either (a) re-wire it as the standalone/local-mode TDD path so it is actually reachable, or (b) formally mark it deprecated. Today it is silently orphaned — neither documented as deprecated nor wired in.

---

## Missing capabilities

| # | Capability (gap to complete) | Priority | Depends on |
|---|---|---|---|
| 1 | **Real failure propagation on retry-exhaustion** — surface the child's actual `errorMessage` (`GetTddErrorOutput(tddResult)`) on the failure `SetOutput`, not a generic "retry limit reached" string. Caller/audit currently never learn the cause. No-silent-failure. | P0 | none |
| 2 | **`tenantId` actually honored downstream** — this workflow forwards `tenantId`, but `tdd-cycle` (`TddWorkflow`) and `debugging` (`DebuggingWorkflow`) do not declare/read it, so SaaS resolves system (not tenant) prompts/conventions/creds. Either capture+thread `tenantId` in the children, or this workflow's forwarding is dead. Tenant→system→error, never silently wrong-scope. | P0 | 32-5 / 32-3 (child-side `tenantId` capture) |
| 3 | **Emit DCB audit events** at graph boundaries: `TDD.CYCLE.STARTED`, `TDD.CYCLE.PASSED`, `TDD.CYCLE.FAILED`, `TDD.DEBUG.ATTEMPTED`, `TDD.RETRY.EXHAUSTED`, `TDD.COMPLETED.SUCCESS` (tags: `storyId, issueNumber, tenantId, attempt`). None emitted today. | P1 | none |
| 4 | **Structured logging (Story 13.1 mandatory)** — inject `ILogger<T>`, accept a `parentWorkflowInstanceId` input for correlation, log the 8 defined events (success/retry-WARN), and redact `planJson`. None implemented. | P1 | none |
| 5 | **Reset `tddDebugAttempt` on entry + expose it as output** — mirror `CiWithDebugRetryWorkflow` (reset for re-entrancy; emit `attempts`/`tddDebugAttempt` output) so a re-dispatched instance gets a full retry budget and callers see retries burned. | P1 | none |
| 6 | **Stable / idempotent session correlation** — derive a deterministic `sessionId` (e.g. `tdd-{issueNumber}-{storyId}`) shared across `tddCycle` and `dispatchTddDebugging` instead of `Guid.NewGuid()` per dispatch, so retries correlate into one TDD session and resumed runs dedupe. | P1 | none |
| 7 | **Resolve the orphan status** — either re-wire as the standalone/single-user (LocalExecutor) TDD path so it is reachable, or formally mark deprecated/superseded by Story 19-5's `ExecuteAgentActivity`. Today it is silently unreferenced. | P2 | 19-5 / Epic 13 owner decision |
| 8 | **Distinguish failure reasons on output** — when `debugging` itself errors vs. genuine TDD non-convergence, emit a `finishReason` (mirrors `TddWorkflow`'s `finishReason`/`test-syntax-invalid` pattern) so a debugger-crash isn't reported identically to "tests still red after N tries". | P2 | none |
| 9 | **Capture/branch on the `debugging` result** — `debugResult` is captured but never inspected; if the dispatched `debugging` workflow returns `success=false` (escalated), looping straight back to `tddCycle` burns a retry on a known-unfixable failure. Consider short-circuiting to failure when the debugger escalates. | P2 | none |

---

## Ordered build-out spec (to reach complete + robust)

Steps ordered so independent correctness/observability fixes land first, then the tenant-mediation work that is coupled to 32-5 and the children.

### Phase 1 — Correctness & contract (P0/P1, independent of pivot)

1. **Propagate the real failure (cap. 1).** In `TddRetryFinishFailure`, change the `errorMessage` `SetOutput` to read the child failure: `(object)$"TDD failed after {maxRetries.Get(ctx)} attempts: {GetTddErrorOutput(tddResult.Get(ctx))}"`. Add a `finishReason` output (`"tdd-not-converged"` vs `"debugger-error"`) so callers/audit get the cause (cap. 8 foundation).
2. **Reset counter on entry + expose it (cap. 5).** In `InitTddRetryInputs`, add `tddDebugAttempt.Set(ctx, 0)` (mirror `CiWithDebugRetryWorkflow`'s `ciRetryCount.Set(ctx, 0)`). Add a third `SetOutput("tddDebugAttempt", tddDebugAttempt.Get(ctx))` to **both** finish sequences.
3. **Stable session id (cap. 6).** Add a `sessionId` workflow variable computed once in init (`$"tdd-{issueNumber}-{storyId}"`); use it in both the `tddCycle` and `dispatchTddDebugging` `Input` maps instead of `Guid.NewGuid()`.
4. **Branch on the debugger result (cap. 9).** Capture is already wired (`debugResult`). Add a `debuggerEscalated?` `FlowDecision` reading `debugResult["success"]` after `dispatchTddDebugging`; on `False` (debugger escalated/couldn't fix) → route to `TddRetryFinishFailure` with `finishReason="debugger-escalated"` instead of looping back to `tddCycle` and wasting a retry.

### Phase 2 — Observability & audit (P1, independent)

5. **Structured logging (cap. 4).** Inject `ILogger<TddWithDebugRetryWorkflow>` (or resolve from execution context). Add a `parentWorkflowInstanceId` input. Add `WriteLine`/log steps for the 8 Story-13.1 events (started, cycle dispatched, cycle result, guard evaluated, counter incremented, debugging dispatched, completed-success, retry-limit WARN), each tagging `{WorkflowInstanceId, ParentWorkflowInstanceId, StoryId, BranchName, TddDebugAttempt}`. **Redact `planJson`** — never log its content.
6. **DCB audit events (cap. 3).** Add an `EmitEventActivity` (reuse the engine-callback event-emit seam used by analytics/tenant workflows) and emit at graph boundaries: `TDD.CYCLE.STARTED` (before `tddCycle`), `TDD.CYCLE.PASSED` / `TDD.CYCLE.FAILED` (on `tddSuccess` outcomes), `TDD.DEBUG.ATTEMPTED` (after `incrementTddDebug`), `TDD.RETRY.EXHAUSTED` (guard False), `TDD.COMPLETED.SUCCESS` (finish-success). Tags: `{ storyId, issueNumber, tenantId, attempt }`.

### Phase 3 — Tenant mediation (P0 contract, coupled to 32-5 / children)

7. **Make `tenantId` real end-to-end (cap. 2).** This workflow already forwards `tenantId`. The fix is in the children: add `tenantId` capture in `TddWorkflow` (it currently reads none) and a `tenantId` variable + dispatch-input in `DebuggingWorkflow`, so the value this workflow passes is honored for tenant prompt/convention/credential resolution. Until then, the forwarding here is documented-dead; do not rely on it for SaaS scoping. (Tracked under 32-5 / 32-3; cross-referenced in the structural audit rule 5 and the `Debugging.md` companion audit cap. 10.)

### Phase 4 — Lifecycle decision (P2, owner call)

8. **Resolve the orphan (cap. 7).** Decide with the Epic 13 / Epic 19 owners: either (a) wire `tdd-with-debug-retry` as the single-user / LocalExecutor TDD path (so it is reachable and Phases 1–3 pay off), or (b) mark it `superseded by Story 19-5 (ExecuteAgentActivity)` in its doc-comment and sprint-status, and stop maintaining it. Do not leave it silently unreferenced.

---

## Overall

- **Maturity:** thin — a correct, pivot-clean *extraction* that fully meets Story 13.1's mechanical scope, but with no DCB/logging, a half-honored `tenantId` contract, no real failure propagation, no counter reset/output, and currently **orphaned** (the main loop moved to `ExecuteAgentActivity` per Story 19-5).
- **Overall priority:** P1 — it contains two P0 contract/correctness gaps (failure detail dropped; `tenantId` silently ineffective in SaaS), but it is not on the live autonomous path today, so the blast radius is limited until it is re-wired.
- **Effort:** M — Phases 1–2 are self-contained graph edits to one ~260-line file; Phase 3 is small here but rides the 32-5/children workstream; Phase 4 is a decision, not code.
