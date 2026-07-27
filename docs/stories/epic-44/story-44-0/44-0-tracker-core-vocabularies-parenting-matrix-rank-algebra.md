# Story 44-0: Tracker Core — Vocabularies, `WorkItemRef`, Hierarchy Invariants, Rank Algebra, Fail-Loud Index

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

**Read this one specifically:** `.dev/findings/linear-comparison-against-story-44-0.md` — the comparison
against Linear's published GraphQL schema that produced the vocabulary and hierarchy shape below. Every
"why not the obvious thing" in this story traces to it.

## User Story

As a **platform engineer** building the native tracker,
I want the tracker's closed vocabularies, identity type, hierarchy invariants and ordering algebra to exist as a pure, dependency-free core in `Tamma.Core` — validated at startup, count-pinned by tests, and reachable from every assembly,
So that storage, API, engine and UI all bind to one vocabulary instead of four, an out-of-vocabulary kind or status cannot be expressed at all, and grouping logic ("is it started?", "which board column group?") is defined once rather than as set literals at every call site.

## Priority

P0 — Wave 0. Every other story in Epic 44 depends on these types. `Tamma.Core` is the only assembly with zero `ProjectReference`s and is therefore the only place `Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer` and `Tamma.Api` can all reach — the same reason `AgentAction`, `DocumentTypeKey` and Epic 43's `ActionKey` live there.

## Architectural Context (READ FIRST)

- **The `[Wire]` mechanism is the closed-vocabulary guarantee, and it is self-enforcing.** `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs:20` defines `WireAttribute`; the `EnumWire<TEnum>` static constructor (`:39-59`) throws on first touch if a member lacks `[Wire]` (`:50`), two members share a wire string (`:52-54`), or the enum is `[Flags]` (`:46-48`). `TryParse` is **ordinal and case-sensitive** (`:65`), so non-canonical casing in persisted data is rejected rather than coerced. Two caveats to know: there is **no** test asserting that every enum in the solution carries `[Wire]` — enforcement is lazy, triggered only when `EnumWire<T>` is first used for that `T`; and `[Wire]` says **nothing about declaration order**, so any ordinal-sorted vocabulary needs its own order pin. This story adds explicit round-trip tests per enum and an ordinal pin for `TriagePriority`.
- **The status-vocabulary precedent to copy exactly:** `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:20-29` — 7 `[Wire]` members, count-pinned at 7 by `DocumentInstanceStatusTests`, and a DB CHECK constraint `ck_document_instances_status` mirroring the exact wire strings (documented at `:12-14`). `WorkItemStatus` is that shape.
- **The vocabularies to reuse, not re-invent:** `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs:14-20` (`TriagePriority`: urgent/high/normal/low) and `:23-31` (`TriageIssueType`: bug/feature/chore/question/security/docs). Both are `[Wire]`, both have an alias-aware parser (`TriageVocabulary`, `:52-109`, folding `critical`→`Urgent` and `medium`→`Normal`), both are count-pinned (`tests/Tamma.Core.Tests/Documents/Types/TriageDecisionTypeTests.cs:34-43`) — and both are referenced **nowhere outside their own file and tests**. This story gives them their first consumer.
- **`TriageIssueType` already carries `bug` and `chore`, and that is why `WorkItemKind` does not.** Verified: `TriageIssueType = {bug, feature, chore, question, security, docs}` (`TriageDecision.cs:23-31`). A `WorkItemKind` containing `Bug` and `Chore` makes `(Kind=Bug, Type=Feature)` and `(Kind=Story, Type=Bug)` both representable and neither meaningful. See AC1.
- **Do NOT adopt `TriageComplexity`** (`TriageDecision.cs:34-41`). Its `[Wire("epic")]` member is a *size* estimate and would read as a hierarchy level beside `WorkItemKind.Epic`. Epic README §1 records this as an accepted, flagged collision — alongside the larger `bug`/`chore` one this story resolves by deletion.
- **The built-index idiom, kept:** `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:43-163` declares 93 `(role, action)` eligibility cells and *builds* the by-role index at `:170-171` — never hand-maintains it. `TrackerHierarchy` keeps that shape, but the declared cells are now **per-kind structural invariants**, not a `(parentKind, childKind)` whitelist. Why the whitelist is gone is AC3.
- **The fail-loud posture:** `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs:88` validates in a pure `Build(files)` core and throws naming the offender; `SystemPrompts.cs:96` calls it from a static initializer, so a bad tree is a `TypeInitializationException` and the process refuses to serve. `DocumentTypeRegistry.cs:95` and `AcceptanceDefaults.cs:25` both cite it explicitly.
- **The depth-4 chain that already exists in code.** `DecompositionTask` (`Tamma.Core/Documents/Types/Decomposition.cs:29`) and `PlanTask` (`Types/Plan.cs:12`) are separate shipped record types, so `Epic → Story → DecompositionTask → PlanTask` is **depth 4**. The epic README lists materializing `PlanTask`s as a named v2 candidate. A `MaxDepth = 3` forecloses the epic's own roadmap before it is written; AC3 raises it to 6.
- **Both of those types already carry `dependsOn`** (`Decomposition.cs:36`, `Plan.cs:17`). Dependency between units of work is an *existing* concept in this codebase and it is not parenting. AC10 gives it a vocabulary rather than letting it be smuggled into the tree.
- **UUIDv7 already exists:** `apps/tamma-elsa/src/Tamma.Core/Documents/UuidV7.cs` — use it, do not add a second time-sortable id helper.
- **`Tamma.Core.Tracking` is a free namespace** (verified: zero matches in `src/`). So are the type names `WorkItemKind`, `WorkItemStatus`, `Project`, `Iteration`, `Rank`.

