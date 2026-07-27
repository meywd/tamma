# Finding: Linear's model vs Story 44-0 — the parenting matrix encodes level, not kind

**Date**: 2026-07-25
**Type**: 📚 Lesson Learned / design correction
**Category**: Architecture
**Status**: 🔍 Open — changes to apply to Story 44-0 before it is implemented

Research source: Linear's **published GraphQL schema** (`packages/sdk/src/schema.graphql`,
github.com/linear/linear — 49,840 lines, generated from their live API, docstrings written by
Linear). `linear.app/docs/*` returns 403 to fetching, so anything from those pages came via search
snippets and is weaker; where they disagreed, the schema won.

## What Linear does

| Dimension | Linear |
|---|---|
| Issue kinds | **None.** One `type Issue`, no `kind`/`type` field. The only `issueTypeId` in the whole schema is retained Jira sync metadata. |
| Hierarchy | `parent`/`children` only, **any issue may parent any issue**, capped at **10 levels** (stated in the `Issue` docstring). |
| Where epics live | **Project** — a different entity, not an issue kind. Their Jira importer maps Jira Epics → Linear *Projects*. |
| Status | Per-team **named** rows (`WorkflowState`), each carrying a closed **category**: `triage, backlog, unstarted, started, completed, canceled, duplicate` (7 readable; 5 user-writable — `triage` and `duplicate` are system-managed). |
| Priority | `Float`, `0 = No priority, 1 = Urgent … 4 = Low`. **"Unset" is a first-class value.** |
| Estimate | Open number; the *scale* is the closed vocabulary and lives on the team (`notUsed, exponential, fibonacci, linear, tShirt`). **Hours are not an option.** |
| Ordering | Three `Float` columns (`sortOrder`, `subIssueSortOrder`, `prioritySortOrder`). No fractional index. |
| Identity | **Team**-scoped `ENG-123` + `previousIdentifiers[]` for team moves. Projects have **no key and no number**. |
| "It's a bug" | A **label** (with `isGroup` label-groups acting as single-select enums). |

## The finding that matters most is internal to 44-0

44-0's parenting matrix:

```
Epic  → {Story, Bug, Spike}
Story → {Task, Chore}      Bug → {Task, Chore}      Spike → {Task, Chore}
Task  → {}                 Chore → {}
```

**Three distinct rows.** `Story`, `Bug` and `Spike` are interchangeable; `Task` and `Chore` are
interchangeable. The matrix encodes exactly *root / branch / leaf* — so **`WorkItemKind` is a
3-member level enum wearing a 6-member kind enum's clothes.** That is Linear's thesis (nesting is
structural, classification is orthogonal) arrived at independently, from inside our own design doc.

What the matrix forbids, concretely:

- **A task directly under an epic.** An agent decomposing a small epic into three chores must
  fabricate a filler Story — which then carries a status, a rank, an assignee slot and an event
  stream, polluting the very backlog 44-9 must be able to generate `sprint-status.yaml` from.
- **A sub-bug.** A bug found while fixing a bug becomes a Task and loses its classification.
- **A sub-epic.** Linear's answer is sub-initiatives; we have no equivalent.
- **Decomposing a task** (`Task → {}`) — and this one is not hypothetical. **Verified in code:**
  `DecompositionTask` (`Types/Decomposition.cs:29`) and `PlanTask` (`Types/Plan.cs:12`) are separate
  shipped types, so Epic → Story → DecompositionTask → PlanTask is **depth 4 against
  `MaxDepth = 3`**. The epic README lists materializing `PlanTask`s as a v2 candidate; **44-0's
  matrix pre-forecloses it**, and neither document notices.

**But copying Linear wholesale is wrong for us.** Linear can omit epics because *Project is the
epic* — a planning object with status, lead, milestones and updates. Our `Project` is deliberately
thin (README §3: "not a work item and never appears on a board"), so it cannot absorb that role. We
genuinely need a hierarchy level inside the work-item table; Linear does not.

There is also an argument *for* a closed kind vocabulary that Linear doesn't need and we do:
**agents.** A `[Wire]`-checked, count-pinned enum is a far better classification target for an LLM
than free-form labels, and it is testable. Linear's labels would drift immediately under agent
authorship.

