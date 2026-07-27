# Story 44-3: Hierarchy, Ranking, and the `BacklogOrdering` Apply Seam

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

As a **product owner**,
I want to nest work items under a bounded hierarchy and reorder a backlog by dragging — and I want an accepted `BacklogOrdering` document from Story 41-3 to be *applied* to that order in one operation,
So that prioritisation produced by a workflow becomes the actual order of the actual backlog, instead of an accepted document nobody can act on.

## Priority

P1 — Wave 1. Hierarchy and ranking are what make a list a tracker. The apply seam is the concrete answer to "where does 41-3's output go", and the epic boundary depends on it existing.

## Architectural Context (READ FIRST)

- **The hierarchy invariants and the rank algebra already exist as pure Core types** from Story 44-0: `TrackerHierarchy.CanParent(parent, child)`, `TrackerHierarchy.MaxDepth` (**6**), `Rank.Between/First/Append/Prepend` (there is no `Rank.Last()` — a fixed sentinel collides on two consecutive appends; append is `Append(currentMax)`). This story adds the *enforcement* and the *I/O* — Core stays pure (44-0 AC15).
- **There is no `(parentKind, childKind)` matrix to enforce.** 44-0 deleted it: it had three distinct rows, so it encoded *level* while claiming to encode *kind*, and it rejected valid decompositions (a task under a small epic; decomposing a task, against the shipped depth-4 `Epic → Story → DecompositionTask → PlanTask` chain). `CanParent` now enforces exactly one kind rule — *an Epic may not be a child of a non-Epic* — and everything else this story validates (cycles, depth, cross-project) is structural. A null parent is always legal for every kind (`IsDefaultRoot` is a UI hint, not a validator), so `CanParent` is simply not consulted for a top-level item.
- **Grouping by status goes through `WorkItemStatus.Category()`** (44-0 AC3), never through a set literal in this story. Three statuses (`in_progress`, `in_review`, `blocked`) are all category `started`; writing that set by hand here is how it drifts from 44-4, 44-6, 44-7 and 44-9.
- **The rank column's ordering only holds under `C` collation** (44-1 D3, `work_items."Rank" text COLLATE "C"`). Every query in this story orders by it in SQL, never in memory.
- **`ParentId` is `ON DELETE RESTRICT`** (44-1 Data & Migrations). Reparenting, not cascading, is the model.
- **41-3 produces a document, not an order.** `docs/stories/epic-41/story-41-3/41-3-backlog-prioritization-and-grooming.md:17-25`: "Thin binding over document-lifecycle. `consumes: [backlog items (issues), TriageDecisions …, Findings]` / `produces: BacklogOrdering`. Produce cell `(product_owner, prioritize-backlog)`." The document is "a total order over the referenced item set; every item has a rationale + value/effort estimate; no ties" (`docs/stories/epic-41/story-41-1/41-1b-new-document-types.md:32`). It is stored as one immutable, revisioned row in `document_instances` (41-1b AC3 `:62`; 39-11 is `done`, `docs/sprint-status.yaml:540`).
- **`BacklogOrdering` does not exist in code yet.** `DocumentTypeKey` (`apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeKey.cs:22-33`) has exactly ten members and 41-1b is `drafted` (`docs/sprint-status.yaml:603`). The apply seam is therefore written against the *shape* and reads through `IDocumentInstanceRepository`, so it compiles and tests against a fixture today.
- **The reference implementation for reading an accepted document:** Story 39-11's repository and lineage API — `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs` (`IssueId:37`, `DocumentType:34`, `Status:61`, `Revision:55`) and `Tamma.Api/Endpoints/DocumentEndpoints.cs`. Accepted-status wire is `DocumentInstanceStatus.Accepted` (`Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:25`).
- **The `issueId` join** (epic README §2): a `BacklogOrdering` references items by the same string a work item's `WorkItemRef.ToWire()` produces, so the apply seam resolves by key with no translation table.

## Acceptance Criteria

1. **Reparenting endpoint** `POST /api/work-items/{id:guid}/parent` accepting `{ parentId | null }`. Rejects with `400` naming the rule when: `TrackerHierarchy.CanParent(parent.Kind, child.Kind)` is false; the move would exceed `TrackerHierarchy.MaxDepth`; the parent is the item itself or one of its descendants (cycle); or parent and child are in different projects.

2. **Depth and cycle checks are computed from the persisted chain**, not from the request, and are performed inside the same transaction as the write. A concurrency test drives two simultaneous reparents that would jointly create a cycle and asserts exactly one succeeds.

3. **Move endpoint** `POST /api/work-items/{id:guid}/move` accepting `{ afterId? , beforeId? }` and writing exactly **one** row: `Rank = Rank.Between(after.Rank, before.Rank)`. A test asserts a single `UPDATE` statement and that no other row's `Rank` changes.

