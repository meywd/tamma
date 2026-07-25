# Implementation Plan — Story 44-3: Hierarchy, Ranking, and the `BacklogOrdering` Apply Seam

## Scope & Deliverable

When this story is done a work item can be reparented under rules the database and the service both enforce, moved between two neighbours with exactly one row write, read as a subtree in one recursive CTE with roll-up counts computed in SQL, and — the load-bearing half — an **accepted `BacklogOrdering` document from Story 41-3 can be applied to the backlog's actual order in one idempotent transaction**. That apply seam is the concrete answer to a boundary the epic draws in two directions: 41-3 owns the proposal, Epic 44 owns the record, and neither changes the other.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §3 (the parenting matrix and why one table), §4 (rank), the 41-3 boundary row
- `docs/stories/epic-44/story-44-0/implementation-plan.md` — D4 (`TrackerHierarchy`, `MaxDepth` enforced elsewhere), D7 (rank algebra + the collation trap)
- `docs/stories/epic-44/story-44-1/implementation-plan.md` — D3 (`COLLATE "C"`), Data & Migrations (`ParentId` `RESTRICT`, indexes), `BulkSetRankAsync`
- `docs/stories/epic-44/story-44-2/implementation-plan.md` — D2 (tri-state PATCH), D7 (keyset paging), D10 (catalog descriptors)
- `docs/stories/epic-41/story-41-3/41-3-backlog-prioritization-and-grooming.md:17-25,:44` — what 41-3 produces and its AC3
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md:32,:53,:62,:64-67` — `BacklogOrdering`'s domain rule, its `backlog-ordering` wire, its store round-trip AC, and **the two count pins this story must not move**
- `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs:34,37,55,61` — `DocumentType`, `IssueId` (string — the join key), `Revision`, `Status`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:20-29` — `accepted` is the wire the seam requires
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs:22-33` — ten members today; **this story adds none**
- **All referenced paths exist.** NOT FOUND (planned by 41-1b, no code): a `backlog-ordering` `DocumentTypeKey` member and its `IDocumentType`. AC9's port is written so that absence blocks nothing.

## Design Decisions

- **D1 — Hierarchy invariants are enforced in the service, inside the write transaction, computed from persisted state.** The database cannot express "no cycles at depth ≤ 3 across a self-referencing FK" without a trigger, and a trigger here would be a second rule engine disagreeing with `TrackerHierarchy`. So: `SELECT … FOR UPDATE` the ancestor chain, evaluate `CanParent` + depth + cycle, write, commit. AC2's concurrency test drives two reparents that jointly form a cycle and asserts exactly one commits — the case a naive read-then-write silently allows.

- **D2 — Three distinct rejection reasons, each named on the wire.** `KIND_NOT_PERMITTED` (`Epic` under `Task`), `MAX_DEPTH_EXCEEDED`, `WOULD_CREATE_CYCLE`, `CROSS_PROJECT_PARENT`. A single `400 "invalid parent"` is unactionable in a drag-and-drop UI, where the user needs to know *which* rule stopped them — and 44-6 renders the reason inline.

- **D3 — `move` is neighbour-addressed `(afterId?, beforeId?)`, never index-addressed.** An index is a position in a list the client last saw; under concurrent drags it is stale by construction and the resulting write is *wrong*. Neighbours are a statement about the intended relation, and the worst outcome under concurrency is a rank between two items that have since moved — visible and harmless. Both null means "to the end". The write is exactly one `UPDATE`, which is the whole point of the fractional index (44-0 D7); a test asserts the statement count.

- **D4 — Kind change re-validates against parent *and* children, in one check.** A `PATCH` that changes `kind` is a hierarchy mutation wearing a field update's clothes. Validating only against the parent lets a `Story` with `Task` children become a `Task`, which `TrackerHierarchy` forbids in the other direction. The check enumerates both edges and returns every violation, not the first.

- **D5 — Subtree is one recursive CTE with an explicit depth guard, and roll-ups are SQL aggregates.** `WITH RECURSIVE` bounded by `WHERE depth < TrackerHierarchy.MaxDepth`, so a cycle introduced by a bug elsewhere is a truncated result rather than an infinite scan. `childCount` / `childCountByStatus` are `LEFT JOIN LATERAL` aggregates in the same statement — AC6's test asserts a **constant** query count in the number of rows, because the natural implementation of a roll-up is N+1 and it will not be visible until a customer has 2 000 items.

- **D6 — The apply seam reads `BacklogOrdering` through `IBacklogOrderingReader`, a narrow port, and adds no `DocumentTypeKey` member.** `DocumentTypeKey` has exactly ten members (`:22-33`) and its count is pinned twice — `DocumentTypeKeyTests.cs:20` `Be(10)` and `DocumentTypeRegistryTests.cs:37` `HaveCount(10)`. 41-1b's D2 owns moving those pins as part of registering six types. Moving one of them here would take a pin out of its owning story, break 41-1b's diff, and register a type with no `IDocumentType` validator behind it.
  So the port is:
  ```csharp
  public interface IBacklogOrderingReader
  {   // returns null if the document is not an accepted backlog-ordering
      Task<BacklogOrderingView?> ReadAcceptedAsync(Guid documentId, CancellationToken ct);
  }
  public sealed record BacklogOrderingView(
      Guid DocumentId, int Revision, IReadOnlyList<BacklogOrderingEntry> Entries);
  public sealed record BacklogOrderingEntry(string IssueId, string? Rationale, decimal? Value, decimal? Effort);
  ```
  Production impl reads `DocumentInstance.Body` and matches `DocumentType == "backlog-ordering"` **by wire string**, which works whether or not the enum member exists yet. Tests use a fixture. When 41-1b lands, the string comparison becomes an enum comparison in one line.

- **D7 — The seam requires `Status == accepted` and returns `409`, not `400`, when it is not.** The caller's request is well-formed and they are authorized; the *document* is not in a state the system will act on. That is the same distinction Epic 43 draws for its gate (`epic-43/README.md:306`). Two failure codes, named: `DOCUMENT_NOT_BACKLOG_ORDERING`, `DOCUMENT_NOT_ACCEPTED`.

- **D8 — Partial application with a per-item outcome list, not all-or-nothing.** A `BacklogOrdering` is produced against a snapshot; by the time a human accepts it, some referenced items may be deleted, moved to another project, or never have existed (an LLM-authored ordering can reference a key that does not resolve). Failing the whole apply because one of forty keys is stale makes the feature unusable. Every entry returns `applied | not-found | wrong-project`, the resolvable subset is written in one transaction, and the response is the audit record of what was skipped. 44-5 emits one `WORKITEM.ORDERING_APPLIED.SUCCESS` carrying the counts.

- **D9 — Unreferenced items are appended after the applied set, preserving their relative order.** The alternatives are worse: interleaving by old rank buries newly-filed work in the middle of a stale ordering; leaving them in place means the applied ordering is not actually the order. Appending is the only option in which the document's order is exactly honoured *and* nothing is hidden. Implemented as: applied items get ranks from `Rank.First()` forward; unreferenced items are re-ranked after the last applied one, in their existing relative order.

- **D10 — Idempotence is by outcome, not by a marker column.** Re-applying the same accepted document recomputes the same ranks and writes the same values, so the second run is a no-op by construction — no `AppliedDocumentId` column, no dedupe table. A test asserts row-for-row equality after a second apply. (A marker column would also be wrong: the same ordering *should* be re-appliable after someone drags a card, to reset to the accepted state.)

- **D11 — The apply seam is a tracker operation that reads a document; it is not a lifecycle step.** It emits no `DOCUMENT.*` event, does not mutate the document, does not create a revision, and does not call into `DocumentLifecycleWorkflow`. 41-3 remains unchanged and unaware. Stated explicitly because the natural instinct is to make it a lifecycle terminal, which would put a tracker write inside a workflow that has its own re-entry and acceptance semantics.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/TrackerHierarchyService.cs`** — `ReparentAsync`, `ValidateKindChangeAsync`, `GetSubtreeAsync`. All hierarchy invariants (D1/D2/D4) live here; `TrackerService` (44-2) delegates.

