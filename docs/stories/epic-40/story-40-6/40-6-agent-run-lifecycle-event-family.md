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
- **`AGENT.RUN.STARTED/SUCCESS/FAILED`** — the Story 32-5 *managed-run record*, emitted by
  `ManagedAgent` (`Tamma.Api/Services/Agents/ManagedAgent.cs:286`, `:448`, `:525`) from a class
  **already named `AgentRunEventTypes`**
  (`Tamma.Api/Services/Agents/AgentRunEventTypes.cs:17`). Two consequences this story must own:
  the new catalogue **cannot reuse that class name** (`Tamma.Api` project-references
  `Tamma.Activities`, so both would be in scope), and `AGENT.RUN.*` vs `AGENT_RUN.*` are one
  character apart on the wire — the distinction has to be documented and pinned, not assumed.
- `AGENT.TASK.*` — the 32-6 per-agent action trail (`AgentTrailEventTypes`), for completeness.

What is **missing** is the *durable wait* lifecycle that 40-2 introduces: the moment the workflow
**suspends** on the agent-run bookmark, the moment it is **received** (and by which path), the
**timeout**, and the crash **re-entry** decision (40-4). 40-2 and 40-3 and 40-4 currently
placeholder-pin these constants; this story defines them in one place and wires the emissions.

**Events are keyed for re-entry — and carry the task's identity.** 40-4 reconstructs the resume
index from per-task events keyed by `adl-{issue}-task-{index}`. This story guarantees the
wait-lifecycle events carry that correlation id + `issueNumber` + `tenantId` (so the 4-7 query
surface can retrieve them per task) **and** the `taskIndex`/`taskId` pair 40-4 AC9 needs to prove a
rehydrated task list still matches the run those events describe.

## Acceptance Criteria

