# Implementation Plan — Story 40-4: Per-Task Re-Entry from Git + DCB Events

## Scope & Deliverable

When this story is done, a fresh `SingleIssueCycleWorkflow` dispatched after a crash re-enters
the TDD task loop at the first **unlanded** task instead of index 0. A `TaskLoopReEntryService`
reconstructs the resume index from git (commits on the branch, via the mediated reads already in
`ActionsResultAggregator`) corroborated by per-task DCB events keyed by `adl-{issue}-task-{index}`
(via the Story 4-7 event query); the loop's init consults it; git/event disagreement fails loud;
and the whole thing ships behind a `NullTaskLoopReEntryService` (returns 0) so it goes live by DI
swap. Scope is the task loop only — earlier cycle stages re-run as today (boundary stated).

## Pre-Reading

- `docs/stories/epic-40/story-40-4/40-4-per-task-re-entry-from-git-and-events.md` — this story (ACs are source of truth)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:513-592` — the loop: `initTaskLoop`/`hasMoreTasks`/`extractCurrentTask`/`tddForTask`/`incrementTask`, the `CurrentTaskIndex`/`TotalTasks` vars, the `adl-{issue}-task-{index}` session id (line 581)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — the calculator/service split (D1), `LifecycleResumeCalculator` pure-fold, `ILifecycleReEntryService`, `NullLifecycleReEntryService` seam (D7), `INCONSISTENT_STATE` fail-loud — the shape to mirror with git as the store
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/ActionsResultAggregator.cs` — mediated compare/PR/commit reads (`TryCompareAsync`, `TryFindPullRequestAsync`) reused for "which files/commits are on the branch"
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — Story 4-7 query surface (`QueryAsync(tenant, type, issueNumber, …)`, `ListByCorrelationIdAsync`) — per-task event read by session id
- `apps/tamma-elsa/src/Tamma.Api/Services/Engine/Replay/ReplayReconstructor.cs` — the pure left-fold state-from-events style (forensic fallback)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentResultArtifactParser.cs` — `files_changed` shape used to compare expected-vs-committed
- `docs/stories/epic-4/story-4-7/*` + `story-4-8/*` — event-query / replay contracts
- **NOT FOUND (prerequisite):** `WaitForAgentRunActivity` (40-2), `AgentRunEventTypes` (40-6). See Dependencies & Sequencing.

## Design Decisions

- **D1 — Pure calculator (`Tamma.Core`) + I/O service (`Tamma.Activities`), the 39-10 split, git
  as the store.** `TaskLoopResumeCalculator` (pure) folds a per-task evidence list
  `IReadOnlyList<TaskEvidence>` → the resume index; it has zero I/O and is exhaustively unit-
  tested. `TaskLoopReEntryService` (I/O) gathers the evidence — git compare/PR reads +
  `IEventRepository` per-task queries — maps to `TaskEvidence`, calls the calculator. Placement
  mirrors 39-10 D1: the pure half in `Tamma.Core/AgentDispatch/Resume/` (reachable from the
  engine), the I/O half in `Tamma.Activities/AgentDispatch/` DI-registered in both hosts.
- **D2 — "Landed" = committed-on-branch AND a success run event, per task.** For task index `i`:
  gather (a) the `adl-{issue}-task-{i}` events (`AGENT_RUN.RECEIVED` success / `AGENT.EXECUTION.SUCCESS`
  / `CODE.COMMITTED`-style) and (b) whether the task's expected `files_changed` (from its
  accepted plan slice) are present in the branch's committed diff. `TaskEvidence { Index,
  HasSuccessEvent, HasCommittedChanges }`. `Landed ⇔ HasSuccessEvent && HasCommittedChanges`.
  The resume index = the lowest `i` in `[0, TotalTasks)` that is not landed (tasks are sequential
  by dependency order — the loop advances one at a time — so the first gap is the resume point).
- **D3 — Disagreement is a typed inconsistency, never a guess (39-10 principle).** `HasSuccessEvent
  && !HasCommittedChanges` (events say done, commits absent — a push that never landed) or
  `!HasSuccessEvent && HasCommittedChanges` (commits present, events lost) at the *boundary* task
  ⇒ the calculator throws `TammaError CODE.REENTRY.INCONSISTENT_STATE` with the offending index +
  a pointer to the 4-8 replay surface. The service surfaces it; the cycle routes it to the loud
  fail sink (`emitStepFailed`). Conservative default when only ONE signal is weakly present and
  it's below the boundary: treat as landed only with both; ambiguity at/after the boundary fails
  loud. (Rationale: re-implementing a landed task is wasteful but safe; SKIPPING an unlanded task
  is a silent correctness hole — so bias to fail-loud, never to over-skip.)
- **D4 — `tasksJson` is rehydrated, not regenerated, for a re-entering issue.** Re-entry indexes
  into `tasksJson[i]`, so the ordering must match the original run. The service reads the tasks
  from the durable source the cycle already commits — the plan/task `.md` files on the PR branch
  (created by the `pull-request`/`task-creation` steps) or the prior `TASK.CREATED`/task-creation
  event — and the loop uses that rehydrated `tasksJson` when re-entering, rather than a fresh
  `task-creation` LLM run that could reorder. A fresh issue (no PR/branch) regenerates as today.
  *(This is the subtlest correctness dependency — called out as its own AC-adjacent concern.)*
- **D5 — `NullTaskLoopReEntryService` default (39-10 D7 seam).** Ships returning index 0 (today's
  behavior). Real service behind a config flag; go-live is a DI swap, no workflow change. So the
  loop wiring (D6) lands safely even before the read model is fully validated.
- **D6 — One node before `hasMoreTasks`, sets `CurrentTaskIndex`.** `initTaskLoop` keeps computing
  `TotalTasks`; a new `ComputeTaskResumeIndexActivity` (resolves `ITaskLoopReEntryService` via
  `context.GetService<T>()`, the `EventPersistenceMiddleware` pattern) sets `CurrentTaskIndex` to
  the reconstructed index and emits `AGENT_RUN.TASK_REENTERED` only when `resumeIndex > 0`. Inputs
  read serialization-tolerantly (`ResumeInput`); the node is skipped/short-circuits to 0 when the
  service is Null. This is the coding analogue of 39-10's `ComputeReEntryPositionActivity`.
- **D7 — Scope boundary: the task loop only.** Earlier stages (context/plan/tasks/branch/PR)
  re-run on a fresh dispatch. Stated in the story and here so no reader assumes whole-cycle
  re-entry. Extending re-entry upstream is a future story; `tasksJson` rehydration (D4) is the
  minimum needed to make task-loop re-entry correct.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/AgentDispatch/Resume/TaskEvidence.cs` +
   `TaskLoopResumePosition.cs` + `TaskLoopResumeCalculator.cs`** (D1/D2/D3) — pure fold over
   ordered `TaskEvidence` → `TaskLoopResumePosition { ResumeIndex, LandedIndices[], Basis }`;
   inconsistency → `TammaError CODE.REENTRY.INCONSISTENT_STATE`. Exhaustive matrix in tests.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ITaskLoopReEntryService.cs`,
   `TaskLoopReEntryService.cs`, `NullTaskLoopReEntryService.cs`** (D1/D5) — the service gathers
   git evidence (mediated compare/PR reads — reuse the `ActionsResultAggregator` client seam,
   extracted behind an interface if needed) + per-task events (`IEventRepository` by
   `adl-{issue}-task-{i}` correlation), maps to `TaskEvidence`, calls the calculator; exposes
   `RehydrateTasksJsonAsync` (D4). Null seam returns index 0.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ComputeTaskResumeIndexActivity.cs`**
   (D6) — inputs `IssueNumber`, `BranchName`, `TasksJson`, `TenantId`; outputs `ResumeIndex`,
   `RehydratedTasksJson`; resolves `ITaskLoopReEntryService`; emits `AGENT_RUN.TASK_REENTERED`
   (40-6 constant, placeholder-pinned) when `ResumeIndex > 0`; service missing → loud `TammaError`.

4. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`** —
   insert `computeTaskResumeIndex` between `initTaskLoop` and `hasMoreTasks`; set
   `CurrentTaskIndex` from its output (default 0); use `RehydratedTasksJson` for the loop when
   re-entering (D4). Route its inconsistency outcome to `emitStepFailed` (D3). Minimal edit; the
   `[ResumeBehavior]` declaration is 40-5.

5. **DI registration** (both hosts) — `NullTaskLoopReEntryService` as default, real service behind
   the config flag (D5). Extract the git-read seam from `ActionsResultAggregator` if it is not
   already interface-reachable from `Tamma.Activities`.

6. **CREATE tests** (see Test Plan). Finish with `dotnet ef migrations has-pending-model-changes`
   (clean — no schema) + `dotnet test`.

## Data & Migrations

None. Re-entry reads git (mediated) + the existing `domain_events` table (4-7 surface). No new
table. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (40-6 family, placeholder-pinned):** `AGENT_RUN.TASK_REENTERED` (tags `issueNumber`,
  `tenantId`, `branchName`; data `resumeIndex`, `landedIndices`, `basis`) — only when a task is
  skipped. Migrate the constant to 40-6's `AgentRunEventTypes` at merge (conscious pin).
- **Consumes (re-entry read):** per-task `AGENT_RUN.RECEIVED`/`AGENT.EXECUTION.SUCCESS`/`CODE.*`
  events by `adl-{issue}-task-{i}` correlation; no other family.

## Test Plan

All NUnit + FluentAssertions (+ Moq; git/event reads faked).

- **`TaskLoopResumeCalculatorTests`** (unit, pure) — the matrix: no evidence → 0; tasks 0..k
  landed → k+1; gap at 0 → 0; all landed → `TotalTasks` (loop completes); boundary inconsistency
  (event-without-commit / commit-without-event) → `INCONSISTENT_STATE`; below-boundary weak signal
  → conservative; determinism. **Covers AC1, AC2, AC4.**
- **`TaskLoopReEntryServiceTests`** (unit, Moq'd git-read seam + `IEventRepository`) — landed tasks
  from combined git+events; per-task correlation query by `adl-{issue}-task-{i}`; `Null` service →
  0; `RehydrateTasksJsonAsync` reads committed plan, not a fresh LLM run. **Covers AC1, AC5, AC6, D4.**
- **`ComputeTaskResumeIndexActivityTests`** (unit, activity harness) — sets `CurrentTaskIndex`;
  emits `TASK_REENTERED` only when `>0`; service-missing loud fail; read-back tolerance. **Covers AC3, AC7.**
- **`SingleIssueCycleReEntryStructureTests`** (graph walk) — the compute node sits between
  `initTaskLoop` and `hasMoreTasks`, inconsistency edge → `emitStepFailed`. **Covers AC3.**
- *(Full crash → fresh-instance-skips-landed-task integration is in 40-7.)*

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — task re-entry read model | 1, 2 | `TaskLoopResumeCalculatorTests`, `TaskLoopReEntryServiceTests` |
| 2 — idempotent guard, no double dispatch | 1, 4 | `TaskLoopResumeCalculatorTests`; 40-7 exactly-once |
| 3 — wired into the loop | 3, 4 | `ComputeTaskResumeIndexActivityTests`, `SingleIssueCycleReEntryStructureTests` |
| 4 — disagreement fails loud | 1, 4 | `TaskLoopResumeCalculatorTests` inconsistency cases |
| 5 — reuses git/event reads | 2 | `TaskLoopReEntryServiceTests` |
| 6 — null-seam default | 2, 5 | `TaskLoopReEntryServiceTests` Null case |
| 7 — emits re-entry event | 3 | `ComputeTaskResumeIndexActivityTests` |
| 8 — per-mode correct | 2, 5 | `TaskLoopReEntryServiceTests` (read-source seams) |

## Dependencies & Sequencing

- **Hard prerequisite:** 40-2 (`WaitForAgentRunActivity` — the loop node the guard precedes).
- **Soft:** 39-10 (calculator/service split + `NullX` seam pattern to mirror; the 4-7/4-8 read
  path). 40-6 (event constant — placeholder-pin until it merges).
- **In place, verified:** `ActionsResultAggregator` git reads, `IEventRepository` (4-7), the
  `adl-{issue}-task-{index}` scheme, `ReplayReconstructor` (forensic fallback).
- **Feeds:** 40-5 (the `[ResumeBehavior]` `LatestStateReEntry`/`Both` declaration cites this
  compute node), 40-7 (crash integration proof).
- **Sequencing within the story:** 1 → 2 → 3 → 4 → 5 → 6.

## Risks & Mitigations

- **Over-skipping an unlanded task = silent correctness hole (worse than no re-entry).**
  Mitigation: D2/D3 require BOTH git commits and a success event to mark landed; boundary
  disagreement fails loud; bias is always to re-implement (safe) over skip (unsafe).
- **`tasksJson` reordering on re-entry makes indices meaningless.** Mitigation: D4 rehydrates
  tasks from the committed plan / prior event, not a fresh LLM run; a dedicated service test.
- **Session-id scheme change silently breaks addressing.** Mitigation: Technical Note pins the
  scheme; a test asserts the loop's session id matches the re-entry query key format.
- **Git-read seam not reachable from `Tamma.Activities`.** Mitigation: step 5 extracts the read
  behind an interface (the mediation client already crosses this boundary for dispatch/collect).
- **Scope creep into whole-cycle re-entry.** Mitigation: D7 boundary stated in story + plan;
  earlier stages explicitly re-run.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | pure calculator + position types + inconsistency | 1.0 |
| 2 | re-entry service (git + event gather, rehydrate) + null seam | 1.75 |
| 3 | compute activity + event emit | 0.75 |
| 4 | loop wiring + inconsistency routing | 1.0 |
| 5 | DI + git-read seam extraction | 0.5 |
| 6 | unit tests (calculator, service, activity, structure) | 1.5 |
| **Total** | | **6.5** (story estimate: 5-7 days) |