2. **MODIFY `apps/tamma-elsa/src/Tamma.Data/Repositories/WorkItemRepository.cs`** — add `GetAncestorChainAsync(Guid id)` (`SELECT … FOR UPDATE` over a recursive CTE), `GetSubtreeAsync(Guid id, int maxDepth)` (D5's CTE + lateral roll-ups), `ListWithRollupsAsync(WorkItemQuery)`.

3. **MODIFY `WorkItemRepository`** — `MoveAsync(Guid id, string newRank, int expectedVersion)`: one `UPDATE … WHERE "Id" = @id AND "Version" = @v`.

4. **CREATE `Tamma.Api/Services/Tracker/IBacklogOrderingReader.cs` + `DocumentBacklogOrderingReader.cs`** — per D6. Matches `DocumentType` by the `"backlog-ordering"` wire string; requires `Status == DocumentInstanceStatus.Accepted.ToWire()`.

5. **CREATE `Tamma.Api/Services/Tracker/OrderingApplyService.cs`** — per D7/D8/D9/D10. Reads the view, resolves entries by `issueId` → `work_items."Key"`, computes the rank sequence, calls `BulkSetRankAsync` in one transaction, returns the outcome list.

6. **MODIFY `Tamma.Api/Endpoints/TrackerEndpoints.cs`** — add `SetParent`, `Move`, `GetSubtree`, `ApplyOrdering`.

7. **MODIFY `Program.cs`** — map the four routes in the 44-2 tracker group. `parent` / `move` require `TrackerView` (a member reorders their own board); `apply-ordering` requires `TrackerManage` (it rewrites the whole project's order). Rate limit `ConfigWrite`.

8. **MODIFY `Program.cs` — DI**: `TrackerHierarchyService`, `OrderingApplyService`, `IBacklogOrderingReader` → `DocumentBacklogOrderingReader`, all `AddScoped`.

9. **MODIFY `Tamma.Api/Services/Tracker/TrackerActionDescriptors.cs`** — three entries: `effect:work-item.reparent`, `effect:work-item.move`, `effect:backlog.apply-ordering`. The last carries a higher `DefaultMinAutonomy` than the first two — it rewrites an entire project's order in one call.

10. **CREATE tests** under `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/`.

## Data & Migrations

**None.** 44-1 created `ParentId` (self-FK, `RESTRICT`), `Rank` (`COLLATE "C"`) and the `(ParentId)` and `(ProjectId, Status, Rank)` indexes. If the subtree CTE shows a missing index in the AC6 benchmark, it is added in **44-1's** migration if that has not yet shipped, or in a follow-up — this story does not open a second tenant migration (44-1 D4: tenant migrations are the scarcest resource in the repo).

## Events

None emitted here — 44-5 owns emission and adds it inside these services. 44-5's catalogue reserves: `WORKITEM.REPARENTED.SUCCESS`, `WORKITEM.MOVED.SUCCESS`, `WORKITEM.ORDERING_APPLIED.SUCCESS` (data: `documentId`, `revision`, `applied`, `notFound`, `wrongProject`), `WORKITEM.ORDERING_APPLIED.FAILED`. Listed here so 44-5 does not have to re-derive them.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `HierarchyTests.Permitted_parenting_matrix_is_honoured` | all 36 `(parent, child)` pairs against `TrackerHierarchy` |
| 2 | `HierarchyTests.Exceeding_max_depth_is_rejected` | `MAX_DEPTH_EXCEEDED` |
| 3 | `HierarchyTests.Self_and_descendant_parents_are_rejected` | `WOULD_CREATE_CYCLE` |
| 4 | `HierarchyTests.Cross_project_parent_is_rejected` | `CROSS_PROJECT_PARENT` |
| 5 | `HierarchyTests.Concurrent_reparents_cannot_jointly_cycle` | two parallel writes → exactly one commits — **AC2** |
| 6 | `HierarchyTests.Kind_change_validates_parent_and_children` | `Story`+`Task` children → `task` rejected, both edges listed — **AC4** |
| 7 | `RankMoveTests.Move_writes_exactly_one_row` | statement/row-count assertion — **AC3** |
| 8 | `RankMoveTests.Move_to_ends_and_between_are_all_defined` | `(null,x)`, `(x,null)`, `(null,null)`, `(a,b)` |
| 9 | `RankMoveTests.Order_survives_a_thousand_random_moves` | invariant: SQL order == intent after 1 000 moves |
| 10 | `SubtreeTests.Returns_descendants_to_max_depth_in_one_query` | one round trip; depth guard honoured |
| 11 | `SubtreeTests.Rollup_query_count_is_constant` | 5 vs 500 items → same statement count — **AC6** |
| 12 | `ApplyOrderingTests.Rejects_non_accepted_document` | `409 DOCUMENT_NOT_ACCEPTED` |
| 13 | `ApplyOrderingTests.Rejects_wrong_document_type` | `409 DOCUMENT_NOT_BACKLOG_ORDERING` |
| 14 | `ApplyOrderingTests.Applies_the_documents_order_exactly` | resulting SQL order == entry order |
| 15 | `ApplyOrderingTests.Partial_application_reports_per_item_outcomes` | stale/foreign/valid keys mixed — **AC7/D8** |
| 16 | `ApplyOrderingTests.Second_apply_is_a_no_op` | row-for-row rank equality — **AC8/D10** |
| 17 | `ApplyOrderingTests.Unreferenced_items_are_appended_in_relative_order` | **AC8/D9** |
| 18 | `ApplyOrderingTests.Rejects_when_no_document_exists` | the pre-41-1b state resolves cleanly, not with a 500 |
| 19 | `TrackerCatalogDescriptorTests.Three_new_routes_have_descriptors` | extends 44-2's test 20 |

Tests 5, 7, 9, 11, 14–17 are Testcontainers; the rest are service-level with a fake repository.

## Definition of Done

- 19 tests green.
- `DocumentTypeKey` still has exactly ten members and `DocumentTypeKeyTests.cs:20` / `DocumentTypeRegistryTests.cs:37` are **unmodified** — grep-checked in review (D6).
- No file under `docs/stories/epic-41/` or `apps/tamma-elsa/src/Tamma.Core/Documents/` is modified by this story.
- No new tenant migration.
- The apply seam emits no `DOCUMENT.*` event and creates no document revision (D11), asserted by test 12–17 event-stream inspection.

## Dependencies & Sequencing

- **Blocked by:** 44-0, 44-1, 44-2.
- **Blocks:** 44-4 (the `SprintPlan` apply seam copies this seam's shape wholesale), 44-6 (drag-and-drop calls `move`/`parent`).
- **Non-blocking dependency:** 41-1b + 41-3. The seam is inert until an accepted `BacklogOrdering` exists; test 18 pins that the inert state is a clean rejection.
- **Shared-edit register:** `TrackerEndpoints.cs`, `Program.cs` tracker group, `TrackerActionDescriptors.cs`, `WorkItemRepository.cs` — all shared with 44-4, which lands immediately after. Sequence rather than parallelise.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **Roll-ups ship as N+1** and are invisible until a customer has thousands of items. | Test 11 asserts a constant statement count at two very different row counts. |
| **`BacklogOrdering`'s body shape is guessed.** 41-1b is `drafted`; the field names in `BacklogOrderingEntry` are inferred from 41-1b `:32`'s domain rule. | The port (D6) is the entire coupling surface — one interface, one record, one parse method. If the shape lands differently, one file changes. The rest of the seam addresses items by `issueId`, which 41-1b cannot change without breaking its own lineage AC. |
| **Partial application looks like data loss** to a user who does not read the outcome list. | 44-6 renders the skipped entries explicitly; 44-5 emits the counts as event data, so the skip is in the audit trail, not only in a response body. |
| **Appending unreferenced items (D9) surprises someone** who expected their new bug to stay where they put it. | The alternative surprises are worse and are argued in D9. Recorded as a decision so it is revisited deliberately, not by bug report. |
| **`move` under heavy concurrency produces rank strings that grow.** | 44-0's `Rank` test 10 bounds length over 10 000 midpoint insertions between a *fixed* pair — the worst case. Real usage spreads insertions. No rebalancing job is needed at v1 scale; if one is ever needed it is a single ordered rewrite. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–3 (hierarchy service, ancestor/subtree CTEs, move) | 1.25 |
| Steps 4–5 (reader port + apply service, D8/D9/D10) | 1.0 |
| Steps 6–9 (endpoints, mapping, DI, descriptors) | 0.5 |
| Step 10 (19 tests, incl. two concurrency and one benchmark) | 1.0 |
| Review | 0.25 |
| **Total** | **4.0** |
