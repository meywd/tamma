# Implementation Plan — Story 44-3: Hierarchy, Ranking, and the `BacklogOrdering` Apply Seam

> **⚠️ AMENDED 2026-08-01 (scoping round). Read the story's Amendment section
> first — it is normative where this plan disagrees with it.** Six corrections
> land here: D6's justification was false in all three premises and its port
> rationale is void (A2); `BacklogOrderingEntry`'s field names and types are
> wrong three ways (A5); step 9's `TrackerActionDescriptors.cs` does not exist
> (A1); the 43-8 governance ratchet makes a `.Governs` binding **mandatory** for
> the three new routes and neither this plan nor the story mentioned it (A3);
> 44-2's rank-uniqueness constraint had no AC and is now AC12, taken via the
> `(Rank, Id)` tie-break (A4); and steps 2–3 name repository methods
> (`MoveAsync`, `BulkSetRankAsync`) that 44-1 shipped under different names.

## Scope & Deliverable

When this story is done a work item can be reparented under rules the database and the service both enforce, moved between two neighbours with exactly one row write, read as a subtree in one recursive CTE with roll-up counts computed in SQL, and — the load-bearing half — an **accepted `BacklogOrdering` document from Story 41-3 can be applied to the backlog's actual order in one idempotent transaction**. That apply seam is the concrete answer to a boundary the epic draws in two directions: 41-3 owns the proposal, Epic 44 owns the record, and neither changes the other.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §3 (hierarchy invariants — the parenting matrix was deleted — and why one table), §4 (rank), the 41-3 boundary row
- `docs/stories/epic-44/story-44-0/implementation-plan.md` — D4 (`TrackerHierarchy`, `MaxDepth` enforced elsewhere), D7 (rank algebra + the collation trap)
- `docs/stories/epic-44/story-44-1/implementation-plan.md` — D3 (`COLLATE "C"`), Data & Migrations (`ParentId` `RESTRICT`, indexes), `BulkSetRankAsync`
- `docs/stories/epic-44/story-44-2/implementation-plan.md` — D2 (tri-state PATCH), D7 (keyset paging), D10 (catalog descriptors)
- `docs/stories/epic-41/story-41-3/41-3-backlog-prioritization-and-grooming.md:17-25,:44` — what 41-3 produces and its AC3
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md:32,:53,:62,:64-67` — `BacklogOrdering`'s domain rule, its `backlog-ordering` wire, its store round-trip AC. *[2026-08-01: read this as **history**. 41-1b is `done`; the code (`Types/BacklogOrdering.cs`) is authoritative over the story text, and the pins it moved now read 17, not 16 — 41-1c added `prose` after it. See that story's own dated AC4 amendment.]*
- `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs:34,37,55,61,92` — `DocumentType`, `IssueId`, `Revision`, `Status`, **`BodyJson`** (the payload column; this plan previously called it `Body`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:20-29` — `accepted` is the wire the seam requires
- ~~`apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs:22-33` — ten members today; **this story adds none**~~ *[CORRECTED 2026-08-01 — A2.]* `DocumentTypeKey.cs:40` — `[Wire("backlog-ordering")] BacklogOrdering` **exists**; the enum has **17** members, not ten. This story still adds none, because there is none to add.
- **NEW pre-reading (2026-08-01):**
  - `apps/tamma-elsa/src/Tamma.Core/Documents/Types/BacklogOrdering.cs:13-30` — the **shipped** `BacklogItem` / `BacklogOrdering` records the seam must deserialize; `:38-184` the validator and its eight violation codes; `:196-241` the LLM contract + examples
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/KnownUngovernedEndpoints.cs:112-157` — the ratchet, its `PinnedCount`/`PinHistory`/`PinnedInScopeCount`, and 44-2's ten `binding-owned-by` entries at `:210,218,242,254,264,407,519,521,523,591`
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/GovernedEndpointCoverageSweepTests.cs:96-125,245-295` — the coverage rule and the shrink-only assertion
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/GovernedEndpointBindingSweepTests.cs:71-115` — the ordinal `SiteKey`↔route check every new descriptor must satisfy
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/TrackerRbacTests.cs:296-334` — 44-2's bidirectional descriptor harness (`HaveCount(10)` at `:325`, `tracker.` prefix filter at `:320`/`:331`)
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/TrackerEndpointsTests.cs:639-671` — `Duplicate_ranks_within_a_project_are_accepted_today`, written for this story to flip
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IWorkItemRepository.cs:135-158` — the **real** method names: `SetRanksAsync`, `SetParentAsync`, `RekeyAsync`
- ~~**All referenced paths exist.** NOT FOUND (planned by 41-1b, no code): a `backlog-ordering` `DocumentTypeKey` member and its `IDocumentType`. AC9's port is written so that absence blocks nothing.~~ *[WRONG — CORRECTED 2026-08-01.]* Both exist (`DocumentTypeKey.cs:40`, `Types/BacklogOrdering.cs:38`). The one path in this plan that genuinely does **not** exist is step 9's `TrackerActionDescriptors.cs` — see step 9.