## Acceptance Criteria

1. **`WorkItemKind`** — a `[Wire]` enum in `apps/tamma-elsa/src/Tamma.Core/Tracking/WorkItemKind.cs` with exactly four members: `epic`, `story`, `task`, `spike`. Wire round-trip and count (`Be(4)`) pinned by tests.
   **`bug` and `chore` are deliberately absent.** `TriageIssueType` (`TriageDecision.cs:23-31`) already carries both, and AC7 binds it as the work item's *type* axis — so `Kind=Bug` would be a second, partially-overlapping vocabulary for the same fact, and `(Kind=Bug, Type=Feature)` would be a representable statement that means nothing. Kind answers *what may contain what*; type answers *what sort of thing is it*. Two axes, two vocabularies, no overlap.

2. **`WorkItemStatus`** — a `[Wire]` enum with exactly eight members: `triage`, `backlog`, `ready`, `in_progress`, `in_review`, `blocked`, `done`, `cancelled`. Count pinned at 8. The multi-word wires use `_` exactly as `DocumentInstanceStatus`'s `in_review` does — no hyphens, no camelCase.
   **`triage` is the new member and it goes in now, not later.** 44-8 imports GitHub issues and `FetchUntriagedItemsActivity` already exists, so items arrive from outside with nobody having looked at them. Without `triage` those land in `backlog`, silently merging *"we decided not now"* with *"nobody has decided"* — which is the entire value of the queue under README open question 3 (an agent filing 40 items overnight). Adding an enum member **later** is not a feature flag: it is a migration over the `ck_work_items_status` CHECK on `work_items`, the highest-row-count tenant table, **across every tenant schema**, through the migrate-all sweep 44-1 is itself building for the first time. Members are cheap now and expensive forever after.

