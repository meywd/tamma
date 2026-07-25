# Implementation Plan — Story 44-5: DCB Events and the Event-Name Drift Ratchet

## Scope & Deliverable

When this story is done every tracker mutation appends exactly one DCB event under the platform's `AGGREGATE.ACTION.STATUS` convention, with uniform tags keyed on the same `issueId` string Epic 39's documents use — so `GET /api/work-items/{id}/timeline` interleaves the tracker's own history with the `DOCUMENT.*` / `APPROVAL.*` / `ESCALATION.*` rows the loop produced for the same item, which is the payoff of the join-key decision and the reason a comments table is deferred. It also ships the naming ratchet the repo has never had: a shrink-only allowlist test over ~300 existing constants that makes the fourth, fifth and sixth new family cheaper to review than the third.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §2 (the `issueId` join), §1 (taken family names), Drift prevention
- `docs/stories/epic-44/story-44-3/implementation-plan.md` "Events" and `story-44-4/implementation-plan.md` "Events" — the constants those stories reserved
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:7-47` + `EventRepository.cs:46-80` — `AppendAsync`, the null-tenant delegation and its throw
- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs:1-25` — the row, and the `SequenceNumber` doc explaining why it, not `Id`, is the cursor
- `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/20260610013731_InitialTenant.cs:96-113,:458-468` — the table and its indexes
- `apps/tamma-elsa/src/Tamma.Api/Services/AcceptanceRules/AcceptanceRulesEventsService.cs:14` — emission-inside-the-service, the shape copied
- `apps/tamma-elsa/src/Tamma.Activities/Documents/DocumentEvents.cs:28`, `Tamma.Api/Services/Jira/JiraEventTypes.cs:11`, `Tamma.Api/Services/Git/GitEventTypes.cs:13` — three catalogue styles; note Jira's "exactly one per call" contract at `:5-7`
- `apps/tamma-elsa/src/Tamma.Activities/ADL/CycleEvents.cs:44-47`, `ADL/MergeEvents.cs:48`, `ADL/IssueStatusEvents.cs:30`, `Documents/ChannelEvents.cs:6-7` — the taken prefixes
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `KnownContractViolations`, the shrink-only + staleness ratchet discipline
- `apps/tamma-elsa/src/Tamma.Core/Audit/SensitiveActionCatalog.cs:16-28` — the "verified by grep at authoring time, not by a test" admission that motivates AC8
- **All referenced paths exist.** NOT FOUND (this story creates them): `Tamma.Api/Services/Tracker/{Project,WorkItem,Iteration}Events.cs`, `TrackerEventsService.cs`, `tests/Tamma.Core.Tests/Events/`.

## Design Decisions

- **D1 — Three families, one per aggregate: `PROJECT.*`, `WORKITEM.*`, `ITERATION.*`.** Not one `TRACKER.*` family: the repo's convention is aggregate-first (`DOCUMENT`, `PR`, `BRANCH`, `MERGE`, `TENANT`, `SECRET`…), and a single prefix would make `TRACKER.WORKITEM.STATUS_CHANGED.SUCCESS` a four-segment name where three suffice. **`WORKITEM`, one word** — `_` is used within a compound aggregate name in `ISSUE_STATUS` but the shorter form reads better beside `DOCUMENT` and `APPROVAL`, and one form must be fixed before three files are written.
  Explicitly avoided, each with an incumbent: `CYCLE.*` (`CycleEvents.cs:44-47`), `SPRINT.*` (41-6), `TASK.*` (reserved, `ChannelEvents.cs:6-7`), `ISSUE.*` (`MergeEvents.cs:48`), `ISSUE_STATUS.*` (`IssueStatusEvents.cs:30`). AC10 is the test.

- **D2 — Emission lives in a `TrackerEventsService` the mutating services call, not in endpoints and not inline.** `AcceptanceRulesEventsService.cs:14` is the shipped precedent. Endpoint-level emission misses every internal caller (the apply seams, 44-7's loop integration, 44-8's importer) and would need duplicating in each. Consequence recorded: **this story changes no endpoint and no DTO**, so it can land after 44-4 without touching the API surface.

- **D3 — Exactly one terminal event per mutation, never a started/completed pair.** `JiraEventTypes.cs:5-7` states this contract for its own family and it is the right default: a started/completed pair doubles the row count for operations that complete in milliseconds and creates an orphan class (started with no terminal) that every consumer must then handle. The two apply seams emit one `*_APPLIED.SUCCESS` carrying the per-outcome counts, or one `*_APPLIED.FAILED`.

