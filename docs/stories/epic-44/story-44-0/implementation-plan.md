# Implementation Plan — Story 44-0: Tracker Core — Vocabularies, `WorkItemRef`, Hierarchy Invariants, Rank Algebra, Fail-Loud Index

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.Core/Tracking/` exists and contains the whole tracker vocabulary as pure, I/O-free types: four `[Wire]` enums for the work item itself (`WorkItemKind` 4, `WorkItemStatus` 8, `WorkItemStatusCategory` 6, `WorkItemRelationKind` 3) plus `EstimateScale` (5) for the project; a `WorkItemStatus.Category()` extension that is the **one** place grouping logic is defined; a `TrackerHierarchy` of structural invariants (no cycles, `MaxDepth = 6`, and exactly one kind rule — an Epic may not be a child of a non-Epic) whose per-kind rule index is *built* and throws at first touch if a kind has no rule; a `WorkItemRef` that mints the `PROJ-123` string written into `DocumentInstance.IssueId` and DCB `tags.issueId`, frozen at creation, with a `WorkItemKeyHistory` helper for the one sanctioned re-key case; and a `Rank` fractional-index algebra whose output sorts identically under C# ordinal comparison and Postgres `C`-collation `ORDER BY`, serving two columns (`Rank`, `SiblingRank`) and exposing `Append`/`Prepend` instead of a collision-prone `Last()`. Priority binds to a **nullable** `TriagePriority` and item type to `TriageIssueType`, giving two dead vocabularies their first consumer. Count pins, wire round-trips, an ordinal pin, a purity test and a fail-loud index test ship with it. No table, no endpoint, no event.

## Pre-Reading

- **`.dev/findings/linear-comparison-against-story-44-0.md`** — the comparison against Linear's published GraphQL schema that produced this story's v2 shape. Read it before questioning any of D3/D4/D8/D10/D11/D12/D13 below.
- `docs/stories/epic-44/README.md` — §1 (naming + the two flagged vocabulary collisions), §2 (the `issueId` join key), §3 (kind/status/hierarchy), §4 (rank), Decisions D1/D2/D4/D7/D10/D11
- `docs/stories/epic-44/story-44-0/44-0-tracker-core-vocabularies-parenting-matrix-rank-algebra.md` — the ACs are the source of truth
- `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs:20-70` — `WireAttribute`, the throwing static ctor (`:39-59`), ordinal `TryParse` (`:65`). Note what it does **not** guarantee: declaration order (D12).
- `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:1-45` — the count-pinned + CHECK-mirrored status vocabulary this copies, including the `in_review` underscore note (`:16-18`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs:14-109` — `TriagePriority`, `TriageIssueType`, `TriageComplexity` (**not** adopted), and `TriageVocabulary`'s alias parser
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Decomposition.cs:29-37` and `Types/Plan.cs:12-19` — `DecompositionTask` and `PlanTask`, the shipped depth-4 chain and the existing `DependsOn` fields (D4, D13)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:43-171` — declared cells + **built** index; the shape `TrackerHierarchy` keeps
- `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs:88-115` + `Auth/SystemPrompts.cs:96` — pure `Build` core, throw naming the offender, called from a static initializer
- `apps/tamma-elsa/src/Tamma.Core/Documents/UuidV7.cs` — the existing time-sortable id helper; do not add a second
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Types/TriageDecisionTypeTests.cs:34-53` — the count-pin + round-trip + alias test shape to mirror
- **All referenced paths exist.** NOT FOUND (this story creates them): `apps/tamma-elsa/src/Tamma.Core/Tracking/` and everything under it.

## Design Decisions

- **D1 — `Tamma.Core.Tracking`, not `Tamma.Core.Documents.Tracking`.** A work item is not a document. Nesting under `Documents` would put it behind `DocumentTypeKey`'s registry conventions and invite a future reviewer to ask why it has no `IDocumentType`. Sibling namespace, sibling directory. `Tamma.Core` is the assembly for the same reason `AgentAction` and `ActionKey` are: it has zero `ProjectReference`s, so `Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer` and `Tamma.Api` can all bind the same vocabulary.

- **D2 — `WorkItemKind` and `WorkItemStatus` are separate vocabularies from `DocumentTypeKey`/`DocumentInstanceStatus`, deliberately.** They rhyme and they are not the same thing: `DocumentInstanceStatus` describes a *revision's* review position (`draft → validated → in_review → accepted|rejected|superseded|escalated`); `WorkItemStatus` describes a *thing to be done*. Merging them would put `superseded` on a backlog board and `cancelled` on a document. The visual similarity is the argument *for* two count pins, not one enum.

- **D3 — Four kinds, eight statuses, six categories, all frozen in v1.**
  Kinds: `epic|story|task|spike`. **`bug` and `chore` are deleted from v1's six.** `TriageIssueType` (`TriageDecision.cs:23-31`) already ships both, and D8 binds it as the work item's type axis; keeping them in `WorkItemKind` gives the repo two partially-overlapping vocabularies for one fact — `(Kind=Bug, Type=Feature)` and `(Kind=Story, Type=Bug)` both representable and neither meaningful — with each vocabulary carrying members the other lacks. And under D4 `Kind=Bug` had *identical structural behaviour* to `Kind=Story`, so the member bought nothing the type axis did not already give. The epic README's §1 collision note now flags this pair (larger than the `TriageComplexity.epic` one it already flags) as **resolved by deletion**.
  Statuses: `triage|backlog|ready|in_progress|in_review|blocked|done|cancelled`. `blocked` is a *status*, not a flag, because a board column is where a human looks for it; a boolean would need a second rendering path everywhere — but D13's relation edge is what records *what* blocks it. `cancelled` is distinct from `done` because velocity and completion reporting must not count it. Multi-word wires use `_` exactly as `DocumentInstanceStatus.in_review` does; a mixed convention across two status enums in the same solution is a permanent papercut.
  **`triage` is new and it goes in now.** 44-8 imports GitHub issues; `FetchUntriagedItemsActivity` exists; the entire triage vocabulary exists. Without it an imported item lands in `backlog` and silently merges *"we decided not now"* with *"nobody has looked"*. Adding a member later is **a migration**, not a flag: `ck_work_items_status` on `work_items` — the highest-row-count tenant table — replayed across every tenant schema through the sweep 44-1 is building for the first time in this repo. Ship the member.

- **D4 — `TrackerHierarchy` declares structural invariants, not a `(parentKind, childKind)` matrix. The built index and the fail-loud posture survive; the whitelist does not.**
  The v1 matrix was `Epic → {Story, Bug, Spike}`; `Story|Bug|Spike → {Task, Chore}`; `Task|Chore → {}`. **Three distinct rows.** `Story`/`Bug`/`Spike` were interchangeable and `Task`/`Chore` were interchangeable, so the matrix encoded *root / branch / leaf* — **level** — while presenting as a rule over **kind**. `WorkItemKind` was a 3-member level enum wearing a 6-member kind enum's clothes.
  What it forbade was ordinary work, not nonsense: a `Task` directly under a small `Epic` (an agent decomposing a 3-chore epic must fabricate a filler `Story` that then carries a status, a rank, an assignee slot and an event stream into the backlog 44-9 must generate `sprint-status.yaml` from); a sub-spike; and decomposing a task at all. That last one is **verified in code, not hypothetical**: `DecompositionTask` (`Decomposition.cs:29`) and `PlanTask` (`Plan.cs:12`) are separate shipped types, so `Epic → Story → DecompositionTask → PlanTask` is **depth 4 against `MaxDepth = 3`** — the v1 matrix pre-foreclosed the epic README's own named v2 candidate, and neither document noticed.
  **The failure modes are asymmetric and they decide it.** Closed *vocabulary*: an agent's worst case is "picked the wrong member" — one field, visible, recoverable. Closed *parenting matrix*: "produced a correct decomposition the matrix rejects", recoverable only by fabricating structure that then pollutes the record. **Rejecting a valid plan costs more than mislabelling one.**
  **We do not copy Linear wholesale either.** Linear has no issue kinds because *Project is the epic* — a planning object with status, lead, milestones and updates. Our `Project` is deliberately thin (epic README §3: "not a work item and never appears on a board"), so it cannot absorb that role and we genuinely need a hierarchy level inside the work-item table. And a `[Wire]`-checked, count-pinned enum is a far better classification target for an **agent** than Linear's free-form labels, which would drift immediately under agent authorship. So: keep the closed kind vocabulary, delete the whitelist over pairs of it.
  Resulting shape — same idiom, different cells:
  ```csharp
  public static class TrackerHierarchy
  {
      public const int MaxDepth = 6;

      // One row per WorkItemKind. The row is the kind's structural invariant,
      // not its child set. Missing row => TrackerVocabularyException at first touch.
      private static readonly (WorkItemKind Kind, bool MayParentAnEpic)[] s_rules =
      [
          (WorkItemKind.Epic,  true),
          (WorkItemKind.Story, false),
          (WorkItemKind.Task,  false),
          (WorkItemKind.Spike, false),
      ];

      // The only kind rule: an Epic may not be a child of a non-Epic.
      // Epic-under-Epic IS allowed (sub-epics). Every other pair is permitted.
      public static bool CanParent(WorkItemKind parent, WorkItemKind child);

      // ADVISORY ONLY — "where the UI puts it when nobody said". Never a validator. (D6)
      public static bool IsDefaultRoot(WorkItemKind kind);
  }
  ```
  The static initializer throws `TrackerVocabularyException` naming the member if the declared row set is not exactly `Enum.GetValues<WorkItemKind>()`. **Adding a fifth kind without a rule stays a boot failure** — the `PromptFileLoader.cs:105` posture, proven over 101 prompt files. The mechanism was never the problem.

- **D5 — `MaxDepth = 6` lives on `TrackerHierarchy` but is enforced by 44-3's service.** The type states the invariants; evaluating depth or a cycle needs a parent chain, which needs I/O. Putting the constant here and the check there gives 44-3 exactly one symbol to reference and keeps Core pure (AC15).
  **Six, not three.** The bound exists so `WITH RECURSIVE` stays fixed-cost and a board render stays unambiguous — both of which six satisfies. Three did not clear the structures the codebase already contains (the depth-4 `DecompositionTask`/`PlanTask` chain), and a bound below the data is not a bound, it is a future migration.

- **D6 — `IsDefaultRoot` is advisory; any kind may be top-level.** An item of any kind, `task` included, may have a null parent and no service rejects it. `IsDefaultRoot` returns true for `Epic` only, is documented as a placement hint, and is consumed by 44-6's create form and 44-8's import defaults — never by a validator.
  **Why this is not a nicety.** With `IsRoot` as an invariant, an imported GitHub issue cannot exist until someone invents a parent epic for it — which breaks 44-8's bulk import (repos have issues, not epics) and the entire triage path, whose premise (D3) is that an item can arrive before anybody has decided anything about it, including where it belongs. `CanParent` is simply not consulted when the parent is null, and a test asserts that.

- **D7 — `WorkItemRef` is a `readonly record struct`, its `ToWire()` is the join key, and the key is frozen at creation.** `(string ProjectKey, int Number)` → `"TAM-142"`. `ProjectKey` regex `^[A-Z][A-Z0-9]{1,9}$` and `Number >= 1`, both rejected loud rather than normalized — the `EnumWire.TryParse` ordinal posture (`:65`), for the same reason: a key that round-trips through a lower-casing layer and back is a silent identity change on a row other tables reference by string. The hyphen is the separator because it is what every tracker a user has seen uses, and because `_` is already the intra-token separator in the status wires.
  **This value is what 44-1 writes into `work_items."Key"`, what 44-7 writes into `DocumentInstance.IssueId` (`Tamma.Data/Entities/DocumentInstance.cs:37`, a `string`) and into DCB `tags.issueId`.**
  **The project-move rule, which v1 of this story did not state and had to.** The key is minted once from the creating project's sequence and **never re-minted**, including on a move to another project. After a move the key prefix no longer matches the project. That is intended.
  Re-minting is not an option: the key is already in `DocumentInstance.IssueId` and in DCB `tags.issueId`, and **event tags are append-only** — there is no update path — so a re-mint orphans the item's whole document lineage and event history, silently and unrecoverably. Linear needed `previousIdentifiers` because it re-mints on a *team* move, which is rare; a project move here is the common case, so we freeze and pay a cosmetic prefix mismatch instead of a data loss.
  `PreviousKeys string[]` (stored by 44-1, empty by default) covers the one case a freeze cannot: a deliberate operator **re-key**, e.g. renaming a project prefix `TAM` → `TAMMA`. The outgoing key is appended and **lookup resolves current-or-previous**, so every already-written `IssueId` and event tag still finds its item. 44-0 ships the pure helper (`WorkItemKeyHistory`, step 5) so the rule has exactly one implementation.

- **D8 — Priority binds to a *nullable* `TriagePriority`, type to `TriageIssueType`, both with a *drift pin*, not a copy.** `TriagePriority` (4) and `TriageIssueType` (6) are `[Wire]`, alias-parsed and count-pinned already, and are used by nothing but their own tests today. Binding to them gives them a consumer and avoids a fifth priority vocabulary in a repo whose triage vocabulary *already* drifts from the shipped `triage-intake` prompt (`TriageDecision.cs:212-217`). The pin is a test asserting the tracker's accepted wire sets equal `EnumWire<TriagePriority>`'s and `EnumWire<TriageIssueType>`'s.
  **Nullable, because "unset" and "normal" are different facts.** `null` = nobody has prioritised this; `normal` = somebody looked and said normal. Under README open question 3 (an agent files 40 items overnight) that distinction *is* the queue. Linear makes it first-class with `0 = No priority`; nullability gets us there without touching the shipped `TriageDecision.cs` — which is the cheap route and the reason we take it over adding a member.
  **`TriageComplexity` is explicitly not adopted** — its `[Wire("epic")]` member (`:39`) is a size estimate and would read as a hierarchy level beside `WorkItemKind.Epic`. Its fate is an open question for the product owner, not this story's call.

- **D9 — No `IWorkItem` interface, no registry.** `DocumentTypeKey` needs `DocumentTypeRegistry` because each key carries per-type *validation behaviour*. A work-item kind carries no behaviour — under D4 it carries a single boolean. Adding a registry for symmetry would be one more fail-loud index to keep alive for nothing.

- **D10 — `WorkItemStatusCategory` + `Category()`: grouping is defined once, and it is the seam a full status/category split grows through.**
  Map the eight statuses onto categories and **three collapse into `started`** (`in_progress`, `in_review`, `blocked`) — so the status enum is already a *mixture* of categories (`backlog`, `done`) and names (`blocked`, `in_review`). Without a category, `IsTerminal` is the only derived predicate in the type, and "is it in flight?" / "does it count as started?" / "which board column group?" / "should the loop pick it up?" each become a **hardcoded set literal** at every call site across 44-3, 44-4, 44-6, 44-7 and 44-9. Those drift, and the drift is invisible until a status is added.
  `Category()` is a `switch` **expression** over the enum with no `default:` arm, so adding a status without assigning a category is a **compile error**, not a runtime surprise. `IsTerminal` becomes `Category() is Completed or Cancelled` rather than a second hand-maintained set.
  **The fuller Linear shape is better and we are not taking it now.** Linear's `WorkflowState` is a *named row* per team (open data — a team adds "Waiting on customer") carrying a closed category; that yields `startedAt`/`completedAt`/`cancelledAt` free off category transitions, and turns README D11's "defer custom statuses" from a migration into a genuine feature flag. It is deferred because it needs a `work_item_statuses` table, per-project seeding, a default-set migration, an ordering column and a management UI — most of a story, on the critical path, before the first board renders. Shipping the *category vocabulary* now means that when the rows arrive, the grouping contract is unchanged and no `Category()` caller is rewritten; the `WorkItemStatus` enum becomes the seed set for the default rows.
  **Spelling:** the category member is `cancelled`, matching `WorkItemStatus.Cancelled` and the repo. Linear spells it `canceled`. One word, one spelling, inside one namespace — noted here so a future reader diffing against Linear's schema does not read it as a mapping error.

- **D11 — `Rank` is a base-62 fractional index over the ordinal alphabet `0-9A-Za-z`, it serves two columns, and there is no `Last()`.**
  The alphabet choice is about Postgres, not aesthetics. ASCII ordinal order for that set is `0-9` < `A-Z` < `a-z`, which matches C# `StringComparer.Ordinal` and Postgres `ORDER BY` **only under the `C` collation**. Under `en_US.UTF-8` Postgres collates case-insensitively and interleaves, so `a` sorts before `B` and the board order silently diverges from the API order. **44-1 must create both rank columns `COLLATE "C"`** and this story's test file carries a comment naming that dependency.
  Rejected: an integer rank (a drag rewrites every row below the insertion point — O(n) writes per board interaction); a `double` (IEEE-754 midpointing between two fixed neighbours exhausts mantissa precision in ~52 insertions, one grooming session, and the failure mode is two items silently comparing equal). Linear uses `Float` for all three of its sort columns; this is the one place 44-0 is right and Linear is not.
  **`Last()` is deleted.** A `Last()` returning a fixed sentinel reproduces the `double` failure exactly — two consecutive appends both get the sentinel and compare equal. Appending requires the caller's current maximum. Making the parameter unavoidable is the point; a signature that cannot be called wrongly beats a doc comment saying don't. *(Amended 2026-07-28: shipped `Append`/`Prepend` use digit-increment with carry, not `Between` with an open side — the open-side form grows rank length linearly under pure append chains. See `Rank.cs`.)*
  **Two rank *columns*, one algebra.** `Rank` is the flat project-backlog position; `SiblingRank` is the position among siblings under the same parent (null parent included). With only a project rank, tidying an epic's three children rewrites their positions in the global backlog and a backlog re-prioritisation reshuffles subtree display order — the two orderings are genuinely different questions. Linear ships `sortOrder` and `subIssueSortOrder` for exactly this.
  **This does not reopen the per-status rank, and the README's rejection of that stands.** A board column's order *is* the project rank filtered by status, Linear has no per-status order either, and a per-status rank gives one item N positions of which N−1 are stale. Parent is a different axis: an item has exactly one parent, so `SiblingRank` is single-valued and cannot disagree with itself.

- **D12 — `[Wire]` pins strings, not ordinals, so `TriagePriority`'s declaration order gets its own pin.** `EnumWire`'s static ctor (`:39-59`) validates presence, uniqueness and non-`[Flags]`. It says **nothing** about member order — yet `(Urgent, High, Normal, Low) == (0, 1, 2, 3)` is what every priority-sorted board and every `ORDER BY` in 44-3/44-4/44-7 rests on, and a well-meaning alphabetisation of that enum would silently invert them. A test pins the four ordinals literally. `TrackerPriority.SortKey(TriagePriority?)` returns the ordinal for a value and `int.MaxValue` for `null` — so unset sorts **after** `low` — and that rule is pinned once here rather than re-derived in each query.

- **D13 — `WorkItemRelationKind {blocks, duplicate, related}` ships as a vocabulary in this story; the edge table and its enforcement do not.**
  `blocked` is a status (D3) with no way to record **what** blocks the item — a half-feature. The worse consequence is structural: with no relation edge, "A must land before B" has exactly one place to go, which is parenting — and dependency-as-hierarchy corrupts the tree that 44-3's recursive CTE, 44-4's board roll-ups and 44-9's `sprint-status.yaml` generation all read. Dependency is not a new concept here either: `DecompositionTask.DependsOn` (`Decomposition.cs:36`) and `PlanTask.DependsOn` (`Plan.cs:17`) already ship it inside document bodies, so the tracker inheriting nothing for it is a gap, not a simplification.
  **Direction convention (44-0's other deliverable here):** `blocks` is directed source→target; `duplicate` and `related` are symmetric and stored canonically with the lower id first, so an edge cannot be inserted twice in mirror form.
  **Boundary:** the `work_item_relations` table (two FKs, one enum column, one unique index) is **44-1's**; validation — no self-edge, no cross-project edge, and deliberately **no cycle detection**, because a blocking cycle is a real situation a user should be shown rather than prevented from recording — is **44-3's**. Both additive, both small, both flagged in the epic README rather than assumed into someone else's estimate.
  **If the product owner declines the feature, this enum is deleted, not left unreferenced.** The repo already carries `Issue` (`Platforms.Abstractions/Models/Issue.cs:7`) and `TriageComplexity` as dead vocabularies and the epic README spends a table on why that is expensive.

- **D14 — `Estimate`, not `EstimateHours`; the scale is project configuration.** Naming the scale in the column makes changing scale a migration and mixing scales across projects impossible. Every scale Linear ships (`notUsed, exponential, fibonacci, linear, tShirt`) pointedly excludes hours, because an hours-shaped estimate invites the reading that the number is a commitment. So: a scale-free `Estimate` (`decimal?`) on the work item and an `EstimateScale` `[Wire]` enum (5 members, count-pinned) on the project. Nothing reads it in v1 — estimation, velocity and burndown are Epic 36's per the epic README's Deferred section — it is stored so the history exists when something does.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Tracking/WorkItemKind.cs`** — the **four**-member `[Wire]` enum (`epic`, `story`, `task`, `spike`) plus `WorkItemKindExtensions.ToWire` / `TryParse` delegating to `EnumWire<WorkItemKind>` (the `DocumentInstanceStatus.cs:31-48` extension shape). XML doc states why `bug`/`chore` are absent and points at `TriageIssueType` (D3).

