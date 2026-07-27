# Story 44-4: Iterations, the Board Read Projection, and the `SprintPlan` Apply Seam

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

As a **scrum master or product owner**,
I want time-boxed iterations that work items are committed to, a board query that returns a project's work grouped into status columns in one call, and a way to *apply* an accepted `SprintPlan` from Story 41-6 to an iteration,
So that sprint planning produces an actual sprint rather than an accepted document, and the board the team looks at every day is one HTTP request.

## Priority

P1 — Wave 1. The board projection is what 44-6 renders; the apply seam is the second half of the Epic 41 boundary (44-3 owns the first).

## Architectural Context (READ FIRST)

- **`iterations` already exists in the schema.** 44-1 created it (`Id`, `ProjectId`, `Name`, `StartsOn`, `EndsOn`, `Status` ∈ `planned|active|closed`, `CapacityPoints`, `Version`) in the single `AddTrackerCore` tenant migration, deliberately shipping it empty rather than forcing a second tenant migration two stories later (44-1 D4). **This story must not open a new tenant migration.**
- **`WorkItem.IterationId` is an FK with `ON DELETE SET NULL`** (44-1 Data & Migrations) — closing or deleting an iteration must never delete work.
- **The name is `Iteration`, not `Cycle` or `Sprint`.** `CYCLE.*` is taken by the ADL loop (`apps/tamma-elsa/src/Tamma.Activities/ADL/CycleEvents.cs:44-47` — `CYCLE.STARTED`, `CYCLE.STEP_FAILED`, `CYCLE.COMPLETED`, `CYCLE.FAILED`), alongside `SingleIssueCycleWorkflow`, `TriageItemCycleWorkflow`, `CycleExitReason` and `WaitForCycleCallbackActivity`. `SPRINT.*` is claimed by 41-6 for its `SprintPlan` document lifecycle. Epic README §1 records both.
- **The board is a query, not a table** (epic Decisions D8). No `boards` table is created. The precedent is `DocumentInstance`'s own doc — "a read-optimized projection of the DCB stream, rebuildable" (`apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs:7-14`) — and 39-19's D7, which builds its task inbox as an event projection with **no new table** (`docs/stories/epic-39/story-39-19/implementation-plan.md:35`, `:105-107`).
- **41-6 produces a document, not a sprint.** `docs/stories/epic-41/story-41-6/41-6-sprint-planning.md:18-20`: `consumes: [BacklogOrdering (41-3), team capacity, prior SprintPlan carry-over]` / `produces: SprintPlan`, cell `(scrum_master, plan-sprint)`. Domain rule (`docs/stories/epic-41/story-41-1/41-1b-new-document-types.md:33`): "committed set ≤ stated capacity; every committed item has an owner-role + estimate; carry-over flagged".
- **⚠ 41-6 AC3 needs narrowing, and this story is where the correction lands in code.** 41-6 currently states *"Committed items produce role-scoped Task View entries via 39-20"* (`41-6:45`; see also `:32`). The Task View is the **suspended-decision inbox** — four task types, each backed by a 39-8 bookmark (`docs/stories/epic-39/story-39-19/39-19-orchestrator-chat-primary-user-interface-and-task-view.md:33`). Committing an item to a sprint is a tracker mutation with **no pending human decision**, so it cannot be a Task-View row without inventing a fifth task type. The epic README's boundary table records the recommended AC3 rewording. This story implements the correct behaviour: commitment writes `IterationId`, and no Task-View entry is raised.
- **`SprintPlan` does not exist in code.** `DocumentTypeKey` has exactly ten members (`Tamma.Core/Documents/DocumentTypeKey.cs:22-33`) and 41-1b is `drafted`. The apply seam uses the same narrow-port technique 44-3 D6 established and **adds no `DocumentTypeKey` member and moves no count pin**.

## Acceptance Criteria