- **D4 — Tags are the query surface; `IssueNumber` stays NULL.** `tags.issueId` is the work item's `WorkItemRef.ToWire()` string — **the same string** `DocumentInstance.IssueId` holds, which is what makes AC7's interleaved timeline a single indexed read instead of a join across two coordinate systems. `domain_events.IssueNumber` is `integer NULL` (`:102`) with two indexes (`:462-466`) and cannot hold `TAM-142`; native tracker events leave it null and a test pins that, so nobody later "fixes" it by widening the column or by stuffing the numeric suffix in (which would collide across projects).

- **D5 — Every state-transition event carries `from` and `to`.** An event recording only the new value is not replayable and forces a reader to reconstruct the previous state by scanning backwards. Applies to `STATUS_CHANGED`, `REPARENTED`, `ASSIGNED`, `MOVED`. Cheap now, impossible to retrofit once rows exist.

- **D6 — Emission failure policy is split, per constant, and the split is a test.** Uniformly best-effort would let an assignment change land with no audit row, which is a compliance hole. Uniformly transactional would let an event-store hiccup block a card drag. So:
  - **In-transaction, failure rolls back:** `ASSIGNED`, `STATUS_CHANGED`, `DELETED`, `ORDERING_APPLIED.*`, `PLAN_APPLIED.*`. These change who may act, what the order is, or what exists.
  - **Best-effort, warn and continue:** `CREATED`, `UPDATED`, `MOVED`, `REPARENTED`, `COMMITTED`, `UNCOMMITTED`, `LINKED`, and all `PROJECT.*` / `ITERATION.*` except `CLOSED`.
  This mirrors Epic 43's posture — emission best-effort *except* denials under enforcement, which "are not swallowed — a block with no audit row is a compliance hole" (`epic-43/README.md:334-336`). The mapping is a `static readonly FrozenSet<string> Transactional` in `TrackerEventsService`, and a test asserts every constant in the three catalogues appears in exactly one of the two sets — so a new constant added without a policy fails the build.

- **D7 — The timeline endpoint queries by `tags.issueId`, not by a tracker-owned filter, and returns foreign families unmodified.** `GET /api/work-items/{id}/timeline` resolves the item's key, then reads `domain_events WHERE Tags->>'issueId' = @key ORDER BY "SequenceNumber"`. It does **not** whitelist `WORKITEM.*` — the whole point is that a work item run through the loop shows its `DOCUMENT.PRODUCED`, `APPROVAL.REQUESTED` and `ESCALATION.TRIGGERED` rows in the same list. Cursor is `SequenceNumber` (the column's own doc, `DomainEvent.cs:14-22`, says consumers use it as the tiebreak and never `Id`), never `CreatedAt` (same-millisecond collisions).
  This is the concrete payoff of the join key and it is why the epic defers a comments table.

- **D8 — The ratchet reflects over *files*, filtered by name, not over all constants.** Scope: `public const string` fields whose value contains `.`, in types declared in files matching `*Events.cs` or `*EventTypes.cs`, across `Tamma.Core`, `Tamma.Activities`, `Tamma.Api`, `Tamma.Platforms*`. The file-name filter is what keeps the lowercase SSE bus strings in `TaskQueueProcessor.cs:196,213,229,241` (`task.claimed` etc. — **not DCB types**) out of scope without allowlisting them, which would be a lie about what they are.
  Shape: `^[A-Z][A-Z0-9_]*(\.[A-Z][A-Z0-9_]*){2,3}$` — three or four segments, each upper snake. Four-segment names exist and are legitimate (`CODE_REVIEW.ITERATION.STARTED` is three; `AGENT.TOOL_CALL.*` variants reach four), so the shape permits an optional sub-aggregate rather than forcing a rename.

- **D9 — The allowlist is generated once, carries `file:line` per entry, is count-pinned, shrink-only, and staleness-checked.** `ContractBindingTests`'s `KnownContractViolations` discipline exactly: an entry that now complies **fails** the build telling you to delete it. Generation is a one-off run committed as a data file, not a runtime discovery — a runtime-discovered allowlist ratchets nothing.
  **AC9's guard matters more than it looks:** a test asserts no `PROJECT.`/`WORKITEM.`/`ITERATION.` constant is in the allowlist. Without it, the cheapest way to make the new families pass is to add them, which would make this story's own deliverable self-defeating.

