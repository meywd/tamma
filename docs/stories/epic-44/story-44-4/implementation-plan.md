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
- ~~**All referenced paths exist.** NOT FOUND (41-1b's, no code): a `sprint-plan` `DocumentTypeKey` member. AC9's port makes that absence non-blocking.~~ **[WRONG — CORRECTED 2026-08-01; see Amendment A3.]** The `sprint-plan` member **is** in code: `Tamma.Core/Documents/DocumentTypeKey.cs:41`. Nothing is missing. Read these instead of guessing:
  - `apps/tamma-elsa/src/Tamma.Core/Documents/Types/SprintPlan.cs:12-41` — the shipped record trio; `:51-81` — the validator's ten named violation codes; `:208-225` — the render contract the LLM is given; `:227-259` — the two shipped examples
  - `apps/tamma-elsa/src/Tamma.Core/Tracking/WorkItemStatus.cs:37-44` and `tests/Tamma.Core.Tests/Tracking/WorkItemStatusTests.cs:20-26` — the **eight**-member status vocabulary and its pin (D6)
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/TrackerRbacTests.cs:325,329-332` — the mutating-route count and the `AutonomyDial.Min` assertion that constrain step 10
  - `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs:418-460` — where tracker descriptors actually live
  - `docs/stories/epic-44/story-44-3/implementation-plan.md` and `…/44-3-*.md` **as amended 2026-08-01** — 44-3's own scoping round moved several of the facts this plan was written against

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

- **D6 — `groupBy=status` returns all ~~seven~~ *[CORRECTED 2026-08-01 → **eight**; Amendment A1]* columns in enum order, including empty ones; `groupBy=assignee` is capped.** Status is a closed vocabulary, so the complete skeleton is knowable server-side and the client must never synthesise it — a board whose "Blocked" column vanishes when nothing is blocked is unusable, and every client would reimplement the same fill-in. Assignee is unbounded, so it is capped at 20 columns by item count plus a distinguished `unassigned` column, with the remainder folded into `other`; the cap and the fold are on the wire so 44-6 can render them honestly rather than pretending the board is complete.
  **Build the skeleton by iterating `Enum.GetValues<WorkItemStatus>()`** (8 members, `Tamma.Core/Tracking/WorkItemStatus.cs:37-44`, pinned by `WorkItemStatusTests.cs:20`) — never from a literal list and never from a hard-coded count. Two copies of the vocabulary is how the seven/eight error got written in the first place.

- **D7 — The `SprintPlan` seam is a copy of 44-3's, deliberately, down to the outcome vocabulary.** Same narrow port, same `409` type/status rejection with two named codes, same partial application with a per-entry outcome list, same idempotence-by-outcome with no marker column. Two seams with different error semantics for the same user-visible operation ("apply an accepted planning document") would be a support cost with no upside. The one addition is a fifth outcome, `already-committed-elsewhere` (D8).
  **[CORRECTED 2026-08-01 — Amendment A4.]** The port is `ISprintPlanReader` returning the **shipped** `Tamma.Core.Documents.Types.SprintPlan`. It does **not** return a `SprintPlanView` that re-describes the entries — 44-3's amended D6/AC9 took exactly this correction for `BacklogOrdering`, and the two seams staying "a copy of each other" now means copying that, not the pre-amendment shape. The port's job is the three things a fixture must be able to stand in for: the tenant-scoped read, the type + `accepted` gate, and substitutability in tests.

- **D8 — `already-committed-elsewhere` is a distinct outcome, not a silent reassignment.** An item already committed to a *different* iteration is moved, but reported distinctly — a scrum master applying a plan needs to see that it pulled three cards out of the active sprint. Reporting it as plain `applied` hides a consequential change inside a success count.

- **D9 — The apply seam raises no Task-View entry. This is the 41-6 AC3 correction, in code.** 41-6 as drafted says committed items "produce role-scoped Task View entries via 39-20" (`41-6:45`). The Task View is a projection of *suspended workflow decisions* — its four task types are `acceptance_decision | review | approval | clarification`, each backed by a 39-8 bookmark (39-19 plan `:88`). A sprint commitment has no bookmark and no decision: nobody can "resolve" it, so a row for it would sit in the inbox permanently or need a fifth, action-less task type. The reasoning stands unchanged; **the test that was supposed to prove it did not** — see below.
  ~~AC10's test inspects the post-apply event stream and asserts only `ITERATION.*` / `WORKITEM.*`, never `TASK.*`.~~ **[UNFALSIFIABLE — CORRECTED 2026-08-01; story Amendment A2.]** That assertion ranges over an empty set: this story emits nothing (see the Events section, and the tracker as shipped references no `IEventRepository` in `TrackerEndpoints.cs` or `TrackerService.cs`), and there is no Task View to inspect — 39-19/39-20 are both `ready-for-dev` (`docs/sprint-status.yaml:575-576`) and no `TaskView` type or bare `TASK.*` constant exists under `apps/tamma-elsa/src/`. The replacement, per the rewritten AC10, is two assertions that can fail today:
  1. **Zero-emission delta** — `domain_events` row count for the tenant is unchanged across a successful apply. Red the moment anyone emits from this path instead of routing through 44-5. Explicitly temporary: 44-5 replaces it when it lands emission.
  2. **Structural isolation** — no file under `Tamma.Api/Services/Tracker/` references `ITaskAudienceResolver` (`Api/Services/Access/ITaskAudienceResolver.cs`, 39-18's fail-closed stub), `ChannelAudience`, or a `"TASK."` literal. This is what actually catches an implementer following 41-6 AC3 as written, because resolving a per-item audience requires that import.
  **When 44-5 makes clause 1 obsolete**, its behavioural successor must match event types by **ordinal prefix**, never `Contains("TASK.")` — `AGENT.TASK.*` exists (Story 32-6's agent trail, `Api/Services/Agents/AgentTrailEventTypes.cs:12-14`) and would false-positive.
  **The docs half of this correction is a one-line edit in `docs/stories/epic-41/story-41-6/` and is recommended in the epic README (`docs/stories/epic-44/README.md:393`), not performed by this story** — Epic 44 does not edit Epic 41's files.

- **D10 — `CapacityPoints` is stored and surfaced but enforces nothing.** 41-1b's `SprintPlan` rule is "committed set ≤ stated capacity", which is the *document's* validator, not the tracker's. Enforcing it here would mean the tracker rejects a commitment that an accepted document contains — two authorities on the same rule. The field exists so the check has somewhere to read from if someone later wants it; the summary endpoint reports the numbers side by side and lets a human draw the conclusion.

- **D11 — No `boards` table, no saved views.** `groupBy=status` answers everything a column definition would encode, and a stored board config is a schema whose only reader is a UI that does not exist yet. `tracker_preferences.BoardGroupBy` (44-1) holds one default per principal, which is the entire persisted board state in v1.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Tracking/BoardGroupBy.cs`** — the five-member `[Wire]` enum (D5) plus extensions. Core, beside 44-0's vocabularies, because 44-1's `tracker_preferences.BoardGroupBy` column stores its wire.

2. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/IterationService.cs`** — CRUD, the `planned → active → closed` transition table, D1's at-most-one-active check, D2's three-mode close, and the summary aggregate.

3. **CREATE `Tamma.Api/Services/Tracker/BoardProjectionService.cs`** — D4's single window-function query, D6's skeleton fill and assignee cap, keyset cursors per column.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Data/Repositories/IterationRepository.cs`** (created by 44-1 — 108 lines, not a stub) — ~~`ListByProjectAsync`,~~ *[already shipped: `IIterationRepository.cs:13`, impl `IterationRepository.cs:31-40`, ordered `StartsOn` then `Name`. So are `GetAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`. Extend the interface, do not redeclare it.]* `SetStatusAsync`, `CarryOverAsync(mode, targetId?)`, `SummaryAsync`.

5. **MODIFY `WorkItemRepository`** — `BoardQueryAsync(BoardQuery)` (raw SQL, D4) and `BulkSetIterationAsync(ids, iterationId, expectedVersions)`.

6. **CREATE `Tamma.Api/Services/Tracker/ISprintPlanReader.cs` + `DocumentSprintPlanReader.cs`.**
   ~~44-3 D6's port shape:~~
   ~~```csharp~~
   ~~public sealed record SprintPlanView(Guid DocumentId, int Revision, IReadOnlyList<SprintPlanEntry> Entries);~~
   ~~public sealed record SprintPlanEntry(string IssueId, string? OwnerRole, decimal? Estimate, bool CarryOver);~~
   ~~```~~
   **[WRONG SHAPE — CORRECTED 2026-08-01; story Amendment A4.]** `SprintPlanEntry` does not exist and **no shipped type carries a `bool CarryOver` field**. The document splits the two sets into separate lists, and a carry-over entry carries a `Reason` string a boolean cannot represent. Declaring the guessed record would have merged `committed` and `carryOver` into one list and committed unfinished work into the new iteration.

   **The real shape** (`Tamma.Core/Documents/Types/SprintPlan.cs`):
   ```csharp
   // :35-41
   public sealed record SprintPlan {
       string SprintId;                              // "sprintId"   :37
       decimal? Capacity;                            // "capacity"   :38
       IReadOnlyList<SprintCommittedItem> Committed; // "committed"  :39
       IReadOnlyList<SprintCarryOverItem> CarryOver; // "carryOver"  :40
   }
   public sealed record SprintCommittedItem { string IssueId; string OwnerRole; decimal? Estimate; } // :12-17
   public sealed record SprintCarryOverItem { string IssueId; string Reason; }                       // :23-27
   ```

   So the reader:
   - reads `IDocumentInstanceRepository.GetByIdAsync(tenantId, documentId, ct)` (`:40`);
   - requires `DocumentType == DocumentTypeKey.SprintPlan.ToWire()` (`DocumentInstance.cs:34`; the wire is `"sprint-plan"`, `DocumentTypeKey.cs:41`) and `Status == DocumentInstanceStatus.Accepted.ToWire()` (`DocumentInstance.cs:61`; wire `"accepted"`, `Store/DocumentInstanceStatus.cs:25`) — two separately-named `409`s;
   - deserializes `DocumentInstance.BodyJson` (`:92`) into the shipped `SprintPlan` with `DocumentJson.Options`;
   - returns that record plus `DocumentInstance.Revision` (`:55`) for the response/audit, and **declares no entry record of its own**.

   **Apply `Committed` only.** `CarryOver` is read for the response's audit line and nothing else. **Re-validate nothing** — `SprintPlanDocumentType` (`:51-195`) already enforced the ten named codes (`:54-81`) before the document could reach `accepted`; a second capacity check here would contradict D10.

7. **CREATE `Tamma.Api/Services/Tracker/SprintPlanApplyService.cs`** — D7/D8. ~~reusing `OrderingApplyService`'s outcome-list shape (extract the shared outcome record into `Tamma.Api/Services/Tracker/ApplyOutcome.cs` and have 44-3's service use it too — a small refactor inside the same epic, called out in the shared-edit register).~~ **[SCOPE CORRECTED 2026-08-01 — story Amendment A6.]** This is **not** a reuse; it is a refactor of 44-3's just-landed code that 44-4 owns and 44-3 does not know about. 44-3's plan (step 5) creates `OrderingApplyService.cs` with a three-value outcome vocabulary (`applied | not-found | wrong-project`, its D8) and **names no `ApplyOutcome` type**; its shared-edit register as amended 2026-08-01 (`story-44-3/implementation-plan.md:199`) does not list `ApplyOutcome.cs` either. Therefore:
   - **44-4 creates `Tamma.Api/Services/Tracker/ApplyOutcome.cs`**, moves 44-3's outcome shape onto it, and adds the fifth value `already-committed-elsewhere`.
   - The addition must be **additive on the wire** — 44-3's tests pin the three outcomes it emits, and the ordering seam must keep emitting exactly those three. A shared type whose extra member leaks into 44-3's responses breaks 44-3's tests, which is the failure mode to watch for in review.
   - **This step is unwritable before 44-3 lands.** Do not start it against an imagined `OrderingApplyService`.

