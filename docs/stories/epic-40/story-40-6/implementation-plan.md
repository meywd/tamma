# Implementation Plan — Story 40-6: Agent-Run Lifecycle Event Family + Re-Entry Feed

## Scope & Deliverable

When this story is done, the durable agent-run *wait* lifecycle is a first-class `AGENT_RUN.*`
DCB family in one catalogue (`AgentRunWaitEventTypes`): suspend, received (with wake path),
timed-out, resume-unresolved, task-reentered. 40-2/40-3/40-4's placeholder-pinned constants are
replaced by it **without any wire-string change** (D5); every event carries the per-task
`adl-{issue}-task-{index}` correlation + `issueNumber`/`tenantId`/`taskIndex`/`taskId` so 40-4's
re-entry read consumes and *verifies* against it via the 4-7 query. The family is additive beside
the existing `AGENT_DISPATCH.RUN_*` (mediation), `AGENT.EXECUTION.*` (activity), `AGENT.RUN.*`
(32-5 managed run) and `AGENT.TASK.*` (32-6 trail) families — no transition double-emitted.

## Pre-Reading

- `docs/stories/epic-40/story-40-6/40-6-agent-run-lifecycle-event-family.md` — this story (ACs are source of truth)
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchEventTypes.cs` — the sibling mediation family (naming style `AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS`) to align with
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRunEventTypes.cs:17` — **the class name this story may NOT reuse** (32-5, `AGENT.RUN.STARTED/SUCCESS/FAILED`, emitted by `ManagedAgent.cs:286/:448/:525`); `Tamma.Api` project-references `Tamma.Activities` (`Tamma.Api.csproj:78`), so two `AgentRunEventTypes` in scope is an ambiguity waiting to happen. Sibling: `AgentTrailEventTypes.cs` (32-6, `AGENT.TASK.*`)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/DocumentEvents.cs:35`, `:53` — `DOCUMENT.ACCEPTED` / `DOCUMENT.REENTERED`: the shipped precedent for 2-segment type strings on status-less transitions
- `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs:50` — `AppendAsync` and queries only; there is no update/delete surface, which is why D5/AC8 matter
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Plan.cs:13-19` — `PlanTask.Id`, the `taskId` recorded per event (AC3) that 40-4 AC9(b) verifies against
- `apps/tamma-elsa/src/Tamma.Activities/Core/` — `TammaEventEmitter` (engine drain emission) + `ITammaActivity` `BuildStartData`/`BuildEndData` conventions
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs:274` — `ReadTenantIdFromContext` (the tenant-resolution pattern events reuse)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — the 4-7 query surface AC3/AC6 assert against
- `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs:55` — the `AGENT.DISPATCH.` prefix handling to not disturb (AC7)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs:209` — an inline `"AGENT.RESULTS.PARTIAL"` literal (the anti-pattern to avoid)
- CLAUDE.md — DCB event conventions (`AGGREGATE.ACTION.STATUS`, JSONB tags, `metadata.eventSource`)
- `docs/stories/epic-40/story-40-2/implementation-plan.md`, `-40-3`, `-40-4` — the placeholder pins this story replaces (emission sites)
- **NOT FOUND (prerequisite):** `WaitForAgentRunActivity` (40-2), `AgentRunResumer` (40-3), `ComputeTaskResumeIndexActivity` (40-4). See Dependencies & Sequencing.
- **NOTE for the other plans:** 40-3's plan (`:128`), EXECUTION-PLAN.md (`:112`) and sprint-status.yaml (`:41`) still call this catalogue `AgentRunEventTypes`; they track the D1 rename to `AgentRunWaitEventTypes`. The five *string values* are unchanged, so nothing else in those plans moves.

## Design Decisions

