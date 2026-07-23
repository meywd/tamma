# Story 40-6: Agent-Run Lifecycle Event Family + Re-Entry Feed

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

As an **operator debugging a coding run via time-travel** (and as 40-4's re-entry read model),
I want the durable agent-run *wait* lifecycle — suspended, received, timed-out, re-entered — to
be first-class `AGENT_RUN.*` events on the DCB stream, keyed for per-task re-entry,
So that the audit trail shows when a run suspended, how it woke (webhook vs poll vs timeout), and
which task a crash re-entered at — and so 40-4 has a durable, queryable trail to reconstruct from.

## Priority

P1 — The epic functions without a dedicated event family (40-2 keeps the existing
`AGENT.EXECUTION.*` emission), but re-entry (40-4) reads events keyed by the per-task session id,
and the wait lifecycle (suspend/received/timeout) is invisible to time-travel today. This story
formalizes the family the other stories placeholder-pin against and makes the re-entry feed
first-class.

## Architectural Context (READ FIRST)

**There are already several agent-adjacent event families — this story adds the missing *wait*
layer beside them, it does not reinvent them:**

- `AGENT.DISPATCH.STARTED/SUCCESS/FAILED` — engine-side `DispatchAgentWorkflowActivity` /
  `ExecuteAgentActivity` via `TammaEventEmitter` (`EventType="AGENT.DISPATCH"`/`"AGENT.EXECUTION"`).
- `AGENT.RESULTS.*` — `CollectAgentResultsActivity`.
- `AGENT_DISPATCH.RUN_TRIGGERED/RUN_POLLED/RESULTS_COLLECTED.SUCCESS/FAILED` — the **Tamma.Api**
  mediation audit (`AgentDispatchEventTypes`,
  `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchEventTypes.cs`), one terminal
  event per mediated call.

What is **missing** is the *durable wait* lifecycle that 40-2 introduces: the moment the workflow
**suspends** on the agent-run bookmark, the moment it is **received** (and by which path), the
**timeout**, and the crash **re-entry** decision (40-4). 40-2 and 40-3 and 40-4 currently
placeholder-pin these constants; this story defines them in one place and wires the emissions.

**Events are keyed for re-entry.** 40-4 reconstructs the resume index from per-task events keyed
by `adl-{issue}-task-{index}`. This story guarantees the wait-lifecycle events carry that
correlation id + `issueNumber` + `tenantId` so the 4-7 query surface can retrieve them per task.

## Acceptance Criteria

1. **`AgentRunEventTypes` constant catalogue.** A single source of truth (e.g.
   `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentRunEventTypes.cs`) defines the wait
   lifecycle family: `AGENT_RUN.WAIT_SUSPENDED`, `AGENT_RUN.RECEIVED`, `AGENT_RUN.TIMED_OUT`,
   `AGENT_RUN.RESUME_UNRESOLVED`, `AGENT_RUN.TASK_REENTERED` (names may refine in the plan; the
   set covers suspend / wake / timeout / unresolved-signal / crash-reentry). Each has a stable
   `AGGREGATE.ACTION.STATUS`-style constant.

2. **Emission wired at the real transitions.** `WaitForAgentRunActivity` (40-2) emits
   `WAIT_SUSPENDED` on suspend, `RECEIVED` on resume (with the wake path: `webhook`|`poll`|`local`),
   `TIMED_OUT` on the DelayFor edge; `AgentRunResumer`/reconciler (40-3) emits `RESUME_UNRESOLVED`
   on an unresolvable/timed-out row; `ComputeTaskResumeIndexActivity` (40-4) emits
   `TASK_REENTERED`. The placeholder pins in 40-2/40-3/40-4 are replaced by these constants.

3. **Correlation + tags for re-entry.** Every event carries `correlationId = adl-{issue}-task-{index}`
   (the per-task session id), `issueNumber`, `tenantId`, `repository`, `branchName`, and the
   wake-path/basis in `data`. This is the trail 40-4's re-entry read consumes; a test asserts the
   4-7 query by `(tenantId, AGENT_RUN.RECEIVED, issueNumber)` returns the per-task rows.

4. **Tenant-folded, per-mode.** Events are tenant-scoped (SaaS) / central (single-user) exactly
   like the existing families; no cross-tenant leakage. The `TenantId` tag is resolved from the
   workflow context the same way `DispatchAgentWorkflowActivity.ReadTenantIdFromContext` does.

5. **Relationship to existing families documented, no duplication.** The plan states how
   `AGENT_RUN.*` (engine wait lifecycle) relates to `AGENT_DISPATCH.RUN_*` (Tamma.Api mediation
   audit) and `AGENT.EXECUTION.*` (activity start/end) — the wait family is *additive*, the others
   are unchanged. No event is emitted twice for the same transition.

6. **Feeds re-entry, verified end-to-end.** A test drives a per-task suspend→received sequence,
   then runs 40-4's `TaskLoopReEntryService` against the emitted stream and asserts it reconstructs
   the landed task from these events (corroborated by git) — proving the family is a usable re-entry
   feed, not just audit decoration.

7. **Analytics/dashboards unbroken.** The existing `PlatformAnalyticsService` `AGENT.DISPATCH.`
   prefix handling is not disturbed; if `AGENT_RUN.*` should surface in any dashboard, it is added
   deliberately (not required, but the plan notes the touchpoint).

## Technical Notes

- **One catalogue, not scattered string literals.** The whole point is a single
  `AgentRunEventTypes` the other stories reference — avoid the `"AGENT.RESULTS.PARTIAL"`-style
  inline literals (`CollectAgentResultsActivity.cs:209`) proliferating.
- **`RECEIVED` records the wake path.** `webhook` (40-3 durable resume), `poll` (reconciler),
  `local` (single-user in-process) — so time-travel and metrics can tell how completion was
  actually observed (the current in-memory registry made this invisible).
- **This is P1 and can land slightly after 40-2/40-3/40-4** — those ship with placeholder
  constants; this story consolidates them. Keep the placeholder→catalogue migration mechanical.
- Follow the DCB event conventions in CLAUDE.md (`AGGREGATE.ACTION.STATUS`, JSONB tags, UUID v7,
  `metadata.eventSource`).

## Dependencies

- **Stories 40-2, 40-3, 40-4 — HARD (consumers of the constants).** This story replaces their
  placeholder pins; it can be developed against their emission sites in lockstep or land just
  after, migrating the placeholders.
- **Story 4-7 (event query API)** — the retrieval surface AC3/AC6 assert against. Existing.
- **Existing (verified):** `TammaEventEmitter`, `IEventRepository`/`IAlertEventEmitter`,
  `AgentDispatchEventTypes` (the sibling mediation family), the DCB conventions.

## Estimated Effort

3-4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