1. **Iteration CRUD.** `GET/POST /api/projects/{projectId:guid}/iterations`, `GET/PATCH/DELETE /api/iterations/{id:guid}`. `TrackerManage` for create/patch/delete (iteration structure is everyone's calendar); `TrackerView` for read.

2. **At most one `active` iteration per project**, enforced in the same transaction as the status change and proven by a concurrency test. `planned → active → closed` are the only permitted transitions; `closed → active` is rejected `409`.

3. **Commitment endpoints.** `POST /api/iterations/{id:guid}/items` (`{ workItemIds[] }`) and `DELETE /api/iterations/{id:guid}/items/{workItemId:guid}`, both bulk-safe, both rejecting items from another project. Requires `TrackerView` — committing your own work to the current sprint is not an admin action.

4. **Closing an iteration never deletes or hides work.** `PATCH /api/iterations/{id}` to `closed` accepts a `carryOver` mode: `move-to` (a named target iteration), `to-backlog` (`IterationId = null`), or `leave` (items stay attached to the closed iteration). The default is `to-backlog`. A test asserts every non-terminal item is accounted for under each mode and that terminal items (`done`/`cancelled`) are never moved.

5. **Board projection.** `GET /api/projects/{projectId:guid}/board?groupBy=status[&iterationId=…][&assigneeId=…][&kind=…]` returns ordered columns in **one round trip**: `{ columns: [{ key, label, items[], totalCount, hasMore }] }`, each column's items ordered by `Rank` in SQL, each column capped at a per-column limit with `hasMore` and a keyset cursor. A test asserts one statement regardless of column count.

6. **`groupBy` is a closed vocabulary** — `status | assignee | kind | priority | iteration` — expressed as a `[Wire]` enum, rejected loud with the accepted set on an unknown value. Not a free-text column name (that is a SQL-injection surface and an unbounded query shape).

7. **Column skeleton is complete and stable.** `groupBy=status` returns **all seven** `WorkItemStatus` columns in enum order, including empty ones. A board whose columns appear and disappear as work moves is unusable, and the client must not have to synthesise the missing ones.

8. **Iteration summary.** `GET /api/iterations/{id:guid}/summary` returning committed count, count and `Estimate` sum by **`WorkItemStatus.Category()`** (44-0 AC3 — never a hand-written status set; `Estimate` is scale-free, its scale being `Project.EstimateScale`), and `CapacityPoints`. Computed in SQL. **No velocity, burndown or forecast** — those are Epic 36's and are listed in the epic's Deferred section.

9. **Apply seam** `POST /api/iterations/{id:guid}/apply-plan` accepting `{ documentId }`, following 44-3's seam contract exactly: requires `DocumentType == "sprint-plan"` and `Status == "accepted"` (else `409` naming which failed); resolves committed entries by `issueId`; sets `IterationId` in one transaction; returns per-item outcomes (`applied | not-found | wrong-project | already-committed-elsewhere`); is idempotent.

10. **The apply seam raises no Task-View entry and emits no `DOCUMENT.*` or `TASK.*` event.** A test inspects the event stream after an apply and asserts only `ITERATION.*` / `WORKITEM.*` rows — the concrete implementation of the 41-6 AC3 correction.

11. **Catalog descriptors** for every mutating route added to `TrackerActionDescriptors` in the `issue-tracking` group.

## Technical Notes

- The board query is the single most performance-sensitive read in the epic. It is one statement using a window function partitioned by the group key and ordered by `Rank`, filtered to the per-column limit — not N queries, and not one unbounded query the API slices in memory.
- `groupBy=assignee` has an unbounded column count. It is capped (default 20 columns by item count, plus an `unassigned` column) with the remainder folded into an `other` column, and the cap is on the wire so the client can show it.
- `CapacityPoints` is stored and returned; nothing computes against it in v1. It exists so 41-6's "committed set ≤ stated capacity" rule has somewhere to read from when someone builds the check.
- `already-committed-elsewhere` is a distinct outcome from `applied` because silently stealing an item out of an active iteration is the kind of change a scrum master needs to see in the response, not discover on the board.

## Dependencies

- **Story 44-0** (`WorkItemStatus` order for AC7, `Rank`), **44-1** (the `iterations` table, `IterationId` FK, `BulkSetIterationAsync`), **44-2** (endpoint class, RBAC, DTO conventions), **44-3** (the apply-seam contract this copies, and the `IBacklogOrderingReader` port shape). All blocking.
- **Story 39-11 Document Store** (`done`) — the apply seam's read.
- **Story 41-1b / 41-6** — define and produce `SprintPlan`. **Not blocking** (narrow port, AC9).

## Out of Scope

- Adding `SprintPlan` to `DocumentTypeKey` / `DocumentTypeRegistry` — 41-1b's, with its count pins.
- Any change to 41-6's workflow or prompt cell. **This story implements the corrected behaviour; rewording 41-6's AC3 is a docs edit in Epic 41's own file and is recommended, not performed here.**
- Velocity, burndown, forecast, capacity enforcement — deferred (epic README).
- Saved board configurations, custom swimlanes, board-level filters persisted per user — deferred; `tracker_preferences.BoardGroupBy` (44-1) holds the single default and nothing more.
- Any UI — 44-6.

## Estimated Effort

4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
