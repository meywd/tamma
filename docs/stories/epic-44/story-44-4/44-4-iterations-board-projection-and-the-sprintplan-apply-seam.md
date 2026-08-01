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
- ~~**`SprintPlan` does not exist in code.** `DocumentTypeKey` has exactly ten members (`Tamma.Core/Documents/DocumentTypeKey.cs:22-33`) and 41-1b is `drafted`. The apply seam uses the same narrow-port technique 44-3 D6 established and **adds no `DocumentTypeKey` member and moves no count pin**.~~ **[WRONG — CORRECTED 2026-08-01; see Amendment A3.]** `SprintPlan` is **shipped**. The enum member is `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs:41` (`[Wire("sprint-plan")] SprintPlan`); the typed body is `Tamma.Core/Documents/Types/SprintPlan.cs:12-41` (`SprintCommittedItem` / `SprintCarryOverItem` / `SprintPlan`); the validator is `SprintPlanDocumentType` (`:51-195`), registered at `DocumentTypeRegistry.cs:45`; the acceptance policy is `s_humanProductOwnerRules` (`Policy/AcceptanceDefaults.cs:216`). `DocumentTypeKey` has **17** members, not ten (`DocumentTypeKeyTests.cs:24` — `Be(17)`; `DocumentTypeRegistryTests.cs:42` — `HaveCount(17)`). 41-1b is `done` (`docs/sprint-status.yaml:630`). The last clause survives for a different reason: this story adds no `DocumentTypeKey` member and moves no document-type pin **because there is nothing left to add** — not because it is deferring to 41-1b. Write the seam against the shipped types, not a guessed shape.

## Acceptance Criteria