## Design Decisions

- **D1 — Hierarchy invariants are enforced in the service, inside the write transaction, computed from persisted state.** The database cannot express "no cycles at depth ≤ `MaxDepth` across a self-referencing FK" without a trigger, and a trigger here would be a second rule engine disagreeing with `TrackerHierarchy`. So: `SELECT … FOR UPDATE` the ancestor chain, evaluate `CanParent` + depth + cycle, write, commit. AC2's concurrency test drives two reparents that jointly form a cycle and asserts exactly one commits — the case a naive read-then-write silently allows.

- **D2 — Three distinct rejection reasons, each named on the wire.** `KIND_NOT_PERMITTED` (`Epic` under `Task`), `MAX_DEPTH_EXCEEDED`, `WOULD_CREATE_CYCLE`, `CROSS_PROJECT_PARENT`. A single `400 "invalid parent"` is unactionable in a drag-and-drop UI, where the user needs to know *which* rule stopped them — and 44-6 renders the reason inline.

- **D3 — `move` is neighbour-addressed `(afterId?, beforeId?)`, never index-addressed.** An index is a position in a list the client last saw; under concurrent drags it is stale by construction and the resulting write is *wrong*. Neighbours are a statement about the intended relation, and the worst outcome under concurrency is a rank between two items that have since moved — visible and harmless. Both null means "to the end". The write is exactly one `UPDATE`, which is the whole point of the fractional index (44-0 D7); a test asserts the statement count.

- **D4 — Kind change re-validates against parent *and* children, in one check.** A `PATCH` that changes `kind` is a hierarchy mutation wearing a field update's clothes. Under 44-0's invariants the only kind rule is "an Epic may not be a child of a non-Epic", so the case that actually bites is changing an item **to** `Epic` while it has a non-Epic parent — validating only against the parent, or only against the children, misses one direction of it. The check enumerates both edges and returns every violation, not the first.

- **D5 — Subtree is one recursive CTE with an explicit depth guard, and roll-ups are SQL aggregates.** `WITH RECURSIVE` bounded by `WHERE depth < TrackerHierarchy.MaxDepth`, so a cycle introduced by a bug elsewhere is a truncated result rather than an infinite scan. `childCount` / `childCountByStatus` are `LEFT JOIN LATERAL` aggregates in the same statement — AC6's test asserts a **constant** query count in the number of rows, because the natural implementation of a roll-up is N+1 and it will not be visible until a customer has 2 000 items.

- ~~**D6 — The apply seam reads `BacklogOrdering` through `IBacklogOrderingReader`, a narrow port, and adds no `DocumentTypeKey` member.** `DocumentTypeKey` has exactly ten members (`:22-33`) and its count is pinned twice — `DocumentTypeKeyTests.cs:20` `Be(10)` and `DocumentTypeRegistryTests.cs:37` `HaveCount(10)`. 41-1b's D2 owns moving those pins as part of registering six types. Moving one of them here would take a pin out of its owning story, break 41-1b's diff, and register a type with no `IDocumentType` validator behind it.~~
  ~~```csharp~~
  ~~public sealed record BacklogOrderingEntry(string IssueId, string? Rationale, decimal? Value, decimal? Effort);~~
  ~~```~~
  ~~Production impl reads `DocumentInstance.Body` and matches `DocumentType == "backlog-ordering"` **by wire string**, which works whether or not the enum member exists yet. When 41-1b lands, the string comparison becomes an enum comparison in one line.~~

