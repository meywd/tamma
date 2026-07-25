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

**39-10 has landed; code needs its own read model.** 39-10's `LifecycleReEntryService`
reconstructs a document workflow's position from the **39-11 document store + DCB events**, via the
one shipped re-entry node `ComputeReEntryPositionActivity`
(`Tamma.Activities/Documents/ComputeReEntryPositionActivity.cs:36`). That node is **document-coupled
by construction** — a `DocumentType` input (`:43-44`), a hard `ILifecycleReEntryService` resolve
(`:70-76`), and a `DOCUMENT.REENTERED` emission (`:141`) — and Epic 39's README is explicit that
**code is NOT a document type**; code's store is git. So the coding loop can reuse neither the
document-store read nor that node: it needs its own node (AC3) and its own read model, which
reconstructs from:

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

3. **Wired into the loop as a named node.** A new activity **`ComputeTaskResumeIndexActivity`**
   (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`) sits between `initTaskLoop`
   (`SingleIssueCycleWorkflow.cs:517`) and `hasMoreTasks` (`:530`), consults the re-entry service,
   and sets the initial `CurrentTaskIndex` to the reconstructed resume index instead of hard-`0`
   when re-entering an issue that already has landed tasks. A fresh issue with no history resolves
   to `0` (today's behavior, zero risk). **The type name is load-bearing** — 40-5 (gate clause c),
   40-6 (emission site) and 40-7 (integration assertion) all reference this exact type.

4. **Disagreement fails loud, never guesses.** If git and events disagree (a task's events say
   landed but its commits are absent, or vice-versa), the service throws a typed
   `TammaError`-style inconsistency (pointing at the 4-8 replay surface) rather than silently
   skipping or re-implementing — the 39-10 "mis-reconstruction is worse than no re-entry"
   principle. The cycle routes the inconsistency to its loud fail/escalation sink.

5. **Reuses git/event reads, no new fetch stack.** The git side reuses the mediated
   compare/PR/commit reads already in `ActionsResultAggregator`/the mediation client; the event
   side reuses `IEventRepository` (Story 4-7 query surface) — the same reads 39-10 uses. No new
   GitHub client, no second event query path.

6. **Null seam with a NAMED flip.** The service ships behind a `NullTaskLoopReEntryService` that
   always returns index `0` (today's behavior), so re-entry goes live by a DI swap without a
   workflow-code change (the 39-10 D7 pattern). Unlike a bare seam, the flip is specified:
   - **Key:** `Coding:TaskReEntryDisabled`, read in both hosts exactly as 39-10's shipped
     `Documents:ReEntryDisabled` is (`Tamma.ElsaServer/Program.cs:178-187`,
     `Tamma.Api/Program.cs:250-260`) — `true` ⇒ `NullTaskLoopReEntryService`, otherwise the real
     `TaskLoopReEntryService`.
   - **Shipped default at 40-4's merge:** the key's *default value* is `true` — a stock deployment
     gets the Null seam and today's behavior while the read model is unvalidated.
   - **Who flips it, and when:** **40-7**, once its crash-re-entry scenarios (40-7 AC3/AC4) are
     green, changes that one default literal to `false`. **Shipped default at the END of the epic
     is therefore the REAL service**, with `Coding:TaskReEntryDisabled=true` left as the operator
     kill-switch — the same posture 39-10 reached after 39-11 landed.

   Falsifiable: a DI test per host asserts the flag `true` resolves `NullTaskLoopReEntryService`
   and the flag absent/`false` resolves `TaskLoopReEntryService`, plus a pin on the shipped default
   (Null at 40-4; 40-7 flips the pin with the literal).

7. **Emits a re-entry event.** When re-entry skips ≥1 task, the cycle emits
   `AGENT_RUN.TASK_REENTERED` (40-6 family) with `{ issueNumber, resumeIndex, landedIndices,
   basis }` so time-travel debugging shows the crash-recovery decision. A fresh (index-0) start
   is not a re-entry and emits nothing.

8. **Per-mode correct.** SaaS: git reads are the tenant's installation-scoped mediated reads;
   events are tenant-folded. Single-user: local git + central-schema events. The reconstruction
   logic is mode-agnostic; only the read sources differ (as they already do for dispatch/collect).

9. **The task list is rehydrated and verified before any reconstructed index is used.** A resume
   index is a position in the **original** task list, so it is meaningless against a regenerated
   one — and on a fresh dispatch both `task-creation` (`SingleIssueCycleWorkflow.cs:317`) and
   `task-review` (`:370`, which *rewrites* `tasksJson` from the review result) are LLM producers
   that may reorder or rewrite. Therefore:
   - **(a) Rehydrate, don't regenerate.** For an issue with prior landed tasks the loop indexes
     into the list read from the durable accepted **task-breakdown `plan` document** (39-15's
     non-provisional `task-creation → Plan` binding, `DocumentTypeRegistry.cs:154`), not a freshly
     generated one.
   - **(b) Verify before trusting.** `tasks[i].id` (`PlanTask.Id`, `Plan.cs:14`) must equal the
     `taskId` recorded for index `i` on the prior run's per-task events (40-6 AC3).
   - **(c) Mismatch ⇒ index 0.** Verification failure, un-rehydratable list, or a task without an
     id ⇒ resume index `0` (re-implement, safe) with the reason on the position's basis. Never a
     best-effort skip.

   Falsifiable: given landed evidence for tasks 0..k and a *reordered* rehydrated list, the service
   returns `0` with a `TASK_LIST_MISMATCH` basis — not `k+1`.

10. **The 39-10 build gate can see the new node (clause-(c) extension seam).** 39-10's clause (c)
    (`ResumableStandardStructuralTests.cs:240-261`) asserts **exact type-identity** membership of
    `ComputeReEntryPositionActivity` in the built graph (`:252`) — one hardcoded type, and one this
    story cannot reuse (document-coupled, see Architectural Context). 40-4 therefore lands the
    extension seam and registers into it: a canonical **re-entry registry**
    (`CanonicalReEntryActivities`) mirroring the shipped
    `LifecycleBookmarks.CanonicalSuspendActivities` (`LifecycleBookmarks.cs:98-105`); clause (c)
    widens to "the graph contains ≥1 node whose type is in the registry"; the registry ships
    seeded with `ComputeReEntryPositionActivity` **and** `ComputeTaskResumeIndexActivity`.
    Falsifiable both ways: every workflow that declares `LatestStateReEntry`/`Both` today keeps
    the same verdict, and a declaring workflow whose graph holds neither registered type still
    fails, naming the workflow and listing the registered types.

## Technical Notes

- **Git is the authority for "landed"; events are the corroborating trail.** A task is landed
  when its expected changes are committed on the branch AND its `adl-{issue}-task-{index}` run
  event says success. Requiring both avoids skipping a task whose events fired but whose push
  failed (the plan's D3 inconsistency case), and avoids re-implementing a task whose events were
  lost but whose commits are present (corroborate, then trust git).
- **The session-id scheme is the join key — keep it deterministic.** `adl-{issue}-task-{index}`
  must remain stable; any change to it breaks re-entry addressing. If a future story changes it,
  re-entry must migrate in lockstep.
- **This does not re-run earlier stages.** Re-entry here is scoped to the *TDD task loop* — the
  cycle's earlier stages (context/plan/tasks/branch/PR) are re-run on a fresh dispatch as today
  (they are cheap/idempotent relative to coding, and `tasksJson` must be regenerated to index
  into). A future story may extend re-entry to skip those too; out of scope here. *(State this
  boundary explicitly so the scope is honest.)*
- **`tasksJson` determinism matters — now AC9, not a hope.** Re-entry indexes into
  `tasksJson[CurrentTaskIndex]`; if regeneration reorders the list, the index is silently wrong
  (skipping an unimplemented task). AC9 pins rehydrate-then-verify-then-fall-back-to-0.
- **AC9 is the ONE place the coding path reads the 39-11 document store — deliberately, and only
  for the task LIST.** The epic's rule "code is not a document type; re-entry reads git + events"
  governs the resume *position* (AC1), and it still holds. The task list, by contrast, genuinely
  *is* a document after 39-15 (`task-creation` produces a non-provisional `plan`,
  `DocumentTypeRegistry.cs:154`), and it is the only order-stable durable copy that exists today.
  Recorded here so the read is a decision, not an accident. Alternatives considered and rejected:
  the prior task-creation DCB event (carries ids/metadata, not a guaranteed body) and committing a
  `.tamma/tasks.json` to the branch (new write behavior, a separate story).
- **Two of this story's edits land in the tests project.** `ResumableStandardStructuralTests.cs` is
  a test fixture; AC10's clause-(c) widening edits it (`:240-261`). 40-5 edits the same file
  (deleting the `SingleIssueCycleWorkflow` allowlist entry at `:75`), and 40-5's `Both` declaration
  *arms* clause (c) — so **40-4's seam must merge before 40-5** or 40-5 reddens CI.

## Dependencies

- **Story 40-2 — HARD.** The loop node is `WaitForAgentRunActivity`; re-entry sets the index it
  resumes at. 40-4's guard skips landed tasks before that suspend.
- **Story 39-10 — LANDED; the pattern, the read path, and the gate this story widens.**
  `LifecycleReEntryService`/`LifecycleResumeCalculator` are the pure-calculator + I/O-service split
  to mirror (not consumed — different store); `ResumableStandardStructuralTests.cs` is the gate
  AC10 extends. *(Corrected: earlier drafts treated 39-10 as an unlanded prerequisite. It is in the
  tree — `ResumeBehavior.cs:11/:39`, `LifecycleBookmarks.cs:30/:98`,
  `ComputeReEntryPositionActivity.cs:36`, `ResumableStandardStructuralTests.cs:34`.)*
- **Story 4-7 (event query API) + 4-8 (`ReplayReconstructor`)** — the DCB read + forensic
  fallback. Existing.
- **Story 40-6 — SOFT, but AC9(b) needs it.** The `taskId`-per-index recorded on the per-task
  events is what makes rehydration verifiable. Until 40-6 lands, AC9(b) verifies against whatever
  the placeholder-pinned emission carries; if no `taskId` is available the rule degrades to AC9(c)
  (index 0), which is safe.
- **Existing (verified):** `ActionsResultAggregator` (git compare/PR reads), `IEventRepository`,
  the `adl-{issue}-task-{index}` session scheme (`SingleIssueCycleWorkflow.cs:581`), the loop
  (`:513-592`), the accepted task-breakdown `plan` document (39-15).

## Estimated Effort

5-7 days (plan totals 7.0 — see the plan's Effort Breakdown; +0.5 vs. the pre-review 6.5 for the
previously unbudgeted clause-(c) seam)

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
| 2026-07-24 | 1.1.0   | Review pass: AC3 names `ComputeTaskResumeIndexActivity`; AC6 names the flip (key, default, owner); new AC9 (task-list rehydrate + verify) and AC10 (clause-(c) re-entry registry seam); 39-10 recorded as landed | Claude |
