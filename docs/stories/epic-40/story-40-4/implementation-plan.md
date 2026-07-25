# Implementation Plan — Story 40-4: Per-Task Re-Entry from Git + DCB Events

## Scope & Deliverable

When this story is done, a fresh `SingleIssueCycleWorkflow` dispatched after a crash re-enters
the TDD task loop at the first **unlanded** task instead of index 0. A `TaskLoopReEntryService`
reconstructs the resume index from git (commits on the branch, via the mediated reads already in
`ActionsResultAggregator`) corroborated by per-task DCB events keyed by `adl-{issue}-task-{index}`
(via the Story 4-7 event query); a new `ComputeTaskResumeIndexActivity` sets `CurrentTaskIndex`
from it; the task list is rehydrated and id-verified before that index is used; git/event
disagreement fails loud; and the whole thing ships behind a `NullTaskLoopReEntryService` (returns
0) on the `Coding:TaskReEntryDisabled` key, defaulting to Null until 40-7 flips it. This story also
lands the **clause-(c) extension seam** in 39-10's build gate so the new node can satisfy it.
Scope is the task loop only — earlier cycle stages re-run as today (boundary stated).

## Pre-Reading

- `docs/stories/epic-40/story-40-4/40-4-per-task-re-entry-from-git-and-events.md` — this story (ACs are source of truth)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:513-592` — the loop: `initTaskLoop`/`hasMoreTasks`/`extractCurrentTask`/`tddForTask`/`incrementTask`, the `CurrentTaskIndex`/`TotalTasks` vars, the `adl-{issue}-task-{index}` session id (line 581)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — the calculator/service split (D1), `LifecycleResumeCalculator` pure-fold, `ILifecycleReEntryService`, `NullLifecycleReEntryService` seam (D7), `INCONSISTENT_STATE` fail-loud — the shape to mirror with git as the store
- **39-10 as SHIPPED (read the code, not just the plan):** `apps/tamma-elsa/src/Tamma.Activities/Documents/ComputeReEntryPositionActivity.cs:36` (the node — note its document coupling at `:43-44`, `:70-76`, `:141`), `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs:240-261` (clause (c), the hardcoded `typeof(ComputeReEntryPositionActivity)` at `:252`), `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs:98-105` (the registry shape D8 mirrors), `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:178-187` + `apps/tamma-elsa/src/Tamma.Api/Program.cs:250-260` (the shipped `Documents:ReEntryDisabled` seam D5 mirrors)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Plan.cs:13-19` — `PlanTask { id, description, files, dependsOn, testing }`; `id` is the stable per-task identity AC9(b) verifies against
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:154` — 39-15's non-provisional `task-creation → Plan` binding: the durable, order-stable task list AC9(a) rehydrates from
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/ActionsResultAggregator.cs` — mediated compare/PR/commit reads (`TryCompareAsync`, `TryFindPullRequestAsync`) reused for "which files/commits are on the branch"
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — Story 4-7 query surface (`QueryAsync(tenant, type, issueNumber, …)`, `ListByCorrelationIdAsync`) — per-task event read by session id
- `apps/tamma-elsa/src/Tamma.Api/Services/Engine/Replay/ReplayReconstructor.cs` — the pure left-fold state-from-events style (forensic fallback)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentResultArtifactParser.cs` — `files_changed` shape used to compare expected-vs-committed
- `docs/stories/epic-4/story-4-7/*` + `story-4-8/*` — event-query / replay contracts
- **NOT FOUND (prerequisite):** `WaitForAgentRunActivity` (40-2), `AgentRunWaitEventTypes` (40-6 — renamed from `AgentRunEventTypes`, which is already taken by 32-5's `Tamma.Api.Services.Agents.AgentRunEventTypes`; see 40-6 D1). Everything cited above under "39-10 as SHIPPED" **exists** — 39-10 is not a prerequisite, it is the substrate.

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
- **D4 — `tasksJson` is rehydrated from the accepted plan document, then id-verified (AC9).**
  Re-entry indexes into `tasksJson[i]`, so the ordering must match the original run — and on a
  fresh dispatch BOTH producers can reorder it (`createTasks` dispatch at
  `SingleIssueCycleWorkflow.cs:317` → `extractTasks` `:332`, and `extractTaskReview` at `:370`
  which overwrites `tasksJson` with the review's revised list). **Decision:** the durable,
  order-stable source is the accepted **task-breakdown `plan` document** — 39-15 made
  `task-creation` a non-provisional `Plan` producer (`DocumentTypeRegistry.cs:154`), so the list is
  persisted, revision-pinned and readable via `IDocumentInstanceRepository`. `RehydrateTasksJsonAsync`
  reads it; the loop uses the rehydrated list when re-entering; a fresh issue (no prior document)
  regenerates as today. **Then verify:** `tasks[i].id` (`Plan.cs:14`) must equal the `taskId`
  recorded for index `i` on the prior per-task events (40-6). Mismatch / no document / missing id ⇒
  resume index 0 with the reason on `Basis`. *(Rejected: the prior task-creation DCB event — no
  guaranteed body; a committed `.tamma/tasks.json` — new write behavior, separate story. This is
  the coding path's ONE document-store read, and it is for the task LIST only; the resume position
  itself stays git+events. Recorded so the coupling is a decision, not an accident.)*
- **D5 — `NullTaskLoopReEntryService` default on a NAMED key, flipped by 40-7 (39-10 D7 seam,
  completed).** 39-10's seam worked because the flip was specified ("when 39-11 merges", plan
  `:38`/`:105`) and then actually happened (`Tamma.ElsaServer/Program.cs:178-187`,
  `Tamma.Api/Program.cs:250-260`). 40-4 copies both halves:
  `Coding:TaskReEntryDisabled` (one key, both hosts, same `GetValue<bool>` shape) — `true` ⇒ Null,
  otherwise the real `TaskLoopReEntryService`. **40-4 ships the key's default as `true`** (Null
  wins on a stock deployment); **40-7 flips that single default literal to `false`** once its
  crash-re-entry scenarios are green, leaving the key as the operator kill-switch. The key name
  never changes, so the flip is one visible literal in one diff, owned by one story.
- **D6 — One node before `hasMoreTasks`, sets `CurrentTaskIndex`.** `initTaskLoop` keeps computing
  `TotalTasks`; a new `ComputeTaskResumeIndexActivity` (resolves `ITaskLoopReEntryService` via
  `context.GetService<T>()`, the `EventPersistenceMiddleware` pattern) sets `CurrentTaskIndex` to
  the reconstructed index and emits `AGENT_RUN.TASK_REENTERED` only when `resumeIndex > 0`. Inputs
  read serialization-tolerantly (`ResumeInput`); the node short-circuits to 0 when the service is
  the Null seam. It is the coding analogue of 39-10's `ComputeReEntryPositionActivity` — a separate
  type, because that one is document-coupled (`DocumentType` input `:43-44`, `ILifecycleReEntryService`
  `:70-76`, `DOCUMENT.REENTERED` `:141`) and Epic 40 forbids that coupling (epic README, "NOT a
  dependency"). **This name is consumed verbatim by 40-5/40-6/40-7 — do not rename silently.**
- **D7 — Scope boundary: the task loop only.** Earlier stages (context/plan/tasks/branch/PR)
  re-run on a fresh dispatch. Stated in the story and here so no reader assumes whole-cycle
  re-entry. Extending re-entry upstream is a future story; `tasksJson` rehydration (D4) is the
  minimum needed to make task-loop re-entry correct.
- **D8 — The gate's clause (c) gains a canonical RE-ENTRY REGISTRY; 40-4 owns it (AC10).**
  `ResumableStandardStructuralTests.EveryReEntryWorkflow_HasAComputeReEntryNode` (`:240-261`)
  tests `nodeTypes.Contains(typeof(ComputeReEntryPositionActivity))` (`:252`) — exact type identity
  against ONE hardcoded type (a subclass would not satisfy it either), and that type is unusable
  here (D6). So a second re-entry activity is *unrepresentable* in the gate until the seam exists.
  **Decision:** add `CanonicalReEntryActivities` — a static
  `IReadOnlyDictionary<Type, string>` (activity type → re-entry-kind label) in
  `apps/tamma-elsa/src/Tamma.Activities/Resume/CanonicalReEntryActivities.cs`, seeded
  `[typeof(ComputeReEntryPositionActivity)] = "document-position"` and
  `[typeof(ComputeTaskResumeIndexActivity)] = "task-index"` — and rewrite clause (c) as
  "graph contains ≥1 node whose type is in the registry", printing the registry's keys in the
  failure message. It mirrors `LifecycleBookmarks.CanonicalSuspendActivities`
  (`LifecycleBookmarks.cs:98-105`) exactly, so the gate keeps ONE idiom.
  *Alternatives rejected:* a `ResumeBehaviorAttribute.ReEntryActivities` property (the workflow
  would declare the node it contains — self-certification; clause (b) avoids that only because it
  intersects the declaration WITH the canonical registry, `:177-189`), and a marker interface
  (anything can implement it — same self-certification weakness, and it loses the enumerable
  "which re-entry kinds are sanctioned"). A registry keeps the ratchet property: adding a
  sanctioned re-entry node is a deliberate central edit.
  *Not in scope:* a clause-(c) **inverse** (a registered re-entry node inside an undeclared
  workflow). None exists today and the shipped gate has no such check; noted so its absence is
  known rather than assumed. **Placement note:** new namespace `Tamma.Activities.Resume` rather
  than `…/Documents/`, because the registry now spans a document node and a coding node.

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
   `RehydrateTasksJsonAsync` reading the accepted task-breakdown `plan` document and id-verifying
   it against the recorded per-index `taskId` (D4/AC9). Null seam returns index 0.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ComputeTaskResumeIndexActivity.cs`**
   (D6) — inputs `IssueNumber`, `BranchName`, `TasksJson`, `TenantId`; outputs `ResumeIndex`,
   `RehydratedTasksJson`; resolves `ITaskLoopReEntryService`; emits `AGENT_RUN.TASK_REENTERED`
   (40-6 constant, placeholder-pinned) when `ResumeIndex > 0`; service missing → loud `TammaError`.

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Resume/CanonicalReEntryActivities.cs` + MODIFY
   `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs`**
   (D8/AC10) — the registry seeded with `ComputeReEntryPositionActivity` ("document-position") and
   `ComputeTaskResumeIndexActivity` ("task-index"); rewrite clause (c) at `:252` from
   `nodeTypes.Contains(typeof(ComputeReEntryPositionActivity))` to registry membership, and print
   the registry keys in the violation message. Re-run the whole gate: every currently-declaring
   workflow must keep its verdict. **This is a tests-project edit that 40-5 also touches (`:75`) —
   merge this one FIRST** (40-5's `Both` declaration arms the clause).

5. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`** —
   insert `computeTaskResumeIndex` between `initTaskLoop` (`:517`) and `hasMoreTasks` (`:530`); set
   `CurrentTaskIndex` from its output (default 0); use `RehydratedTasksJson` for the loop when
   re-entering (D4). Route its inconsistency outcome to `emitStepFailed` (D3). Minimal edit; the
   `[ResumeBehavior]` declaration is 40-5.

6. **DI registration** (both hosts, D5) — one `Coding:TaskReEntryDisabled` branch mirroring
   `Program.cs:178-187` (ElsaServer) / `:250-260` (Api), with the key's **default `true`** at this
   story (Null wins; 40-7 flips the literal). Extract the git-read seam from
   `ActionsResultAggregator` if it is not already interface-reachable from `Tamma.Activities`.

7. **CREATE tests** (see Test Plan). Finish with `dotnet ef migrations has-pending-model-changes`
   (clean — no schema) + `dotnet test`.

## Data & Migrations

None. Re-entry reads git (mediated) + the existing `domain_events` table (4-7 surface). No new
table. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (40-6 family, placeholder-pinned):** `AGENT_RUN.TASK_REENTERED` (tags `issueNumber`,
  `tenantId`, `branchName`; data `resumeIndex`, `landedIndices`, `basis`) — only when a task is
  skipped. Migrate the constant to 40-6's `AgentRunWaitEventTypes` at merge (conscious pin; the
  string value must not change — see 40-6 D5).
- **Consumes (re-entry read):** per-task `AGENT_RUN.RECEIVED`/`AGENT.EXECUTION.SUCCESS`/`CODE.*`
  events by `adl-{issue}-task-{i}` correlation, and their recorded `taskIndex`/`taskId` (40-6 AC3)
  for the AC9(b) id-verification; no other family.

## Test Plan

All NUnit + FluentAssertions (+ Moq; git/event reads faked).

- **`TaskLoopResumeCalculatorTests`** (unit, pure) — the matrix: no evidence → 0; tasks 0..k
  landed → k+1; gap at 0 → 0; all landed → `TotalTasks` (loop completes); boundary inconsistency
  (event-without-commit / commit-without-event) → `INCONSISTENT_STATE`; below-boundary weak signal
  → conservative; determinism. **Covers AC1, AC2, AC4.**
- **`TaskLoopReEntryServiceTests`** (unit, Moq'd git-read seam + `IEventRepository` +
  `IDocumentInstanceRepository`) — landed tasks from combined git+events; per-task correlation
  query by `adl-{issue}-task-{i}`; `Null` service → 0; `RehydrateTasksJsonAsync` reads the accepted
  task-breakdown plan, not a fresh LLM run; **a reordered rehydrated list with the same landed
  evidence returns index 0 + `TASK_LIST_MISMATCH`, not `k+1`**; a plan with an un-idd task → 0.
  **Covers AC1, AC5, AC9.**
- **`TaskLoopReEntryRegistrationTests`** (unit, DI) — `Coding:TaskReEntryDisabled=true` resolves
  `NullTaskLoopReEntryService`; absent/`false` resolves `TaskLoopReEntryService`; the shipped
  default resolves Null at this story (the pin 40-7 flips). Both hosts. **Covers AC6.**
- **`ComputeTaskResumeIndexActivityTests`** (unit, activity harness) — sets `CurrentTaskIndex`;
  emits `TASK_REENTERED` only when `>0`; service-missing loud fail; read-back tolerance. **Covers AC3, AC7.**
- **`SingleIssueCycleReEntryStructureTests`** (graph walk) — the compute node sits between
  `initTaskLoop` and `hasMoreTasks`, inconsistency edge → `emitStepFailed`. **Covers AC3.**
- **`ResumableStandardStructuralTests` (existing gate, widened)** — clause (c) resolves through
  `CanonicalReEntryActivities`; every currently-declaring workflow keeps its verdict; a
  `LatestStateReEntry`/`Both` workflow with neither registered node still fails, naming it.
  **Covers AC10.**
- *(Full crash → fresh-instance-skips-landed-task integration is in 40-7.)*

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — task re-entry read model | 1, 2 | `TaskLoopResumeCalculatorTests`, `TaskLoopReEntryServiceTests` |
| 2 — idempotent guard, no double dispatch | 1, 5 | `TaskLoopResumeCalculatorTests`; 40-7 exactly-once |
| 3 — wired into the loop as `ComputeTaskResumeIndexActivity` | 3, 5 | `ComputeTaskResumeIndexActivityTests`, `SingleIssueCycleReEntryStructureTests` |
| 4 — disagreement fails loud | 1, 5 | `TaskLoopResumeCalculatorTests` inconsistency cases |
| 5 — reuses git/event reads | 2 | `TaskLoopReEntryServiceTests` |
| 6 — null seam + named flip | 2, 6 | `TaskLoopReEntryRegistrationTests` (both hosts, default pin) |
| 7 — emits re-entry event | 3 | `ComputeTaskResumeIndexActivityTests` |
| 8 — per-mode correct | 2, 6 | `TaskLoopReEntryServiceTests` (read-source seams) |
| 9 — task list rehydrated + id-verified | 2, 5 | `TaskLoopReEntryServiceTests` reorder/mismatch cases |
| 10 — clause-(c) re-entry registry seam | 4 | `ResumableStandardStructuralTests` (widened clause c) |

## Dependencies & Sequencing

- **Hard prerequisite:** 40-2 (`WaitForAgentRunActivity` — the loop node the guard precedes).
- **Substrate, already in the tree:** 39-10 — the calculator/service split + `NullX` seam pattern
  to mirror, the 4-7/4-8 read path, and the build gate whose clause (c) this story widens
  (`ResumableStandardStructuralTests.cs:252`). *(Corrected: not a pending prerequisite.)*
- **Soft:** 40-6 (event constant + the per-index `taskId` AC9(b) verifies against — placeholder-pin
  until it merges).
- **In place, verified:** `ActionsResultAggregator` git reads, `IEventRepository` (4-7), the
  `adl-{issue}-task-{index}` scheme, `ReplayReconstructor` (forensic fallback), 39-15's accepted
  task-breakdown `plan` document.
- **Feeds:** 40-5 (its `Both` declaration is only satisfiable because of step 4's seam + step 3's
  node — **40-4 must merge before 40-5**), 40-7 (crash integration proof, and the owner of the
  D5 seam flip).
- **Sequencing within the story:** 1 → 2 → 3 → 4 → 5 → 6 → 7.

## Risks & Mitigations

- **Over-skipping an unlanded task = silent correctness hole (worse than no re-entry).**
  Mitigation: D2/D3 require BOTH git commits and a success event to mark landed; boundary
  disagreement fails loud; bias is always to re-implement (safe) over skip (unsafe).
- **`tasksJson` reordering on re-entry makes indices meaningless.** Mitigation: D4/AC9 rehydrate
  from the accepted task-breakdown plan document, id-verify against the recorded per-index
  `taskId`, and fall back to index 0 on any mismatch; a dedicated reorder test.
- **The gate cannot represent a second re-entry node (blocking for 40-5).** Mitigation: D8/AC10
  land the `CanonicalReEntryActivities` seam in this story and merge it before 40-5.
- **The seam is never flipped and re-entry ships inert.** Mitigation: D5 names the key, the
  default at each point in time, and the owning story (40-7); 40-7's DoD carries the flip.
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
| 2 | re-entry service (git + event gather, rehydrate + id-verify) + null seam | 1.75 |
| 3 | compute activity + event emit | 0.75 |
| 4 | `CanonicalReEntryActivities` + clause-(c) widening + gate re-run | 0.5 |
| 5 | loop wiring + inconsistency routing | 1.0 |
| 6 | DI (config key, both hosts) + git-read seam extraction | 0.5 |
| 7 | unit tests (calculator, service, activity, registration, structure) | 1.5 |
| **Total** | | **7.0** (story estimate: 5-7 days) |

> **Knock-on for EXECUTION-PLAN.md — ABSORBED, see that file for the authoritative roll-up.** This
> total was **6.5** before the clause-(c) seam was budgeted. *(Superseded arithmetic: this note
> originally computed wave 2's pole as `max(40-3 6.75, 40-4 7.0) = 7.0`, wall-clock 23.25 and epic
> total 38.0. It was written before 40-3's own review pass raised **6.75 → 8.25**, which makes 40-3
> the wave-2 pole and puts the critical path on `40-2 → 40-3 → 40-6 → 40-7`.)* The reconciled
> figures now live in EXECUTION-PLAN.md: total **39.5**, critical path **22.5**, wall-clock
> **24.5**. Do not re-derive them from this note.