- **D6 (REWRITTEN 2026-08-01 — story Amendments A2 + A5).** *What the old D6 said and why it is void:* every one of its three premises was false. `DocumentTypeKey` has **17** members, not ten (`DocumentTypeKey.cs`; pins are `DocumentTypeKeyTests.cs:24` `Be(17)` and `DocumentTypeRegistryTests.cs:42` `HaveCount(17)`, not `:20`/`:37` at 10); `[Wire("backlog-ordering")] BacklogOrdering` is at `DocumentTypeKey.cs:40` with `BacklogOrderingDocumentType` behind it (`Types/BacklogOrdering.cs:38`); and 41-1b is `done` (`docs/sprint-status.yaml:630`). The port's stated purpose — "works whether or not the enum member exists yet", "when 41-1b lands, one line changes" — describes a problem that no longer exists.

  **The half of D6 that survives, with a new reason:** this story adds no `DocumentTypeKey` member and moves no document-type pin, because **there is nothing to add**. `DocumentTypeKeyTests.cs:24`, `DocumentTypeRegistryTests.cs:42` and `ActionVocabularyCountTests.cs:41` all stay at 17 and must be unmodified in the diff.

  **The half that does not survive:** the port is no longer a coupling firewall, it is an ordinary seam. Keep it for the two reasons that are still true — it holds the tenant-scoped `GetByIdAsync(tenantId, documentId, ct)` read (`IDocumentInstanceRepository.cs:40`) and AC7's type+status gate in one testable place, and it is fixture-substitutable. **Do not keep it as a shape declaration.** The shipped types are the shape:

  ```csharp
  public interface IBacklogOrderingReader
  {   // null when the document is missing, not backlog-ordering, or not accepted
      Task<BacklogOrderingView?> ReadAcceptedAsync(Guid tenantId, Guid documentId, CancellationToken ct);
  }

  // Entries are Tamma.Core.Documents.Types.BacklogItem — the SHIPPED record.
  // Do NOT declare a parallel entry type here (story AC9 pins this with a test).
  public sealed record BacklogOrderingView(
      Guid DocumentId, int Revision, IReadOnlyList<BacklogItem> Entries);
  ```

  Production impl: `GetByIdAsync` → check `DocumentType == DocumentTypeKey.BacklogOrdering.ToWire()` and `Status == DocumentInstanceStatus.Accepted.ToWire()` → `JsonSerializer.Deserialize<BacklogOrdering>(row.BodyJson, DocumentJson.Options)` (the column is `BodyJson`, `DocumentInstance.cs:92`) → `doc.Items`.

- **D6b — the entry shape, corrected three ways (2026-08-01, story Amendment A5).** The old `BacklogOrderingEntry(string IssueId, string? Rationale, decimal? Value, decimal? Effort)` was wrong in three independent ways against `Types/BacklogOrdering.cs:13-20`:
  1. **`itemId`, not `issueId`** (`:15`). `issueId` is `DocumentInstance`'s lineage anchor column — a different field at a different level. Consuming `issueId` would deserialize to empty strings for every entry and resolve nothing.
  2. **`value` / `effort` are non-nullable free-text `string`s, not `decimal?`** (`:18-19`). Deliberate: *"estimate units differ per team, so the vocabulary is deliberately NOT closed"* (`:9-11`), and the validator rejects whitespace in either (`:121-124`, code `ITEM_MISSING_ESTIMATE`). A `decimal?` binding fails to parse `"high"` / `"1d"` — the values in the shipped example (`:222-227`).
  3. **Order comes from `BacklogItem.Rank`, an `int?` on the entry (`:16`), not from array position.** The validator forces it to be the unique, gap-free `1..N` sequence with no ties (`AddRankViolations`, `:140-184`, codes `RANK_DUPLICATED` / `RANK_NOT_TOTAL_ORDER`), so `Rank` **is** the accepted order and `items[]` need not be sorted. Reading array position silently applies a different order than the human accepted, and no test catches it unless one is written for it. **The apply service sorts by `BacklogItem.Rank` before assigning tracker ranks**, and a test feeds a document whose `items[]` are shuffled relative to their `rank` values and asserts the tracker order follows `rank`.

  Note the name collision and keep it visible in code: `BacklogItem.Rank` is a **1-based `int` position inside the document**; `WorkItemEntity.Rank` is a **base-62 fractional-index string** (`WorkItemEntity.cs:83`). They are not the same thing and must never be assigned across.

- **D7 — The seam requires `Status == accepted` and returns `409`, not `400`, when it is not.** The caller's request is well-formed and they are authorized; the *document* is not in a state the system will act on. That is the same distinction Epic 43 draws for its gate (`epic-43/README.md:306`). Two failure codes, named: `DOCUMENT_NOT_BACKLOG_ORDERING`, `DOCUMENT_NOT_ACCEPTED`.

- **D8 — Partial application with a per-item outcome list, not all-or-nothing.** A `BacklogOrdering` is produced against a snapshot; by the time a human accepts it, some referenced items may be deleted, moved to another project, or never have existed (an LLM-authored ordering can reference a key that does not resolve). Failing the whole apply because one of forty keys is stale makes the feature unusable. Every entry returns `applied | not-found | wrong-project`, the resolvable subset is written in one transaction, and the response is the audit record of what was skipped. 44-5 emits one `WORKITEM.ORDERING_APPLIED.SUCCESS` carrying the counts.