1. **`AgentRunWaitEventTypes` constant catalogue.** A single source of truth
   (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentRunWaitEventTypes.cs`) defines the
   wait lifecycle family with **exactly these five string values**, which are the values
   40-2/40-3/40-4 placeholder-pin and must not change here (AC8):
   `AGENT_RUN.WAIT_SUSPENDED`, `AGENT_RUN.RECEIVED`, `AGENT_RUN.TIMED_OUT`,
   `AGENT_RUN.RESUME_UNRESOLVED`, `AGENT_RUN.TASK_REENTERED` — covering suspend / wake / timeout /
   unresolved-signal / crash-reentry. *(Corrected on two points: the class name was
   `AgentRunEventTypes`, which is taken by 32-5 (`Tamma.Api/Services/Agents/AgentRunEventTypes.cs:17`);
   and these are 2-segment `AGGREGATE.ACTION` names, not `AGGREGATE.ACTION.STATUS` — the wait
   lifecycle has no separate status axis, exactly like the shipped `DOCUMENT.REENTERED` /
   `DOCUMENT.ACCEPTED` (`DocumentEvents.cs:53`, `:35`). Status is carried in the event's `Status`
   field, not in the type string.)*

2. **Emission wired at the real transitions.** `WaitForAgentRunActivity` (40-2) emits
   `WAIT_SUSPENDED` on suspend, `RECEIVED` on resume (with the wake path: `webhook`|`poll`|`local`),
   `TIMED_OUT` on the DelayFor edge; `AgentRunResumer`/reconciler (40-3) emits `RESUME_UNRESOLVED`
   on an unresolvable/timed-out row; `ComputeTaskResumeIndexActivity` (40-4) emits
   `TASK_REENTERED`. The placeholder pins in 40-2/40-3/40-4 are replaced by these constants.

3. **Correlation + tags for re-entry, including the task identity.** Every event carries
   `correlationId = adl-{issue}-task-{index}` (the per-task session id), `issueNumber`, `tenantId`,
   `repository`, `branchName`, and — in `data` — `taskIndex` (int) **and `taskId`** (the
   `PlanTask.id` of the task slice being run, `Plan.cs:14`), plus the wake-path/basis. The
   `taskId` is not decoration: 40-4 AC9(b) verifies a rehydrated task list against the `taskId`
   recorded for each index before it trusts a reconstructed resume index, so without it 40-4
   degrades to always resuming at 0. A test asserts the 4-7 query by
   `(tenantId, AGENT_RUN.RECEIVED, issueNumber)` returns the per-task rows with both fields.

4. **Tenant-folded, per-mode.** Events are tenant-scoped (SaaS) / central (single-user) exactly
   like the existing families; no cross-tenant leakage. The `TenantId` tag is resolved from the
   workflow context the same way `DispatchAgentWorkflowActivity.ReadTenantIdFromContext` does.

5. **Relationship to existing families documented, no duplication — and the near-miss pinned.**
   The plan's emission map states how `AGENT_RUN.*` (engine wait lifecycle) relates to
   `AGENT_DISPATCH.RUN_*` (mediation audit), `AGENT.EXECUTION.*` (activity start/end),
   `AGENT.RUN.*` (32-5 managed-run record) and `AGENT.TASK.*` (32-6 trail). The wait family is
   *additive*; no transition is emitted twice. Falsifiable: a test asserts no constant in this
   catalogue starts with `AGENT.RUN.` or `AGENT.DISPATCH.` (so it can never be swept up by the
   `AGENT.DISPATCH.` prefix consumers at `PlatformAnalyticsService.cs:55` or confused with 32-5's
   family), and that this catalogue's values are disjoint from `AgentDispatchEventTypes`' and
   32-5 `AgentRunEventTypes`'.

6. **Feeds re-entry, verified end-to-end.** A test drives a per-task suspend→received sequence,
   then runs 40-4's `TaskLoopReEntryService` against the emitted stream and asserts it reconstructs
   the landed task from these events (corroborated by git) — proving the family is a usable re-entry
   feed, not just audit decoration.

7. **Analytics/dashboards unbroken.** The existing `PlatformAnalyticsService` `AGENT.DISPATCH.`
   prefix handling (`PlatformAnalyticsService.cs:55`) is not disturbed; if `AGENT_RUN.*` should
   surface in any dashboard, it is added deliberately (not required, but the plan notes the
   touchpoint).

8. **Consolidation changes C# identifiers only — the wire strings are frozen.** 40-2/40-3/40-4
   emit these events *before* this story lands, and their rows are already in `domain_events`.
   `IEventRepository` is append-only (`AppendAsync` + queries; no update/delete), so **a changed
   type string is a stream data migration, not a rename**: previously-written rows keep the old
   type forever, and 40-4's re-entry read — a correctness path, not just audit — would stop seeing
   them, silently re-implementing landed tasks or tripping the inconsistency fail-loud. Therefore:
   each string value is byte-frozen at the merge of the story that first emits it (40-2 for
   `WAIT_SUSPENDED`/`RECEIVED`/`TIMED_OUT`, 40-3 for `RESUME_UNRESOLVED`, 40-4 for
   `TASK_REENTERED`), and this story only replaces local placeholder constants with catalogue
   references. Falsifiable: a byte-parity test pins each
   constant to its literal, so any value change fails the build and forces the migration
   conversation. If a value *must* change, the story must additionally ship a dual-read alias in
   the re-entry query + the 4-7 surface and say so explicitly. *(Scope, honestly: Tamma has no
   production users, so this is not a customer-data problem — it is an in-flight-issue and
   QA-stream problem across the upgrade, plus a truthfulness problem in the plan, which used to
   assert the consolidation "is a rename" unconditionally.)*

## Technical Notes

- **One catalogue, not scattered string literals.** The whole point is a single
  `AgentRunWaitEventTypes` the other stories reference — avoid the `"AGENT.RESULTS.PARTIAL"`-style
  inline literals (`CollectAgentResultsActivity.cs:209`) proliferating.
- **`RECEIVED` records the wake path.** `webhook` (40-3 durable resume), `poll` (reconciler),
  `local` (single-user in-process) — so time-travel and metrics can tell how completion was
  actually observed (the current in-memory registry made this invisible).
- **This is P1 and can land slightly after 40-2/40-3/40-4** — those ship with placeholder
  constants; this story consolidates them. The migration stays mechanical *because* AC8 freezes
  the values; it is only mechanical under that condition.
- Follow the DCB event conventions in CLAUDE.md (JSONB tags, UUID v7, `metadata.eventSource`).
  Note the shipped codebase uses 2-segment `AGGREGATE.ACTION` type strings for transitions with no
  status axis (`DOCUMENT.ACCEPTED`, `DOCUMENT.REENTERED`) — this family follows that precedent.

## Dependencies

- **Stories 40-2, 40-3, 40-4 — HARD (consumers of the constants).** This story replaces their
  placeholder pins; it can be developed against their emission sites in lockstep or land just
  after, migrating the placeholders.
- **Story 4-7 (event query API)** — the retrieval surface AC3/AC6 assert against. Existing.
- **Existing (verified):** `TammaEventEmitter`, `IEventRepository`/`IAlertEventEmitter` (append +
  query only — no update/delete, hence AC8), `AgentDispatchEventTypes` (sibling mediation family),
  `AgentRunEventTypes`/`AgentTrailEventTypes` (32-5/32-6 — the name and prefix this story must
  stay clear of), the DCB conventions.
- **Feeds:** 40-4 AC9(b) (the `taskId` per index), 40-7 (stream assertions).

## Estimated Effort

3-4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
| 2026-07-24 | 1.1.0   | Review pass: catalogue renamed `AgentRunWaitEventTypes` (32-5 owns `AgentRunEventTypes`/`AGENT.RUN.*`); AC1 pins exact strings and drops the false `AGGREGATE.ACTION.STATUS` claim; AC3 adds `taskIndex`/`taskId` for 40-4's verification; new AC8 — frozen wire strings, because consolidating persisted rows is a stream migration, not a rename | Claude |