3. **`WorkItemStatusCategory` and `WorkItemStatus.Category()`** — a `[Wire]` enum with exactly six members: `triage`, `backlog`, `unstarted`, `started`, `completed`, `cancelled`. Count pinned at 6. A `Category(this WorkItemStatus)` extension maps every status to exactly one category, total and exhaustive (no `default:` arm that swallows a new member — a `switch` expression, so adding a status without a category is a **compile** error):

   | Status | Category |
   |---|---|
   | `triage` | `triage` |
   | `backlog` | `backlog` |
   | `ready` | `unstarted` |
   | `in_progress` | `started` |
   | `in_review` | `started` |
   | `blocked` | `started` |
   | `done` | `completed` |
   | `cancelled` | `cancelled` |

   **Why:** three of the eight statuses (`in_progress`, `in_review`, `blocked`) are the same fact wearing three names — an item somebody has started. Without a category, every consumer answers "is it in flight?", "does it count as started?", "which board column group?", "should the loop pick it up?" with a **hardcoded set literal**, and 44-3, 44-4, 44-6, 44-7 and 44-9 each get their own copy to drift. One `Category()` is the single definition. `IsTerminal` becomes derived (`Category() is Completed or Cancelled`) rather than a second hand-maintained set.
   A test pins the full status→category table literally, and asserts every `WorkItemStatusCategory` member is reachable from at least one status (an unreachable category is a vocabulary bug).

4. **`TrackerHierarchy` — structural invariants, not a `(parentKind, childKind)` matrix.** The type exposes:
   - `MaxDepth = 6` — a named constant on the type, not a literal at a call site.
   - `CanParent(WorkItemKind parent, WorkItemKind child)` — enforcing exactly **one** kind rule: *an Epic may not be a child of a non-Epic.* Every other `(parent, child)` pair is permitted. (Epic-under-Epic is allowed: sub-epics are Linear's sub-initiatives, and forbidding them buys nothing.)
   - `IsDefaultRoot(WorkItemKind kind)` — **advisory only**, see AC6.
   - No cycles and depth ≤ `MaxDepth` are stated here as the remaining invariants; both need a parent chain and therefore I/O, so 44-3 enforces them (AC5 of that story) against these constants.

   **Why the matrix is gone, stated here so nobody reinstates it.** The v1 matrix was `Epic → {Story, Bug, Spike}`; `Story|Bug|Spike → {Task, Chore}`; `Task|Chore → {}`. That is **three distinct rows** — `Story`, `Bug` and `Spike` interchangeable; `Task` and `Chore` interchangeable. It encoded *root / branch / leaf*, i.e. **level**, while claiming to encode **kind**. What it forbade was not nonsense but ordinary work: a task directly under a small epic (forcing an agent to fabricate a filler Story that then carries a status, a rank, an assignee slot and an event stream into the very backlog 44-9 must generate `sprint-status.yaml` from); a sub-spike; decomposing a task at all — and that last one is not hypothetical, because `Epic → Story → DecompositionTask → PlanTask` is depth 4 against the old `MaxDepth = 3`, pre-foreclosing the v2 the epic README names.
   **The failure modes are asymmetric, and that is the whole argument.** Under a closed *vocabulary* an agent's worst case is "picked the wrong member" — one field, visible, recoverable. Under a closed *parenting matrix* it is "produced a correct decomposition the matrix rejects", whose only recovery is to fabricate structure that then pollutes the record. **Rejecting a valid plan costs more than mislabelling one.** The closed kind vocabulary stays (AC1) — a `[Wire]`-checked, count-pinned enum is a far better classification target for an LLM than free-form labels, and it is testable; the whitelist over pairs of them does not.

5. **The fail-loud built index survives the matrix's deletion.** `TrackerHierarchy` still *declares* a row per `WorkItemKind` and *builds* its lookup (`RolePhaseMap.cs:170-171` idiom) — the row is now the kind's structural invariant rather than a child set:
   ```csharp
   private static readonly (WorkItemKind Kind, bool MayParentAnEpic)[] s_rules =
   [
       (WorkItemKind.Epic,  true),
       (WorkItemKind.Story, false),
       (WorkItemKind.Task,  false),
       (WorkItemKind.Spike, false),
   ];
   ```
   The static initializer **throws `TrackerVocabularyException` naming the member** if the declared row set is not exactly `Enum.GetValues<WorkItemKind>()`. Adding a fifth kind without deciding its rule stays a boot failure — the `PromptFileLoader.cs:105` posture, already proven over 101 prompt files. The mechanism was never the problem; the 4×4 whitelist it was pointed at was.

6. **Any kind may be top-level; `IsDefaultRoot` is a placement hint, not an invariant.** A work item of any kind — including `task` — may have a null parent, and no service rejects it. `IsDefaultRoot(WorkItemKind)` returns `true` for `Epic` only and is documented as **"where the UI puts it when nobody said"** — consumed by 44-6's create form and 44-8's import defaults, never by a validator.
   **Why:** with `IsRoot` as an enforced invariant, an imported GitHub issue or a triaged item cannot exist until somebody invents a parent epic for it. That breaks 44-8's import path (which creates items in bulk from a repo that has no epics) and the triage path (AC2's whole point is that an item can arrive before anyone has decided anything about it, including where it belongs). A test asserts `CanParent` is never consulted for a null parent.