**So the answer is neither.** Keep the closed kind vocabulary; delete the `(parentKind, childKind)`
whitelist; replace it with structural invariants only (no cycles, depth ≤ N, at most "an Epic may not
be a child of a non-Epic"). Keep the fail-loud built-index mechanism — it is good — and point it at
the invariants.

**The failure modes decide it, and they are asymmetric.** Under a closed *vocabulary* an agent's
worst case is "picks the wrong member" — one field, visible, recoverable. Under a closed *parenting
matrix* it is "has a correct decomposition the matrix rejects", whose only recovery is to fabricate
structure. **Rejecting a valid plan costs more than mislabelling one.**

## Second finding: the flat status enum is a migration waiting to happen

Map 44-0's seven members onto Linear's categories and **three of the seven collapse into `started`**:
`in_progress`, `in_review`, `blocked`. So the enum is already a *mixture* of categories (`backlog`,
`done`) and names (`blocked`, `in_review`) — which is exactly the distinction Linear separates.

Consequences:

1. `IsTerminal` is the only derived predicate 44-0 defines. "Is it in flight?", "does it count as
   started?", "which board column group?", "should the loop pick it up?" all become **hardcoded set
   literals at each call site** across 44-3/44-4/44-6/44-7/44-9. Those drift. Linear derives all of
   it from one category field, and gets `startedAt`/`completedAt`/`canceledAt` free off category
   transitions (44-0 has none).
2. D11's "defer custom statuses" is **a migration, not a feature flag** — enum + count pin +
   `ck_work_items_status` CHECK + every set literal, on `work_items` (the highest-row-count tenant
   table), across every tenant schema, via the migrate-all sweep 44-1 is itself building for the
   first time.
3. **There is no `triage` status, and we need one more than Linear does.** 44-8 imports GitHub
   issues; `FetchUntriagedItemsActivity` exists; the whole triage vocabulary exists. Linear reserves
   a *system-managed category* for "arrived from outside, nobody has decided". We have nowhere to put
   an imported item but `backlog`, which silently merges "we decided not now" with "nobody has
   looked". Under README open question 3 — an agent filing 40 items overnight — that distinction is
   the entire value of the queue.

## Third finding: `bug` and `chore` collide, and it is unflagged

**Verified:** `TriageIssueType` = `{bug, feature, chore, question, security, docs}`
(`TriageDecision.cs:23-31`). `WorkItemKind` = `{epic, story, task, bug, chore, spike}`.

**Two** overlapping members — and each vocabulary has members the other lacks (`spike` is a kind not
a type; `feature`/`question`/`security`/`docs` are types not kinds). Partial overlap with partial
coverage on both sides.

The README flags the *smaller* `TriageComplexity.epic` ↔ `WorkItemKind.Epic` collision as "accepted
and must be flagged". This one is larger — two members, and both vocabularies are actually adopted
by 44-0 — and is flagged nowhere.

`(Kind=Bug, Type=Feature)` and `(Kind=Story, Type=Bug)` are both representable and neither means
anything. And per the first finding, `Kind=Bug` has *identical structural behaviour* to
`Kind=Story` — so the member buys nothing the `Type` axis doesn't already give.

## Where 44-0 is right and Linear is wrong

- **`Rank` beats Linear's.** Linear uses `Float` for all three sort columns. D7 rejects float for the
  correct reason — IEEE-754 midpoint exhaustion in ~52 insertions between fixed neighbours, failing
  by two items silently comparing equal. The base-62 fractional index is right.
- **The `COLLATE "C"` catch in D7 is excellent** — base-62 ordinal order agreeing between C#
  `StringComparer.Ordinal` and Postgres only under `C` collation, with `en_US.UTF-8` interleaving
  case so API order and board order silently diverge. That is the trap most implementations find in
  production.
- **Separate status vocabularies per entity type (D2) is right** — Linear maintains three
  (`WorkflowState`, `ProjectStatus`, `InitiativeStatus`) rather than one universal one.
- **"The board is a query, not a table" (D8) matches Linear** — there is no board entity in their
  schema.
- **Strict, ordinal, non-normalizing `WorkItemRef.TryParse` is right.**

## Fourth finding: the identifier is tied to the volatile container

44-0 mints `PROJECTKEY-123` and writes it into `DocumentInstance.IssueId` and DCB `tags.issueId` —
the join key the whole epic rests on. Linear tied identity to **Team** (stable, mandatory) and gave
**Project** (volatile, optional) *no identity role at all*.

**Neither 44-0 nor its plan mentions moving a work item between projects**, and both answers are bad:
re-mint and every already-written `IssueId` and event tag is orphaned (event tags are append-only —
unrecoverable, silent); keep it and the key no longer matches its project and the per-project
sequence can collide. Linear needed `previousIdentifiers` for the *rare* team move; ours is the
*common* one.

## Changes to apply to 44-0, ranked

1. **Add a `triage` status member and a `Category()` extension now.** Full category+name split is the
   better shape; the minimum that avoids a future migration is a `triage` member (adding one later is
   the migration) plus one place where grouping logic is defined.
2. **Delete the parenting matrix.** Structural invariants only; raise `MaxDepth` to 5–6.
3. **Delete `WorkItemKind.Bug` and `.Chore`.** `TriageIssueType` carries both.
4. **Freeze the key at creation, add `PreviousKeys`, state the project-move rule.**
5. **Confirm any kind may be top-level** — make `IsRoot` advisory, or say parenting is optional.
   Otherwise an imported bug needs an invented epic before it can exist (breaks 44-8 and triage).
6. **Make priority nullable.** "The agent didn't prioritise this" is the most useful signal in an
   overnight queue, and `normal` erases it. Also pin `TriagePriority`'s ordinal order — `[Wire]` says
   nothing about declaration order, and any priority-sorted board depends on it.
7. **Add a per-parent sibling rank**, or state that sibling order is project rank and accept that
   reordering an epic's children perturbs the flat backlog. Linear kept `subIssueSortOrder` for
   exactly this.
8. **Rename `EstimateHours` → `Estimate`**, scale as project config. Linear's five scales pointedly
   exclude hours.
9. **Consider a `WorkItemRelation {blocks, duplicate, related}` edge.** `blocked` as a status with no
   way to record *what* blocks it is a half-feature — and with a restrictive matrix, dependency gets
   encoded as parenting, corrupting the hierarchy.
10. **Clarify `Rank.Last()`** — if it returns a fixed sentinel, two consecutive appends collide.

## Related

- `docs/stories/epic-44/story-44-0/`
- Linear's schema: https://github.com/linear/linear (`packages/sdk/src/schema.graphql`)