8. **MODIFY `Tamma.Api/Endpoints/TrackerEndpoints.cs`** — iteration CRUD, commitment, board, summary, apply-plan.

9. **MODIFY `Program.cs`** — map the routes in the 44-2 group with the AC1/AC3 policy split; `AddScoped` the three new services and the reader.

10. ~~**MODIFY `TrackerActionDescriptors.cs`** — entries for iteration create/patch/delete, commit/uncommit, and `apply-plan` (the highest `DefaultMinAutonomy` of the group — it moves an entire sprint's worth of commitments).~~ **[FILE DOES NOT EXIST, AND THE THRESHOLD IS FORBIDDEN — CORRECTED 2026-08-01; story Amendment A5.]** Two independent errors in one line.

    **(a) The target file.** `TrackerActionDescriptors.cs` is nowhere in the tree. 44-2's own plan already recorded that it was never created, and 44-3's 2026-08-01 amendment (its A1) corrected the identical sentence. The descriptors live in **two Core files**:
    - **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/ExternalEffect.cs`** — one `[Wire]` member per new mutating route, in the 44-2 block (`:172-220`). Wires **must** begin `tracker.` — `TrackerRbacTests.cs:320` filters the catalog with `StartsWith("tracker.", StringComparison.Ordinal)`, so anything else is invisible to the harness. Suggested: `tracker.iteration.create`, `tracker.iteration.update`, `tracker.iteration.delete`, `tracker.iteration.commit`, `tracker.iteration.uncommit`, `tracker.iteration.apply-plan`.
    - **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs`** — one `Effect(…)` row per member beside 44-2's ten (`:441-460`), `ActionGroup.IssueTracking`. **`SiteKey` must be `"{METHOD} {live route pattern}"` verbatim including `:guid`** — `GovernedEndpointBindingSweepTests` compares it ordinally against the endpoint's `RawText`, and 44-2 shipped this exact bug once (its MODERATE-5 correction, recorded in the file at `:434-439`). `ActionRisk`: `Mutating` for create/update/commit/uncommit/apply-plan; **`Destructive` + `reversible: false` for iteration delete** — it is the one route here that can detach a whole sprint's worth of `IterationId`s via the FK's `SET NULL`, matching how 44-2 graded its two deletes (`:445`, `:451`).

    **(b) `DefaultMinAutonomy` must be `AutonomyDial.Min` for all of them, `apply-plan` included.** The instruction to give `apply-plan` "the highest `DefaultMinAutonomy` of the group" is forbidden by a currently-green test: `TrackerRbacTests.cs:329-332` asserts **every** `tracker.*` descriptor satisfies `d.DefaultMinAutonomy == AutonomyDial.Min`. That is also the `Effect(…)` helper's default parameter (`ActionCatalog.Descriptors.cs:58`, `int min = AutonomyDial.Min`) and the posture the file states in prose at `:428-429` ("MinAutonomy = AutonomyDial.Min throughout (behaviour-preserving, epic decision D1: nothing gates these today)"). **Blast radius is expressed by the route's policy, not by the dial** — `apply-plan` requires `TrackerManage` (step 9), matching how 44-3 resolved the same conflict for `apply-ordering`.

10b. **MODIFY the count pins in the same commit** — all are hard failures otherwise, and **each baseline is 44-3's post-land value, not the number written in the file today**:
    - `ActionVocabularyCountTests.cs:80` — `Enum.GetValues<ExternalEffect>().Should().HaveCount(39)`; 44-3 takes it to 42; this story adds its own members on top.
    - `ActionVocabularyCountTests.TotalCatalogMembers_is_197` (`:132`, `:147-148`) — 44-3 takes it to 200; add this story's count and update the derivation comment in the file's established style (`:134`).
    - `TrackerRbacTests.cs:325` — `mutating.Should().HaveCount(10, "AC2's ten mutating tracker routes")`; 44-3 takes it to 13; reword the reason string rather than leaving it saying "ten".
    - `KnownUngovernedEndpoints.PinnedInScopeCount` (`:157`) — 237 today, 240 after 44-3.
    - **`PinnedCount` (`:128`) and `PinHistory` (`:142`) are NOT touched** — story AC12: the ratchet is strictly shrink-only, so new routes must be `.Governs`-bound, never baselined.

11. **CREATE tests** under `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/`.

## Data & Migrations

~~**One index, and it goes into 44-1's migration if that has not shipped.**~~ **[THE PREFERRED PATH IS CLOSED — CORRECTED 2026-08-01; story Amendment A7.]** D1 still needs `CREATE UNIQUE INDEX ix_iterations_one_active ON iterations ("ProjectId") WHERE "Status" = 'active'`. What changed is that only one of the two branches is available.

~~- **If 44-1 has not been deployed anywhere**, add it to `AddTrackerCore` and regenerate. Preferred, and the sequencing makes it likely — 44-1 and 44-4 are in the same epic and the tenant-migration sweep is an operator action per deploy (44-1 D2).~~

**Why it is closed.** `AddTrackerCore` has landed as `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/20260729035027_AddTrackerCore.cs` (44-1 is `done`, `docs/sprint-status.yaml:771`), and it is **no longer the last tenant migration**: `20260729070033_AddDocumentInstanceAudience.cs` was applied after it the same day. Editing `AddTrackerCore` in place and regenerating would mean rewriting a migration that a successor's `Designer` and the `TenantDbContextModelSnapshot` are both built on top of, and any environment that has already applied the successor will never re-run the predecessor — so the index would silently not exist exactly where it matters. "Fold it into 44-1" is not a decision that is still open.

**The path this story takes** (the surviving branch, now the only branch): **add a second tenant migration `AddIterationActiveIndex`** and the operator runs the migrate-all-provisioned-tenants sweep again (44-1's sweep, `docs/sprint-status.yaml:771`). Budget for it explicitly — 44-1 D4 calls tenant migrations the scarcest resource in the repo, and this story spends one.

**What `AddTrackerCore` already gave the `iterations` table**, so the new migration adds only the index and nothing else (`20260729035027_AddTrackerCore.cs`):
- the table itself (`:63`) with `Id`/`ProjectId`/`Name`/`StartsOn`/`EndsOn`/`Status`/`CapacityPoints`/`Version` — entity at `Tamma.Data/Entities/IterationEntity.cs`
- `PK_iterations` (`:79`), the FK to `projects` (`:82`)
- `ck_iterations_status` — `"Status" IN ('planned','active','closed')` (`:80`), so the three-state vocabulary is already a database fact; the transition *table* is this story's service code
- `UX_iterations_project_name` (`:176`) — name uniqueness per project
- on `work_items`: `IterationId` (`:105`) and `FK_work_items_iterations_IterationId` (`:126-128`) with the `SET NULL` behaviour AC4 depends on, plus its index (`:213`)
- **No one-active partial index** — grep confirms `ix_iterations_one_active` appears nowhere in `apps/tamma-elsa/`. D1's index is genuinely new work.

No column is added by this story.

## Events

None emitted here — 44-5 owns emission. Its catalogue reserves, from this story:
`ITERATION.CREATED.SUCCESS`, `ITERATION.STARTED.SUCCESS`, `ITERATION.CLOSED.SUCCESS` (data: `carryOverMode`, `movedCount`, `leftCount`), `WORKITEM.COMMITTED.SUCCESS`, `WORKITEM.UNCOMMITTED.SUCCESS`, `ITERATION.PLAN_APPLIED.SUCCESS` (data: `documentId`, `revision`, `applied`, `notFound`, `wrongProject`, `alreadyCommittedElsewhere`), `ITERATION.PLAN_APPLIED.FAILED`.

**Explicitly not emitted:** `CYCLE.*` (ADL's, `CycleEvents.cs:44-47`), `SPRINT.*` (41-6's document lifecycle), `TASK.*` (39-20's — **which does not exist yet**: 39-20 is `ready-for-dev`, `docs/sprint-status.yaml:576`, and no bare `TASK.*` constant is in `apps/tamma-elsa/src/`). ~~AC10 tests the last one.~~ **[CORRECTED 2026-08-01 — A2.]** AC10 as drafted could not test it: **nothing at all is emitted by this story**, so an assertion about which event types appear was vacuous. What AC10 now pins is the emptiness itself (test 19, delta 0) plus a structural bar on importing the task plane (test 19b). The `TASK.*` question becomes testable only when 44-5 lands emission, and 44-5 owns it. **Note for whoever writes that test:** `AGENT.TASK.*` is a live, unrelated family (Story 32-6's agent trail, `Api/Services/Agents/AgentTrailEventTypes.cs:12-14`), so match by ordinal prefix on the full event type — a `Contains("TASK.")` filter is wrong.

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
| 8 | ~~`BoardTests.Status_board_returns_all_seven_columns_in_enum_order`~~ **`BoardTests.Status_board_returns_every_status_column_in_enum_order`** *[RENAMED 2026-08-01 — A1: the old name asserted seven and there are eight, so the test as specified was red on day one and the cheap way to make it pass was to break the board. The new name carries no number; the body compares against `Enum.GetValues<WorkItemStatus>()`, so it cannot drift from `WorkItemStatusTests.cs:20` again.]* | every member present in enum order, including empties — **AC7** |
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
| 19 | ~~`ApplyPlanTests.Raises_no_task_view_entry_and_no_document_event`~~ **`ApplyPlanTests.Apply_writes_no_domain_event`** *[REPLACED 2026-08-01 — A2: the original asserted a property of an empty set. See D9.]* | `domain_events` count for the tenant is unchanged across a successful apply (delta 0) — **AC10a**. Temporary pin; 44-5 replaces it with the behavioural assertion when emission lands |
| 19b | **`TrackerSourceIsolationTests.Tracker_services_do_not_reference_the_task_plane`** *(new 2026-08-01)* | no file under `src/Tamma.Api/Services/Tracker/` mentions `ITaskAudienceResolver`, `ChannelAudience`, or a `"TASK."` literal — **AC10b / D9, the 41-6 correction**. Not Testcontainers; a source scan |
| 20 | `ApplyPlanTests.Rejects_when_no_document_exists` | ~~pre-41-1b state is~~ *[A3: 41-1b is `done`; the pre-condition being tested is a missing/unaccepted **document instance**, not a missing type]* a clean rejection |
| 20b | **`ApplyPlanTests.CarryOver_entries_are_not_committed`** *(new 2026-08-01)* | a fixture with non-empty `carryOver` — none of those `issueId`s acquire an `IterationId` — **AC9 / A4**. This is the test that would have caught the guessed `bool CarryOver` shape |
| 20c | **`SprintPlanReaderTests.No_tracker_type_redeclares_the_document_shape`** *(new 2026-08-01)* | no type under `Services/Tracker/` declares `sprintId`/`committed`/`carryOver`/`ownerRole`/`estimate` — **AC9**, mirroring 44-3's equivalent |
| 21 | `TrackerCatalogDescriptorTests.New_routes_have_descriptors` | extends 44-2's `TrackerRbacTests.Every_mutating_route_has_a_descriptor` (`:296`) — **note it is bidirectional and carries a hard `HaveCount` at `:325`**, so it is edited, not merely extended |

Tests 1–12, 14–19, 20–20b are Testcontainers. Tests 19b and 20c are source/reflection scans and run without a container.

**24 tests, not 21** *(recount 2026-08-01: 19b, 20b, 20c added; test 19 replaced in place).*

## Definition of Done

- ~~21~~ **24** tests green.
- ~~`DocumentTypeKey` unchanged at ten members; `DocumentTypeKeyTests.cs:20` and `DocumentTypeRegistryTests.cs:37` unmodified~~ **[CORRECTED 2026-08-01 — A3: all three numbers were wrong.]** `DocumentTypeKey` unchanged at **17** members; **`DocumentTypeKeyTests.cs:24`** (`Be(17)`) and **`DocumentTypeRegistryTests.cs:42`** (`HaveCount(17)`) unmodified. `Types/SprintPlan.cs`, `DocumentTypeRegistry.cs` and `Policy/AcceptanceDefaults.cs` unmodified too — this story does not touch that assembly.
- No file under `docs/stories/epic-41/` modified.
- Grep confirms no `CYCLE.*` or `SPRINT.*` constant is introduced.
- `ApplyOutcome` is one shared record used by both apply seams (step 7), not two copies — **and 44-3's three outcome wires are byte-identical before and after the extraction** (the shared type gains a fifth member; 44-3's responses must not).
- **A second tenant migration `AddIterationActiveIndex` exists**, `AddTrackerCore` is unmodified, and the PR description says the sweep must be re-run. *(Superseded 2026-08-01: the old wording let the author choose between two paths; only one is available — see Data & Migrations.)*
- **Every new mutating route is `.Governs`-bound**; `KnownUngovernedEndpoints.PinnedCount` and `PinHistory` are unmodified; `PinnedInScopeCount` moved by exactly the number of new mutating routes.
- **The board's status skeleton is derived from `Enum.GetValues<WorkItemStatus>()`** — grep confirms no literal column list and no hard-coded `8` in the board code or its tests (A1).

## Dependencies & Sequencing

- **Blocked by:** 44-0 (`done`), 44-1 (`done`), 44-2 (`done`), **44-3** (`drafted`, `docs/sprint-status.yaml:773`) — ~~the seam contract and the outcome record it extracts~~ *[CORRECTED 2026-08-01 — A6: 44-3 extracts no outcome record; **this** story does, from 44-3's landed code. See story Dependencies for the three couplings.]*
- **⚠ Do not start this story before 44-3 lands.** Not a preference. Step 7 refactors a service 44-3 has not written; steps 8-9 and 10b edit the same files and the same four count pins; and the board's `WorkItemResponse` mapping is written against a positional record whose arity 44-3 AC6 changes (`TrackerDtos.cs:178-205`, `TrackerEndpoints.cs:560-566`).
- **Blocks:** 44-6 (the board projection is what it renders), 44-9 (the dogfood import maps epics to iterations).
- **Non-blocking:** ~~41-1b /~~ *[A3: `done` — a shipped input, not a dependency]* 41-6 (`drafted`, `:636`; non-blocking for the code, blocking for the feature on the `issueId` contract in story AC9).
- **Shared-edit register:** `TrackerEndpoints.cs`, `Program.cs` tracker group (`:2977-3016`), ~~`TrackerActionDescriptors.cs`~~ **`Tamma.Core/Actions/ExternalEffect.cs` + `Tamma.Core/Actions/ActionCatalog.Descriptors.cs`** *[CORRECTED 2026-08-01 — A5: that file does not exist]*, `WorkItemRepository.cs`, and **the four count-pin sites** (`ActionVocabularyCountTests.cs:80,132`; `TrackerRbacTests.cs:325`; `KnownUngovernedEndpoints.cs:157`) — all shared with 44-3. **Sequence after it, do not parallelise.**

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| ~~**The one-active index arrives as a second tenant migration**, forcing a second operator sweep.~~ **[NOT A RISK — IT IS THE PLAN, 2026-08-01, A7.]** The residual risk is that someone still tries the closed path: **editing `AddTrackerCore` in place**, breaking the successor migration's Designer and the model snapshot, and shipping an index that never reaches an environment which already ran `AddDocumentInstanceAudience`. | Data & Migrations now records only one path and states why the other is unavailable, with both migration filenames. The DoD requires `AddTrackerCore` to be **unmodified** in the diff — a reviewer can check that without reading the reasoning. |
| **The seven/eight error propagates into the board code** because the plan taught it in three places (AC7, D6, a test name). | All three corrected in place with the strike-through visible; the skeleton must be derived from `Enum.GetValues<WorkItemStatus>()` and the DoD greps for a hard-coded `8`. |
| **AC10's replacement rots into a second vacuous test** when 44-5 lands emission and clause (a)'s delta stops being 0. | Clause (a) is labelled temporary with a named successor in AC10, D9 and the test plan; 44-5's plan owns the handoff, and the successor's matching rule (ordinal prefix, never `Contains`) is written down now rather than rediscovered. |
| **The `bool CarryOver` shape resurfaces** because it is the intuitive shape and the story text was wrong for a week. | Step 6 shows the real record with line numbers, test 20b fails any implementation that commits carry-over items, and test 20c fails any redeclaration of the shape under `Services/Tracker/`. |
| **The board query is written as N queries** because it is the obvious implementation. | D4 specifies the window function; test 9 counts statements at three column counts and fails the obvious version. |
| **Closing an iteration loses work.** The single highest-consequence bug this story can ship. | D2's explicit three modes, `to-backlog` default, terminal items pinned; tests 4–7 cover every mode plus the DB-level `SET NULL`. |
| **`already-committed-elsewhere` gets collapsed into `applied`** by an implementer optimising the outcome list. | D8 states the reason; test 17 pins it. |
| **Someone implements 41-6's AC3 as written** and adds a fifth Task-View task type. | D9 argues it; ~~test 19 asserts no `TASK.*` event~~ *[A2: that assertion could not fail]* **test 19b fails on the import an audience resolution would require**; the epic README recommends the docs correction (`README.md:393`) so the two do not diverge again. |
| ~~**`SprintPlan`'s body shape is guessed** (41-1b is `drafted`). \| Same containment as 44-3 D6: one interface, one record, one parse method. Entries address items by `issueId`, which 41-1b cannot change without breaking its own lineage AC.~~ **[BOTH HALVES WRONG — 2026-08-01, A4.]** 41-1b is `done` and the shape is shipped, so nothing needs guessing — but the plan guessed anyway and got it wrong. The surviving risk is the **`issueId` join**: `SprintCommittedItem.IssueId`'s only validator rule is "not blank" (`Types/SprintPlan.cs:133-135`) and the shipped contract example uses `"issue-7"` (`:215`), not the `TAM-142` shape `WorkItemRef.ToWire()` emits (`WorkItemRef.cs:91`). The claim that 41-1b "cannot change it without breaking its own lineage AC" is unsupported — 41-1b has already shipped, and it never constrained the field. | Deserialize the shipped record; declare nothing parallel (tests 20b/20c). The join stays a **cross-story contract with 41-6** — an unresolvable `issueId` is `not-found`, an existing AC9 outcome, so the seam degrades honestly instead of guessing. Fixture-tested until 41-6 fixes the contract. |

## Effort Breakdown

*(Pre-amendment figures, kept for comparison. The 2026-08-01 scoping round adds AC12's `.Governs` binding work, four count-pin reconciliations layered on 44-3's, three new tests, and a second tenant migration that the old plan hoped to avoid. Treat 4.0 as a floor.)*

| Task | Days |
|---|---|
| Steps 1–2, 4 (iteration lifecycle, carry-over, summary) | 1.25 |
| Steps 3, 5 (board projection — the single query, skeleton, caps, cursors) | 1.0 |
| Steps 6–7 (reader port, apply service, shared `ApplyOutcome` refactor) | 0.75 |
| Steps 8–10 (endpoints, mapping, DI, descriptors) | 0.25 |
| Step 11 (~~21~~ **24** tests, incl. a concurrency race and a statement-count benchmark) | 0.5 |
| Review | 0.25 |
| **Total** | **4.0** *(floor)* |

## Amendment — 2026-08-01 (scoping round: plan vs. tree)

Checked against the working tree at commit `6429691`. Corrections are struck through in place above; this is the index.

| # | Was | Is |
|---|---|---|
| **A1** | AC7 / D6 / test 8's name all said the status board has **seven** columns. | **Eight** (`Tamma.Core/Tracking/WorkItemStatus.cs:37-44`; `WorkItemStatusTests.cs:20` pins `HaveCount(8)` and the eight wires literally). The test *name* carried the error, so the test as specified was red on day one and the cheapest way to green it was to break the board. Test renamed to carry no number; the skeleton is built from `Enum.GetValues<WorkItemStatus>()`. |
| **A2** | AC10 / D9 / test 19: "the post-apply event stream contains only `ITERATION.*`/`WORKITEM.*`, never `TASK.*`". | Unfalsifiable — this story emits nothing (Events section; no `IEventRepository` in `TrackerEndpoints.cs`/`TrackerService.cs`) and no Task View exists (39-19/39-20 `ready-for-dev`, `docs/sprint-status.yaml:575-576`; no `TaskView` type or bare `TASK.*` constant in `src/`). **Replaced, not deleted**, by a zero-emission delta pin (test 19) and a source-isolation scan (test 19b), both able to fail today. Also recorded: `AGENT.TASK.*` exists, so the eventual behavioural check must match by ordinal prefix, not `Contains`. |
| **A3** | Pre-Reading: "NOT FOUND (41-1b's, no code): a `sprint-plan` `DocumentTypeKey` member". DoD: "`DocumentTypeKey` unchanged at ten members; `DocumentTypeKeyTests.cs:20` / `DocumentTypeRegistryTests.cs:37`". | 41-1b is `done` (`docs/sprint-status.yaml:630`). The member is `DocumentTypeKey.cs:41`; the type is registered at `DocumentTypeRegistry.cs:45` with an acceptance policy at `Policy/AcceptanceDefaults.cs:216`. **17** members, and the pins are at `DocumentTypeKeyTests.cs:24` / `DocumentTypeRegistryTests.cs:42` — all four numbers in the old DoD line were wrong. |
| **A4** | Step 6 declared `SprintPlanEntry(string IssueId, string? OwnerRole, decimal? Estimate, bool CarryOver)`. | **No such type, and no shipped type carries a boolean carry-over field.** `SprintPlan` (`Types/SprintPlan.cs:35-41`) holds `Committed: IReadOnlyList<SprintCommittedItem>` and `CarryOver: IReadOnlyList<SprintCarryOverItem>` as **separate lists**; carry-over entries carry a `Reason` string (`:26`) a boolean cannot express. The guessed shape would have merged the sets and committed unfinished work. Validation is already done by `SprintPlanDocumentType` with ten named codes (`:54-81`) — this story re-validates none of it. |
| **A5** | Step 10: "MODIFY `TrackerActionDescriptors.cs` … `apply-plan` (the highest `DefaultMinAutonomy` of the group)". | The file **has never existed** (same error 44-3's A1 corrected; both inherited it from a shared draft). Descriptors live in `Core/Actions/ExternalEffect.cs:172-220` + `Core/Actions/ActionCatalog.Descriptors.cs:441-460`. And the raised threshold is **forbidden by a green test**: `TrackerRbacTests.cs:329-332` asserts every `tracker.*` descriptor is `AutonomyDial.Min` — also the `Effect(…)` helper default (`:58`) and the file's stated posture (`:428-429`). Blast radius goes on the route policy, not the dial. New step 10b lists the four count pins, and story AC12 records the `.Governs` ratchet obligation neither document mentioned. |
| **A6** | Step 7: "reusing `OrderingApplyService`'s outcome-list shape … a small refactor". Dependencies: 44-3 supplies "the outcome record it extracts". | 44-3 **names no `ApplyOutcome` type** — its plan creates `OrderingApplyService.cs` with a three-value vocabulary (its D8) and its amended shared-edit register (`story-44-3/implementation-plan.md:199`) does not list the file. So the extraction is **this** story's refactor of 44-3's landed code, and it must stay additive so 44-3's three outcome wires do not change. With the shared endpoint file, the four count pins, and `WorkItemResponse`'s positional arity (24 params, `TrackerDtos.cs:178-205`, constructed positionally at `TrackerEndpoints.cs:560-566`, which 44-3 AC6 changes), 44-3 is a **hard sequencing blocker**. |
| **A7** | Data & Migrations offered two paths and preferred folding the index into `AddTrackerCore`. | The preferred path is **closed**. `AddTrackerCore` shipped (`Migrations/Tenant/20260729035027_AddTrackerCore.cs`; 44-1 `done`, `:771`) and a later tenant migration exists (`20260729070033_AddDocumentInstanceAudience.cs`), so editing it in place breaks a successor's Designer and the model snapshot, and never reaches an environment that already ran the successor. Only the second-migration path survives; the DoD now requires `AddTrackerCore` to be unmodified in the diff. |

**Scoping-report corrections.** The brief that prompted this round was right on all four numbered problems. Two things it did **not** say, found here and recorded above: (i) the story's flat claim that *"`SprintPlan` does not exist in code"* is false — 41-1b landed the whole thing, which is why the guessed shape in A4 was avoidable; (ii) the `issueId` join is an **unfixed cross-story contract**, not the settled thing the risk table claimed, for the same reason 44-3 recorded its C2.

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-01 | 1.1.0   | Scoping round against the tree — A1 (seven → eight columns, incl. the test name), A2 (AC10/test 19 replaced; it could not fail), A3 (`SprintPlan` is shipped; four wrong numbers in the DoD), A4 (the guessed `bool CarryOver` entry shape), A5 (non-existent descriptor file + a forbidden `DefaultMinAutonomy`, plus the `.Governs` ratchet), A6 (44-3 is a hard blocker; the `ApplyOutcome` "reuse" is a refactor), A7 (migration path re-pointed). Test count 21 → 24. | Claude |
| 2026-07-25 | 1.0.0   | Initial plan | Claude |