7. **`WorkItemRef`** — a `readonly record struct WorkItemRef(string ProjectKey, int Number)` with `ToWire() => $"{ProjectKey}-{Number}"` and a strict `TryParse`. `ProjectKey` is validated `^[A-Z][A-Z0-9]{1,9}$` (upper-case, 2–10 chars) and `Number >= 1`. **`ToWire()` is the string written into `DocumentInstance.IssueId` and DCB `tags.issueId`** — the epic's join key (README §2). A test asserts round-trip and asserts rejection of lower-case, empty, over-long and zero/negative inputs. Non-normalizing: a bad key is rejected, never coerced.

8. **The key is frozen at creation, and `PreviousKeys` records the only sanctioned exception.**
   - **Rule (stated in `WorkItemRef`'s XML doc and in 44-1's column doc):** a work item's key is minted once, from the sequence of the project it is created in, and **never re-minted** — including when the item is moved to another project. After a move, the item's key prefix no longer matches its project's key. That is intended and must not be "fixed".
   - **Why freeze-and-record is the only safe answer:** the key is already written into `DocumentInstance.IssueId` and into DCB `tags.issueId`, and **event tags are append-only** — there is no update path. Re-minting on a move therefore orphans the item's entire document lineage and event history silently and unrecoverably. Linear needed `previousIdentifiers` because it re-mints on a *team* move, which is rare; a project move here is the common case, so we freeze instead of re-mint and pay a cosmetic mismatch rather than a data one.
   - **`PreviousKeys string[]`** (stored on the work item by 44-1, empty by default) exists for the one case a freeze cannot cover: a deliberate operator **re-key**, e.g. renaming a project's key prefix `TAM` → `TAMMA`. When that happens the outgoing key is appended to `PreviousKeys` and **lookup by key must resolve current-or-previous**, so every already-written `IssueId` and event tag still finds its item.
   - 44-0 ships the pure helper the storage and service layers use:
     ```csharp
     public static class WorkItemKeyHistory
     {
         public static IReadOnlyList<string> Record(IReadOnlyList<string> previousKeys, WorkItemRef outgoingKey);
         public static bool Matches(WorkItemRef candidate, WorkItemRef currentKey, IReadOnlyList<string> previousKeys);
     }
     ```
     Tests assert: `Record` is idempotent (re-recording the same outgoing key does not duplicate it), preserves order oldest-first, and `Matches` resolves both the current key and every previous one — and that a project move alone produces **no** `PreviousKeys` entry, because the key did not change.

9. **`Rank`** — a fractional-index algebra: `Rank.Between(string? left, string? right)` returning a base-62 string that sorts strictly between its neighbours under **ordinal** comparison, plus `Rank.First()`, `Rank.Append(string? currentMax)` and `Rank.Prepend(string? currentMin)`.
   **There is no `Rank.Last()`.** A `Last()` that returns a fixed sentinel is a collision waiting to happen — two consecutive appends both get the sentinel and the two items compare equal, which is the exact failure D7 rejects `double` for. Appending is `Between(currentMax, null)` and it needs the caller's current maximum; `Append(currentMax)` makes that parameter unavoidable at the call site. `Prepend` is its mirror.
   Property tests assert: (a) 10 000 sequential midpoint insertions between a fixed pair never collide and never exceed a stated length bound; (b) ordinal sort order of a shuffled generated sequence matches insertion intent; (c) `Between(null, null)`, `Between(x, null)` and `Between(null, x)` are all defined; (d) **two consecutive `Append` calls produce distinct, strictly increasing ranks** — the regression test for the deleted `Last()`.

