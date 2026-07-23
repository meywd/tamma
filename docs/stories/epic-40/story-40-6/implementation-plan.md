# Implementation Plan — Story 40-6: Agent-Run Lifecycle Event Family + Re-Entry Feed

## Scope & Deliverable

When this story is done, the durable agent-run *wait* lifecycle is a first-class `AGENT_RUN.*`
DCB family in one catalogue (`AgentRunEventTypes`): suspend, received (with wake path), timed-out,
resume-unresolved, task-reentered. 40-2/40-3/40-4's placeholder-pinned constants are replaced by
it; every event carries the per-task `adl-{issue}-task-{index}` correlation + `issueNumber`/
`tenantId` so 40-4's re-entry read consumes it via the 4-7 query. The family is additive beside
the existing `AGENT_DISPATCH.RUN_*` (mediation) and `AGENT.EXECUTION.*` (activity) families — no
transition double-emitted.

## Pre-Reading

- `docs/stories/epic-40/story-40-6/40-6-agent-run-lifecycle-event-family.md` — this story (ACs are source of truth)
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchEventTypes.cs` — the sibling mediation family (naming style `AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS`) to align with
- `apps/tamma-elsa/src/Tamma.Activities/Core/` — `TammaEventEmitter` (engine drain emission) + `ITammaActivity` `BuildStartData`/`BuildEndData` conventions
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs:274` — `ReadTenantIdFromContext` (the tenant-resolution pattern events reuse)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — the 4-7 query surface AC3/AC6 assert against
- `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs:55` — the `AGENT.DISPATCH.` prefix handling to not disturb (AC7)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs:209` — an inline `"AGENT.RESULTS.PARTIAL"` literal (the anti-pattern to avoid)
- CLAUDE.md — DCB event conventions (`AGGREGATE.ACTION.STATUS`, JSONB tags, `metadata.eventSource`)
- `docs/stories/epic-40/story-40-2/implementation-plan.md`, `-40-3`, `-40-4` — the placeholder pins this story replaces (emission sites)
- **NOT FOUND (prerequisite):** `WaitForAgentRunActivity` (40-2), `AgentRunResumer` (40-3), `ComputeTaskResumeIndexActivity` (40-4). See Dependencies & Sequencing.

## Design Decisions

- **D1 — One `AgentRunEventTypes` catalogue in `Tamma.Activities/AgentDispatch/`.** Constants:
  `WaitSuspended = "AGENT_RUN.WAIT_SUSPENDED"`, `Received = "AGENT_RUN.RECEIVED"`,
  `TimedOut = "AGENT_RUN.TIMED_OUT"`, `ResumeUnresolved = "AGENT_RUN.RESUME_UNRESOLVED"`,
  `TaskReentered = "AGENT_RUN.TASK_REENTERED"`. Placed in `Tamma.Activities` (not `Tamma.Api`)
  because the emitters are engine-side activities/services; `AgentDispatchEventTypes` (mediation)
  stays in `Tamma.Api` for its side. Two catalogues, two layers, clearly labelled.
- **D2 — Additive, never double-emitting.** The mediation family already records dispatch/poll/
  collect on the *wire* side; `AGENT.EXECUTION.*` records the activity start/end. `AGENT_RUN.*`
  records the *durable wait* transitions that neither captures (suspend/wake-path/timeout/
  reentry). Mapping table in the plan (below) pins who emits what so no transition is covered
  twice. 40-2's interim `AGENT.EXECUTION.*` start/end is retained (it is the activity lifecycle);
  `AGENT_RUN.*` adds the wait lifecycle on top.
- **D3 — `RECEIVED` carries `wakePath ∈ {webhook, poll, local}`.** The resumer/activity knows how
  the wake happened; recording it makes "how was completion observed" queryable — the visibility
  the in-memory registry never gave. Metrics can histogram wake path.
- **D4 — Correlation id is the per-task session id.** Every `AGENT_RUN.*` event sets
  `correlationId = adl-{issue}-task-{index}` and tags `issueNumber`/`tenantId`/`repository`/
  `branchName`. This is the exact key 40-4 queries; the family is designed as the re-entry feed,
  not just audit (AC3/AC6). Emitted via `TammaEventEmitter` (engine drain) with the tenant resolved
  by the `ReadTenantIdFromContext` pattern.
- **D5 — Mechanical placeholder→catalogue migration.** 40-2/40-3/40-4 each define a local
  placeholder constant with the same string value; this story deletes those and points the
  emissions at `AgentRunEventTypes`. Because the string values are agreed up front, the migration
  is a rename, not a behavior change — landable just after those stories with zero risk.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentRunEventTypes.cs`** (D1) —
   the constant catalogue + a `StatusForEvent`/tag-builder helper mirroring `AgentDispatchEventTypes`.

