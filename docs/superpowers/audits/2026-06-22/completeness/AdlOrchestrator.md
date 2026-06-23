# Completeness Audit — AdlOrchestratorWorkflow

**Audited:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/ADL/*` (Init / SelectWorkItem / DispatchTriage / CheckLimits / DispatchCycle / Cooldown / SetExitReason / DispatchAdl)
**Definition id:** `adl-orchestrator` · **Display name:** "ADL Orchestrator"

---

## 1. Purpose & owner

The ADL (Autonomous Development Loop) Orchestrator is the **top-level supervisory loop** of the
platform. One instance does exactly one "tick": load config → select the single highest-priority
work item from multiple sources → check operational limits → fire-and-forget a `single-issue-cycle`
(the heavy 15-step pipeline) → cooldown → **dispatch a fresh `adl-orchestrator` instance and finish**.
Continuous operation is achieved by self-redispatch (`DispatchAdlActivity`), so the "loop" is really
a chain of one-tick instances rather than a long-running durable loop. The real development work is
delegated to the child `SingleIssueCycleWorkflow` (which is mature and well-built — validate →
context → plan → plan-review → tasks → task-review → branch → draft PR → test cases → TDD-per-task →
code review → wait approval → merge → close → deploy).

**Owning epic / story:** **Epic 2 — Autonomous Development Loop (Core)** (`docs/epics.md:677`),
specifically **Story 2.1 Issue Selection with Filtering** (`docs/epics.md:689`) and **Story 2.11
Auto-Next Issue Selection** (`docs/epics.md:888`); the loop's operational-safety envelope is the
`OperationalLimits` model (`apps/tamma-elsa/src/Tamma.Activities/ADL/Models/AdlModels.cs:112`). The
14-step autonomous loop is a headline architecture goal (`docs/architecture.md:21,1979`).

---

## 2. Maturity: **partial**

The orchestration skeleton is real and correctly wired: config resolution with a defaults→json→input
override chain, a genuine multi-source priority selector (`SelectWorkItemActivity`) with three
outcomes (`Selected` / `NothingFound` / `NeedsTriage`), a live concurrency limiter that queries the
Elsa instance store, fire-and-forget cycle/triage dispatch, cooldown, self-redispatch, and DCB audit
events on every step (every activity inherits `TammaActivity`/`TammaAsyncActivity`/`TammaOutcomeActivity`
which auto-emit `{EventType}.STARTED/.COMPLETED/.FAILED`). That is well beyond a thin happy-path stub.

It is **not complete** because the documented operational-safety envelope is largely **declared but
not enforced**, several inputs are resolved-then-dropped, the failure surface is thin, and the loop
lacks the controlled-termination machinery its own story specifies:

- `CheckLimitsActivity`'s own description claims it checks "concurrency, **budget**, and **emergency
  stop**", but **budget is never implemented** and the emergency-stop input is hardcoded `false`
  (`CheckLimitsActivity.cs:45`) — never wired from config/`OperationalLimits.EmergencyStop`. So
  `DailyBudgetUsd`, `DailyIssueQuota`, and `MaxCycleDuration` (`AdlModels.cs:115-117`) are **dead
  config** — the loop can spend without bound.
- `InitAdlConfig` resolves `MaxIssuesPerRun` but the workflow maps it into the `MaxConcurrent`
  variable (`AdlOrchestratorWorkflow.cs:48,71,109`) — conflating two different limits — and there is
  **no per-run issue counter or max-iterations stop** (Story 2.11 AC3-5).
- `DispatchTriageActivity.UntriagedCount` is never set from `SelectWorkItemActivity.UntriagedCount`
  (the workflow wires only `Repository` at lines 94-100), so triage is always dispatched logging
  `0` untriaged and the count audit data is lost.
- There is **no failure/error edge**: if `SelectWorkItem` or `CheckLimits` or a dispatch throws, the
  activity rethrows and the instance faults with **no `SetExitReasonActivity("error")`, no cooldown,
  and — critically — no self-redispatch**, so a single transient error **silently kills the entire
  autonomous loop** (no new ADL instance is ever scheduled again).
- No graceful shutdown / SIGTERM-aware stop, no idle long-poll (Story 2.1 AC6 wants idle-poll every
  5 min; the loop instead busy-redispatches on a fixed 10s cooldown), and no run-level summary audit
  event describing what the tick did.

---

## 3. Current capabilities (what it does today)

- **Load Config** (`InitAdlConfigActivity`) — start from `AdlConfig` defaults, overlay parsed
  `configJson` (warn-and-continue on bad JSON), overlay direct inputs (repository, labels, bot,
  baseBranch). Outputs repository / labels / botAssignee / baseBranch / cooldownSeconds /
  maxIssuesPerRun / full resolved configJson. Emits `ADL.CONFIG.INIT.*`.
- **Select Work Item** (`SelectWorkItemActivity`) — multi-source priority selector. Fetches auto-
  labeled issues via the engine callback (`Engine:CallbackUrl` → `GET /api/engine/issues`), filters
  excluded labels and foreign assignees, resolves priority from labels, counts untriaged issues,
  sorts urgent→low then oldest-first, picks **one** item. Three outcomes: `Selected`,
  `NothingFound`, `NeedsTriage`. Falls back to a single mock candidate when `Anthropic:UseMock` or no
  callback URL is configured. Emits `ADL.WORKITEM.SELECT.*`.
- **Dispatch Triage** (`DispatchTriageActivity`) — on `NeedsTriage`, fire-and-forget dispatch of
  `issue-triage` (waits-for-completion=false; comment says "waits" but it does not). Emits
  `ADL.TRIAGE.DISPATCH.*`.
- **Check Limits** (`CheckLimitsActivity`) — on `Selected`: emergency-stop check (hardcoded input
  `false`) then active `single-issue-cycle` instance count vs `MaxConcurrent` via
  `IWorkflowInstanceStore.CountAsync`; **fails open** (returns 0) on query failure. Outcomes
  `Continue` / `Stop`. Emits `ADL.LIMITS.CHECK.*`.
- **Dispatch Issue Cycle** (`DispatchCycleActivity`) — on `Continue`: fire-and-forget dispatch of
  `single-issue-cycle` with a generated instance id, passing repository/workItemJson/issueNumber/
  botAssignee/baseBranch/tenantId (tenantId defaults to `""`). Emits `ADL.CYCLE.DISPATCH.*`.
- **Exit (No Issues) / Exit (Limits)** (`SetExitReasonActivity`) — record `exitReason` on workflow
  output; emit `ADL.EXIT.*`.
- **Cooldown** (`CooldownActivity`) — `await Task.Delay(seconds)` (in-process, not a durable Elsa
  timer). Emits `ADL.COOLDOWN.*`.
- **Dispatch ADL** (`DispatchAdlActivity`) — fire-and-forget self-redispatch of `adl-orchestrator`
  passing only `configJson`. Emits `ADL.SELF.DISPATCH.*`.
- **Routing:** `NothingFound→ExitNoIssues→Cooldown`; `NeedsTriage→DispatchTriage→Cooldown`;
  `Selected→CheckLimits`; `Stop→ExitLimits→Cooldown`; `Continue→DispatchCycle→Cooldown`; every
  path → `Cooldown → DispatchAdl → Finish`.

---

## 4. Intended full scope (with citations)

**Story 2.1 — Issue Selection with Filtering** (`docs/epics.md:689`):
AC1-2 query+label-filter (✓), AC3 prioritize by age (partially — selector sorts by priority then
age), AC4 **assign the selected issue to the bot account** (✗ — selection picks an item but never
assigns it; the bot assignee is only passed downstream), AC5 log selection (✓ via event), **AC6 if
no issues match, enter idle state and poll every 5 minutes** (✗ — fixed 10 s cooldown busy-loop),
AC7 integration test with mock platform.

**Story 2.11 — Auto-Next Issue Selection** (`docs/epics.md:888`):
AC1 10 s cooldown after success (✓), AC2 return to selection (✓ via self-redispatch),
**AC3 maintain a loop counter and log iteration number** (✗), **AC4 max-iterations limit
(configurable, default infinite)** (✗), **AC5 graceful shutdown signal (SIGINT/SIGTERM) to stop
after current iteration** (✗), AC6 log loop continuation (partially — `ADL.SELF.DISPATCH`).

**`OperationalLimits` envelope** (`AdlModels.cs:112-117`): `DailyIssueQuota=20`, `DailyBudgetUsd=50`,
`EmergencyStop`, `MaxCycleDuration=2h`. `CheckLimitsActivity`'s own header (`CheckLimitsActivity.cs:16-23,29`)
documents checks for "Emergency stop / Active < max concurrent / **Budget remaining (from cost
tracker)**" — only concurrency is real today; budget, daily quota, max-cycle-duration, and a wired
emergency stop are all missing.

**Architecture** (`docs/architecture.md`): the platform's defining feature is the "<2 hr autonomous
loop" with "3-retry quality gates" (`:21,29,1979`); graceful shutdown via SIGTERM/SIGINT handlers is
a stated cross-service pattern (`:284,1273-1289`).

**Agent-architecture pivot — cross-cutting non-LLM mediation rule**
(`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md:17` and §5 `:558-599`):
*"A workflow STEP MUST NEVER call an external API/provider directly."* The orchestrator itself does
no LLM work, but **`SelectWorkItemActivity` calls GitHub/engine over HTTP directly via
`IHttpClientFactory`** (`SelectWorkItemActivity.cs:177-227`). Under the pivot, such non-LLM platform
reads are to be mediated by the tamma-api / a Git-platform step-mediation seam (Epic 38, e.g.
`docs/superpowers/plans/2026-06-21-38-1-git-platform-step-mediation-plan.md`) rather than the
activity holding raw `IHttpClientFactory` + building URLs inline.

**CLAUDE.md project rules:** tenant→system→error resolution with **no empty/plain fallback**; no
silent-failure / false-success; emit DCB audit events for every operation; two scoping models
(single-user vs SaaS) for any tenant-aware behavior.

Domain best-practice for a top-level autonomous supervisor additionally implies: a durable
restart that cannot be silently broken by one bad tick (the self-redispatch must be unconditional,
including on error); a cost/budget circuit breaker; idempotent dispatch (don't double-dispatch the
same issue while a cycle for it is already running); a run-level summary event; and a real idle/poll
state instead of a tight redispatch loop.

---

## 5. Missing capabilities

| # | Capability | Priority | dependsOn |
|---|---|---|---|
| 1 | **Error path + guaranteed restart.** No failure edge exists: any throw in SelectWorkItem / CheckLimits / a dispatch faults the instance with no `SetExitReason("error")`, no cooldown, and **no self-redispatch** — one transient error permanently kills the autonomous loop. Add a fault-catching `Exit(error)` branch that still flows to cooldown→DispatchAdl so the loop survives. | P0 | none |
| 2 | **Budget / cost circuit breaker.** `CheckLimits` advertises a budget check but never implements one; `DailyBudgetUsd` is dead config. Wire spend (Epic 34/36 cost data) and `Stop` when the daily budget is exhausted. Without it the loop can spend unbounded. | P0 | Epic 34/35/36 cost+usage (34-11 price-book / 36 analytics) |
| 3 | **Wire EmergencyStop from config.** `CheckLimitsActivity.EmergencyStop` is hardcoded `false`; map `OperationalLimits.EmergencyStop` (and a runtime kill-switch) into it so the documented emergency stop actually halts dispatch. | P0 | none |
| 4 | **Daily issue quota + max-iterations + loop counter** (Story 2.11 AC3-4). `MaxIssuesPerRun`/`DailyIssueQuota` are resolved/declared but never enforced; there is no iteration counter and no max-iterations stop, so "default infinite" is the *only* behavior. | P0 | none |
| 5 | **Graceful shutdown (SIGINT/SIGTERM)** (Story 2.11 AC5; architecture `:284,1273`). No signal-aware stop: the loop cannot be told to stop after the current tick; it self-redispatches forever. Add a shutdown flag checked before `DispatchAdl`. | P1 | none |
| 6 | **Wire `DispatchTriage.UntriagedCount`** from `SelectWorkItem.UntriagedCount`. Today only `Repository` is passed, so triage logs `0` and the count audit datum is dropped. | P1 | none |
| 7 | **Bot assignment on selection** (Story 2.1 AC4). Selection identifies an item but never assigns it to the bot account before dispatching the cycle — concurrent ADL instances could both grab the same issue. | P1 | Epic 38 git-platform mediation |
| 8 | **Idempotent dispatch / dedupe.** Concurrency is bounded by *count* but not by *identity*: nothing prevents re-selecting and re-dispatching an issue that already has a running `single-issue-cycle` (Story 2.1 AC4 bot-assign would partly cover this). Skip items already in-flight. | P1 | none |
| 9 | **Conflate MaxConcurrent vs MaxIssuesPerRun.** `ResolvedMaxIssuesPerRun` is mapped into the `MaxConcurrent` variable; these are distinct limits (per-run cap vs simultaneous cycles). Separate them and feed both into the right checks. | P1 | none |
| 10 | **Idle/poll state instead of busy-redispatch** (Story 2.1 AC6: idle, poll every 5 min). On `NothingFound`, back off to a longer durable poll interval rather than the fixed 10 s cooldown to avoid hammering the platform when the repo is clean. | P2 | none |
| 11 | **Durable cooldown.** `CooldownActivity` uses in-process `Task.Delay`; a restart during the delay loses the tick. Use a durable Elsa `Delay`/timer bookmark so cooldown survives engine restart. | P2 | none |
| 12 | **Max-cycle-duration enforcement.** `OperationalLimits.MaxCycleDuration` (2 h) is unused; the orchestrator dispatches cycles fire-and-forget and never reaps/timeouts a stuck `single-issue-cycle`, so a hung cycle counts against concurrency forever. Add a watchdog that times out or flags overrunning cycles. | P2 | none |
| 13 | **Run-level summary DCB event.** Per-step events exist, but there is no single `ADL.TICK.COMPLETED` (or `ADL.RUN.SUMMARY`) event capturing `{ selected?, issueNumber, type, priority, dispatchedInstanceId, activeInstances, exitReason, untriagedCount }` to reconstruct a tick in the audit/time-travel view. | P2 | none |
| 14 | **Non-LLM step mediation** for `SelectWorkItemActivity`'s direct `IHttpClientFactory` GitHub/engine calls, per the pivot's "steps never call external APIs directly" rule. | P2 | Epic 38 (38-1 git-platform step mediation) |
| 15 | **Tenant scoping.** `tenantId` into `DispatchCycle` defaults to `""` and the orchestrator does not resolve a tenant for config/budget/prompt scoping; in SaaS mode the loop must run per-tenant with tenant-scoped limits and credentials. | P2 | 32-5 / tenancy |
| 16 | **Multi-source selection completeness.** `SelectWorkItem`'s doc promises security-alert and failed-CI-on-main sources (`SelectWorkItemActivity.cs:16-21`) but only the issues source is implemented; the urgent paths (security/CI) never produce candidates. | P3 | Epic 38 git-platform mediation |

---

## 6. Ordered build-out spec (to reach complete)

Each step names the activity/edge to add or change, the branch condition, the DCB event, and the
failure edge. Honor: no silent-failure / no false-success; tenant→system→error with no empty/plain
fallback; non-LLM external calls route through the platform mediation seam; emit DCB audit events.

1. **Add a guaranteed-restart error path (P0, #1).** Wrap the tick in a fault-catching branch: add a
   `SetExitReasonActivity("error")` node (`ExitError`) fed by a `Fault`/catch edge from
   `SelectWorkItem`, `CheckLimits`, `DispatchTriage`, and `DispatchCycle`. Route
   `ExitError → cooldown` so **every** terminal state — including error — flows
   `cooldown → DispatchAdl → finish`. This makes self-redispatch unconditional: one bad tick can
   never silently end the loop. Emit `ADL.EXIT` with `reason="error"` and the error detail.

2. **Wire EmergencyStop + resolve it in config (P0, #3).** Add `ResolvedEmergencyStop` to
   `InitAdlConfig` (from `OperationalLimits.EmergencyStop` and/or a runtime kill-switch source) into
   an `emergencyStop` variable; pass it into `CheckLimitsActivity.EmergencyStop`. On `true` →
   `Stop` → `ExitLimits` (reason `"emergencyStop"`). The existing emergency-stop code path then
   becomes live.

3. **Add a budget circuit breaker to CheckLimits (P0, #2).** Extend `CheckLimitsActivity` with a
   `DailyBudgetUsd` input and a `SpentTodayUsd` reader (Epic 34/36 cost/usage seam). Order the checks
   emergency-stop → budget → daily-quota → concurrency; `Stop` with a typed `StopReason`
   (`"budgetExhausted"`, `"dailyQuota"`, `"maxConcurrent"`, `"emergencyStop"`). On the cost-reader
   failure, **fail closed** for budget (do not dispatch on unknown spend) — distinct from the
   fail-open concurrency query. Emit `ADL.LIMITS.CHECK` with the resolved reason.

4. **Enforce daily quota + max-iterations + loop counter (P0, #4; P1, #9).** Split `MaxConcurrent`
   and `MaxIssuesPerRun` into separate resolved values. Carry a durable `iterationCount` /
   `issuesDispatchedToday` across the self-redispatch chain (pass them in the `configJson`/input that
   `DispatchAdl` forwards). In `CheckLimits`, `Stop` when `issuesDispatchedToday >= DailyIssueQuota`
   or `iterationCount >= MaxIterations` (default infinite). Increment after a successful
   `DispatchCycle`. Log iteration number (Story 2.11 AC3).

5. **Wire triage count (P1, #6).** Pass `SelectWorkItem.UntriagedCount` into
   `DispatchTriageActivity.UntriagedCount` (add the workflow output binding + the input wire at the
   `NeedsTriage` edge). Emit `ADL.TRIAGE.DISPATCH` with the real count.

6. **Assign the issue to the bot + dedupe in-flight (P1, #7, #8).** Before `DispatchCycle`, add a
   mediated `AssignIssueActivity` (via the Epic 38 git-platform step seam) that assigns the selected
   issue to `botAssignee` and skips (re-selects) if an active `single-issue-cycle` already exists for
   that issue number (query `IWorkflowInstanceStore` by a tag, or check assignment). On assign
   failure → error path (step 1). Emit `ADL.WORKITEM.ASSIGNED.{SUCCESS,FAILED}`.

7. **Graceful shutdown gate (P1, #5).** Add a shutdown-flag source (SIGINT/SIGTERM handler setting a
   durable flag, or an admin kill-switch) checked by a `FlowDecision` just before `DispatchAdl`: if
   shutting down, route to `Finish` **without** self-redispatch and emit `ADL.SHUTDOWN`. Otherwise
   redispatch as today. This lets the loop "stop after current iteration" per Story 2.11 AC5.

8. **Idle/poll backoff on NothingFound (P2, #10; durable cooldown #11).** Replace the single fixed
   cooldown with a durable Elsa `Delay`/timer bookmark; use a short interval after a dispatch and a
   longer poll interval (default 5 min, Story 2.1 AC6) after `NothingFound`, so a clean repo idles
   instead of busy-redispatching. Emit `ADL.IDLE` on the long-poll path.

9. **Max-cycle-duration watchdog (P2, #12).** Add a reaper (scheduled activity or a check at the top
   of each tick) that finds `single-issue-cycle` instances older than `MaxCycleDuration`, flags/
   cancels them, and emits `ADL.CYCLE.TIMEOUT` so a hung cycle stops permanently consuming a
   concurrency slot.

10. **Mediate the platform reads (P2, #14, #16).** Move `SelectWorkItemActivity`'s direct
    `IHttpClientFactory` GitHub/engine calls behind the Epic 38 git-platform step-mediation seam and
    implement the promised security-alert and failed-CI sources so the urgent priority lanes produce
    candidates. No empty/plain fallback — if the mediated read fails, surface the error (step 1), do
    not silently return `NothingFound`.

11. **Tenant scoping (P2, #15).** Resolve a tenant for the run (SaaS: per-tenant ADL; single-user:
    the sole user) and thread `tenantId` into config/budget/limit resolution and into
    `DispatchCycle` (replacing the `""` default) so cycles, prompts, credentials, and budgets are
    correctly scoped.

12. **Emit a run-level summary DCB event (P2, #13).** On every terminal path emit `ADL.TICK.COMPLETED`
    with tags `{ tenantId, repository, exitReason }` and data
    `{ selected, issueNumber, workItemType, priority, dispatchedInstanceId, activeInstances,
    untriagedCount, iterationCount, issuesDispatchedToday }` so a tick is fully reconstructable in
    the time-travel debugger.

---

## 7. Summary

The ADL Orchestrator has a **correct and complete supervisory skeleton** — multi-source priority
selection with three outcomes, a live concurrency limiter, fire-and-forget cycle/triage dispatch,
self-redispatch for continuous operation, and DCB audit events on every step — and it delegates the
heavy lifting to the mature `single-issue-cycle` child. It is **partial**, not complete, because the
operational-safety envelope it advertises is mostly unenforced (**budget, daily quota,
emergency-stop, and max-cycle-duration are dead config**), several resolved inputs are dropped
(`UntriagedCount`, `MaxIssuesPerRun`, `tenantId`), and — most importantly — it has **no error edge,
so a single transient failure permanently kills the autonomous loop** because the self-redispatch is
not reached on the fault path. Reaching "complete" is primarily P0 work: an unconditional
error-path-to-restart, a real budget/quota/emergency-stop circuit breaker, and loop-counter/
max-iterations + graceful-shutdown control, then P1/P2 hardening (triage-count wiring, bot
assignment + in-flight dedupe, durable idle/poll, cycle watchdog, platform-read mediation, tenant
scoping, and a run-level summary event).