10. **Two rank axes, not one — and not one per status column.** `Rank` (the type) serves two *columns* on the work item, both project-scoped strings over the same algebra:
    - `Rank` — position in the flat project backlog. What the board column's order is a filtered view of.
    - `SiblingRank` — position among the item's siblings under the same parent (null parent included, so top-level items have a sibling order too).

    **Why the second one:** with only a project rank, reordering an epic's three children is a rewrite of their positions in the *flat* backlog, so tidying a subtree perturbs an unrelated global ordering — and conversely a backlog re-prioritisation silently reshuffles subtree display order. Linear ships `sortOrder` and `subIssueSortOrder` for exactly this and has done since long before we noticed.
    **This does not reopen the per-status rank.** The epic README rejects a rank per `(project, status)` column and that rejection is correct — a board column's order *is* the project rank filtered by status, and Linear has no per-status order either. Parent and status are different axes: an item has exactly one parent (so `SiblingRank` is single-valued and cannot disagree with itself), whereas a per-status rank would give the same item N positions of which N−1 are stale.
    44-0 ships the algebra once; 44-1 creates both columns, both `COLLATE "C"`.

11. **Priority is nullable, and `TriagePriority`'s ordinal order is pinned.** The work item's priority binds to `TriagePriority?` — **`null` means "nobody has prioritised this"**, which is a different fact from `normal` and the most useful signal in an overnight agent-filed queue. Linear makes the same distinction with a first-class `0 = No priority`; nullability achieves it here without touching the shipped `TriageDecision.cs`.
    A test pins `TriagePriority`'s **declaration order** — `(Urgent, High, Normal, Low) == (0, 1, 2, 3)` — because `[Wire]` guarantees the strings and says nothing about the ordinals, and every priority-sorted board and every `ORDER BY` in 44-3/44-4/44-7 depends on them. `TrackerPriority.SortKey(TriagePriority?)` returns the ordinal for a value and `int.MaxValue` for `null`, so unset sorts **after** `low`; the sort rule is pinned by a test rather than re-derived per query.

12. **Type reuse with a drift pin.** The work item's type dimension binds to the existing `TriageIssueType` (`TriageDecision.cs:23-31`) — **no new enum is introduced**, and its `bug`/`chore` members are now the *only* place those words appear in the tracker vocabulary (AC1). A test pins that the tracker's accepted priority and type wire sets are exactly `TriagePriority`'s and `TriageIssueType`'s, so a member added there flows through rather than drifting. The `critical`→`urgent` / `medium`→`normal` aliases keep working via `TriageVocabulary`.