- **D10 — Do not fix existing violators in this story.** The `GATE` / `APPROVAL.GATE` split (`MergeApprovalEvents.cs:57-59`) and the `TOOL_LOOP.*` family are shipped, persisted in `domain_events` rows, and consumed by dashboards and the replay reconstructor. Renaming them is a data-and-consumer change with its own blast radius. Seed them, record the count, move on — that is what a ratchet is for.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/ProjectEvents.cs`** — `Created/Updated/Archived` × `SUCCESS`, plus an XML doc naming the tag contract.

2. **CREATE `.../Tracker/WorkItemEvents.cs`** — the twelve constants of story AC2, each with a doc line naming its `Data` payload keys and its D6 failure policy.

3. **CREATE `.../Tracker/IterationEvents.cs`** — the five constants reserved by 44-4's plan.

4. **CREATE `.../Tracker/TrackerEventsService.cs`** — `ITrackerEventsService` with one method per event, each building `DomainEvent { Type, TenantId, IssueNumber = null, Tags, Metadata, Data }` and calling `IEventRepository.AppendAsync`. Carries D6's `Transactional` set and an `AppendAsync(evt, transactional)` core that rethrows or warns accordingly.

5. **MODIFY `Tamma.Api/Services/Tracker/TrackerService.cs`** (44-2) — inject `ITrackerEventsService`; emit on create/update/assign/status/delete. In-transaction calls for the D6 transactional set.

6. **MODIFY `TrackerHierarchyService.cs`** (44-3) and **`OrderingApplyService.cs`** (44-3) — emit `REPARENTED`, `MOVED`, `ORDERING_APPLIED.*`.

7. **MODIFY `IterationService.cs`** and **`SprintPlanApplyService.cs`** (44-4) — emit the `ITERATION.*` set and `WORKITEM.COMMITTED/UNCOMMITTED`.

8. **CREATE the timeline read** — `WorkItemRepository.GetTimelineAsync(issueId, cursor, limit)` over `domain_events` (D7), plus `TrackerEndpoints.GetTimeline` and its `Program.cs` mapping under `TrackerView` + `ConfigRead`. (This is the one endpoint the story adds; D2's "no endpoint changes" refers to the mutation surface.)

9. **CREATE `apps/tamma-elsa/tests/Tamma.Core.Tests/Events/EventTypeNamingRatchetTests.cs`** + `KnownEventNameViolations.cs` (the generated, `file:line`-annotated, count-pinned allowlist).

10. **CREATE `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/TrackerEventsTests.cs`** and `TrackerTimelineTests.cs`.

## Data & Migrations

**None.** `domain_events` already has everything needed. No new index: `IX_domain_events_TenantId_IssueNumber` is filtered on `IssueNumber` and does not serve `Tags->>'issueId'` — the timeline read relies on the existing `Type_CreatedAt` and `TenantId` indexes plus a sequential scan bounded by tenant schema. **If the AC7 benchmark shows it is insufficient, a GIN index on `Tags` is a follow-up in 44-1's migration if unshipped, or a separate one** — noted rather than speculatively added, since a GIN index on a high-write table is not free.

## Events

This story *is* the events story. Twenty-two constants across three catalogues. Nothing outside `Tamma.Api/Services/Tracker/` gains a constant.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `TrackerEventsTests.Every_mutation_emits_exactly_one_event` | drives all 22 paths; one append each — **AC2** |
| 2 | `TrackerEventsTests.Tags_are_uniform` | `issueId`/`tenantId`/`projectId`/`userId`/`correlationId` present per contract — **AC3** |
| 3 | `TrackerEventsTests.IssueNumber_is_always_null` | **AC3 / D4** |
| 4 | `TrackerEventsTests.Transitions_carry_from_and_to` | the four transition events — **AC4 / D5** |
| 5 | `TrackerEventsTests.Transactional_failure_rolls_the_write_back` | injected append failure on `ASSIGNED` → no row change — **AC6** |
| 6 | `TrackerEventsTests.Best_effort_failure_warns_and_proceeds` | injected failure on `CREATED` → row written, warning logged |
| 7 | `TrackerEventsTests.Every_constant_has_exactly_one_failure_policy` | reflection over the three catalogues vs the `Transactional` set — **D6** |
| 8 | `TrackerEventsTests.No_endpoint_or_dto_changed` | (review-level; asserted by the PR diff, not a test) |
| 9 | `TrackerTimelineTests.Interleaves_document_and_tracker_events` | seed `WORKITEM.CREATED` + `DOCUMENT.PRODUCED` + `APPROVAL.REQUESTED` on the same `issueId`; assert all three in `SequenceNumber` order — **AC7, the payoff test** |
| 10 | `TrackerTimelineTests.Cursor_is_sequence_number` | same-millisecond rows page correctly |
| 11 | `EventTypeNamingRatchetTests.All_event_constants_match_the_shape_or_are_allowlisted` | **AC8** |
| 12 | `EventTypeNamingRatchetTests.Allowlist_is_shrink_only_and_count_pinned` | pinned count |
| 13 | `EventTypeNamingRatchetTests.A_stale_allowlist_entry_fails` | a now-compliant entry fails naming itself — **D9** |
| 14 | `EventTypeNamingRatchetTests.No_epic_44_constant_is_allowlisted` | **AC9, the self-defeat guard** |
| 15 | `EventFamilyCollisionTests.No_taken_prefix_is_reused` | `CYCLE.`/`SPRINT.`/`TASK.`/`ISSUE.`/`ISSUE_STATUS.` — **AC10** |
| 16 | `EventTypeNamingRatchetTests.Sse_bus_strings_are_out_of_scope` | `TaskQueueProcessor`'s lowercase strings are neither matched nor allowlisted — **D8** |

Tests 1–10 Testcontainers; 11–16 pure reflection, fast, run on every build.

## Definition of Done

- 16 tests green.
- 22 constants, three files, every one carrying its payload doc and its failure policy.
- The allowlist is committed with `file:line` per entry and a pinned count; **its size is recorded in the PR description** as the baseline future stories shrink from.
- No existing event constant is renamed (D10) — grep-checked.
- `GET /api/work-items/{id}/timeline` returns foreign families unfiltered (test 9).
- A `.dev/findings/` note recording that the repo had no event-naming test before this story, with the three near-miss tests it was mistaken for.

## Dependencies & Sequencing

- **Blocked by:** 44-1 (rows to emit about), 44-2, 44-3, 44-4 (the services that call the emitter; each reserved its constants in its own plan).
- **Blocks:** 44-6 (renders the timeline), 44-9 (the import's audit trail is how the dogfood is verified).
- **Shared-edit register:** `TrackerService.cs`, `TrackerHierarchyService.cs`, `IterationService.cs`, `OrderingApplyService.cs`, `SprintPlanApplyService.cs` — all Epic 44 files, all touched additively. The ratchet's allowlist is a **new** file and conflicts with nothing, but it is generated from the tree, so regenerate rather than merge if another epic adds a family concurrently.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The ratchet finds far more violations than expected** and the allowlist is enormous, making the test theatre. | The count is pinned and recorded as a baseline; the shape (D8) permits the legitimate four-segment names rather than forcing a rename; the file-name filter (D8) excludes SSE bus strings that are not DCB types at all. If the seed is still large, that is the finding, and it is worth having. |
| **Someone allowlists an Epic 44 constant** to make the build pass. | Test 14 exists for exactly this and names it as the self-defeat guard. |
| **The timeline read is a sequential scan** and is slow at volume. | Bounded by tenant schema; benchmarked in test 9's fixture; Data & Migrations names the GIN-on-`Tags` follow-up and why it is not added speculatively. |
| **The transactional/best-effort split gets simplified to "all best-effort"** in review as over-engineering. | D6 cites Epic 43's identical posture and the specific compliance argument; test 7 makes an unpoliced constant a build failure, so simplification requires deleting a test. |
| **`WORKITEM` vs `WORK_ITEM` bikeshed after three files exist.** | D1 fixes it before the files are written and AC8's shape test accepts either, so the cost of the decision is zero and the cost of relitigating it is three renames plus persisted rows. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–4 (three catalogues + the emitter with D6's policy split) | 1.0 |
| Steps 5–7 (wire emission into five services) | 0.75 |
| Step 8 (timeline read + endpoint) | 0.5 |
| Step 9 (ratchet test + allowlist generation + staleness check) | 1.0 |
| Step 10 (tests 1–10) | 0.5 |
| Findings note, review | 0.25 |
| **Total** | **4.0** |
