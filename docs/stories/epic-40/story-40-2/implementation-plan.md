# Implementation Plan — Story 40-2: `WaitForAgentRunActivity` — Durable Bookmark Suspend + `DelayFor` Timeout

## Scope & Deliverable

When this story is done, the coding/TDD step no longer holds a workflow instance Running for
~35 minutes. A new `WaitForAgentRunActivity` dispatches the agent run in its synchronous
`Execute`, then **suspends on a durable, tenant-folded Elsa bookmark plus a `DelayFor`
timeout** — the `WaitForCIResultsActivity` shape — resuming to collect the result
(`Received`) or timing out on a durable deadline (`Timeout`), with `Failed` for a dispatch that
never produces a run. The activity is registered in 39-10's `CanonicalSuspendActivities`, and
`SingleIssueCycleWorkflow`'s `tddForTask` node switches to it. The dispatch/collect halves of
the old inline path are reused; only the inline monitor `await` is removed.

## Pre-Reading

- `docs/stories/epic-40/story-40-2/40-2-wait-for-agent-run-activity-durable-bookmark.md` — this story (ACs are source of truth)
- `apps/tamma-elsa/src/Tamma.Activities/Testing/WaitForCIResultsActivity.cs` — the SUSPEND SHAPE to mirror: `CreateBookmark` (`:87`) + `DelayFor` (`:94`), `[FlowNode("Received","Timeout")]` (`:42`), fail-closed sentinel, `ResumeInput.AsBool` read-back. **Do NOT copy its naming:** it addresses the bookmark with a bespoke `CIResultBookmarkPayload` object (`:80`) and is deliberately absent from `LifecycleBookmarks.CanonicalSuspendActivities`. D7 records the decision to use the canonical registry instead.
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs` — the current inline activity (inputs/outputs to mirror, DI-guard shape, event emission via `TammaEventEmitter`)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs` — the dispatch→monitor→collect composition to split (dispatch pre-suspend, collect post-resume); `TimeoutMinutes` computation (`request.TimeoutMinutes + 5`)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentDispatchService.cs` + `IAgentDispatchServices.cs` — `DispatchAsync` (returns `AgentDispatchResult { Success, DispatchedAt, … }`) run in `Execute`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentResultCollectorService.cs` + `CollectAgentResultsActivity.cs` — `CollectAsync(request, monitorResult, ct)` run in `OnResumeAsync`; `AgentMonitorResult` shape it needs
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs` — 39-10's builder, **shipped**: `Compose` `:38`, `ForStageGate` `:55`, `ForDecisionSession` `:66`, `ForDocumentInput` `:82`, `CanonicalSuspendActivities` `:98` (two entries: `:101`, `:104`). This story adds `ForAgentRun` + a third registry entry; `NormalizeSegment` comes from `WaitForMergeApprovalActivity` (used at `:43`/`:46`)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentExecutorFactory.cs:69-77` — mode auto-detection (`local` when no GitHub App); the D5 local branch keys off this
- `apps/tamma-elsa/src/Tamma.Activities/ResumeInput.cs` — `ResumeInput.AsBool` coercion (#15/#437)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Clarify/ClarifyResumeReadBackTests.cs` — the read-back tolerance matrix shape
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:571` — the `tddForTask` node + loop wiring (`Completed`/`Failed` → incrementTask / dispatchTddRetry)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — `LifecycleBookmarks`/`CanonicalSuspendActivities`/`ResumeBehavior` contracts consumed here
- *Corrected:* an earlier draft listed `LifecycleBookmarks.cs` / `CanonicalSuspendActivities` as **NOT FOUND (prerequisite)**. **They exist** (39-10 landed) — the paths above are real and this story edits them in place. There is no shim step.

## Design Decisions

- **D1 — Dispatch in `Execute` (sync), suspend only once a run exists.** The activity's
  `Execute` calls `IAgentDispatchService.DispatchAsync` (204 = queued) and arms the discover
  window exactly as `GitHubActionsExecutor` does today, so a failed dispatch takes `Failed`
  immediately with no bookmark (nothing to wait for). Only on a successful dispatch does it
  `CreateBookmark` + `DelayFor` and return (suspend). This preserves the current "dispatch
  failure is loud and immediate" behavior while making the *wait* durable.
- **D2 — `ForAgentRun` bookmark shape keyed by (tenant, repo, branch, session).** Add
  `LifecycleBookmarks.ForAgentRun(string? tenantId, string repository, string branchName, string sessionId) => Compose("agent-run", tenantId, repository, branchName, sessionId)`.
  All four business coordinates are known at dispatch AND recomputable by 40-3's resume from the
  webhook (repo, branch) + the persisted signal row (session, tenant) — so "same inputs → same
  name" holds across the suspend/resume boundary with durable inputs (mirrors 39-10 D2's
  session-shape determinism argument). Segments normalized via `NormalizeSegment`.
- **D3 — Two bookmarks, mutual burn (the CI-wait invariant).** `CreateBookmark(resultPayload,
  OnResumeAsync)` for the completion signal + `DelayFor(timeout, OnTimeoutAsync)` for the
  deadline. Whichever resumes first completes the activity; Elsa burns the remaining bookmark
  (`AutoBurn`, as `WaitForCIResultsActivity` relies on). The timeout uses the same
  `request.TimeoutMinutes + safety` the executor computes, so wall-clock behavior is unchanged.
- **D4 — Collect in the resume handlers, from the signal payload.** `OnResumeAsync` builds an
  `AgentMonitorResult` from the resumed run-completion payload (run id, conclusion, artifacts
  url — 40-3's signal shape) and calls `IAgentResultCollectorService.CollectAsync(request,
  monitorResult, ct)` → `AgentExecutionResult`; sets the same outputs as `ExecuteAgentActivity`
  (`SetOutputs`) and the `LastAgentExecutionResult` variable the loop reads; takes `Received`.
  `OnTimeoutAsync` sets a fail-closed `AgentExecutionResult.Failed("agent run timed out …")` and
  takes `Timeout`. Collect failures fail closed to `Received`-with-failure (never a green result
  from an unreadable run), matching `GitHubActionsExecutor` step-3 semantics.
- **D5 — Local mode runs to completion in `Execute`, short-circuits `Received` (no external
  suspend).** For `LocalExecutor` there is no `workflow_run` webhook; suspending on an external
  signal would hang. The factory-selected mode is known in `Execute`
  (`AgentExecutorFactory.cs:69-77` — `local` is the auto-resolved default when no GitHub App
  is configured); when it is `local`, the activity runs the executor synchronously (as today)
  and completes `Received` without creating the external result bookmark — it still arms
  `DelayFor` for a hard timeout. So the workflow definition stays mode-agnostic (AC8) while
  only the GHA path uses the external suspend. Decision recorded rather than forcing a fake
  local webhook.

  **Scope honesty (AC8).** This branch preserves today's local behaviour; it does not make
  local *work*. The local path is currently broken on entry-point resolution
  (`LocalExecutor.cs:246` relative `CliEntryPoint` vs. the temp-dir `WorkingDirectory` at
  `:94`/`:184-193`; no built `packages/cli/dist/`), which **40-1 AC8** fixes. Until then this
  story's local coverage is unit-level against a stubbed `IAgentExecutor` (assert `Received`,
  outputs set, **no external bookmark**), and 40-7's mode matrix is where a real single-user
  run is proven. Do not write a local integration test here that cannot fail until 40-1 lands.
- **D6 — Reuse `ExecuteAgentActivity`'s input/output surface verbatim.** Same `Input<>`/`Output<>`
  properties, same `AgentExecutionRequest` construction, same `BuildStartData`/`BuildEndData`
  event data — so the `SingleIssueCycleWorkflow` node swap (AC7) is a near-mechanical
  type change with identical mappings, and downstream consumers (`LastAgentExecutionResult`) are
  unaffected. `ExecuteAgentActivity` is left intact for non-resumable callers.
- **D7 — DECISION: the new wait joins the canonical registry; Epic 40 ships no second ad-hoc
  bookmark scheme.** One entry `{ typeof(WaitForAgentRunActivity), "agent-run" }` added to
  `LifecycleBookmarks.CanonicalSuspendActivities` (`LifecycleBookmarks.cs:98` — two entries
  today at `:101`/`:104`, three after this story), and the bookmark itself addressed by a
  `Compose`-built name (D2), **not** by a payload object. *The previously open question — copy
  `WaitForCIResultsActivity`'s `CIResultBookmarkPayload` scheme or fold into 39-10's builder —
  is settled by 40-3:* the resume runs on a **different pod** and must recompute the name from
  durable inputs, which a payload-object bookmark cannot do tenant-folded. `ExecuteAgentActivity`
  stays OUT of the registry (it is not a suspend point). Migrating `WaitForCIResultsActivity`
  itself onto the registry is **out of scope and unowned** — after Epic 40 the codebase still
  carries that one legacy scheme; the point of this decision is that the cycle does not add a
  second.
- **D8 — Fail-loud DI guards + event emission parity.** Missing dispatch service/collector ⇒
  loud `Failed` with a diagnostic (mirrors `ExecuteAgentActivity.cs:167`). Emit
  `TammaEventEmitter.EmitStart` at dispatch; the wait-lifecycle events (`AGENT_RUN.WAIT_*`) are
  40-6's — this story emits the existing `AGENT.EXECUTION.*` start/end via the same emitter so
  no audit regression occurs before 40-6 lands.

## Implementation Steps

1. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`** (39-10's
   shipped file) — add `ForAgentRun(...)` next to `ForDocumentInput` (`:82`) delegating to
   `Compose` (`:38`) (D2), and add the `{ typeof(WaitForAgentRunActivity), "agent-run" }` entry
   to `CanonicalSuspendActivities` (`:98`) (D7). *Corrected: an earlier draft budgeted a "shim
   if 39-10 has not merged". 39-10 has merged — edit the real file.*

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WaitForAgentRunActivity.cs`**
   (AC1) — `[Activity("Tamma.AgentDispatch","Wait For Agent Run",…)]`,
   `[FlowNode("Received","Timeout","Failed")]`, `ITammaActivity`. Inputs/outputs copied from
   `ExecuteAgentActivity` (D6). `Execute` (D1): DI guard (D8) → build `AgentExecutionRequest` →
   resolve mode via `AgentExecutorFactory` → if `local` run-to-completion + `Received` (D5) →
   else `IAgentDispatchService.DispatchAsync`; dispatch fail → `Failed`; dispatch ok →
   `CreateBookmark(ForAgentRun payload, OnResumeAsync)` + `DelayFor(timeout, OnTimeoutAsync)`
   (D3) and return.

3. **IMPLEMENT `OnResumeAsync` / `OnTimeoutAsync`** (D4, AC4/AC3) — resume: read the completion
   payload serialization-tolerantly (D—AC5, `ResumeInput`), build `AgentMonitorResult`, collect,
   `SetOutputs` + `LastAgentExecutionResult`, `CompleteActivityWithOutcomesAsync("Received")`;
   timeout: fail-closed `AgentExecutionResult.Failed`, `SetOutputs`, `"Timeout"`.

4. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`**
   (AC7) — replace the `tddForTask` `ExecuteAgentActivity` node with `WaitForAgentRunActivity`
   (identical input mappings, `TenantId` threaded from the `tenantId` variable). Wire edges:
   `Received` → the existing result gate (the loop's `Completed`/`Failed` on
   `LastAgentExecutionResult.Success` — add a small `FlowDecision` if the current wiring relied on
   the activity's own `Completed`/`Failed`; preserve routing to `incrementTask` vs
   `dispatchTddRetry`); `Timeout` → the tdd-failed escalation sink (`notifyTddFailed` →
   fail-cycle, distinct from agent-failure retry); `Failed` → same escalation. Keep this change
   minimal; the `[ResumeBehavior]` + re-entry node wiring is 40-4/40-5.

5. **DI registration** — register `WaitForAgentRunActivity` in the Elsa activity registration
   (both `Tamma.ElsaServer` and `Tamma.Api` hosts, wherever `ExecuteAgentActivity` is
   registered) so DI resolves its logger + factory + services.

6. **CREATE tests** (see Test Plan) — `WaitForAgentRunActivityTests`,
   `WaitForAgentRunReadBackTests`, `LifecycleBookmarksAgentRunTests`, and a Testcontainers
   suspend/resume smoke (folded into 40-7's integration but a minimal in-process resume test
   here). Finish with `dotnet ef migrations has-pending-model-changes` (clean) + `dotnet test`.

## Data & Migrations

None in this story — the durable *bookmark* persists via Elsa's existing EF bookmark store; the
persisted *signal row* is 40-3's migration. `dotnet ef migrations has-pending-model-changes`
stays clean.

## Events

- **Emits (via `TammaEventEmitter`, unchanged family):** `AGENT.EXECUTION.STARTED` at dispatch,
  `AGENT.EXECUTION.SUCCESS`/`FAILED` at resume/timeout — parity with `ExecuteAgentActivity` so no
  audit regression. The dedicated `AGENT_RUN.WAIT_SUSPENDED`/`RECEIVED`/`TIMED_OUT` family is
  **40-6** (this story does not add event constants).
- **Consumes:** the 40-3 resume payload (run id, conclusion, artifacts url).

## Test Plan

All NUnit + FluentAssertions (+ Moq).

- **`LifecycleBookmarksAgentRunTests`** (unit) — `ForAgentRun` determinism (same inputs →
  byte-identical), tenant folding (A ≠ B), null tenant → `none` segment, hostile-char
  normalization; `CanonicalSuspendActivities` contains `WaitForAgentRunActivity` with gate
  `"agent-run"`. Plus the D7 pin: the registry has exactly **three** entries after this story
  (`WaitForDocumentDecisionActivity`, `WaitForDocumentInputActivity`, `WaitForAgentRunActivity`)
  — a conscious count, so a fourth suspend point added without registration is a visible edit.
  **Covers AC2, AC6.**
- **`WaitForAgentRunActivityTests`** (unit, in-process `IWorkflowRunner` or activity harness,
  Moq'd dispatch/collect) — dispatch fail → `Failed`, no bookmark; dispatch ok → suspends with a
  bookmark named `ForAgentRun(...)` + a `DelayFor` bookmark present; resume with a success
  payload → collects → `Received` with outputs set; resume with agent-failure payload → `Received`
  with `Success:false`; timeout resume → `Timeout` with fail-closed sentinel; local mode
  (stubbed `IAgentExecutor`) → runs-to-completion + `Received` **and no external bookmark
  created** (D5); DI missing → loud `Failed`. **Covers AC1, AC3, AC4, AC8 (unit scope), AC9.**
- **`WaitForAgentRunReadBackTests`** (unit, `ClarifyResumeReadBackTests` shape) — completion
  payload read-back across boxed-bool/`"true"`/`"True"`/`JsonElement`, truthy+falsy;
  missing/unparseable → fail-closed failure result, never a green pass. **Covers AC5.**

*(Full crash/restart + cross-instance resume is proven in 40-7's Testcontainers suite.)*

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — suspend/resume activity, `Received`/`Timeout`/`Failed` | 2, 3 | `WaitForAgentRunActivityTests` |
| 2 — tenant-folded deterministic bookmark | 1 | `LifecycleBookmarksAgentRunTests` |
| 3 — durable `DelayFor` timeout, no thread held | 2, 3 | `WaitForAgentRunActivityTests` timeout case |
| 4 — resume collects the result, same outputs | 3 | `WaitForAgentRunActivityTests` resume cases |
| 5 — serialization-tolerant read-back | 3 | `WaitForAgentRunReadBackTests` |
| 6 — registered canonical suspend activity | 1 | `LifecycleBookmarksAgentRunTests` |
| 7 — TDD loop uses it (GHA) | 4 | `WaitForAgentRunActivityTests` + `SingleIssueCycle` structure test (40-5) |
| 8 — local not regressed, mode-agnostic definition | 2, 3 | `WaitForAgentRunActivityTests` local case (stubbed executor). *Single-user end-to-end is 40-1 AC8 + 40-7, not this row.* |
| 9 — fail-loud DI guards | 2 | `WaitForAgentRunActivityTests` DI-missing case |

## Dependencies & Sequencing

- **39-10 — LANDED, no gate.** `LifecycleBookmarks`, `CanonicalSuspendActivities`,
  `NormalizeSegment`, `ResumeInput` all exist and are consumed directly. *Corrected: an earlier
  draft called this a hard unmerged prerequisite and budgeted a shim; both are struck.*
- **40-1 (AC8) — blocking for the single-user END-TO-END proof only.** The local coding path
  cannot resolve its runner entry point today; 40-1 AC8 fixes it. 40-2's local branch (D5) is
  unit-testable against a stubbed executor without 40-1, and the GHA path is entirely
  independent — so this edge does **not** gate 40-2's start, only the scope of what AC8 can
  claim.
- **Lockstep:** 40-3 — the resume side that fires this bookmark from the webhook; agree the
  `ForAgentRun` name contract (AC2) + the completion payload shape in both plans up front. 40-2
  is unit-testable via direct bookmark resume before 40-3 lands.
- **In place, verified:** `WaitForCIResultsActivity` (shape only — see D7), the dispatch/collect
  services, `AgentExecutorFactory`, Elsa bookmarks + `DelayFor` + EF store.
- **Feeds:** 40-4 (re-entry routes into this suspend point), 40-5 (declares the workflow
  resumable citing this canonical activity), 40-6 (adds the wait event family), 40-7 (integration
  proof).
- **Sequencing within the story:** 1 → 2 → 3 → 4/5 → 6.

## Risks & Mitigations

- **Suspending before a run exists ⇒ a bookmark no webhook can match (permanent Timeout).**
  Mitigation: D1 dispatches (and confirms 204 + discover-window arming) in `Execute` before any
  bookmark; failed dispatch never suspends.
- **Bookmark-name divergence from 40-3's resume ⇒ silent no-resume → always Timeout.**
  Mitigation: single `ForAgentRun` builder used by both; a byte-parity pin test shared with 40-3;
  the name is recomputable from durable inputs only.
- **Local mode hangs waiting for a webhook that never comes.** Mitigation: D5 runs local
  to-completion in `Execute`; local never creates the external result bookmark.
- **AC8 is read as "single-user works".** It does not: the local runner path is broken
  independently of this story (40-1 AC8). Mitigation: AC8's wording and D5's scope note state
  the boundary, and the local test uses a stubbed executor so it measures *this* story's
  behaviour rather than 40-1's.
- **A reviewer takes "copy `WaitForCIResultsActivity`" literally** and ships a
  `CIResultBookmarkPayload`-style bookmark that 40-3 cannot address. Mitigation: D7's decision
  + the AC2/AC6 falsifiability clauses; the Pre-Reading entry flags the naming divergence at
  the point of reading.
- **Loop-routing regression when swapping the node (AC7).** Mitigation: preserve the exact
  `Completed`/`Failed` gate on `LastAgentExecutionResult.Success`; keep `Timeout` a *distinct*
  escalation edge from agent-failure-retry (Technical Note); covered by the structure test in 40-5.
- **Double audit / missing audit during the split.** Mitigation: D8 keeps the existing
  `AGENT.EXECUTION.*` emission until 40-6 formalizes the wait family — no gap, no double count.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | `ForAgentRun` + registry entry | 0.5 |
| 2 | `WaitForAgentRunActivity` `Execute` (dispatch + suspend + local branch) | 1.5 |
| 3 | resume/timeout handlers + collect + fail-closed | 1.25 |
| 4 | `SingleIssueCycleWorkflow` node swap + edge rewiring | 1.0 |
| 5 | DI registration (both hosts) | 0.25 |
| 6 | unit tests (activity, read-back, bookmarks) | 1.5 |
| **Total** | | **6.0** (story estimate: 5-7 days) |