- **D9 — Unreferenced items are appended after the applied set, preserving their relative order.** The alternatives are worse: interleaving by old rank buries newly-filed work in the middle of a stale ordering; leaving them in place means the applied ordering is not actually the order. Appending is the only option in which the document's order is exactly honoured *and* nothing is hidden. Implemented as: applied items get ranks from `Rank.First()` forward; unreferenced items are re-ranked after the last applied one, in their existing relative order.

- **D10 — Idempotence is by outcome, not by a marker column.** Re-applying the same accepted document recomputes the same ranks and writes the same values, so the second run is a no-op by construction — no `AppliedDocumentId` column, no dedupe table. A test asserts row-for-row equality after a second apply. (A marker column would also be wrong: the same ordering *should* be re-appliable after someone drags a card, to reset to the accepted state.)

- **D11 — The apply seam is a tracker operation that reads a document; it is not a lifecycle step.** It emits no `DOCUMENT.*` event, does not mutate the document, does not create a revision, and does not call into `DocumentLifecycleWorkflow`. 41-3 remains unchanged and unaware. Stated explicitly because the natural instinct is to make it a lifecycle terminal, which would put a tracker write inside a workflow that has its own re-entry and acceptance semantics.

- **D12 — The three new routes MUST be `.Governs`-bound; baselining them is mechanically impossible (NEW 2026-08-01, story AC11 / Amendment A3).** Neither the story nor this plan mentioned Epic 43's ratchet, and the obvious path — copy what 44-2 did — is closed. 44-2 baselined its ten routes as `binding-owned-by Story 44-2` (`KnownUngovernedEndpoints.cs:210-211` and nine siblings), but that was before the ratchet turned. It has turned since: `PinnedCount = 216` (`:128`) must equal `PinHistory[^1]`, `PinHistory = [237, 216]` (`:142`), and `TheRatchetPin_IsMechanicallyShrinkOnly` asserts the history strictly decreases (`GovernedEndpointCoverageSweepTests.cs:269-295`). Three more baseline entries need `219`; appending `219` after `216` turns that fixture red, and the failure message says so in as many words: *"A new ungoverned route is not a reason to raise the pin — it is the signal the ratchet exists to produce."*
  So the binding is not a nice-to-have this story may defer to 43-9 — **it is the only way the three routes can exist**. Concretely:
  - `.Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.TrackerWorkItemReparent.ToWire()))` and siblings, chained on each `Map*` call (`GovernsExtensions.cs:28`; the live shape at `Program.cs:3126`). Note the group overload was **removed** (`GovernsExtensions.cs:31-38`) — bind per route, never per group.
  - **Do not** touch `PinnedCount` / `PinHistory`. **Do** bump `PinnedInScopeCount` 237 → 240 (`:157`): a bound route leaves the baseline but stays in the mutating surface, and `InScopeEndpointCount_isPinned` (`:245-254`) is a plain `HaveCount` with no direction rule.
  - **Do not** opportunistically bind 44-2's ten. That decrements `PinnedCount` and requires a `PinHistory` append — legal, but it moves another story's ledger inside this diff and makes the review two arguments instead of one.
  - Cost check: binding is **metadata only** today (`ActionGateMetadata` is "a marker with no behaviour: Story 43-9 adds the endpoint filter that reads it"), and all three descriptors ship at `AutonomyDial.Min` like 44-2's ten (`TrackerRbacTests.cs:329-332` asserts it). So nothing is gated by this story; the ACs stay behaviour-preserving.