- **D1 — One `AgentRunWaitEventTypes` catalogue in `Tamma.Activities/AgentDispatch/`.** Constants:
  `WaitSuspended = "AGENT_RUN.WAIT_SUSPENDED"`, `Received = "AGENT_RUN.RECEIVED"`,
  `TimedOut = "AGENT_RUN.TIMED_OUT"`, `ResumeUnresolved = "AGENT_RUN.RESUME_UNRESOLVED"`,
  `TaskReentered = "AGENT_RUN.TASK_REENTERED"`. Placed in `Tamma.Activities` (not `Tamma.Api`)
  because the emitters are engine-side activities/services; `AgentDispatchEventTypes` (mediation)
  stays in `Tamma.Api` for its side. **The class is `AgentRunWait…`, not `AgentRun…`**
  *(Corrected)*: `Tamma.Api.Services.Agents.AgentRunEventTypes` already exists (32-5) and
  `Tamma.Api` references `Tamma.Activities`, so the original name would put two same-named
  catalogues in scope in the API host — and `AGENT.RUN.*` (a managed run record) is a genuinely
  different thing from `AGENT_RUN.*` (an engine wait transition). The `Wait` in the class name is
  the one-word summary of that difference.
- **D2 — Additive, never double-emitting.** The mediation family already records dispatch/poll/
  collect on the *wire* side; `AGENT.EXECUTION.*` records the activity start/end. `AGENT_RUN.*`
  records the *durable wait* transitions that neither captures (suspend/wake-path/timeout/
  reentry). Mapping table in the plan (below) pins who emits what so no transition is covered
  twice. 40-2's interim `AGENT.EXECUTION.*` start/end is retained (it is the activity lifecycle);
  `AGENT_RUN.*` adds the wait lifecycle on top.
- **D3 — `RECEIVED` carries `wakePath ∈ {webhook, poll, local}`.** The resumer/activity knows how
  the wake happened; recording it makes "how was completion observed" queryable — the visibility
  the in-memory registry never gave. Metrics can histogram wake path.
- **D4 — Correlation id is the per-task session id; `taskId` rides beside it.** Every
  `AGENT_RUN.*` event sets `correlationId = adl-{issue}-task-{index}`, tags
  `issueNumber`/`tenantId`/`repository`/`branchName`, and puts `taskIndex` + `taskId`
  (`PlanTask.Id`, `Plan.cs:14`) in `data`. The correlation id is the *query* key; `taskId` is the
  *verification* key — 40-4 AC9(b) compares a rehydrated task list's `tasks[i].id` against the
  `taskId` recorded for index `i` before trusting a reconstructed index, because an LLM producer
  can reorder the list between runs. Emitted via `TammaEventEmitter` (engine drain) with the tenant
  resolved by the `ReadTenantIdFromContext` pattern.