2. **CREATE `.../Tracking/WorkItemStatus.cs`** — the **eight**-member `[Wire]` enum, extensions, and `IsTerminal(this WorkItemStatus) => Category() is Completed or Cancelled` (**derived, not a second set**). XML doc records the CHECK-constraint contract 44-1 must mirror (`ck_work_items_status`), naming the exact eight wire strings — the `DocumentInstanceStatus.cs:12-18` doc shape.

3. **CREATE `.../Tracking/WorkItemStatusCategory.cs`** — the six-member `[Wire]` enum and the `Category(this WorkItemStatus)` extension as a `switch` **expression** with no `default:` arm (D10), so a new status without a category fails to compile. XML doc names this as the single definition of grouping and tells 44-3/44-4/44-6/44-7/44-9 to call it rather than write set literals.

4. **CREATE `.../Tracking/TrackerHierarchy.cs`** — per D4. Declared `s_rules` (one row per kind), built `FrozenDictionary<WorkItemKind, bool>`, `MaxDepth = 6`, `CanParent`, advisory `IsDefaultRoot`, and the throwing static initializer over a pure `Build(rules)` core (so step 10's fail-loud test can drive it with a synthetic set without reflection tricks).

5. **CREATE `.../Tracking/TrackerVocabularyException.cs`** — one exception type carrying the offending member name, so the fail-loud tests assert on a type rather than a message substring.

6. **CREATE `.../Tracking/WorkItemRef.cs`** — per D7. `readonly record struct`, `ToWire()`, `TryParse(string, out WorkItemRef)`, `Parse` (throws), `static bool IsValidProjectKey(string)` (44-2 reuses it to validate project creation). XML doc carries the **freeze-at-creation rule and the project-move consequence** in full — this is the doc a future reader hits when they wonder why an item's prefix does not match its project.

7. **CREATE `.../Tracking/WorkItemKeyHistory.cs`** — `Record(previousKeys, outgoingKey)` (append-if-absent, order preserved oldest-first) and `Matches(candidate, currentKey, previousKeys)`. Pure, no storage. 44-1's repository and 44-2's lookup endpoint both call it so the resolve-old-keys rule has one implementation.

8. **CREATE `.../Tracking/Rank.cs`** — per D11:
   ```csharp
   public static class Rank
   {
       public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
       public static string First();                              // midpoint of (null, null)
       public static string Between(string? left, string? right); // strict ordinal midpoint
       public static string Append(string? currentMax);           // digit-increment with carry (amended — NOT Between(currentMax, null))
       public static string Prepend(string? currentMin);          // == Between(null, currentMin)
       public static bool IsValid(string candidate);              // alphabet + no trailing '0'
       // NO Last(). A fixed sentinel collides on two consecutive appends — see D11.
   }
   ```
   `Between` appends a digit when neighbours are adjacent, never producing a trailing `0` (which would be non-canonical and break strictness). Length growth is logarithmic in the number of insertions between a fixed pair. The file header comment names 44-1's `COLLATE "C"` obligation **for both columns** (`Rank`, `SiblingRank`).

9. **CREATE `.../Tracking/TrackerPriority.cs`** — a thin binding surface, **not a new enum**: `AcceptedPriorityWires` / `AcceptedTypeWires` (from `EnumWire<TriagePriority>` / `EnumWire<TriageIssueType>`), `TryParse` delegating to `TriageVocabulary.TryParsePriority` (so the `critical`/`medium` aliases keep working), and `SortKey(TriagePriority?)` returning the ordinal or `int.MaxValue` for null (D12). This file is where D8's drift pin and D12's ordinal pin point.

10. **CREATE `.../Tracking/EstimateScale.cs`** (five members, D14) and **`.../Tracking/WorkItemRelationKind.cs`** (three members, D13). Both `[Wire]`, both with extensions, both count-pinned. `WorkItemRelationKind`'s XML doc carries the direction convention and names 44-1 (table) and 44-3 (validation) as its consumers, plus the "delete rather than leave dead" instruction.

11. **CREATE tests under `apps/tamma-elsa/tests/Tamma.Core.Tests/Tracking/`:** `WorkItemKindTests.cs`, `WorkItemStatusTests.cs`, `WorkItemStatusCategoryTests.cs`, `TrackerHierarchyTests.cs`, `WorkItemRefTests.cs`, `WorkItemKeyHistoryTests.cs`, `RankTests.cs`, `TrackerPriorityTests.cs`, `EstimateScaleTests.cs`, `WorkItemRelationKindTests.cs`, `TrackingPurityTests.cs`. Shapes mirror `TriageDecisionTypeTests.cs:34-53`.

12. **No `Program.cs` change, no DI registration.** Every type is static or a value type. Recorded explicitly so a reviewer does not look for a missing wiring step.

## Data & Migrations

None. This story introduces no entity, no DbSet and no migration. It *specifies* constraints and columns 44-1 must honour:

- `ck_work_items_status` mirroring `WorkItemStatus`'s **eight** wire strings; `ck_work_items_kind` mirroring `WorkItemKind`'s **four**.
- `work_items."Rank"` **and** `work_items."SiblingRank"`, both `COLLATE "C"`.
- `work_items."PreviousKeys"` (`text[]`, default `{}`) and a lookup path that resolves current-or-previous (D7).
- `work_items."Priority"` **nullable** (D8), `work_items."Estimate"` (`numeric NULL`) replacing `EstimateHours`, and `projects."EstimateScale"` (D14).
- `work_item_relations` (source, target, kind, unique index) — new to 44-1 as a consequence of D13.

## Events

None. `PROJECT.*` / `WORKITEM.*` / `ITERATION.*` are Story 44-5's, together with the repo-wide event-name ratchet.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `WorkItemKindTests.Member_count_is_pinned` | `Enum.GetValues<WorkItemKind>().Should().HaveCount(4)` |
| 2 | `WorkItemKindTests.Roundtrip_holds_for_every_member` | `TryParse(ToWire(k)) == k` for all; and `TryParse("Epic")` **fails** (ordinal) |
| 3 | `WorkItemKindTests.Bug_and_chore_are_not_kinds` | `TryParse("bug")` and `TryParse("chore")` both fail; `EnumWire<TriageIssueType>` accepts both — the deletion is asserted, not assumed (D3) |
| 4 | `WorkItemStatusTests.Member_count_is_pinned` | `HaveCount(8)`; the eight wire strings pinned literally (the CHECK contract) |
| 5 | `WorkItemStatusTests.Triage_is_a_member` | explicit — the member most likely to be "tidied away" by someone who has not read D3 |
| 6 | `WorkItemStatusCategoryTests.Category_table_is_pinned` | the full 8-row status→category table, literally |
| 7 | `WorkItemStatusCategoryTests.Every_category_is_reachable` | all 6 categories produced by at least one status |
| 8 | `WorkItemStatusCategoryTests.IsTerminal_is_derived_from_category` | `IsTerminal` ⇔ `Category() is Completed or Cancelled`, over all 8 |
| 9 | `TrackerHierarchyTests.Every_kind_has_a_rule` | built index defined for all 4 |
| 10 | `TrackerHierarchyTests.Epic_may_not_be_a_child_of_a_non_epic` | all 16 `(parent, child)` pairs: only `(Story\|Task\|Spike, Epic)` rejected |
| 11 | `TrackerHierarchyTests.Task_under_epic_is_permitted` | the case the deleted matrix forbade — named so a reinstatement fails loudly (D4) |
| 12 | `TrackerHierarchyTests.Missing_rule_fails_loud` | pure `Build(rules)` with a member omitted → `TrackerVocabularyException` naming it |
| 13 | `TrackerHierarchyTests.MaxDepth_is_six` | pins the constant (44-3 references it) and asserts it exceeds the shipped depth-4 chain |
| 14 | `TrackerHierarchyTests.IsDefaultRoot_is_advisory` | `IsDefaultRoot` is `Epic`-only; a null parent never consults `CanParent` (D6) |
| 15 | `WorkItemRefTests.Roundtrip_and_rejection_matrix` | `TAM-1` round-trips; rejects `tam-1`, `T-1`, `TOOLONGKEYX-1`, `TAM-0`, `TAM--1`, `TAM`, `""` |
| 16 | `WorkItemKeyHistoryTests.Project_move_does_not_change_the_key` | the freeze rule: a move produces no `PreviousKeys` entry and `ToWire()` is unchanged (D7) |
| 17 | `WorkItemKeyHistoryTests.Rekey_records_and_resolves` | `Record` appends once, is idempotent, preserves oldest-first order; `Matches` resolves current **and** every previous key |
| 18 | `RankTests.Ten_thousand_midpoints_never_collide` | 10 000 `Between` insertions between a fixed pair: all distinct, strictly ordered, length ≤ stated bound |
| 19 | `RankTests.Ordinal_sort_matches_insertion_intent` | shuffle a generated sequence, `OrderBy(x => x, StringComparer.Ordinal)`, compare to intent |
| 20 | `RankTests.Null_neighbours_are_defined` | `Between(null,null)`, `Between(x,null)`, `Between(null,x)` all valid and correctly ordered |
| 21 | `RankTests.Consecutive_appends_are_distinct_and_increasing` | the `Last()` regression (D11): `a = Append(null); b = Append(a); c = Append(b)` → `a < b < c` ordinally |
| 22 | `RankTests.Never_emits_a_trailing_zero` | canonical-form invariant over 10 000 generated ranks |
| 23 | `TrackerPriorityTests.Accepted_wires_equal_the_triage_vocabularies` | drift pin for both priority and type (D8) |
| 24 | `TrackerPriorityTests.TriagePriority_ordinals_are_pinned` | `(Urgent, High, Normal, Low) == (0,1,2,3)` — D12, the thing `[Wire]` does not guarantee |
| 25 | `TrackerPriorityTests.Unset_priority_sorts_after_low` | `SortKey(null) > SortKey(Low)`; null is representable and distinct from `normal` |
| 26 | `TrackerPriorityTests.Aliases_still_fold` | `critical`→urgent, `medium`→normal via `TriageVocabulary` |
| 27 | `EstimateScaleTests.Member_count_and_roundtrip` | `HaveCount(5)`; wires pinned; `hours` is **not** a member (D14) |
| 28 | `WorkItemRelationKindTests.Member_count_and_roundtrip` | `HaveCount(3)`; wires pinned |
| 29 | `TrackingPurityTests.Namespace_has_no_io_dependencies` | reflection over `Tamma.Core.Tracking` types: no ctor parameter type from `System.Net.Http`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Logging` |

All unit; no Testcontainers, no fixtures. Target: 100% line coverage of `Rank`, `WorkItemRef` and `WorkItemKeyHistory` (they are pure algebra and there is no excuse).

## Definition of Done

- All 29 tests green; `dotnet test` clean for `Tamma.Core.Tests`.
- `Tamma.Core.csproj` still declares zero `ProjectReference`s (asserted by test 29 and by review).
- `TriageComplexity` is referenced nowhere in `Tamma.Core.Tracking` (grep-checked in review).
- **No `(parentKind, childKind)` pair set exists anywhere in the namespace** (grep-checked in review) — the matrix is deleted, not commented out, and D4 is the record of why.
- **No `Rank.Last()`** (grep-checked) — `Append`/`Prepend` only.
- The XML doc on `WorkItemStatus` names the eight-wire CHECK constraint 44-1 must create; `WorkItemStatusCategory`'s doc names itself as the single definition of grouping; `WorkItemRef`'s doc carries the freeze-at-creation rule in full; `RankTests` carries the `COLLATE "C"` comment naming 44-1's obligation for **both** rank columns.
- No DI registration, no `Program.cs` diff, no migration in the change.

## Dependencies & Sequencing

- **Blocked by:** nothing. Day 0 of the epic.
- **Blocks:** 44-1 (entities bind these types), 44-2, 44-3 (`MaxDepth`, `TrackerHierarchy.CanParent`, `Rank.Between/Append/Prepend`, `Status.Category()`), 44-4, 44-5, 44-7 (`WorkItemRef.ToWire()` is the `issueId`).
- **Hands additive work downstream that v1 of this plan did not:** 44-1 gains `SiblingRank`, `PreviousKeys`, `Estimate`/`EstimateScale` and the `work_item_relations` table; 44-3 gains relation-edge validation and must call `Status.Category()` instead of set literals; 44-4/44-6/44-7/44-9 call `Category()` for every grouping question. Flagged, not silently absorbed into their estimates.
- **Shared-edit register:** none. Every file in this story is new and no existing file is modified — the only story in Epic 44 with that property, which is why it can start in parallel with any other epic.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The `Rank` collation trap.** The algebra is correct in C# and wrong in Postgres if 44-1 creates either rank column with the database default collation. Silent: the API returns one order, the board renders another. | D11 records it; `RankTests` carries a comment naming 44-1's obligation for both columns; 44-1's AC includes a Testcontainers test that inserts generated ranks and asserts `ORDER BY` matches `OrderBy(Ordinal)`. |
| **Somebody reinstates the parenting matrix.** It reads as a safety feature and its removal reads as a loosening. | D4 states the argument in full inside the plan; the story's AC4 repeats it; test 11 (`Task_under_epic_is_permitted`) fails loudly on reinstatement; the DoD grep-check names it. |
| **The status enum is still a mixture of category and name, and the full split is deferred.** A team that wants "Waiting on customer" cannot have it. | D10 states the deferral and its reason. The mitigation that matters is that `Category()` ships now, so the eventual named-rows migration changes storage without changing the grouping contract or any `Category()` caller. |
| **`WorkItemRelationKind` ships without its table and becomes dead code** — exactly the `Issue`/`TriageComplexity` failure the epic README criticises. | D13 names 44-1 and 44-3 as the consumers and the epic README carries the note; the enum's own XML doc says "delete rather than leave unreferenced" if the product owner declines. Revisit at the 44-1 review, not later. |
| **Four kinds is a guess, and so is one hierarchy rule.** A product owner may want `feature` or `sub-task`. | The vocabulary is `[Wire]` and count-pinned, so adding a member is a deliberate, test-visible edit with a **forced structural rule** (D4's built index). Under the invariant model a new kind costs one boolean, not a matrix row per existing kind — which is most of the reason the matrix is gone. |
| **`WorkItemStatus` vs `DocumentInstanceStatus` confusion in review and in future greps.** Two 8-ish status enums with overlapping members in one solution. | D2 states the distinction; both XML docs cross-reference each other; the wire sets are deliberately non-identical (`triage`/`ready`/`blocked`/`cancelled` vs `validated`/`superseded`/`escalated`) so a mistaken `TryParse` across them fails rather than succeeding wrongly. |
| **`WorkItemRef` format becomes load-bearing across four stories before anyone has used it.** Changing the separator later is a data migration on `document_instances.IssueId` and on DCB `tags`. | D7 names it as load-bearing and now also states the freeze rule that keeps it stable under a project move; the epic README §2 records the consequence for `domain_events.IssueNumber`; 44-9's dogfood run is the first real-volume exercise and is deliberately sequenced last. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–5 (kind, status, category, hierarchy invariants, exception) | 1.0 |
| Steps 6, 7, 9, 10 (`WorkItemRef`, key history, priority binding + sort rule, `EstimateScale`, `WorkItemRelationKind`) | 1.0 |
| Step 8 (`Rank` algebra incl. `Append`/`Prepend` — the only non-trivial code) | 1.0 |
| Step 11 (29 tests incl. the two property tests) | 1.25 |
| Review, docs, XML contract notes, downstream hand-off notes | 0.25 |
| **Total** | **4.5** |

Up from 4.0. The hierarchy type got *cheaper* (a 4-row boolean table and a 16-pair test in place of a 4×4 whitelist and a 36-pair test), but `WorkItemStatusCategory`, `WorkItemKeyHistory`, `EstimateScale`, `WorkItemRelationKind`, the nullable-priority sort rule and `Append`/`Prepend` are net-new pure types each carrying net-new pins. Half a day is the honest delta; the pure-algebra files are where the time actually goes.