- **D13 — Rank uniqueness is answered with an immutable tie-break, not a unique index (NEW 2026-08-01, story AC12 / Amendment A4).** 44-2 left this story a written constraint and a test built to be flipped (`TrackerEndpointsTests.cs:639-671`). Two branches were offered; take the tie-break.
  - **Why not the unique index.** `UNIQUE (ProjectId, Rank)` needs a **new tenant migration**, which contradicts this plan's own "Data & Migrations: **None**" and spends the resource 44-1 D4 calls the scarcest in the repo. It also needs a backfill for any duplicate already stored, and it converts a benign concurrent equal-rank write into a `23505` on the drag path.
  - **Why the tie-break works.** The defect is compound: a duplicate rank *plus* a `Key` change. `RekeyAsync` is a sanctioned key mutation (`IWorkItemRepository.cs:149-158`); `Id` is a UUIDv7 assigned at create and never changes. Swapping the second sort key from `Key` to `Id` removes the mutable half of the tuple, so duplicates become harmless rather than forbidden.
  - **The edit surface** is small and inside code this story already opens: `WorkItemRepository.cs:106-110` (keyset predicate) and `:115-116` (`OrderBy(Rank).ThenBy(Key)` → `.ThenBy(Id)`), `TrackerService.EncodeCursor/DecodeCursor` (`:485`, `:493`) and their `WorkItemListQuery.AfterRank`/`AfterKey` fields (rename to `AfterId`, typed `Guid?`), and the two call sites at `TrackerService.cs:190` / `:220`.
  - **The honest cost, from 44-2's own A1:** `Key` was chosen *because* it is the column the SQL `ORDER BY` already carries under `COLLATE "C"`, so `Id` is a second, unindexed sort key. The tie set is the rows sharing a byte-identical rank string — normally one — so the sort is bounded and no index is added. If a benchmark ever disagrees, the index goes in **44-1's** migration if unshipped, per this plan's Data & Migrations rule.
  - **Cursor compatibility:** the encoded cursor changes shape, so cursors minted before the change decode to a non-`Guid` second field. Reject them as `TRACKER.INVALID_CURSOR` (the existing code, pinned by `TrackerEndpointsTests.A_forged_cursor_is_400_rather_than_a_silent_restart:673`) rather than silently restarting at page 1. No data migration — cursors are ephemeral.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/TrackerHierarchyService.cs`** — `ReparentAsync`, `ValidateKindChangeAsync`, `GetSubtreeAsync`. All hierarchy invariants (D1/D2/D4) live here; `TrackerService` (44-2) delegates.

2. **MODIFY `apps/tamma-elsa/src/Tamma.Data/Repositories/WorkItemRepository.cs`** — add `GetAncestorChainAsync(Guid id)` (`SELECT … FOR UPDATE` over a recursive CTE), `GetSubtreeAsync(Guid id, int maxDepth)` (D5's CTE + lateral roll-ups), `ListWithRollupsAsync(WorkItemQuery)`.

3. ~~**MODIFY `WorkItemRepository`** — `MoveAsync(Guid id, string newRank, int expectedVersion)`~~ *[CORRECTED 2026-08-01 — the method 44-1 shipped is `SetRanksAsync(Guid id, string? rank, string? siblingRank)` (`IWorkItemRepository.cs:141`, impl `WorkItemRepository.cs:254`), and it covers **both** ordering axes (`Rank` and `SiblingRank`, `WorkItemEntity.cs:83,86`). There is no `MoveAsync` and no `BulkSetRankAsync` anywhere in the tree.]* **MODIFY `WorkItemRepository.SetRanksAsync`** to take an optional `int? expectedVersion` and apply it as `WHERE "Id" = @id AND "Version" = @v`, matching the precondition plumbing 44-2's review added to `UpdateAsync` / `SetStatusAsync` / `DeleteAsync` (`IWorkItemRepository.cs:109-134`). Keep it one `UPDATE`.

3b. **MODIFY `WorkItemRepository`** — apply D13's tie-break: keyset predicate `:106-110` and `OrderBy(Rank).ThenBy(Key)` `:115-116` move to `Id`; add whatever bulk rank write the apply seam needs (**a bulk method does not exist** — 44-1 shipped only the single-row `SetRanksAsync`; step 5 previously assumed a `BulkSetRankAsync` that must actually be written here).

4. **CREATE `Tamma.Api/Services/Tracker/IBacklogOrderingReader.cs` + `DocumentBacklogOrderingReader.cs`** — per the rewritten D6/D6b. Tenant-scoped `GetByIdAsync(tenantId, documentId, ct)`; requires `DocumentType == DocumentTypeKey.BacklogOrdering.ToWire()` and `Status == DocumentInstanceStatus.Accepted.ToWire()`; deserializes `BodyJson` into the **shipped** `Tamma.Core.Documents.Types.BacklogOrdering`. Declares **no** entry record of its own.

5. **CREATE `Tamma.Api/Services/Tracker/OrderingApplyService.cs`** — per D7/D8/D9/D10. Reads the view, **sorts entries by `BacklogItem.Rank`** (D6b#3), resolves entries by `itemId` → `work_items."Key"` *(not `issueId` — D6b#1; and see the story's C2: nothing guarantees `itemId` carries a work-item key, so the resolver's happy path is fixture-tested only until 41-3 says otherwise)*, computes the tracker rank sequence, writes it in one transaction, returns the outcome list.

6. **MODIFY `Tamma.Api/Endpoints/TrackerEndpoints.cs`** — add `SetParent`, `Move`, `GetSubtree`, `ApplyOrdering`.

7. **MODIFY `Program.cs`** — map the four routes in the 44-2 tracker group (`Program.cs:2977-3016`). `parent` / `move` require `TrackerView` (a member reorders their own board); `apply-ordering` requires `TrackerManage` (it rewrites the whole project's order). Rate limit `ConfigWrite`. **Chain `.Governs(…)` on each of the three mutating routes — mandatory, per D12.** (`GET …/subtree` is a read and is neither catalogued nor bound, matching 44-2's rule that the eight tracker GETs are not catalogued.)

8. **MODIFY `Program.cs` — DI**: `TrackerHierarchyService`, `OrderingApplyService`, `IBacklogOrderingReader` → `DocumentBacklogOrderingReader`, all `AddScoped`.

9. ~~**MODIFY `Tamma.Api/Services/Tracker/TrackerActionDescriptors.cs`** — three entries: `effect:work-item.reparent`, `effect:work-item.move`, `effect:backlog.apply-ordering`.~~ **[FILE DOES NOT EXIST — CORRECTED 2026-08-01, story Amendment A1.]** 44-2's own plan already recorded that `TrackerActionDescriptors.cs` was never created (`story-44-2/implementation-plan.md:29-33`). The descriptors live in two Core files:
   - **MODIFY `Tamma.Core/Actions/ExternalEffect.cs`** — three `[Wire]` members in the 44-2 block (`:161-220`). Wires **must** start `tracker.` (`TrackerRbacTests.cs:320` filters on it): `tracker.work-item.reparent`, `tracker.work-item.move`, `tracker.backlog.apply-ordering`. The proposed `effect:work-item.*` / `effect:backlog.*` names would be invisible to 44-2's harness.
   - **MODIFY `Tamma.Core/Actions/ActionCatalog.Descriptors.cs`** — three `Effect(…)` rows next to 44-2's ten (`:441-459`), `ActionGroup.IssueTracking`, `ActionRisk.Mutating`. **`SiteKey` must be `"{METHOD} {live pattern}"` verbatim including `:guid`** — `GovernedEndpointBindingSweepTests` compares it ordinally (`:89-100`) and 44-2 already shipped this bug once (its MODERATE-5 correction, `ActionCatalog.Descriptors.cs:434-439`).
   - *On `DefaultMinAutonomy`:* the old step said apply-ordering "carries a higher `DefaultMinAutonomy` than the first two". **It cannot.** `TrackerRbacTests.cs:329-332` asserts every `tracker.*` descriptor is `AutonomyDial.Min`, and Epic 43's D1 requires descriptors to ship behaviour-preserving. All three ship at `AutonomyDial.Min`; the difference in blast radius is expressed by `apply-ordering` requiring `TrackerManage` at the route (step 7), not by the dial.

9b. **MODIFY the count pins in the same commit** (all four are hard failures otherwise): `ActionVocabularyCountTests.ExternalEffect_has_39_members` (`:54,:80`) → 42 with a derivation comment in the file's established style; `TotalCatalogMembers_is_197` (`:132,:147`) → 200; `TrackerRbacTests.cs:325` `HaveCount(10, "AC2's ten mutating tracker routes")` → 13 with the reason reworded; `KnownUngovernedEndpoints.PinnedInScopeCount` (`:157`) 237 → 240. **`PinnedCount` (`:128`) and `PinHistory` (`:142`) are NOT touched** — D12.

10. **CREATE tests** under `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/`.

## Data & Migrations

**None** — and the AC12 tie-break was chosen partly to keep that true (D13; story Amendment A4). 44-1 created `ParentId` (self-FK, `RESTRICT`), `Rank` (`COLLATE "C"`) and the `(ParentId)` and `(ProjectId, Status, Rank)` indexes. A `UNIQUE (ProjectId, Rank)` index — the other branch 44-2 offered for the rank-uniqueness constraint — **would** have opened a tenant migration and falsified this line; the tie-break is a query + cursor change instead. If the subtree CTE or the new `ORDER BY … "Id"` shows a missing index in the AC6 benchmark, it is added in **44-1's** migration if that has not yet shipped, or in a follow-up — this story does not open a second tenant migration (44-1 D4: tenant migrations are the scarcest resource in the repo).

## Events

None emitted here — 44-5 owns emission and adds it inside these services. 44-5's catalogue reserves: `WORKITEM.REPARENTED.SUCCESS`, `WORKITEM.MOVED.SUCCESS`, `WORKITEM.ORDERING_APPLIED.SUCCESS` (data: `documentId`, `revision`, `applied`, `notFound`, `wrongProject`), `WORKITEM.ORDERING_APPLIED.FAILED`. Listed here so 44-5 does not have to re-derive them.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `HierarchyTests.Epic_may_not_be_a_child_of_a_non_epic` | all 16 `(parent, child)` pairs against `TrackerHierarchy.CanParent` — only `(Story\|Task\|Spike, Epic)` rejected. There is no `(parentKind, childKind)` matrix; 44-0 D4 deleted it |
| 1b | `HierarchyTests.Task_under_epic_is_permitted` | the case the deleted matrix forbade — named so a reinstatement fails loudly |
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
| 18 | `ApplyOrderingTests.Rejects_when_no_document_exists` | ~~the pre-41-1b state~~ *[CORRECTED 2026-08-01 — 41-1b is `done`; the state under test is "no 41-3 producer has written one yet"]* resolves cleanly, not with a 500 |
| 19 | ~~`TrackerCatalogDescriptorTests.Three_new_routes_have_descriptors`~~ **`TrackerRbacTests.Every_mutating_route_has_a_descriptor`** | *[CORRECTED 2026-08-01 — no new fixture; extend the shipped bidirectional test at `TrackerRbacTests.cs:296-334`, moving `:325` `HaveCount(10)` → 13]* |

**Added 2026-08-01 (Amendments A3/A4/A5):**

| # | Test | Asserts |
|---|---|---|
| 20 | `ApplyOrderingTests.Order_follows_the_documents_rank_field_not_array_position` | feed a document whose `items[]` are shuffled relative to their `rank` values; tracker order follows `rank` — **D6b#3**. Red on an array-position implementation |
| 21 | `ApplyOrderingTests.Free_text_value_and_effort_round_trip` | `"high"` / `"1d"` (the shipped example, `Types/BacklogOrdering.cs:222-227`) parse — **D6b#2**. Red on a `decimal?` binding |
| 22 | `ApplyOrderingTests.Reader_declares_no_parallel_entry_record` | reflection over `Tamma.Api/Services/Tracker/` finds no type redeclaring `itemId`/`rank`/`rationale`/`value`/`effort` — **story AC9** |
| 23 | `TrackerGovernanceBindingTests.Three_new_routes_are_bound_not_baselined` | each new route's endpoint carries `IActionGateMetadata`; none appears in `KnownUngovernedEndpoints.All`; `PinnedCount` still `216` and `PinHistory` still `[237, 216]` — **story AC11 / D12** |
| 24 | `RankTieBreakTests.Duplicate_rank_plus_rekey_does_not_dup_or_skip` | the compound 44-2 could not drive: collide two ranks via `SetRanksAsync`, page at `limit: 1`, `RekeyAsync` the served row, page again — **story AC12 / D13**. **Must be RED against the shipped `(Rank, Key)` cursor** — verify that before implementing, or the AC cannot fail |
| 25 | `TrackerEndpointsTests.Duplicate_ranks_within_a_project_are_accepted_today` (**edit, do not delete**) | duplicates stay accepted; the message's "44-3 must either…" instruction is replaced by a pointer to the tie-break — **story AC12** |

Tests 5, 7, 9, 11, 14–17, 24–25 are Testcontainers; 23 needs a booted host (`GovernanceHostFixture`); the rest are service-level with a fake repository.

## Definition of Done

- All tests green. *(A count is deliberately not pinned here — 44-2's plan records that its own DoD number was mistaken for a pin, `story-44-2/implementation-plan.md:22-24`.)*
- ~~`DocumentTypeKey` still has exactly ten members and `DocumentTypeKeyTests.cs:20` / `DocumentTypeRegistryTests.cs:37` are **unmodified**~~ *[CORRECTED 2026-08-01 — A2.]* `DocumentTypeKey` still has exactly **17** members and **`DocumentTypeKeyTests.cs:24`** (`Be(17)`), **`DocumentTypeRegistryTests.cs:42`** (`HaveCount(17)`) and **`ActionVocabularyCountTests.cs:41`** are **unmodified** — grep-checked in review.
- **`KnownUngovernedEndpoints.PinnedCount` and `PinHistory` are unmodified; `PinnedInScopeCount` is 240**; `GovernedEndpointCoverageSweepTests` and `GovernedEndpointBindingSweepTests` are green (D12).
- **`ActionVocabularyCountTests` reads 42 / 200 and `TrackerRbacTests.cs:325` reads 13** (step 9b).
- **The list ordering and cursor are `(Rank, Id)`; no `UNIQUE` index on `(ProjectId, Rank)` exists** (D13).
- No file under `docs/stories/epic-41/` or `apps/tamma-elsa/src/Tamma.Core/Documents/` is modified by this story. *(Still true after A2 — `Types/BacklogOrdering.cs` is consumed, never edited.)*
- No new tenant migration.
- The apply seam emits no `DOCUMENT.*` event and creates no document revision (D11), asserted by test 12–17 event-stream inspection.

## Dependencies & Sequencing

- **Blocked by:** 44-0, 44-1, 44-2.
- **Blocks:** 44-4 (the `SprintPlan` apply seam copies this seam's shape wholesale), 44-6 (drag-and-drop calls `move`/`parent`).
- ~~**Non-blocking dependency:** 41-1b + 41-3.~~ *[CORRECTED 2026-08-01.]* **41-1b is `done`** (`docs/sprint-status.yaml:630`) — a shipped input, not a dependency. **41-3 is `drafted`** (`:633`) and non-blocking for the code: the seam is inert until an accepted `BacklogOrdering` exists and test 18 pins that the inert state is a clean rejection. It **is** blocking for the working feature — see the story's "Cross-Story Contract with 41-3" (C1 store anchor, C2 `itemId` semantics).
- ~~**Shared-edit register:** … `TrackerActionDescriptors.cs` …~~ *[CORRECTED 2026-08-01 — A1: that file does not exist.]* **Shared-edit register:** `TrackerEndpoints.cs`, `Program.cs` tracker group, `Tamma.Core/Actions/ExternalEffect.cs`, `Tamma.Core/Actions/ActionCatalog.Descriptors.cs`, `WorkItemRepository.cs` — all shared with 44-4, which lands immediately after. **Also now shared with Epic 43's ledger**: `ActionVocabularyCountTests.cs`, `KnownUngovernedEndpoints.cs` (`PinnedInScopeCount` only) and `TrackerRbacTests.cs:325`. Sequence rather than parallelise — two stories bumping the same count pins in parallel is a guaranteed conflict.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **Roll-ups ship as N+1** and are invisible until a customer has thousands of items. | Test 11 asserts a constant statement count at two very different row counts. |
| ~~**`BacklogOrdering`'s body shape is guessed.** 41-1b is `drafted`; the field names in `BacklogOrderingEntry` are inferred from 41-1b `:32`'s domain rule.~~ **[MATERIALISED, NOT A RISK — 2026-08-01, Amendment A5.]** The shape was guessed and the guess was wrong three ways; the mitigation ("one file changes") was never exercised because nobody re-read the tree after 41-1b landed. | The shape is now **shipped and validator-pinned** (`Types/BacklogOrdering.cs:13-20`) and the seam consumes it directly (D6/D6b). Tests 20–22 pin each of the three corrections. **Residual:** the story's C2 — nothing constrains `itemId` to a work-item key, so the resolver's happy path is fixture-tested only. |
| **The three new routes cannot be baselined (governance ratchet).** Neither the story nor this plan knew about it; the natural move — copy 44-2's `binding-owned-by` baseline entries — turns `TheRatchetPin_IsMechanicallyShrinkOnly` red. | D12 makes `.Governs` mandatory and enumerates exactly which pins move and which do not. Test 23 asserts bound-not-baselined. Binding is metadata-only until 43-9, so the ACs stay behaviour-preserving. |
| **The `(Rank, Id)` tie-break silently no-ops** if someone writes test 24 after the change instead of before, since keyset paging is stable in the common case regardless. | The AC and test 24 both say it must be **verified RED against the shipped `(Rank, Key)` cursor first**. An acceptance criterion that cannot fail is not one. |
| **`BacklogItem.Rank` (int, 1..N) and `WorkItemEntity.Rank` (base-62 string) share a name.** A single wrong assignment silently writes a document position into the fractional index and corrupts the tracker order with no exception. | D6b names the collision explicitly; the reader returns the shipped `BacklogItem` so the types do not implicitly convert; test 20 would catch a transposition. |
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
| **Total (pre-amendment)** | **4.0** |

**Added 2026-08-01 — not costed in the 4.0 above:**

| Task | Days |
|---|---|
| Step 3b + D13 tie-break (repo query, cursor encode/decode, `WorkItemListQuery`, call sites) + test 24 driven red-then-green + test 25 edit | 0.5 |
| Step 9's real files (two Core files instead of one that does not exist) + step 9b's four count pins + D12 bindings + test 23 | 0.5 |
| D6/D6b rework (consume the shipped types, sort by `BacklogItem.Rank`) + tests 20–22 | 0.25 |
| **Revised total** | **≈5.25** |