1. **Iteration CRUD.** `GET/POST /api/projects/{projectId:guid}/iterations`, `GET/PATCH/DELETE /api/iterations/{id:guid}`. `TrackerManage` for create/patch/delete (iteration structure is everyone's calendar); `TrackerView` for read.

2. **At most one `active` iteration per project**, enforced in the same transaction as the status change and proven by a concurrency test. `planned → active → closed` are the only permitted transitions; `closed → active` is rejected `409`.

3. **Commitment endpoints.** `POST /api/iterations/{id:guid}/items` (`{ workItemIds[] }`) and `DELETE /api/iterations/{id:guid}/items/{workItemId:guid}`, both bulk-safe, both rejecting items from another project. Requires `TrackerView` — committing your own work to the current sprint is not an admin action.

4. **Closing an iteration never deletes or hides work.** `PATCH /api/iterations/{id}` to `closed` accepts a `carryOver` mode: `move-to` (a named target iteration), `to-backlog` (`IterationId = null`), or `leave` (items stay attached to the closed iteration). The default is `to-backlog`. A test asserts every non-terminal item is accounted for under each mode and that terminal items (`done`/`cancelled`) are never moved.

5. **Board projection.** `GET /api/projects/{projectId:guid}/board?groupBy=status[&iterationId=…][&assigneeId=…][&kind=…]` returns ordered columns in **one round trip**: `{ columns: [{ key, label, items[], totalCount, hasMore }] }`, each column's items ordered by `Rank` in SQL, each column capped at a per-column limit with `hasMore` and a keyset cursor. A test asserts one statement regardless of column count.

6. **`groupBy` is a closed vocabulary** — `status | assignee | kind | priority | iteration` — expressed as a `[Wire]` enum, rejected loud with the accepted set on an unknown value. Not a free-text column name (that is a SQL-injection surface and an unbounded query shape).

7. **Column skeleton is complete and stable.** `groupBy=status` returns ~~**all seven**~~ **all eight** *[CORRECTED 2026-08-01 — the count was wrong in v1.0.0; see Amendment A1]* `WorkItemStatus` columns in enum order, including empty ones. A board whose columns appear and disappear as work moves is unusable, and the client must not have to synthesise the missing ones.

   The eight, in enum order, are `triage`, `backlog`, `ready`, `in_progress`, `in_review`, `blocked`, `done`, `cancelled` (`Tamma.Core/Tracking/WorkItemStatus.cs:37-44`; count-pinned and wire-pinned by `WorkItemStatusTests.Member_count_is_pinned`, `tests/Tamma.Core.Tests/Tracking/WorkItemStatusTests.cs:20-26`). **Assert the skeleton against `Enum.GetValues<WorkItemStatus>()`, not against a literal `8`** — a hard-coded count here is a second pin that will drift from `WorkItemStatusTests`'s, which is the very mistake this amendment is correcting.

8. **Iteration summary.** `GET /api/iterations/{id:guid}/summary` returning committed count, count and `Estimate` sum by **`WorkItemStatus.Category()`** (44-0 AC3 — never a hand-written status set; `Estimate` is scale-free, its scale being `Project.EstimateScale`), and `CapacityPoints`. Computed in SQL. **No velocity, burndown or forecast** — those are Epic 36's and are listed in the epic's Deferred section.

9. **Apply seam** `POST /api/iterations/{id:guid}/apply-plan` accepting `{ documentId }`, following 44-3's seam contract exactly: requires `DocumentType == "sprint-plan"` and `Status == "accepted"` (else `409` naming which failed); resolves committed entries by `issueId`; sets `IterationId` in one transaction; returns per-item outcomes (`applied | not-found | wrong-project | already-committed-elsewhere`); is idempotent.

   **AC9 — consumption shape (REWRITTEN 2026-08-01; the v1.0.0 plan guessed a shape that does not exist. See Amendment A4.)** The seam **deserializes the shipped `Tamma.Core.Documents.Types.SprintPlan` record** from `DocumentInstance.BodyJson` (`Tamma.Data/Entities/DocumentInstance.cs:92`) using `DocumentJson.Options`, and **declares no parallel entry record of its own**. The shipped shape (`Types/SprintPlan.cs:12-41`) is:

   | Member | Type | Source |
   |---|---|---|
   | `SprintPlan.SprintId` | `string` (`"sprintId"`) | `:37` |
   | `SprintPlan.Capacity` | `decimal?` (`"capacity"`) | `:38` |
   | `SprintPlan.Committed` | `IReadOnlyList<SprintCommittedItem>` (`"committed"`) | `:39` |
   | `SprintPlan.CarryOver` | `IReadOnlyList<SprintCarryOverItem>` (`"carryOver"`) | `:40` |
   | `SprintCommittedItem` | `IssueId` / `OwnerRole` / `Estimate` (`decimal?`) | `:12-17` |
   | `SprintCarryOverItem` | `IssueId` / `Reason` | `:23-27` |

   **`Committed` and `CarryOver` are two separate lists, not one list with a boolean flag.** There is no `bool CarryOver` field on any entry type, and `SprintCarryOverItem` carries a `Reason` string that a boolean cannot express. **Only `Committed` is applied** — a carry-over entry names work that did *not* finish elsewhere and states why; committing it to this iteration by reading the two lists as one set is the exact bug this row exists to prevent. A test applies a fixture with a non-empty `CarryOver` and asserts none of those `issueId`s acquired an `IterationId`.

   `SprintPlanDocumentType` (`:51-195`) already gates the document with **named violation codes** — `MALFORMED_PAYLOAD`, `SPRINT_ID_MISSING`, `CAPACITY_INVALID`, `NO_COMMITTED_ITEMS`, `COMMITTED_ITEM_MISSING_ISSUE_ID`, `COMMITTED_ITEM_MISSING_OWNER_ROLE`, `OWNER_ROLE_UNKNOWN`, `COMMITTED_ITEM_MISSING_ESTIMATE`, `COMMITMENT_EXCEEDS_CAPACITY`, `CARRYOVER_NOT_FLAGGED` (`:54-81`). **This story re-validates none of them.** An `accepted` document has already passed that validator; re-implementing e.g. the capacity arithmetic here would create a second authority on the same rule (D10 already refuses to enforce capacity). The seam's own `409`s are exactly two — wrong type, not accepted — and no more.

   **A test asserts that no type under `Tamma.Api/Services/Tracker/` redeclares `sprintId`/`committed`/`carryOver`/`ownerRole`/`estimate`.** A second copy of the shape is how this seam would silently diverge from the validator that gates the document. (This mirrors 44-3's AC9 as amended.)

   ⚠ **The `issueId` join is a cross-story contract that is not yet fixed** (new 2026-08-01; the same hazard 44-3 records as its C2). `SprintCommittedItem.IssueId` is `[JsonPropertyName("issueId")]` (`:14`) and the validator's only rule on it is "not null/whitespace" (`:133-135`) — nothing constrains it to a work-item key. The shipped contract example writes `"issueId": "issue-7"` (`:215`, `:237`), which is **not** the `<PROJECT_KEY>-<n>` shape `WorkItemRef.ToWire()` emits (`Tamma.Core/Tracking/WorkItemRef.cs:91`, e.g. `TAM-142`). So the resolver's happy path is **fixture-tested only** until 41-6 states that the field carries a work-item key; an unresolvable string is `not-found`, which is already an AC9 outcome, so the seam degrades honestly rather than guessing.

10. ~~**The apply seam raises no Task-View entry and emits no `DOCUMENT.*` or `TASK.*` event.** A test inspects the event stream after an apply and asserts only `ITERATION.*` / `WORKITEM.*` rows — the concrete implementation of the 41-6 AC3 correction.~~ **[UNFALSIFIABLE AS WRITTEN — REPLACED 2026-08-01; see Amendment A2.]**

    *Why the original could not fail:* it asserted a property of a set that is empty by design and inspected a projection that does not exist. (i) **This story emits nothing.** The plan's own Events section says "None emitted here — 44-5 owns emission", and the tracker as shipped writes no events at all — neither `TrackerEndpoints.cs` nor `TrackerService.cs` references `IEventRepository` or any append. So "the stream after an apply contains only `ITERATION.*`/`WORKITEM.*` rows" is vacuously true over zero rows, and stays true no matter what the implementer writes. (ii) **There is no Task View to raise an entry in.** 39-19 and 39-20 are both `ready-for-dev`/NOT landed (`docs/sprint-status.yaml:575-576`); there is no `TaskView` type, no task-inbox table and no bare `TASK.*` event constant anywhere in `apps/tamma-elsa/src/`. The only `TASK.`-shaped family that exists is `AGENT.TASK.*` (Story 32-6's agent trail, `Services/Agents/AgentTrailEventTypes.cs:12-14`) — which is unrelated, and which a naive `type.Contains("TASK.")` assertion would match, making even the intended check wrong.

    **AC10 (replacement) — two clauses, both able to fail today:**

    a. **Zero-emission pin.** A test counts `domain_events` rows for the acting tenant immediately before and after a successful `apply-plan` and asserts the delta is **0**. This is a real claim about this story's code: it goes red the moment anyone wires an emitter into the apply path instead of routing emission through 44-5, which is the failure the original AC was reaching for. It is explicitly a **temporary pin with a named successor** — when 44-5 lands emission it is *replaced* (not deleted) by the clause below, and 44-5's plan owns that handoff.

    b. **Structural isolation from the task/decision plane.** A source-level test asserts that no file under `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/` references `ITaskAudienceResolver` (`Services/Access/ITaskAudienceResolver.cs` — 39-18's fail-closed stub, awaiting 39-20's real implementation), `ChannelAudience`, or a string literal beginning `"TASK."`. This can fail — an implementer taking 41-6 AC3 literally would resolve an audience for each committed item, and that import is what the test catches — and unlike the original it does not depend on a projection nobody has built.

    **Deferred to 44-5, recorded here so it is not lost:** once emission exists, the behavioural assertion is that an apply emits exactly `ITERATION.PLAN_APPLIED.SUCCESS` and nothing in the `DOCUMENT.*` or `TASK.*` families — matched on the **full event type with an ordinal prefix test**, never `Contains`, so `AGENT.TASK.*` is not mistaken for a Task-View row.

11. **Catalog descriptors** for every mutating route added to ~~`TrackerActionDescriptors` in the~~ **[WRONG FILE — CORRECTED 2026-08-01; see Amendment A5. That file has never existed.]** the `issue-tracking` group — in the two files 44-2 actually shipped its descriptors in:

    - **`apps/tamma-elsa/src/Tamma.Core/Actions/ExternalEffect.cs`** — one `[Wire]` member per new mutating route, in the 44-2 block (`:172-220`). The wire **must** start `tracker.` — `TrackerRbacTests.cs:320` filters the catalog on `d.Key.Key.StartsWith("tracker.")`, so a differently-prefixed member is invisible to the harness that is supposed to guard it.
    - **`apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs`** — one `Effect(…)` row per member, beside 44-2's ten (`:441-460`), `ActionGroup.IssueTracking`. **`SiteKey` must be `"{METHOD} {live route pattern}"` verbatim including `:guid` constraints** — `GovernedEndpointBindingSweepTests` compares it ordinally, and 44-2 shipped this bug once already (its MODERATE-5 correction, recorded in the file at `:434-439`).
    - **`DefaultMinAutonomy` is `AutonomyDial.Min` for every one of them, including `apply-plan`** — see Amendment A5; the plan's "highest `DefaultMinAutonomy` of the group" instruction is forbidden by a currently-green test.
    - **Count pins move in the same commit**, and they move **on top of 44-3's moves** (44-3 lands first and takes `ExternalEffect` 39 → 42, `TotalCatalogMembers` 197 → 200, `TrackerRbacTests` mutating-route count 10 → 13, `PinnedInScopeCount` 237 → 240). Take 44-3's post-land values as this story's baseline, never the values written here.

12. **Every new mutating route carries a `.Governs(…)` binding — mandatory, not optional** (new 2026-08-01, Amendment A5). Epic 43's ratchet requires every mutating endpoint of the booted host to be either `.Governs`-bound or listed in `KnownUngovernedEndpoints.All`, and **baselining is mechanically unavailable**: `PinnedCount = 216` (`tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:128`) must equal `PinHistory[^1]` where `PinHistory = [237, 216]` (`:142`), and the history is asserted strictly decreasing. Appending a larger number to admit new baseline entries turns that fixture red by construction. Bind, do not baseline. (`PinnedInScopeCount` at `:157` is the one that moves.)

## Technical Notes

- The board query is the single most performance-sensitive read in the epic. It is one statement using a window function partitioned by the group key and ordered by `Rank`, filtered to the per-column limit — not N queries, and not one unbounded query the API slices in memory.
- `groupBy=assignee` has an unbounded column count. It is capped (default 20 columns by item count, plus an `unassigned` column) with the remainder folded into an `other` column, and the cap is on the wire so the client can show it.
- `CapacityPoints` is stored and returned; nothing computes against it in v1. It exists so 41-6's "committed set ≤ stated capacity" rule has somewhere to read from when someone builds the check.
- `already-committed-elsewhere` is a distinct outcome from `applied` because silently stealing an item out of an active iteration is the kind of change a scrum master needs to see in the response, not discover on the board.

## Dependencies

- **Story 44-0** (`WorkItemStatus` order for AC7, `Rank`) — `done` (`docs/sprint-status.yaml:756`). **44-1** (the `iterations` table, `IterationId` FK) — `done` (`:771`). **44-2** (endpoint class, RBAC, DTO conventions) — `done` (`:772`).
- **Story 39-11 Document Store** (`done`) — the apply seam's read (`IDocumentInstanceRepository.GetByIdAsync(tenantId, documentId, ct)`, `:40`).
- ~~**Story 41-1b / 41-6** — define and produce `SprintPlan`. **Not blocking** (narrow port, AC9).~~ *[CORRECTED 2026-08-01 — A3.]* **41-1b is `done`** (`docs/sprint-status.yaml:630`) — not a dependency at all any more, a shipped input. **41-6 is still `drafted`** (`:636`) and remains **non-blocking for the code**: the seam compiles and tests against a hand-built `DocumentInstance` fixture, and AC9's test 20 proves it rejects cleanly when no accepted document exists. It is **blocking for the feature**, on the unresolved `issueId` contract recorded in AC9.

### Story 44-3 — hard sequencing blocker (added 2026-08-01, Amendment A6)

**44-4 must not start before 44-3 lands.** 44-3 is `drafted` (`docs/sprint-status.yaml:773`). This is not the usual "sequence rather than parallelise" nicety — three specific couplings make a parallel start produce work that must be redone:

1. **The apply-outcome type.** Plan step 7 says to "reuse `OrderingApplyService`'s outcome-list shape" and extract a shared `ApplyOutcome`. **44-3 names no such type.** Its plan creates `Tamma.Api/Services/Tracker/OrderingApplyService.cs` (step 5) with a three-value outcome vocabulary — `applied | not-found | wrong-project` (44-3 D8) — and its shared-edit register, as amended 2026-08-01, does **not** list `ApplyOutcome.cs`. So the extraction is a **refactor of 44-3's just-landed code that 44-4 owns and 44-3 does not know about**: 44-4 creates the shared record, moves 44-3's outcome shape onto it, and adds the fifth value `already-committed-elsewhere` (D8) — which must be additive, since 44-3's tests pin the three it emits. None of that can be written against code that does not exist yet.
2. **The shared endpoint file and route group.** `apps/tamma-elsa/src/Tamma.Api/Endpoints/TrackerEndpoints.cs` and the tracker group in `Program.cs:2977-3016` are edited by 44-3 (its steps 6-7: `SetParent`, `Move`, `GetSubtree`, `ApplyOrdering`) and again by this story (its steps 8-9). Both also move the same four count pins (AC11). Two stories editing them in parallel is a guaranteed conflict on the pins alone.
3. **DTO arity.** The board projection returns work items through `WorkItemResponse`, which is a **positional** record — 24 parameters today (`Api/Dtos/Tracker/TrackerDtos.cs:178-205`) — constructed positionally by `TrackerEndpoints.Map(WorkItemEntity)` (`:560-566`). 44-3 AC6 adds `childCount` and `childCountByStatus` per item to the same record, changing its arity and every positional construction site. Board code written against the pre-44-3 arity will not compile after 44-3 lands.

## Out of Scope

- ~~Adding `SprintPlan` to `DocumentTypeKey` / `DocumentTypeRegistry` — 41-1b's, with its count pins.~~ *[CORRECTED 2026-08-01 — A3.]* **Any change to `DocumentTypeKey`, `DocumentTypeRegistry`, `Types/SprintPlan.cs` or `Policy/AcceptanceDefaults.cs`.** 41-1b already landed all of it (`DocumentTypeKey.cs:41`; `DocumentTypeRegistry.cs:45`; `AcceptanceDefaults.cs:216`), so there is nothing to add and nothing to own — the document-type pins stay at **17** (`DocumentTypeKeyTests.cs:24`, `DocumentTypeRegistryTests.cs:42`) and this story does not touch that assembly.
- Any change to 41-6's workflow or prompt cell. **This story implements the corrected behaviour; rewording 41-6's AC3 is a docs edit in Epic 41's own file and is recommended, not performed here.**
- Velocity, burndown, forecast, capacity enforcement — deferred (epic README).
- Saved board configurations, custom swimlanes, board-level filters persisted per user — deferred; `tracker_preferences.BoardGroupBy` (44-1) holds the single default and nothing more.
- Any UI — 44-6.

## Estimated Effort

4 days *(the 2026-08-01 amendments add AC12's `.Governs` binding work, pin reconciliation across four test files layered on top of 44-3's moves, and the `ApplyOutcome` extraction that turns out to be a refactor of 44-3 rather than a reuse. Treat 4 days as the pre-amendment floor, not a current estimate.)*

## Amendment — 2026-08-01 (scoping round: story vs. tree)

Every claim below was checked against the working tree at commit `6429691`. Where the story was wrong, the original text is struck through in place rather than removed.

**A1 — AC7 said "all seven" `WorkItemStatus` columns. There are eight.** `WorkItemStatus` has eight members (`Tamma.Core/Tracking/WorkItemStatus.cs:37-44`), its own XML doc says "Count-pinned at 8" (`:8`), and `WorkItemStatusTests.Member_count_is_pinned` asserts `HaveCount(8)` plus the eight wire strings literally (`tests/Tamma.Core.Tests/Tracking/WorkItemStatusTests.cs:20-26`). The likely origin of the error is counting the vocabulary without `triage`, which is exactly the member both `WorkItemStatus.cs:15-21` and `WorkItemStatusTests.Triage_is_a_member` (`:30-38`) single out as the one most likely to be "tidied away" by someone who has not read 44-0 D3. **The error is baked into a plan test *name* as well** (`BoardTests.Status_board_returns_all_seven_columns_in_enum_order`), so the test as specified was red on day one: a board correctly returning eight columns fails a test whose name promises seven, and the cheap fix is to make the board wrong. Both are corrected; AC7 now also requires the assertion be derived from `Enum.GetValues<WorkItemStatus>()` rather than a second hard-coded number.

**A2 — AC10 was unfalsifiable, and is replaced rather than deleted.** It asserted that only `ITERATION.*`/`WORKITEM.*` rows appear on a stream this story writes nothing to, guarding a Task View that has no code. Evidence: the plan's own Events section ("None emitted here — 44-5 owns emission"); no `IEventRepository` reference in `TrackerEndpoints.cs` or `TrackerService.cs`; 39-19 and 39-20 both `ready-for-dev` (`docs/sprint-status.yaml:575-576`); no `TaskView` type and no bare `TASK.*` constant under `apps/tamma-elsa/src/`. **Replaced, not removed** — the risk it was pointing at (an implementer taking 41-6 AC3 literally and wiring the commitment into the decision inbox) is real, so it is re-expressed as two clauses that can each fail today: a zero-emission delta pin, and a source-level assertion that the tracker services import nothing from the task/decision plane. Also recorded: the intended `TASK.*` check must be an ordinal **prefix** test, because `AGENT.TASK.*` exists (`Services/Agents/AgentTrailEventTypes.cs:12-14`) and a `Contains` would match it.

**A3 — `SprintPlan` is shipped; the story said it does not exist.** 41-1b is `done` (`docs/sprint-status.yaml:630`). Corrected in Architectural Context, AC9 and Out of Scope. `DocumentTypeKey` is at 17 members, not ten.

**A4 — the plan guessed the `SprintPlan` body shape and guessed it wrong.** Plan step 6 declared `SprintPlanEntry(string IssueId, string? OwnerRole, decimal? Estimate, bool CarryOver)`. **No such type exists and no entry type carries a boolean carry-over field.** The shipped record splits the two into separate lists — `Committed: IReadOnlyList<SprintCommittedItem>` and `CarryOver: IReadOnlyList<SprintCarryOverItem>` (`Types/SprintPlan.cs:39-40`) — and a carry-over entry carries a `Reason` string (`:26`) that a boolean cannot express. The guessed shape would have merged the two sets and committed unfinished work into the new iteration. AC9 now specifies deserializing the shipped record, applying `Committed` only, and re-validating nothing (the validator's ten named codes are listed).

**A5 — plan step 10 targeted a file that has never existed, and prescribed a threshold a green test forbids.** `TrackerActionDescriptors.cs` is nowhere in the tree; 44-2 shipped its descriptors as `[Wire]` members in `Tamma.Core/Actions/ExternalEffect.cs:172-220` and `Effect(…)` rows in `Tamma.Core/Actions/ActionCatalog.Descriptors.cs:441-460`. (Same error as 44-3's A1 — the two stories inherited it from a shared draft.) Separately, the step said `apply-plan` should carry "the highest `DefaultMinAutonomy` of the group". **It cannot**: `TrackerRbacTests.cs:329-332` asserts every `tracker.*` descriptor is `AutonomyDial.Min`, which is also the `Effect(…)` helper's default (`ActionCatalog.Descriptors.cs:58`) and Epic 43 D1's behaviour-preserving posture recorded in the file at `:428-429`. Blast radius is expressed by `apply-plan` requiring `TrackerManage` at the route, not by the dial. New AC12 records the `.Governs` ratchet obligation that neither the story nor the plan mentioned.

**A6 — 44-3 is a hard sequencing blocker and the story did not say why.** Three named couplings recorded under Dependencies: the `ApplyOutcome` extraction is a refactor of 44-3's code (44-3 names no such type), the shared endpoint file and count pins, and `WorkItemResponse`'s positional arity which 44-3 AC6 changes.

**A7 — the preferred migration path is closed.** Handled in the implementation plan's Data & Migrations section; summarised here because AC2 depends on the index. `AddTrackerCore` shipped (`Tamma.Data/Migrations/Tenant/20260729035027_AddTrackerCore.cs`), 44-1 is `done`, and a **later** tenant migration exists (`20260729070033_AddDocumentInstanceAudience.cs`), so "fold the index into `AddTrackerCore` and regenerate" — the plan's preferred branch — is not available. The second branch is now the only branch.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-08-01 | 1.1.0   | Scoping round against the tree. AC7's column count corrected seven → eight, and the same error found in a plan test name (A1). AC10 replaced — it could not fail (A2). `SprintPlan` corrected from "does not exist" to shipped, in three places (A3). AC9 rewritten against the real `Committed`/`CarryOver` two-list shape and the validator's named codes; the `issueId` join recorded as an unfixed cross-story contract (A4). AC11's descriptor file corrected to the two real files, `DefaultMinAutonomy` reconciled against `TrackerRbacTests`, new AC12 for the 43-8 `.Governs` ratchet (A5). 44-3 recorded as a hard sequencing blocker with three named couplings (A6). Migration path re-pointed (A7). | Claude |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
