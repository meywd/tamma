# Implementation Plan — Story 44-0: Tracker Core — Vocabularies, `WorkItemRef`, Parenting Matrix, Rank Algebra, Fail-Loud Index

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.Core/Tracking/` exists and contains the whole tracker vocabulary as pure, I/O-free types: two `[Wire]` enums (`WorkItemKind` 6, `WorkItemStatus` 7), a `TrackerHierarchy` matrix whose by-parent index is *built* and throws at first touch if a kind has no rule, a `WorkItemRef` that mints the `PROJ-123` string written into `DocumentInstance.IssueId` and DCB `tags.issueId`, and a `Rank` fractional-index algebra whose output sorts identically under C# ordinal comparison and Postgres `C`-collation `ORDER BY`. Priority and item type bind to the already-shipped `TriagePriority` / `TriageIssueType`, giving two dead vocabularies their first consumer. Count pins, wire round-trips, a purity test and a fail-loud index test ship with it. No table, no endpoint, no event.

## Pre-Reading

- `docs/stories/epic-44/README.md` — §1 (naming), §2 (the `issueId` join key), §3 (kind/status/matrix), §4 (rank), Decisions D1/D2/D4/D7/D10/D11
- `docs/stories/epic-44/story-44-0/44-0-tracker-core-vocabularies-parenting-matrix-rank-algebra.md` — the ACs are the source of truth
- `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs:20-70` — `WireAttribute`, the throwing static ctor (`:39-59`), ordinal `TryParse` (`:65`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:1-45` — the count-pinned + CHECK-mirrored status vocabulary this copies, including the `in_review` underscore note (`:16-18`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs:14-109` — `TriagePriority`, `TriageIssueType`, `TriageComplexity` (**not** adopted), and `TriageVocabulary`'s alias parser
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:43-171` — declared cells + **built** index; the shape `TrackerHierarchy` copies
- `apps/tamma-elsa/src/Tamma.Api/Auth/PromptFileLoader.cs:88-115` + `Auth/SystemPrompts.cs:96` — pure `Build` core, throw naming the offender, called from a static initializer
- `apps/tamma-elsa/src/Tamma.Core/Documents/UuidV7.cs` — the existing time-sortable id helper; do not add a second
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/Types/TriageDecisionTypeTests.cs:34-53` — the count-pin + round-trip + alias test shape to mirror
- **All referenced paths exist.** NOT FOUND (this story creates them): `apps/tamma-elsa/src/Tamma.Core/Tracking/` and everything under it.

## Design Decisions

- **D1 — `Tamma.Core.Tracking`, not `Tamma.Core.Documents.Tracking`.** A work item is not a document. Nesting under `Documents` would put it behind `DocumentTypeKey`'s registry conventions and invite a future reviewer to ask why it has no `IDocumentType`. Sibling namespace, sibling directory. `Tamma.Core` is the assembly for the same reason `AgentAction` and `ActionKey` are: it has zero `ProjectReference`s, so `Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer` and `Tamma.Api` can all bind the same vocabulary.

- **D2 — `WorkItemKind` and `WorkItemStatus` are separate vocabularies from `DocumentTypeKey`/`DocumentInstanceStatus`, deliberately.** They rhyme and they are not the same thing: `DocumentInstanceStatus` describes a *revision's* review position (`draft → validated → in_review → accepted|rejected|superseded|escalated`); `WorkItemStatus` describes a *thing to be done*. Merging them would put `superseded` on a backlog board and `cancelled` on a document. The visual similarity is the argument *for* two count pins, not one enum.

- **D3 — Six kinds, seven statuses, both frozen in v1.** Kinds: `epic|story|task|bug|chore|spike`. Statuses: `backlog|ready|in_progress|in_review|blocked|done|cancelled`. `blocked` is a *status*, not a flag, because a board column is where a human looks for it; a boolean would need a second rendering path everywhere. `cancelled` is distinct from `done` because velocity and completion reporting must not count it — even though nothing reads either in v1 (epic Deferred). Multi-word wires use `_` exactly as `DocumentInstanceStatus.in_review` does; a mixed convention across two status enums in the same solution is a permanent papercut.

- **D4 — `TrackerHierarchy` declares cells and *builds* the index; an empty child set is an explicit row, not an omission.** `RolePhaseMap.cs:43-163` declares its 93 cells and builds the by-role lookup at `:170-171`. Copied verbatim in shape:
  ```csharp
  public static class TrackerHierarchy
  {
      public const int MaxDepth = 3;
      private static readonly (WorkItemKind Parent, WorkItemKind[] Children)[] s_rules =
      [
          (WorkItemKind.Epic,  [WorkItemKind.Story, WorkItemKind.Bug, WorkItemKind.Spike]),
          (WorkItemKind.Story, [WorkItemKind.Task,  WorkItemKind.Chore]),
          (WorkItemKind.Bug,   [WorkItemKind.Task,  WorkItemKind.Chore]),
          (WorkItemKind.Spike, [WorkItemKind.Task,  WorkItemKind.Chore]),
          (WorkItemKind.Task,  []),                 // explicit leaf — NOT an omission
          (WorkItemKind.Chore, []),                 // explicit leaf
      ];
      public static IReadOnlySet<WorkItemKind> ChildrenOf(WorkItemKind parent);   // built index
      public static bool CanParent(WorkItemKind parent, WorkItemKind child);
      public static bool IsRoot(WorkItemKind kind);                              // Epic only
  }
  ```
  The static initializer throws `TrackerVocabularyException` naming the member if `Enum.GetValues<WorkItemKind>()` is not exactly the declared parent set. **Adding a seventh kind without a rule is a boot failure**, which is the intended friction — the `PromptFileLoader` posture at `:105`, already proven over 101 prompt files.

- **D5 — `MaxDepth` lives on `TrackerHierarchy` but is enforced by 44-3's service.** The matrix expresses *what may parent what*; depth is a derived consequence and enforcing it needs a parent chain, which needs I/O. Putting the constant here and the check there gives 44-3 exactly one symbol to reference and keeps Core pure (AC7).

- **D6 — `WorkItemRef` is a `readonly record struct`, and its `ToWire()` is the join key.** `(string ProjectKey, int Number)` → `"TAM-142"`. `ProjectKey` regex `^[A-Z][A-Z0-9]{1,9}$` and `Number >= 1`, both rejected loud rather than normalized — the `EnumWire.TryParse` ordinal posture (`:65`), for the same reason: a key that round-trips through a lower-casing layer and back is a silent identity change on a row that other tables reference by string. The hyphen is the separator because it is what every tracker a user has seen uses, and because `_` is already the intra-token separator in the status wires.
  **This value is what 44-1 writes into `work_items."Key"`, what 44-7 writes into `DocumentInstance.IssueId` (`Tamma.Data/Entities/DocumentInstance.cs:37`, a `string`) and into DCB `tags.issueId`.** Recorded here because the format is now load-bearing across four stories.

- **D7 — `Rank` is a base-62 fractional index over the ordinal alphabet `0-9A-Za-z`, and the alphabet choice is about Postgres, not aesthetics.** ASCII ordinal order for that set is `0-9` < `A-Z` < `a-z`, which matches C# `StringComparer.Ordinal` and Postgres `ORDER BY` **only under the `C` collation**. Under `en_US.UTF-8` Postgres collates case-insensitively and interleaves, so `a` sorts before `B` and the board order silently diverges from the API order. The algebra is written here; **44-1 must create the column `COLLATE "C"`** and this story's test file carries a comment naming that dependency so the two are not separated by a refactor.
  Rejected: an integer rank (a drag rewrites every row below the insertion point — O(n) writes per board interaction); a `double` (IEEE-754 midpointing between two fixed neighbours exhausts mantissa precision in ~52 insertions, which is a single grooming session, and the failure mode is two items silently comparing equal).

- **D8 — Priority and item type bind to the shipped triage enums with a *drift pin*, not a copy.** `TriagePriority` (4) and `TriageIssueType` (6) are `[Wire]`, alias-parsed and count-pinned already, and are used by nothing but their own tests today. Binding to them gives them a consumer and avoids a fifth priority vocabulary in a repo whose triage vocabulary *already* drifts from the shipped `triage-intake` prompt (`TriageDecision.cs:212-217`). The pin is a test asserting the tracker's accepted wire set equals `EnumWire<TriagePriority>`'s, so a member added there flows through instead of diverging.
  **`TriageComplexity` is explicitly not adopted** — its `[Wire("epic")]` member (`:39`) is a size estimate and would read as a hierarchy level beside `WorkItemKind.Epic`. Its fate is an open question for the product owner, not this story's call.

- **D9 — No `IWorkItem` interface, no registry.** `DocumentTypeKey` needs `DocumentTypeRegistry` because each key carries per-type *validation behaviour*. A work-item kind carries no behaviour — only parenting rules, which are the matrix. Adding a registry for symmetry would be one more fail-loud index to keep alive for nothing.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Tracking/WorkItemKind.cs`** — the six-member `[Wire]` enum plus `WorkItemKindExtensions.ToWire` / `TryParse` delegating to `EnumWire<WorkItemKind>` (the `DocumentInstanceStatus.cs:31-48` extension shape).

2. **CREATE `.../Tracking/WorkItemStatus.cs`** — the seven-member `[Wire]` enum, extensions, and `IsTerminal(this WorkItemStatus)` returning true for `Done`/`Cancelled` only. XML doc records the CHECK-constraint contract 44-1 must mirror (`ck_work_items_status`), naming the exact wire strings — the `DocumentInstanceStatus.cs:12-18` doc shape.

3. **CREATE `.../Tracking/TrackerHierarchy.cs`** — per D4. Declared `s_rules`, built `FrozenDictionary<WorkItemKind, FrozenSet<WorkItemKind>>`, `MaxDepth = 3`, `ChildrenOf` / `CanParent` / `IsRoot`, and the throwing static initializer.

4. **CREATE `.../Tracking/TrackerVocabularyException.cs`** — one exception type carrying the offending member name, so the fail-loud tests assert on a type rather than a message substring.

5. **CREATE `.../Tracking/WorkItemRef.cs`** — per D6. `readonly record struct`, `ToWire()`, `TryParse(string, out WorkItemRef)`, `Parse` (throws), and `static bool IsValidProjectKey(string)` (44-2 reuses it to validate project creation).

6. **CREATE `.../Tracking/Rank.cs`** — per D7:
   ```csharp
   public static class Rank
   {
       public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
       public static string First();                              // midpoint of (null, null)
       public static string Last();
       public static string Between(string? left, string? right); // strict ordinal midpoint
       public static bool IsValid(string candidate);              // alphabet + no trailing '0'
   }
   ```
   `Between` appends a digit when neighbours are adjacent, never producing a trailing `0` (which would be non-canonical and break strictness). Length growth is logarithmic in the number of insertions between a fixed pair.

7. **CREATE `.../Tracking/TrackerPriority.cs`** — a thin binding surface, **not a new enum**: `TrackerPriority.AcceptedWires` (from `EnumWire<TriagePriority>`), `TryParse` delegating to `TriageVocabulary.TryParsePriority` (so the `critical`/`medium` aliases keep working), and the same for `TriageIssueType`. This file is where D8's drift pin points.

8. **CREATE tests under `apps/tamma-elsa/tests/Tamma.Core.Tests/Tracking/`:** `WorkItemKindTests.cs`, `WorkItemStatusTests.cs`, `TrackerHierarchyTests.cs`, `WorkItemRefTests.cs`, `RankTests.cs`, `TrackerPriorityTests.cs`, `TrackingPurityTests.cs`. Shapes mirror `TriageDecisionTypeTests.cs:34-53`.

9. **No `Program.cs` change, no DI registration.** Every type is static or a value type. Recorded explicitly so a reviewer does not look for a missing wiring step.

## Data & Migrations

None. This story introduces no entity, no DbSet and no migration. It *specifies* two constraints 44-1 must honour: `ck_work_items_status` mirroring `WorkItemStatus`'s wire strings, and `work_items."Rank" COLLATE "C"`.

## Events

None. `PROJECT.*` / `WORKITEM.*` / `ITERATION.*` are Story 44-5's, together with the repo-wide event-name ratchet.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `WorkItemKindTests.Member_count_is_pinned` | `Enum.GetValues<WorkItemKind>().Should().HaveCount(6)` |
| 2 | `WorkItemKindTests.Roundtrip_holds_for_every_member` | `TryParse(ToWire(k)) == k` for all; and `TryParse("Epic")` **fails** (ordinal) |
| 3 | `WorkItemStatusTests.Member_count_is_pinned` | `HaveCount(7)`; wire strings pinned literally (the CHECK contract) |
| 4 | `WorkItemStatusTests.Only_done_and_cancelled_are_terminal` | `IsTerminal` exact set |
| 5 | `TrackerHierarchyTests.Every_kind_has_a_rule` | `ChildrenOf` defined for all 6; `Task`/`Chore` empty |
| 6 | `TrackerHierarchyTests.Epic_is_the_only_root` | `IsRoot` exact set |
| 7 | `TrackerHierarchyTests.Missing_rule_fails_loud` | a synthetic rule set omitting a member → `TrackerVocabularyException` naming it |
| 8 | `TrackerHierarchyTests.MaxDepth_is_three` | pins the constant (44-3 references it) |
| 9 | `WorkItemRefTests.Roundtrip_and_rejection_matrix` | `TAM-1` round-trips; rejects `tam-1`, `T-1`, `TOOLONGKEYX-1`, `TAM-0`, `TAM--1`, `TAM`, `""` |
| 10 | `RankTests.Ten_thousand_midpoints_never_collide` | 10 000 `Between` insertions between a fixed pair: all distinct, strictly ordered, length ≤ stated bound |
| 11 | `RankTests.Ordinal_sort_matches_insertion_intent` | shuffle a generated sequence, `OrderBy(x => x, StringComparer.Ordinal)`, compare to intent |
| 12 | `RankTests.Null_neighbours_are_defined` | `Between(null,null)`, `Between(x,null)`, `Between(null,x)` all valid and correctly ordered |
| 13 | `RankTests.Never_emits_a_trailing_zero` | canonical-form invariant over 10 000 generated ranks |
| 14 | `TrackerPriorityTests.Accepted_wires_equal_TriagePriority` | drift pin (D8) |
| 15 | `TrackerPriorityTests.Aliases_still_fold` | `critical`→urgent, `medium`→normal via `TriageVocabulary` |
| 16 | `TrackingPurityTests.Namespace_has_no_io_dependencies` | reflection over `Tamma.Core.Tracking` types: no ctor parameter type from `System.Net.Http`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Logging` |

All unit; no Testcontainers, no fixtures. Target: 100% line coverage of `Rank` and `WorkItemRef` (they are pure algebra and there is no excuse).

## Definition of Done

- All 16 tests green; `dotnet test` clean for `Tamma.Core.Tests`.
- `Tamma.Core.csproj` still declares zero `ProjectReference`s (asserted by test 16 and by review).
- `TriageComplexity` is referenced nowhere in `Tamma.Core.Tracking` (grep-checked in review).
- The XML doc on `WorkItemStatus` names the CHECK constraint 44-1 must create, and `RankTests` carries the `COLLATE "C"` comment naming 44-1's obligation.
- No DI registration, no `Program.cs` diff, no migration in the change.

## Dependencies & Sequencing

- **Blocked by:** nothing. Day 0 of the epic.
- **Blocks:** 44-1 (entities bind these types), 44-2, 44-3 (`MaxDepth`, `TrackerHierarchy.CanParent`, `Rank.Between`), 44-4, 44-5, 44-7 (`WorkItemRef.ToWire()` is the `issueId`).
- **Shared-edit register:** none. Every file in this story is new and no existing file is modified — the only story in Epic 44 with that property, which is why it can start in parallel with any other epic.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The `Rank` collation trap.** The algebra is correct in C# and wrong in Postgres if 44-1 creates the column with the database default collation. Silent: the API returns one order, the board renders another. | D7 records it; `RankTests` carries a comment naming 44-1's obligation; 44-1 AC includes a Testcontainers test that inserts generated ranks and asserts `ORDER BY "Rank"` matches `OrderBy(Ordinal)`. |
| **Six kinds is a guess.** A product owner may want `feature`, `sub-task`, or a customer-defined set. | The vocabulary is `[Wire]` and count-pinned, so adding a member is a deliberate, test-visible edit with a forced parenting rule (D4). Customer-defined sets are explicitly deferred (epic D11) with the reason. |
| **`WorkItemStatus` vs `DocumentInstanceStatus` confusion in review and in future greps.** Two 7-ish status enums with overlapping members in one solution. | D2 states the distinction; both XML docs cross-reference each other; the wire sets are deliberately non-identical (`ready`/`blocked`/`cancelled` vs `validated`/`superseded`/`escalated`) so a mistaken `TryParse` across them fails rather than succeeding wrongly. |
| **`WorkItemRef` format becomes load-bearing across four stories before anyone has used it.** Changing the separator later is a data migration on `document_instances.IssueId` and on DCB `tags`. | D6 names it as load-bearing; the epic README §2 records the consequence for `domain_events.IssueNumber`; 44-9's dogfood run is the first real-volume exercise and is deliberately sequenced last. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–4 (enums, hierarchy, exception) | 1.0 |
| Steps 5, 7 (`WorkItemRef`, priority binding) | 0.5 |
| Step 6 (`Rank` algebra — the only non-trivial code) | 1.0 |
| Step 8 (16 tests incl. the two property tests) | 1.25 |
| Review, docs, XML contract notes | 0.25 |
| **Total** | **4.0** |
