# Story 40-4: Per-Task Re-Entry — Reconstruct Landed Tasks from Git + DCB Events

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
I want a crashed/restarted `SingleIssueCycleWorkflow` to **re-enter the per-task TDD loop at the
task that was actually in flight** — reconstructed from the commits already on the branch and
the DCB event trail — instead of restarting the cycle from scratch and re-implementing tasks
that already landed,
So that a deploy or eviction during a multi-task issue costs at most one task's rework, not the
whole issue.

## Priority

P0 — This is the coding-side analogue of 39-10's latest-state re-entry. Without it, 40-2's
durable suspend saves a single in-flight run but a *crash* (instance gone) still restarts the
cycle at `CurrentTaskIndex = 0`, re-running context/plan/task/branch/PR and re-implementing
completed tasks. It is what makes the whole coding step "resumable by design," not just
"resumable while suspended."

## Architectural Context (READ FIRST)

**The loop advances a counter with no durable memory of progress.** In
`SingleIssueCycleWorkflow.cs` the TDD loop is:

```
initTaskLoop  (TotalTasks = parse(tasksJson).length; CurrentTaskIndex = 0)   [line 517]
hasMoreTasks  (CurrentTaskIndex < TotalTasks)                                 [line 530]
  → extractCurrentTask (tasksJson[CurrentTaskIndex])                          [line 537]
  → tddForTask  (WaitForAgentRunActivity after 40-2; session adl-{issue}-task-{index}) [line 571]
  → Completed → incrementTask (CurrentTaskIndex++) → hasMoreTasks             [line 590]
  → Failed    → dispatchTddRetry → advance | fail-cycle
```

`CurrentTaskIndex`/`TotalTasks` are Elsa workflow variables. If the instance survives, Elsa
resumes them; **if the instance is gone (crash, definition-version bump, store loss), the
orchestrator re-dispatches a fresh `SingleIssueCycleWorkflow` for the issue and the loop starts
at index 0** — re-running every prior stage and re-implementing landed tasks. There is no read of
"what already happened for this issue."

**39-10 established the pattern; code needs its own read model.** 39-10's `LifecycleReEntryService`
reconstructs a document workflow's position from the **39-11 document store + DCB events**. Epic
39's README is explicit that **code is NOT a document type** — code's store is git. So the coding
loop cannot reuse the document-store read; it reconstructs from:

1. **Git** — the branch's commits (via the mediated compare/PR reads already in
   `ActionsResultAggregator`): which tasks' expected changes are already committed.
2. **DCB events** — per-task `AGENT_RUN.*` / `AGENT.EXECUTION.*` / `CODE.*` events keyed by the
   deterministic session id `adl-{issue}-task-{index}` (queried via the Story 4-7 event API, the
   same surface 39-10 uses).

The deterministic per-task session id (`SingleIssueCycleWorkflow.cs:581`,
`$"adl-{issueNumber}-task-{currentTaskIndex}"`) is the join key that makes this reconstruction
possible — each task's events are addressable.

## Acceptance Criteria

1. **Task re-entry read model.** A component (e.g. `TaskLoopReEntryService` in
   `Tamma.Activities/AgentDispatch/`) answers, for `(tenantId, issueNumber, branchName,
   tasksJson)`: **the lowest task index not yet landed** — i.e. the `CurrentTaskIndex` a fresh
   instance should resume at. It reconstructs from (a) per-task DCB events keyed by
   `adl-{issue}-task-{index}` (a task with an `AGENT_RUN.RECEIVED`/success + `CODE.*` committed
   event is landed) and (b) git branch state (the expected files/commits for the task are present),
   never from Elsa instance internals. It returns a typed position with a human-readable basis.

2. **Idempotent loop guard.** A re-entering cycle **skips the produce/dispatch for any already-
   landed task** and resumes at the first unlanded index. Re-entering twice over the same landed
   state yields zero new agent dispatches for landed tasks and zero duplicate `CODE.*`/agent
   events (mirrors 39-10 AC6).