4. **Kind change is a validated transition, not a free field.** `PATCH /api/work-items/{id}` changing `kind` re-validates the item against its current parent *and* all its current children, and rejects `400` with the violating relationships listed. A test moves a `Story` with `Task` children to `kind: task` and asserts rejection.

5. **Subtree read** `GET /api/work-items/{id:guid}/subtree` returning the item and its descendants to `MaxDepth`, each with its `Rank`, in one query (recursive CTE), ordered so a client can render a tree without a second sort.

6. **Roll-up counts.** `GET /api/work-items` and the subtree read return, per item, `childCount` and `childCountByStatus`. Computed in SQL, not by N+1 reads; a test asserts the query count is constant in the number of items returned.

7. **Apply seam** `POST /api/projects/{projectId:guid}/apply-ordering` accepting `{ documentId }`. It:
   - reads the `DocumentInstance` by id, **requires `DocumentType == "backlog-ordering"` and `Status == "accepted"`**, rejecting `409` otherwise, naming which condition failed;
   - resolves each referenced item by its `issueId` string to a work item in the project;
   - assigns monotone increasing `Rank`s in the document's order, in **one bulk update in one transaction**;
   - returns a per-item outcome list (`applied` / `not-found` / `wrong-project`), and applies the resolvable subset rather than failing wholesale.

8. **The apply seam is idempotent and non-destructive.** Applying the same accepted document twice is a no-op on the second run. Items in the project that the document does not reference **keep their relative order and are placed after the applied set**, never dropped or randomised. A test asserts both.

9. **`BacklogOrdering` is read through a narrow port, not a hard type dependency.** An `IBacklogOrderingReader` in `Tamma.Api/Services/Tracker/` parses the document body into `IReadOnlyList<string> orderedIssueIds` + optional per-item rationale. Its production implementation reads `DocumentInstance.Body`; tests use a fixture. **This story does not add a `DocumentTypeKey` member and does not touch `DocumentTypeRegistry` or its count pins** — that is 41-1b's, and doing it here would move two pins (`DocumentTypeKeyTests.cs:20`, `DocumentTypeRegistryTests.cs:37`) out of the owning story.

10. **Catalog descriptors** for `parent`, `move`, `apply-ordering` added to `TrackerActionDescriptors` (44-2 D10), in the `issue-tracking` group.

## Technical Notes

- The recursive CTE is the reason `MaxDepth` is bounded at all: a `WITH RECURSIVE` with a depth guard is a fixed-cost query; an unbounded one is not, and a cycle introduced by a bug becomes an infinite scan rather than a rejected write. The CTE carries `WHERE depth < TrackerHierarchy.MaxDepth` (6) as a belt-and-braces alongside AC2's write-time check — **referencing the constant, never a literal**, because 44-0 raised it from 3 to 6 exactly once and a hardcoded `3` here would have survived that change silently.
- `move` deliberately does not accept an index. An index is a position in a list the client last saw, which is stale the moment anyone else drags; `(afterId, beforeId)` is a statement about neighbours and is correct under concurrency — worst case it produces a rank between two items that have since moved, which is a visible, harmless outcome rather than a wrong one.
- The apply seam is a **tracker** operation that *reads* a document. It is not a document-lifecycle step, emits no `DOCUMENT.*` event, and does not mutate the document. 41-3's workflow is unchanged and unaware of it.
- Non-referenced items being appended after the applied set (AC8) is a deliberate choice over interleaving: a `BacklogOrdering` produced against a snapshot will always be missing items filed since, and burying newly-filed work in the middle of an old ordering is the worse surprise.

## Dependencies

- **Story 44-0** — `TrackerHierarchy`, `Rank`. Blocking.
- **Story 44-1** — `ParentId`, `Rank` column, `BulkSetRankAsync`. Blocking.
- **Story 44-2** — the endpoint class, RBAC policies, DTO conventions. Blocking.
- **Story 39-11 Document Store** (`done`) — `IDocumentInstanceRepository` is the apply seam's read. No change required to it.
- **Story 41-1b / 41-3** — define and produce `BacklogOrdering`. **Not blocking**, by AC9's port. The seam is inert until an accepted document exists; a test proves it rejects cleanly when none does.

## Out of Scope

- Adding `BacklogOrdering` to `DocumentTypeKey` or `DocumentTypeRegistry` — 41-1b owns those members and their count pins.
- Any change to 41-3's workflow, prompt cell or acceptance path.
- Iterations and the `SprintPlan` apply seam — 44-4 (same shape, different document).
- Cross-project hierarchy. Rejected in AC1 and not planned; a parent in another project makes the project key meaningless as an identifier prefix.
- Dependency edges (`dependsOn`) between work items. `DecompositionTask` and `PlanTask` both carry `dependsOn` inside their document bodies; promoting that to a tracker-level graph needs a cycle model, a critical-path renderer and a scheduling story. Deferred.

## Estimated Effort

4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