13. **`EstimateScale`** — a `[Wire]` enum with exactly five members: `not_used`, `linear`, `fibonacci`, `exponential`, `t_shirt`. Count pinned at 5. It is **project configuration** (stored on `Project` by 44-1); the work item stores a scale-free `Estimate` (`decimal?`), not `EstimateHours`.
    **Why the rename:** an `EstimateHours` column names the scale in the column, which means changing scale is a migration and mixing scales across projects is impossible. Every scale Linear ships (`notUsed, exponential, fibonacci, linear, tShirt`) is pointedly *not* hours — because a hours-shaped estimate invites the reading that the number is a commitment. `Estimate` + a project-level scale is one nullable number and one enum, and it is the shape a team can actually change its mind about.
    Nothing reads `Estimate` in v1 (epic Deferred: estimation/velocity/burndown are Epic 36's); it is stored so that when something does, the history exists.

14. **`WorkItemRelationKind`** — a `[Wire]` enum with exactly three members: `blocks`, `duplicate`, `related`. Count pinned at 3.
    **Why it exists:** `blocked` is a *status* (AC2) with no way to record **what** blocks the item — a half-feature. Worse, in a model with no relation edge, "A must land before B" has exactly one place to go: parenting. Encoding dependency as hierarchy corrupts the tree that 44-3's recursive CTE, 44-4's board roll-ups and 44-9's `sprint-status.yaml` all read. And dependency is not a new idea here — `DecompositionTask.DependsOn` (`Decomposition.cs:36`) and `PlanTask.DependsOn` (`Plan.cs:17`) already ship it inside document bodies.
    **Scope boundary, stated so this does not become dead code:** 44-0 ships the vocabulary and the direction convention only (`blocks` is directed source→target; `duplicate` and `related` are symmetric and stored canonically with the lower id first, so an edge cannot be inserted twice in mirror form). The `work_item_relations` edge table is **44-1's** (one table, two FKs, one enum column, one unique index) and its enforcement — no self-edge, no cross-project edge, and *no cycle detection*, because a blocking cycle is a real thing a user should be shown rather than prevented from recording — is **44-3's**. Both are additive and small; both are flagged in the epic README rather than assumed. If the product owner declines the feature, **this enum is deleted, not left unreferenced** — the repo already carries `Issue` (`Models/Issue.cs:7`) and `TriageComplexity` as dead vocabularies and the epic README spends a table on why that is expensive.

15. **The core is pure.** `Tamma.Core.Tracking` has no `ProjectReference`, no EF, no `HttpClient`, no `ILogger`, no I/O of any kind. A test asserts every public type in the namespace is constructible without DI.

16. **Fail-loud index test.** A test drives the pure `Build(rules)` core with a synthetic `WorkItemKind`-shaped rule set missing a member and asserts it throws naming the offending member — the `PromptFileLoader.Build` assertion shape.

## Technical Notes

- `WorkItemKind` and `WorkItemStatus` deliberately ship as **separate** vocabularies from `DocumentTypeKey` and `DocumentInstanceStatus`. They look similar and are not: a document status describes a *revision's* review position; a work-item status describes a *thing to be done*. Merging them would put `superseded` on a backlog board.
- **`WorkItemStatusCategory` is the seam a full status/category split would grow through, and this story does not take that split.** The fuller shape — and the better long-term design — is Linear's: named status *rows* per project (open data: a team can add "Waiting on customer"), each carrying a closed category. That gets `startedAt`/`completedAt`/`cancelledAt` free off category transitions, and makes README D11's "defer custom statuses" a genuine feature flag instead of a migration. **It is not taken now** because it needs a `work_item_statuses` table, a per-project seeding path, a default-set migration, an ordering column and a UI to manage it — that is most of a story on its own, on the critical path, before the first board renders. What this story does instead is put the category vocabulary in *now*, so that when the rows arrive the grouping contract does not change and no consumer that already calls `Category()` has to be rewritten. The `WorkItemStatus` enum becomes the seed set for the default rows.
- **`triage` is in the enum today for exactly the same reason.** Adding an enum member later is a migration over `ck_work_items_status` on `work_items` — the highest-row-count tenant table — **replayed across every tenant schema** via the migrate-all sweep 44-1 builds for the first time in this repo. The cost of a member we ship and barely use is one row in a CHECK constraint; the cost of one we need later is a fleet-wide migration. That asymmetry is why the enum is generous now.
- `MaxDepth = 6` is enforced by the service layer (44-3), not by `TrackerHierarchy` — the type states the invariant, and evaluating it needs a parent chain, which needs I/O. Six rather than three because the shipped `Epic → Story → DecompositionTask → PlanTask` chain is already 4, and a bound must leave room above the structures the codebase already contains. Six is still a bound: the recursive CTE stays fixed-cost and the board render stays unambiguous, which is the only thing the bound was ever for.
- **The category vocabulary is spelled `cancelled`, matching `WorkItemStatus.Cancelled` and the rest of this repo.** Linear spells the equivalent category `canceled`. Two spellings of one word inside one namespace is the same permanent papercut a mixed hyphen/underscore convention would be; we take the repo's spelling and note the divergence here so a future reader comparing against Linear's schema does not think it is a mapping error.
- The rank alphabet is base-62 (`0-9A-Za-z`) chosen so that ordinal `string` comparison in C# and `ORDER BY "Rank"` in Postgres with the `C` collation agree. **If the column is created with a non-`C` collation the ordering silently differs** — 44-1 D3 pins the collation and this story's tests document why. This applies to **both** rank columns (AC10).
- Do not add a `WorkItemType` enum. "Type" is `TriageIssueType`; "kind" is the hierarchy affordance. Two words, two axes, both already named.

## Dependencies

- **Existing, no change required:** `EnumWire`/`WireAttribute` (`Tamma.Core/Agents/EnumWire.cs`), `UuidV7` (`Tamma.Core/Documents/UuidV7.cs`), `TriagePriority`/`TriageIssueType` (`Tamma.Core/Documents/Types/TriageDecision.cs`).
- **Blocks:** 44-1 (storage binds these), 44-2, 44-3, 44-4, 44-5, 44-7. Nothing in Epic 44 starts before this lands.
- **Blocked by:** nothing. Ships standalone and is independently reviewable.
- **Hands downstream work that did not exist in v1 of this story:** 44-1 gains the `work_item_relations` table, the `SiblingRank` column, the `PreviousKeys` column and the `EstimateScale` column on `Project`; 44-3 gains relation-edge validation and must consult `Status.Category()` rather than its own set literals. Both are additive; see the epic README's per-story notes.

## Out of Scope

- Any table, migration, entity, repository or DbSet — 44-1 (including the `work_item_relations` edge table whose vocabulary AC14 ships).
- Any endpoint, DTO or service — 44-2.
- Any DCB event constant — 44-5.
- **Named status rows per project + a closed category** (the full Linear shape) — deferred with its reason in Technical Notes; AC3's category enum is the seam it grows through.
- Custom or per-principal status sets — deferred (epic README, Decisions D11).
- Reading `Estimate` for velocity, burndown or forecasting — Epic 36 (epic README, Deferred).
- `TriageComplexity` adoption or retirement — an open question for the product owner (epic README, Open questions 5).

## Estimated Effort

**4.5 days** — up from 4.0. Scope moved in both directions and the additions win: deleting the `(parentKind, childKind)` matrix and two `WorkItemKind` members takes work *out* (the hierarchy type shrinks from a 4×4 whitelist plus its 36-pair test to a 4-row invariant table), but `WorkItemStatusCategory` + `Category()` + its pinned table, `WorkItemKeyHistory`, `EstimateScale`, `WorkItemRelationKind`, the nullable-priority sort rule with its ordinal pin, and `Append`/`Prepend` replacing `Last()` are all net-new pure types with net-new tests. Half a day is the honest delta.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-25 | 1.0.0   | Initial story creation | Claude |
| 2026-07-27 | 2.0.0   | Rewritten against `.dev/findings/linear-comparison-against-story-44-0.md`. `WorkItemKind` 6→4 (`bug`/`chore` deleted — `TriageIssueType` carries them). `WorkItemStatus` 7→8 (`triage` added) plus a new `WorkItemStatusCategory` + `Category()`. The `(parentKind, childKind)` matrix deleted and replaced with structural invariants; `MaxDepth` 3→6; the fail-loud built index kept and repointed. `IsRoot` → advisory `IsDefaultRoot`; any kind may be top-level. Key frozen at creation + `PreviousKeys` + `WorkItemKeyHistory`. Priority nullable + `TriagePriority` ordinal pin. `SiblingRank` added as a second rank axis. `EstimateHours` → `Estimate` + `EstimateScale`. `WorkItemRelationKind` added. `Rank.Last()` deleted in favour of `Append`/`Prepend`. Effort 4.0 → 4.5. | Claude |