3. **Wired into the loop.** `SingleIssueCycleWorkflow`'s `initTaskLoop` (or a new node before
   `hasMoreTasks`) consults the re-entry service to set the initial `CurrentTaskIndex` to the
   reconstructed resume index instead of hard-`0`, when re-entering for an issue that already has
   landed tasks. A fresh issue with no history resolves to `0` (today's behavior, zero risk).

4. **Disagreement fails loud, never guesses.** If git and events disagree (a task's events say
   landed but its commits are absent, or vice-versa), the service throws a typed
   `TammaError`-style inconsistency (pointing at the 4-8 replay surface) rather than silently
   skipping or re-implementing — the 39-10 "mis-reconstruction is worse than no re-entry"
   principle. The cycle routes the inconsistency to its loud fail/escalation sink.

5. **Reuses git/event reads, no new fetch stack.** The git side reuses the mediated
   compare/PR/commit reads already in `ActionsResultAggregator`/the mediation client; the event
   side reuses `IEventRepository` (Story 4-7 query surface) — the same reads 39-10 uses. No new
   GitHub client, no second event query path.

6. **Null-seam default.** Until fully wired/validated, the service ships behind a
   `NullTaskLoopReEntryService` that always returns index `0` (today's behavior), so re-entry
   goes live by a DI swap without a workflow-code change (the 39-10 D7 pattern).

7. **Emits a re-entry event.** When re-entry skips ≥1 task, the cycle emits
   `AGENT_RUN.TASK_REENTERED` (40-6 family) with `{ issueNumber, resumeIndex, landedIndices,
   basis }` so time-travel debugging shows the crash-recovery decision. A fresh (index-0) start
   is not a re-entry and emits nothing.

8. **Per-mode correct.** SaaS: git reads are the tenant's installation-scoped mediated reads;
   events are tenant-folded. Single-user: local git + central-schema events. The reconstruction
   logic is mode-agnostic; only the read sources differ (as they already do for dispatch/collect).

## Technical Notes

- **Git is the authority for "landed"; events are the corroborating trail.** A task is landed
  when its expected changes are committed on the branch AND its `adl-{issue}-task-{index}` run
  event says success. Requiring both avoids skipping a task whose events fired but whose push
  failed (D4's inconsistency case), and avoids re-implementing a task whose events were lost but
  whose commits are present (corroborate, then trust git).
- **The session-id scheme is the join key — keep it deterministic.** `adl-{issue}-task-{index}`
  must remain stable; any change to it breaks re-entry addressing. If a future story changes it,
  re-entry must migrate in lockstep.
- **This does not re-run earlier stages.** Re-entry here is scoped to the *TDD task loop* — the
  cycle's earlier stages (context/plan/tasks/branch/PR) are re-run on a fresh dispatch as today
  (they are cheap/idempotent relative to coding, and `tasksJson` must be regenerated to index
  into). A future story may extend re-entry to skip those too; out of scope here. *(State this
  boundary explicitly so the scope is honest.)*
- **`tasksJson` determinism matters.** Re-entry indexes into `tasksJson[CurrentTaskIndex]`; if
  task regeneration produces a different ordering, the index is meaningless. The plan must address
  how `tasksJson` is stabilized/rehydrated for a re-entering issue (e.g. read the tasks from the
  PR's committed plan `.md` files or a prior `TASK.CREATED` event rather than regenerating).

## Dependencies

- **Story 40-2 — HARD.** The loop node is `WaitForAgentRunActivity`; re-entry sets the index it
  resumes at. 40-4's guard skips landed tasks before that suspend.
- **Story 39-10 — SOFT (pattern), and the event-read path.** `LifecycleReEntryService`/
  `LifecycleResumeCalculator` are the pure-calculator + I/O-service split to mirror; the 4-7/4-8
  read path is shared. Not consumed directly (different store), but the shape is copied.
- **Story 4-7 (event query API) + 4-8 (`ReplayReconstructor`)** — the DCB read + forensic
  fallback. Existing.
- **Existing (verified):** `ActionsResultAggregator` (git compare/PR reads), `IEventRepository`,
  the `adl-{issue}-task-{index}` session scheme, `SingleIssueCycleWorkflow` loop.

## Estimated Effort

5-7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
