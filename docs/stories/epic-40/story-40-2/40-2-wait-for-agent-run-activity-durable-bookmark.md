# Story 40-2: `WaitForAgentRunActivity` — Durable Bookmark Suspend + `DelayFor` Timeout

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform operator** (and the orchestrator running unattended),
I want the coding/TDD step to **dispatch the agent run and then suspend on a durable
bookmark** — resuming when the run completes or timing out on a durable deadline — instead of
holding the workflow instance Running for ~35 minutes inside an inline `await`,
So that a deploy, pod eviction, or crash during a coding run does not kill the in-flight
monitor and force the whole issue cycle to restart from scratch.

## Priority

P0 — This is the core "resumable by design" change for the coding step. Without it, the
signal plane (40-3) and re-entry (40-4) have no suspend point to resume, and 40-5's
`[ResumeBehavior]` declaration would be a lie (the workflow declares `BookmarkSuspend` but
has no canonical suspend activity).

## Architectural Context (READ FIRST)

**Today the coding step is inline and non-durable.** In `SingleIssueCycleWorkflow.cs` the TDD
loop node `tddForTask` is an `ExecuteAgentActivity`
(`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs`). Its
`ExecuteAsync` calls `executor.ExecuteAsync(request, ct)` and **awaits inline**
(`ExecuteAgentActivity.cs:199`). For the `GitHubActionsExecutor`
(`GitHubActionsExecutor.cs:42`) that single `await` covers dispatch → **`MonitorAsync` (a
~35-minute poll/webhook loop)** → collect. Throughout, the Elsa workflow instance stays
**Running** (a live async task holds the wait); nothing is persisted as a suspended bookmark.
A restart mid-wait loses the monitor task, and — with no task re-entry (40-4) — the
orchestrator re-dispatches the cycle from the start.

**The durable primitive already exists in the same activity family.**
`WaitForCIResultsActivity` (`apps/tamma-elsa/src/Tamma.Activities/Testing/WaitForCIResultsActivity.cs`)
is the pattern to copy exactly:

- `context.CreateBookmark(payload, OnResumeAsync)` — a result bookmark resumed by an external
  webhook → **`Received`** outcome (`WaitForCIResultsActivity.cs:87`).
- `context.DelayFor(TimeSpan.FromMinutes(timeout), OnTimeoutAsync)` — a **durable** scheduled
  delay bookmark the scheduler auto-resumes at the deadline → **`Timeout`** outcome
  (`WaitForCIResultsActivity.cs:94`). No thread is held for the wait; Elsa burns the loser
  bookmark on completion.