- **D5 — The C# consolidation is a rename ONLY BECAUSE the wire strings are frozen (AC8).**
  40-2/40-3/40-4 each define a local placeholder constant with the *same* string value; this story
  deletes those and points the emissions at `AgentRunWaitEventTypes`. *(Corrected: the earlier
  wording — "the migration is a rename, not a behavior change" — was true of the identifiers and
  false of the data.)* Those stories will already have written rows into `domain_events`, and
  `IEventRepository` exposes append + queries only (`EventRepository.cs:50`; no update, no delete).
  So **changing a type string is a stream data migration**, not an edit: old rows keep the old type
  and 40-4's re-entry read — which decides whether a task gets re-implemented — stops seeing them.
  Hence the byte-parity pin (AC8): the values are frozen, the identifiers move, and any attempt to
  change a value reddens the build so the dual-read/alias conversation happens deliberately. Two
  bounding facts, stated so this is neither ignored nor over-dramatised: Tamma has no production
  users (CLAUDE.md), so the exposure is in-flight issues across the upgrade plus QA streams — real,
  but not customer data.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentRunWaitEventTypes.cs`** (D1) —
   the constant catalogue + a `StatusForEvent`/tag-builder helper mirroring `AgentDispatchEventTypes`,
   with an XML-doc block stating the distinction from 32-5's `AGENT.RUN.*` and 38-2's
   `AGENT_DISPATCH.RUN_*`.

2. **MODIFY `WaitForAgentRunActivity`** (40-2) — emit `WAIT_SUSPENDED` at suspend (after the
   bookmark is created), `RECEIVED` (with `wakePath`) in `OnResumeAsync`, `TIMED_OUT` in
   `OnTimeoutAsync`; carry the D4 correlation/tags. Replace 40-2's placeholder pins.

3. **MODIFY `AgentRunResumer` + `AgentRunReconciler`** (40-3) — emit `RESUME_UNRESOLVED` on an
   unresolvable/timed-out row; set `wakePath=webhook` (resumer) / `poll` (reconciler) into the
   `RECEIVED` path data. Replace 40-3's placeholder pin.

4. **MODIFY `ComputeTaskResumeIndexActivity`** (40-4) — emit `TASK_REENTERED` from
   `AgentRunWaitEventTypes.TaskReentered`. Replace 40-4's placeholder pin.

5. **DOCUMENT the family relationship** — a short section in this plan + a comment block in
   `AgentRunWaitEventTypes.cs` mapping `AGENT_RUN.*` vs `AGENT_DISPATCH.RUN_*` vs
   `AGENT.EXECUTION.*` vs `AGENT.RUN.*` (32-5) vs `AGENT.TASK.*` (32-6) — the D2 table. Note the
   `PlatformAnalyticsService` touchpoint (AC7) — no change unless a dashboard is explicitly
   extended.

6. **CREATE tests** (see Test Plan). Finish with `dotnet ef migrations has-pending-model-changes`
   (clean) + `dotnet test`.

### Emission map (D2 — no double emit)

| Transition | Family / constant | Emitter |
|---|---|---|
| Mediated dispatch/poll/collect (wire) | `AGENT_DISPATCH.RUN_*` (existing, 38-2) | `AgentDispatchMediationService` |
| Managed-agent run record (not this path) | `AGENT.RUN.*` (existing, 32-5 — note the dot) | `ManagedAgent` (`:286`, `:448`, `:525`) |
| Per-agent action trail (not this path) | `AGENT.TASK.*` (existing, 32-6) | `AgentTrailEventTypes` emitters |
| Activity start/end | `AGENT.EXECUTION.*` (existing) | `WaitForAgentRunActivity` (via `TammaEventEmitter`) |
| Durable wait suspended | `AGENT_RUN.WAIT_SUSPENDED` (new) | `WaitForAgentRunActivity` |
| Wait received (+ wakePath) | `AGENT_RUN.RECEIVED` (new) | `WaitForAgentRunActivity` / `AgentRunResumer` |
| Wait timed out | `AGENT_RUN.TIMED_OUT` (new) | `WaitForAgentRunActivity` |
| Signal row unresolvable | `AGENT_RUN.RESUME_UNRESOLVED` (new) | `AgentRunReconciler` |
| Crash re-entry skipped a task | `AGENT_RUN.TASK_REENTERED` (new) | `ComputeTaskResumeIndexActivity` |

## Data & Migrations

None. Events ride the existing `domain_events` drain → `EventRepository`. No schema change.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits (new catalogue):** `AGENT_RUN.WAIT_SUSPENDED`, `AGENT_RUN.RECEIVED`,
  `AGENT_RUN.TIMED_OUT`, `AGENT_RUN.RESUME_UNRESOLVED`, `AGENT_RUN.TASK_REENTERED` — tags
  `issueNumber`, `tenantId`, `repository`, `branchName`, `correlationId=adl-{issue}-task-{index}`;
  data includes `taskIndex`, `taskId`, and `wakePath`/`basis`/`resumeIndex` per event.
- **Frozen:** those five string values (AC8) — persisted rows cannot be rewritten.
- **Unchanged:** `AGENT_DISPATCH.RUN_*`, `AGENT.EXECUTION.*`, `AGENT.RESULTS.*`, `AGENT.RUN.*`
  (32-5), `AGENT.TASK.*` (32-6).

## Test Plan

All NUnit + FluentAssertions (+ Moq; Testcontainers for the 4-7 query round-trip, shareable with 40-7).

- **`AgentRunWaitEventTypesTests`** (unit) — **byte-parity**: each constant equals its exact
  literal (the AC8 freeze); no constant starts with `AGENT.RUN.` or `AGENT.DISPATCH.`; the value
  set is disjoint from `AgentDispatchEventTypes`' and 32-5 `AgentRunEventTypes`'. **Covers AC1, AC5, AC8.**
- **`WaitForAgentRunActivityEventTests`** (unit, capturing event client) — suspend emits
  `WAIT_SUSPENDED`; resume emits `RECEIVED` with the right `wakePath`; timeout emits `TIMED_OUT`;
  all carry the per-task correlation + tenant + `taskIndex`/`taskId`. **Covers AC2, AC3, AC4.**
- **`AgentRunResumerEventTests` / `ComputeTaskResumeIndexActivityEventTests`** (unit) —
  `RESUME_UNRESOLVED` / `TASK_REENTERED` emitted with correct tags. **Covers AC2.**
- **`AgentRunEventFeedTests`** (Testcontainers or in-memory `IEventRepository`) — emit a per-task
  suspend→received sequence, query 4-7 by `(tenant, AGENT_RUN.RECEIVED, issue)`, feed 40-4's
  `TaskLoopReEntryService`, assert it reconstructs the landed task **and** that the recorded
  `taskId` per index is what 40-4's rehydration check reads. **Covers AC3, AC6.**
- **Regression:** `PlatformAnalyticsService` `AGENT.DISPATCH.` handling unaffected. **Covers AC7.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `AgentRunWaitEventTypes` catalogue, exact strings | 1 | `AgentRunWaitEventTypesTests` |
| 2 — emission wired at real transitions | 2, 3, 4 | activity/resumer/compute event tests |
| 3 — correlation + tags (incl. `taskIndex`/`taskId`) | 2, 3, 4 | `WaitForAgentRunActivityEventTests`, `AgentRunEventFeedTests` |
| 4 — tenant-folded, per-mode | 2 | `WaitForAgentRunActivityEventTests` tenant cases |
| 5 — relationship documented, families disjoint | 1, 5 | `AgentRunWaitEventTypesTests` disjointness + emission map |
| 6 — feeds re-entry end-to-end | 2, 3, 4 | `AgentRunEventFeedTests` |
| 7 — analytics unbroken | 5 | `PlatformAnalyticsService` regression |
| 8 — wire strings frozen (no stream migration) | 1 | `AgentRunWaitEventTypesTests` byte-parity pin |

## Dependencies & Sequencing

- **Hard prerequisites (consumers of the constants):** 40-2, 40-3, 40-4 — this story replaces
  their placeholder pins; land it just after, or develop in lockstep with agreed string values.
- **In place, verified:** `TammaEventEmitter`, `IEventRepository` (4-7, append-only),
  `AgentDispatchEventTypes` (38-2 sibling), `AgentRunEventTypes`/`AgentTrailEventTypes` (32-5/32-6
  — the name + prefixes to stay clear of), the DCB conventions.
- **Feeds:** 40-4's re-entry read — both the query key and the `taskId` its AC9(b) verification
  needs — and 40-7's integration (asserts the stream shape).
- **Sequencing within the story:** 1 → 2/3/4 → 5 → 6.

## Risks & Mitigations

- **Double-emitting a transition already covered by the mediation/activity families.** Mitigation:
  D2's emission map pins one emitter per transition; a test asserts no `AGENT_RUN.*` duplicates an
  `AGENT_DISPATCH.RUN_*` semantic.
- **Placeholder/catalogue string drift — the real hazard, because it is unrepairable in place.**
  A value that drifts between 40-2's emission and this consolidation strands the already-persisted
  rows (append-only store), and it is 40-4's correctness read that goes blind. Mitigation: the
  strings are agreed in 40-2/40-3/40-4 up front and pinned byte-for-byte by
  `AgentRunWaitEventTypesTests` (AC8/D5); a deliberate value change must ship a dual-read alias and
  say so, never a silent swap.
- **Name confusion with 32-5's `AGENT.RUN.*`.** Mitigation: D1's class rename +
  the disjointness/prefix assertions in `AgentRunWaitEventTypesTests`.
- **Event-store bloat from per-poll emission.** Mitigation: `RECEIVED`/`TIMED_OUT` are terminal
  (one per run), not per-poll — mirrors the mediation family's AC7 terminal-only discipline.
- **Landing before its consumers destabilizes their placeholder builds.** Mitigation: P1, sequenced
  after them; the placeholders keep 40-2/40-3/40-4 green until this consolidates.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1, 5 | catalogue + relationship doc/comment | 0.75 |
| 2, 3, 4 | wire emissions, replace placeholders | 1.25 |
| 6 | unit + feed tests | 1.25 |
| **Total** | | **3.25** (story estimate: 3-4 days) |
