# Implementation Plan — Story 44-4: Iterations, the Board Read Projection, and the `SprintPlan` Apply Seam

## Scope & Deliverable

When this story is done a project has iterations with a three-state lifecycle and at-most-one-active invariant; work items are committed to and carried over from them without a closed iteration ever eating work; a single `GET /api/projects/{id}/board` returns every status column — including the empty ones — ordered by `Rank` in one statement; and an accepted `SprintPlan` from 41-6 can be applied to an iteration by the same seam contract 44-3 established. It also lands the code half of the 41-6 AC3 correction: committing an item to a sprint writes `IterationId` and raises **no** Task-View entry, because there is no pending human decision in a commitment.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §1 (`Iteration`, not `Cycle`/`Sprint`), Decisions D8 (board is a query), and the **41-6 row of the boundary table** with the recommended AC3 rewording
- `docs/stories/epic-44/story-44-1/implementation-plan.md` — D4 (four tables in one migration; `iterations` ships empty), Data & Migrations (`IterationId` `SET NULL`)
- `docs/stories/epic-44/story-44-3/implementation-plan.md` — **D6 (the narrow-port technique), D7 (409 semantics), D8 (partial application), D10 (idempotence by outcome)** — this story copies all four
- `docs/stories/epic-41/story-41-6/41-6-sprint-planning.md:18-20,:32,:44-45` — what 41-6 produces, and the AC3 sentence being corrected
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md:33,:53` — `SprintPlan`'s domain rule and its `sprint-plan` wire
- `docs/stories/epic-39/story-39-19/39-19-orchestrator-chat-primary-user-interface-and-task-view.md:22,:33` — what the Task View **is** (the reason a commitment is not one)
- `apps/tamma-elsa/src/Tamma.Activities/ADL/CycleEvents.cs:44-47` — why the entity is not called `Cycle`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:20-29`; `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs:34,37,61`
- **All referenced paths exist.** NOT FOUND (41-1b's, no code): a `sprint-plan` `DocumentTypeKey` member. AC9's port makes that absence non-blocking.

## Design Decisions

- **D1 — At-most-one-active is enforced by a partial unique index *and* a transactional check.** `CREATE UNIQUE INDEX … ON iterations ("ProjectId") WHERE "Status" = 'active'` makes the invariant a database fact rather than a service convention. The service check exists too, to return `409 ITERATION_ALREADY_ACTIVE` naming the incumbent instead of surfacing a constraint-violation 500. **The index needs a migration** — see Data & Migrations for how that is handled without opening a second tenant migration.

- **D2 — Closing an iteration is a three-mode operation with `to-backlog` as the default, and terminal items never move.** The failure this prevents is the one every tracker has shipped at least once: a sprint closes and half the work becomes invisible. `move-to` requires a target in `planned` or `active` state. `leave` is offered because some teams want the historical record intact. Terminal items (`done`, `cancelled`) stay attached under every mode — moving completed work into the next sprint is how velocity reporting gets corrupted before it is even built.

- **D3 — Commitment requires `TrackerView`, not `TrackerManage`.** Iteration *structure* (create, rename, close) is everyone's calendar and is admin. Putting *your* card into *this* sprint is the normal daily action of a team member; requiring admin makes the feature unusable in exactly the mode it exists for. Same split 44-2 D3 draws between project structure and work-item CRUD.

- **D4 — The board is ONE statement, using a window function, and the per-column limit is inside it.** The naive shapes both fail: N queries (one per column) is N round trips that grow with the vocabulary; one unbounded query sliced in the API pulls the whole project to return 7 × 50 rows. So:
  ```sql
  SELECT * FROM (
    SELECT w.*, ROW_NUMBER() OVER (PARTITION BY <groupKey> ORDER BY w."Rank") AS rn,
                COUNT(*)     OVER (PARTITION BY <groupKey>)                   AS total
    FROM work_items w WHERE w."ProjectId" = @p AND <filters>
  ) t WHERE t.rn <= @limit;
  ```
  `<groupKey>` is chosen from a closed set (D5), never interpolated from input. One statement, regardless of column count — AC5's test asserts it.

- **D5 — `groupBy` is a `[Wire]` enum, not a column name.** `status | assignee | kind | priority | iteration`. A free-text `groupBy` is a SQL-injection surface even parameterised (it is an identifier, not a value), and it admits query shapes with no index. The enum maps to a fixed `switch` over column expressions. Unknown values are `400` naming the accepted set — the `EnumWire` ordinal posture (44-0).

- **D6 — `groupBy=status` returns all seven columns in enum order, including empty ones; `groupBy=assignee` is capped.** Status is a closed vocabulary, so the complete skeleton is knowable server-side and the client must never synthesise it — a board whose "Blocked" column vanishes when nothing is blocked is unusable, and every client would reimplement the same fill-in. Assignee is unbounded, so it is capped at 20 columns by item count plus a distinguished `unassigned` column, with the remainder folded into `other`; the cap and the fold are on the wire so 44-6 can render them honestly rather than pretending the board is complete.

- **D7 — The `SprintPlan` seam is a copy of 44-3's, deliberately, down to the outcome vocabulary.** Same narrow port (`ISprintPlanReader` → `SprintPlanView`), same `409` type/status rejection with two named codes, same partial application with a per-entry outcome list, same idempotence-by-outcome with no marker column. Two seams with different error semantics for the same user-visible operation ("apply an accepted planning document") would be a support cost with no upside. The one addition is a fifth outcome, `already-committed-elsewhere` (D8).

- **D8 — `already-committed-elsewhere` is a distinct outcome, not a silent reassignment.** An item already committed to a *different* iteration is moved, but reported distinctly — a scrum master applying a plan needs to see that it pulled three cards out of the active sprint. Reporting it as plain `applied` hides a consequential change inside a success count.

- **D9 — The apply seam raises no Task-View entry, and a test proves it. This is the 41-6 AC3 correction, in code.** 41-6 as drafted says committed items "produce role-scoped Task View entries via 39-20" (`41-6:45`). The Task View is a projection of *suspended workflow decisions* — its four task types are `acceptance_decision | review | approval | clarification`, each backed by a 39-8 bookmark (39-19 plan `:88`). A sprint commitment has no bookmark and no decision: nobody can "resolve" it, so a row for it would sit in the inbox permanently or need a fifth, action-less task type. AC10's test inspects the post-apply event stream and asserts only `ITERATION.*` / `WORKITEM.*`, never `TASK.*`.
  **The docs half of this correction is a one-line edit in `docs/stories/epic-41/story-41-6/` and is recommended in the epic README, not performed by this story** — Epic 44 does not edit Epic 41's files.

- **D10 — `CapacityPoints` is stored and surfaced but enforces nothing.** 41-1b's `SprintPlan` rule is "committed set ≤ stated capacity", which is the *document's* validator, not the tracker's. Enforcing it here would mean the tracker rejects a commitment that an accepted document contains — two authorities on the same rule. The field exists so the check has somewhere to read from if someone later wants it; the summary endpoint reports the numbers side by side and lets a human draw the conclusion.

- **D11 — No `boards` table, no saved views.** `groupBy=status` answers everything a column definition would encode, and a stored board config is a schema whose only reader is a UI that does not exist yet. `tracker_preferences.BoardGroupBy` (44-1) holds one default per principal, which is the entire persisted board state in v1.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Tracking/BoardGroupBy.cs`** — the five-member `[Wire]` enum (D5) plus extensions. Core, beside 44-0's vocabularies, because 44-1's `tracker_preferences.BoardGroupBy` column stores its wire.

2. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/IterationService.cs`** — CRUD, the `planned → active → closed` transition table, D1's at-most-one-active check, D2's three-mode close, and the summary aggregate.

3. **CREATE `Tamma.Api/Services/Tracker/BoardProjectionService.cs`** — D4's single window-function query, D6's skeleton fill and assignee cap, keyset cursors per column.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Data/Repositories/IterationRepository.cs`** (created empty-ish by 44-1) — `ListByProjectAsync`, `SetStatusAsync`, `CarryOverAsync(mode, targetId?)`, `SummaryAsync`.

5. **MODIFY `WorkItemRepository`** — `BoardQueryAsync(BoardQuery)` (raw SQL, D4) and `BulkSetIterationAsync(ids, iterationId, expectedVersions)`.

6. **CREATE `Tamma.Api/Services/Tracker/ISprintPlanReader.cs` + `DocumentSprintPlanReader.cs`** — 44-3 D6's port shape:
   ```csharp
   public sealed record SprintPlanView(Guid DocumentId, int Revision, IReadOnlyList<SprintPlanEntry> Entries);
   public sealed record SprintPlanEntry(string IssueId, string? OwnerRole, decimal? Estimate, bool CarryOver);
   ```
   Matches `DocumentType` by the `"sprint-plan"` wire string and requires `Status == accepted`.

7. **CREATE `Tamma.Api/Services/Tracker/SprintPlanApplyService.cs`** — D7/D8, reusing `OrderingApplyService`'s outcome-list shape (extract the shared outcome record into `Tamma.Api/Services/Tracker/ApplyOutcome.cs` and have 44-3's service use it too — a small refactor inside the same epic, called out in the shared-edit register).

8. **MODIFY `Tamma.Api/Endpoints/TrackerEndpoints.cs`** — iteration CRUD, commitment, board, summary, apply-plan.

9. **MODIFY `Program.cs`** — map the routes in the 44-2 group with the AC1/AC3 policy split; `AddScoped` the three new services and the reader.

10. **MODIFY `TrackerActionDescriptors.cs`** — entries for iteration create/patch/delete, commit/uncommit, and `apply-plan` (the highest `DefaultMinAutonomy` of the group — it moves an entire sprint's worth of commitments).

11. **CREATE tests** under `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/`.

## Data & Migrations

**One index, and it goes into 44-1's migration if that has not shipped.** D1 needs `CREATE UNIQUE INDEX ix_iterations_one_active ON iterations ("ProjectId") WHERE "Status" = 'active'`.

- **If 44-1 has not been deployed anywhere**, add it to `AddTrackerCore` and regenerate. Preferred, and the sequencing makes it likely — 44-1 and 44-4 are in the same epic and the tenant-migration sweep is an operator action per deploy (44-1 D2).
- **If 44-1 has shipped**, this story adds a second tenant migration `AddIterationActiveIndex` and the operator runs the sweep again. Recorded so the decision is explicit rather than discovered at review.

No column is added by this story under either path.

## Events

None emitted here — 44-5 owns emission. Its catalogue reserves, from this story:
`ITERATION.CREATED.SUCCESS`, `ITERATION.STARTED.SUCCESS`, `ITERATION.CLOSED.SUCCESS` (data: `carryOverMode`, `movedCount`, `leftCount`), `WORKITEM.COMMITTED.SUCCESS`, `WORKITEM.UNCOMMITTED.SUCCESS`, `ITERATION.PLAN_APPLIED.SUCCESS` (data: `documentId`, `revision`, `applied`, `notFound`, `wrongProject`, `alreadyCommittedElsewhere`), `ITERATION.PLAN_APPLIED.FAILED`.

**Explicitly not emitted:** `CYCLE.*` (ADL's, `CycleEvents.cs:44-47`), `SPRINT.*` (41-6's document lifecycle), `TASK.*` (39-20's). AC10 tests the last one.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `IterationTests.Only_one_active_per_project` | second activate → `409`; the partial index also rejects a direct write |
| 2 | `IterationTests.Concurrent_activation_races_cleanly` | two parallel activates → exactly one succeeds — **AC2** |
| 3 | `IterationTests.Closed_cannot_reopen` | `409` |
| 4 | `IterationTests.Close_to_backlog_accounts_for_every_item` | non-terminal → `IterationId = null`; terminal untouched — **AC4** |
| 5 | `IterationTests.Close_move_to_requires_an_open_target` | rejects a `closed` target |
| 6 | `IterationTests.Close_leave_keeps_attachment` | third mode |
| 7 | `IterationTests.Delete_never_deletes_work` | `SET NULL` proven at the DB level |
| 8 | `BoardTests.Status_board_returns_all_seven_columns_in_enum_order` | including empties — **AC7** |
| 9 | `BoardTests.One_statement_regardless_of_column_count` | statement counter, 2 vs 7 vs 20 columns — **AC5** |
| 10 | `BoardTests.Items_are_rank_ordered_within_a_column` | SQL order, `COLLATE "C"` in play |
| 11 | `BoardTests.Per_column_limit_and_hasMore_are_correct` | 60 items, limit 50 → `hasMore`, cursor pages the rest |
| 12 | `BoardTests.Assignee_board_caps_and_folds` | 30 assignees → 20 + `unassigned` + `other`, cap on the wire — **D6** |
| 13 | `BoardTests.Unknown_groupBy_is_400_naming_the_set` | **D5** |
| 14 | `SummaryTests.Counts_and_estimate_sums_are_sql_computed` | constant statement count |
| 15 | `ApplyPlanTests.Rejects_non_accepted_and_wrong_type` | two named `409`s |
| 16 | `ApplyPlanTests.Commits_the_documents_entries` | `IterationId` set for all resolvable |
| 17 | `ApplyPlanTests.Reports_already_committed_elsewhere_distinctly` | **D8** |
| 18 | `ApplyPlanTests.Second_apply_is_a_no_op` | idempotence by outcome |
| 19 | `ApplyPlanTests.Raises_no_task_view_entry_and_no_document_event` | event stream contains only `ITERATION.*`/`WORKITEM.*` — **AC10 / D9, the 41-6 correction** |
| 20 | `ApplyPlanTests.Rejects_when_no_document_exists` | pre-41-1b state is a clean rejection |
| 21 | `TrackerCatalogDescriptorTests.New_routes_have_descriptors` | extends 44-2 test 20 |

Tests 1–12, 14–19 are Testcontainers.

## Definition of Done

- 21 tests green.
- `DocumentTypeKey` unchanged at ten members; `DocumentTypeKeyTests.cs:20` and `DocumentTypeRegistryTests.cs:37` unmodified (44-3 D6's rule, restated).
- No file under `docs/stories/epic-41/` modified.
- Grep confirms no `CYCLE.*` or `SPRINT.*` constant is introduced.
- `ApplyOutcome` is one shared record used by both apply seams (step 7), not two copies.
- The Data & Migrations path taken (index folded into `AddTrackerCore`, or a second migration) is recorded in the PR description.

## Dependencies & Sequencing

- **Blocked by:** 44-0, 44-1, 44-2, **44-3** (the seam contract and the outcome record it extracts).
- **Blocks:** 44-6 (the board projection is what it renders), 44-9 (the dogfood import maps epics to iterations).
- **Non-blocking:** 41-1b / 41-6.
- **Shared-edit register:** `TrackerEndpoints.cs`, `Program.cs` tracker group, `TrackerActionDescriptors.cs`, `WorkItemRepository.cs` — shared with 44-3; **sequence after it, do not parallelise**. Step 7 refactors 44-3's `OrderingApplyService` to the shared `ApplyOutcome`; land 44-3 first.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The one-active index arrives as a second tenant migration**, forcing a second operator sweep. | Data & Migrations names both paths and prefers folding into `AddTrackerCore`; the PR records which was taken. |
| **The board query is written as N queries** because it is the obvious implementation. | D4 specifies the window function; test 9 counts statements at three column counts and fails the obvious version. |
| **Closing an iteration loses work.** The single highest-consequence bug this story can ship. | D2's explicit three modes, `to-backlog` default, terminal items pinned; tests 4–7 cover every mode plus the DB-level `SET NULL`. |
| **`already-committed-elsewhere` gets collapsed into `applied`** by an implementer optimising the outcome list. | D8 states the reason; test 17 pins it. |
| **Someone implements 41-6's AC3 as written** and adds a fifth Task-View task type. | D9 argues it; test 19 asserts no `TASK.*` event; the epic README recommends the docs correction so the two do not diverge again. |
| **`SprintPlan`'s body shape is guessed** (41-1b is `drafted`). | Same containment as 44-3 D6: one interface, one record, one parse method. Entries address items by `issueId`, which 41-1b cannot change without breaking its own lineage AC. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–2, 4 (iteration lifecycle, carry-over, summary) | 1.25 |
| Steps 3, 5 (board projection — the single query, skeleton, caps, cursors) | 1.0 |
| Steps 6–7 (reader port, apply service, shared `ApplyOutcome` refactor) | 0.75 |
| Steps 8–10 (endpoints, mapping, DI, descriptors) | 0.25 |
| Step 11 (21 tests, incl. a concurrency race and a statement-count benchmark) | 0.5 |
| Review | 0.25 |
| **Total** | **4.0** |