- Resume read-back is **serialization-tolerant** (`ResumeInput.AsBool`, the #15/#437 lesson).
- `[FlowNode("Received", "Timeout")]`, fail-closed sentinel on unparseable/timeout.

**Epic 39-10 provides the bookmark builder.** `LifecycleBookmarks`
(`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`, story 39-10) is the
one canonical **tenant-folded, deterministic** bookmark-name builder (same inputs → same name
on suspend and resume; tenant A ≠ tenant B). This story adds a `ForAgentRun` shape to it (or
consumes it if 39-10 shipped one) and registers `WaitForAgentRunActivity` in
`LifecycleBookmarks.CanonicalSuspendActivities` so 40-5's structural test recognizes it.

**Dispatch and collect are retained, the inline monitor is replaced.** The dispatch half
(`IAgentDispatchService.DispatchAsync`) runs in the activity's synchronous `Execute` *before*
suspending; the collect half (`IAgentResultCollectorService.CollectAsync`) runs in
`OnResumeAsync`/`OnTimeoutAsync` *after* the run completes. Only the ~35-minute
`IAgentMonitorService.MonitorAsync` inline wait is removed — replaced by the bookmark suspend.

## Acceptance Criteria

1. **New `WaitForAgentRunActivity` with a suspend/resume shape.** A new Elsa activity in
   `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/` mirrors `WaitForCIResultsActivity`:
   in `Execute` it **dispatches the agent run** (via `IAgentDispatchService`), then **creates
   a result bookmark + a `DelayFor` timeout bookmark** and returns (the instance suspends). It
   declares `[FlowNode("Received", "Timeout", "Failed")]` — `Failed` for a dispatch that never
   succeeds (no run to wait for).

2. **Tenant-folded, deterministic bookmark name.** The result bookmark name is built through
   `LifecycleBookmarks.ForAgentRun(tenantId, repository, branchName, sessionId)` (a new shape
   delegating to `LifecycleBookmarks.Compose`, all segments via `NormalizeSegment`). Same
   inputs → identical name on suspend and on the 40-3 resume; different tenants → disjoint
   names. The name is recomputable by the resume side from durable inputs alone.

3. **Durable timeout enforced.** A `context.DelayFor(TimeSpan.FromMinutes(TimeoutMinutes), …)`
   arms a durable deadline (default `request.TimeoutMinutes + safety`, matching the current
   `GitHubActionsExecutor` `TimeoutMinutes` computation). On deadline the activity emits a
   fail-closed sentinel result and takes the **`Timeout`** edge — the workflow can never
   suspend forever. No blocking `Task.Delay`; no thread held for the wait.

4. **Resume collects the result.** `OnResumeAsync` reads the run-completion payload
   (workflow_run id/conclusion/artifacts url — delivered by the 40-3 signal), runs
   `IAgentResultCollectorService.CollectAsync` to produce the `AgentExecutionResult`, sets the
   same outputs `ExecuteAgentActivity` sets, and takes the **`Received`** edge (success or
   agent-reported failure both route here; the loop's existing `Completed`/`Failed` gate on the
   result is preserved).

5. **Serialization-tolerant read-back.** Every resumed-input read uses the coercion helpers
   (`ResumeInput.AsBool`/tolerant string/JsonElement matrix), never a bare `is true` — the
   #15/#437 silent-wrong-branch lesson. A read-back test applies the boxed-bool/`"true"`/
   `JsonElement` truthy+falsy matrix.

6. **Registered as a canonical suspend activity.** `WaitForAgentRunActivity`'s type is added to
   `LifecycleBookmarks.CanonicalSuspendActivities` (39-10's registry) with its gate prefix, so
   40-5's `ResumableStandardStructuralTests` recognizes it as a sanctioned suspend point.

7. **The TDD loop uses it (SaaS/GHA path).** `SingleIssueCycleWorkflow`'s `tddForTask` node is
   switched from `ExecuteAgentActivity` to `WaitForAgentRunActivity` (inputs mapped identically:
   repository, branchName, issueNumber, `task="implement"`, `plan_json=currentTaskJson`, the
   deterministic `adl-{issue}-task-{index}` session id, provider, timeout, tenantId). The
   loop's existing `Completed`/`Failed` outcomes are wired to the activity's `Received`(→gate on
   result)/`Timeout`/`Failed` edges preserving current routing (retry on failure, advance on
   success). *(The `SetVariable`/re-entry wiring interplay lands with 40-4/40-5; this story
   delivers the activity + the GHA-mode suspend.)*

8. **Single-user (Local) parity retained.** For `LocalExecutor` (single-user), where there is
   no external webhook, the activity still functions: it may run the local runner to completion
   inside `Execute` and short-circuit to `Received` (no external suspend needed), OR suspend on
   a locally-signaled bookmark — the plan chooses and justifies. Either way the same outputs and
   edges are produced, so the workflow definition is mode-agnostic (the `ExecuteAgentActivity`
   guarantee is preserved).

9. **Fail-loud on missing dependencies.** No `IAgentDispatchService`/collector registered ⇒
   `TammaError`-style loud failure to the `Failed` edge with a diagnostic, never a silent hang
   (mirrors `ExecuteAgentActivity.cs:167` and `DispatchAgentWorkflowActivity` DI guards).

## Technical Notes

- **Dispatch runs before suspend, in `Execute`.** The activity must obtain the run's existence
  (dispatch 204 + the discover window) *before* it can meaningfully wait; a failed dispatch
  takes `Failed` immediately (no bookmark). Consider dispatching synchronously in `Execute`
  then suspending — do not suspend before a run exists to be signaled.
- **The 40-3 signal carries what the webhook lacks.** The `workflow_run.completed` webhook
  knows repo/branch/run-id but not the Tamma session id; 40-3's persisted signal row bridges
  that so the resume can address this exact bookmark. This story defines the bookmark *name
  contract* (AC2) that 40-3's resume recomputes; keep them in lockstep.
- **Do not delete `ExecuteAgentActivity`.** It stays for non-resumable/standalone callers and
  tests; only the SingleIssueCycle TDD loop node migrates (AC7). This bounds blast radius.
- **Timeout semantics ≠ agent failure.** `Timeout` (deadline, no completion) is a distinct edge
  from `Received`-with-`success:false` (agent ran, failed). The cycle routes them differently
  (timeout → escalate/needs-human; agent-failure → tdd-with-debug-retry) — keep them separate.

## Dependencies

- **Story 39-10 (Resumable Standard) — HARD.** `LifecycleBookmarks` (+ the `ForAgentRun` shape
  this story adds), `CanonicalSuspendActivities`, `NormalizeSegment`, `ResumeInput`. Blocking:
  AC2/AC5/AC6 consume it directly.
- **Story 40-3** — the durable signal + resume endpoint that actually resumes this bookmark
  from the webhook. Developed in lockstep against AC2's name contract; 40-2 is testable against
  a direct bookmark resume before 40-3 lands.
- **Existing (verified):** `WaitForCIResultsActivity` (the pattern), `IAgentDispatchService`/
  `IAgentResultCollectorService`/`GitHubActionsExecutor`/`LocalExecutor`, Elsa 3 bookmarks +
  `DelayFor` + EF persistence.

## Estimated Effort

5-7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