2. **MODIFY `WaitForAgentRunActivity`** (40-2) — emit `WAIT_SUSPENDED` at suspend (after the
   bookmark is created), `RECEIVED` (with `wakePath`) in `OnResumeAsync`, `TIMED_OUT` in
   `OnTimeoutAsync`; carry the D4 correlation/tags. Replace 40-2's placeholder pins.

3. **MODIFY `AgentRunResumer` + `AgentRunReconciler`** (40-3) — emit `RESUME_UNRESOLVED` on an
   unresolvable/timed-out row; set `wakePath=webhook` (resumer) / `poll` (reconciler) into the
   `RECEIVED` path data. Replace 40-3's placeholder pin.

4. **MODIFY `ComputeTaskResumeIndexActivity`** (40-4) — emit `TASK_REENTERED` from
   `AgentRunEventTypes.TaskReentered`. Replace 40-4's placeholder pin.

5. **DOCUMENT the family relationship** — a short section in this plan + a comment block in
   `AgentRunEventTypes.cs` mapping `AGENT_RUN.*` vs `AGENT_DISPATCH.RUN_*` vs `AGENT.EXECUTION.*`
   (D2 table). Note the `PlatformAnalyticsService` touchpoint (AC7) — no change unless a dashboard
   is explicitly extended.

6. **CREATE tests** (see Test Plan). Finish with `dotnet ef migrations has-pending-model-changes`
   (clean) + `dotnet test`.

### Emission map (D2 — no double emit)

| Transition | Family / constant | Emitter |
|---|---|---|
| Mediated dispatch/poll/collect (wire) | `AGENT_DISPATCH.RUN_*` (existing) | `AgentDispatchMediationService` |
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
  data includes `wakePath`/`basis`/`resumeIndex` per event.
- **Unchanged:** `AGENT_DISPATCH.RUN_*`, `AGENT.EXECUTION.*`, `AGENT.RESULTS.*`.

## Test Plan

All NUnit + FluentAssertions (+ Moq; Testcontainers for the 4-7 query round-trip, shareable with 40-7).

- **`AgentRunEventTypesTests`** (unit) — constants stable, `AGGREGATE.ACTION.STATUS` shape,
  distinct from the mediation family. **Covers AC1.**
- **`WaitForAgentRunActivityEventTests`** (unit, capturing event client) — suspend emits
  `WAIT_SUSPENDED`; resume emits `RECEIVED` with the right `wakePath`; timeout emits `TIMED_OUT`;
  all carry the per-task correlation + tenant. **Covers AC2, AC3, AC4.**
- **`AgentRunResumerEventTests` / `ComputeTaskResumeIndexActivityEventTests`** (unit) —
  `RESUME_UNRESOLVED` / `TASK_REENTERED` emitted with correct tags. **Covers AC2.**
- **`AgentRunEventFeedTests`** (Testcontainers or in-memory `IEventRepository`) — emit a per-task
  suspend→received sequence, query 4-7 by `(tenant, AGENT_RUN.RECEIVED, issue)`, feed 40-4's
  `TaskLoopReEntryService`, assert it reconstructs the landed task. **Covers AC3, AC6.**
- **Regression:** `PlatformAnalyticsService` `AGENT.DISPATCH.` handling unaffected. **Covers AC7.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `AgentRunEventTypes` catalogue | 1 | `AgentRunEventTypesTests` |
| 2 — emission wired at real transitions | 2, 3, 4 | activity/resumer/compute event tests |
| 3 — correlation + tags for re-entry | 2, 3, 4 | `WaitForAgentRunActivityEventTests`, `AgentRunEventFeedTests` |
| 4 — tenant-folded, per-mode | 2 | `WaitForAgentRunActivityEventTests` tenant cases |
| 5 — relationship documented, no duplication | 5 | Reviewer check (emission map) |
| 6 — feeds re-entry end-to-end | — | `AgentRunEventFeedTests` |
| 7 — analytics unbroken | 5 | `PlatformAnalyticsService` regression |

## Dependencies & Sequencing

- **Hard prerequisites (consumers of the constants):** 40-2, 40-3, 40-4 — this story replaces
  their placeholder pins; land it just after, or develop in lockstep with agreed string values.
- **In place, verified:** `TammaEventEmitter`, `IEventRepository` (4-7), `AgentDispatchEventTypes`
  (sibling family), the DCB conventions.
- **Feeds:** 40-4's re-entry read (the feed), 40-7's integration (asserts the stream shape).
- **Sequencing within the story:** 1 → 2/3/4 → 5 → 6.

## Risks & Mitigations

- **Double-emitting a transition already covered by the mediation/activity families.** Mitigation:
  D2's emission map pins one emitter per transition; a test asserts no `AGENT_RUN.*` duplicates an
  `AGENT_DISPATCH.RUN_*` semantic.
- **Placeholder/catalogue string drift.** Mitigation: agree the exact strings in 40-2/40-3/40-4
  plans up front (they already pin these values); D5 migration is a rename.
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
