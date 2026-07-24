# Implementation Plan — Story 40-5: `[ResumeBehavior]` on `SingleIssueCycleWorkflow` + Allowlist Burn-Down

## Scope & Deliverable

When this story is done, `SingleIssueCycleWorkflow` declares
`[ResumeBehavior(ResumeMode.Both, SuspendActivities = new[] { typeof(WaitForAgentRunActivity) })]`,
its entry in 39-10's `LegacyResumeAllowlist` (`ResumableStandardStructuralTests.cs:75`) is removed,
and the gate passes every clause for it with **no allowlist entry** — the first Epic-40 burn-down,
machine-proving that the coding cycle is resumable by design. No runtime wiring changes here
(that is 40-2/40-3/40-4) and no gate-code changes (clause (c)'s registry seam is 40-4 AC10); this
is the declaration + the allowlist deletion.

## Pre-Reading

- `docs/stories/epic-40/story-40-5/40-5-resume-behavior-declaration-and-allowlist-burndown.md` — this story (ACs are source of truth)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — D3 (`ResumeBehaviorAttribute`/`ResumeMode`), D4 (`CanonicalSuspendActivities`), D5 (`LegacyResumeAllowlist` ratchet), step 8 (`ResumableStandardStructuralTests` clauses a/b/c + honesty inverse)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Resume/ResumeBehavior.cs` — the attribute + enum (39-10)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs:98-105` — `CanonicalSuspendActivities`, today two entries (`WaitForDocumentDecisionActivity`, `WaitForDocumentInputActivity`); 40-2 adds `WaitForAgentRunActivity`
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs` — the gate. `LegacyResumeAllowlist` is a **private static field of the test fixture** at `:43`; the `SingleIssueCycleWorkflow` entry is `:75` and reads *"issue-cycle orchestration composite, delegates to sub-workflows (burn-down: 39-14+)."* — **Corrected:** it does **not** name Epic 40. Clause bodies: (a) `:107`, ratchet `:133`, (b) `:158` (intersection + `Any`, `:177-189`), (b-inverse) `:202`, (c) `:240` (hardcoded `typeof(ComputeReEntryPositionActivity)` at `:252` until 40-4 widens it)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:54` — the shipped declaration to copy verbatim in form (`ResumeMode.Both, SuspendActivities = new[] { typeof(...) }`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — the class to annotate; the loop now containing `WaitForAgentRunActivity` (40-2) + `ComputeTaskResumeIndexActivity` (40-4)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowTestHelper.cs` — `BuildWorkflow` graph-walk used by the gate
- **NOT FOUND (prerequisite):** 40-2's `WaitForAgentRunActivity`, 40-4's `ComputeTaskResumeIndexActivity` + `CanonicalReEntryActivities`. **39-10's resume infrastructure EXISTS** *(Corrected: earlier drafts listed it as missing)*. See Dependencies & Sequencing.

## Design Decisions

- **D1 — `Both`, `SuspendActivities = new[] { typeof(WaitForAgentRunActivity) }`.** The cycle
  satisfies both resume modes (durable suspend on the agent-run bookmark + crash re-entry from
  git/events), so `Both` is the only honest declaration; `SuspendActivities` lists the single
  canonical suspend type the graph contains. The cycle also contains `WaitForCIResultsActivity` and
  `WaitForPRMergedActivity` — legacy waits deliberately NOT in `CanonicalSuspendActivities` — and
  D2 settles that this is fine. Use the array-creation form (`new[] { … }`), as every shipped
  declaration does; attribute arguments must be constant / `typeof` / array-creation expressions.
- **D2 — SETTLED against the shipped gate: clause (b) is an existence check.**
  `EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode` intersects the declaration's
  `SuspendActivities` with `CanonicalSuspendActivities` and then tests
  `nodeTypes.Any(canonical.Contains)` (`ResumableStandardStructuralTests.cs:177-189`) — ≥1, not
  for-all. So listing `WaitForAgentRunActivity` suffices and the legacy
  `WaitForCIResults`/`WaitForPRMerged` waits need not be canonical. *(This was an open question in
  the earlier draft — "if 39-10 shipped a stricter for-all clause, escalate". The code answers it;
  the escalation path is deleted.)* The one for-all-ish check is the **inverse** (`:202-236`): every
  *canonical* node in the graph must be listed in the declaration — satisfied, since
  `WaitForAgentRunActivity` is the only canonical node the cycle contains.
- **D3 — Removal is mandatory (ratchet), not cleanup.** `LegacyResumeAllowlist_HasNoStaleEntries`
  (`:133-154`) fails the build on a stale entry (workflow now declares). So deleting
  `SingleIssueCycleWorkflow`'s line at `:75` is forced by the gate the moment AC1 lands — the two
  edits are one atomic change.
- **D4 — 39-10's names are SHIPPED; consume them verbatim.** `Tamma.Core.Documents.Resume.ResumeMode`
  (`ResumeBehavior.cs:11`), `ResumeBehaviorAttribute(ResumeMode mode)` +
  `Type[] SuspendActivities { get; init; }` (`:39-54`),
  `LifecycleBookmarks.CanonicalSuspendActivities` (`LifecycleBookmarks.cs:98`). *(Corrected: no
  name-drift hedge is needed — the contract is in the tree.)* This story owns no contract.
- **D5 — Clause (c)'s widening belongs to 40-4, not here.** As shipped, clause (c) (`:252`) demands
  exact type identity with `ComputeReEntryPositionActivity`; declaring `Both` here ARMS that clause,
  and the coding re-entry node cannot satisfy it. 40-4 AC10 lands `CanonicalReEntryActivities` +
  the membership rewrite and registers `ComputeTaskResumeIndexActivity`. This story only *verifies*
  the seam is in place (step 3) — it does not edit the clause. Merge order 40-4 → 40-5 is therefore
  a hard constraint, not a preference: landing 40-5 first reddens CI.

## Implementation Steps

1. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`** — add
   `[ResumeBehavior(ResumeMode.Both, SuspendActivities = new[] { typeof(WaitForAgentRunActivity) })]`
   to the class (D1). Add `using Tamma.Core.Documents.Resume;`.

2. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs`**
   — delete the single `["SingleIssueCycleWorkflow"] = …` line from the `LegacyResumeAllowlist`
   field (`:43`, entry at `:75`) (D3). Nothing else in this file changes here — the clause-(c)
   rewrite in the same file is 40-4's edit (D5); rebase on it rather than duplicating it.

3. **VERIFY clause (b)/(c) preconditions before flipping** — assert, in the small dedicated test
   below: (i) `WaitForAgentRunActivity ∈ LifecycleBookmarks.CanonicalSuspendActivities` with gate
   `"agent-run"` (40-2); (ii) `ComputeTaskResumeIndexActivity ∈ CanonicalReEntryActivities` (40-4
   AC10); (iii) the built `SingleIssueCycleWorkflow` graph contains both activity types. If any is
   missing, this story is blocked on that story — the gate working as designed.

4. **RUN `ResumableStandardStructuralTests`** — all clauses green for `SingleIssueCycleWorkflow`
   with no allowlist entry (AC3). Run the full workflow suite (AC6). Finish with `dotnet test`.

## Data & Migrations

None. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

None. No runtime path changes.

## Test Plan

All NUnit + FluentAssertions.

- **`ResumableStandardStructuralTests` (existing, 39-10; clause (c) widened by 40-4)** — now
  asserts `SingleIssueCycleWorkflow` passes (a)/(b)/(b-inverse)/(c) with no allowlist entry; a
  stale entry (if not removed) fails the ratchet. **Covers AC2, AC3, AC5.**
- **`SingleIssueCycleResumeDeclarationTests`** (new, small) — reflect the `[ResumeBehavior]` on
  `SingleIssueCycleWorkflow`: `Mode == Both`, `SuspendActivities` contains
  `WaitForAgentRunActivity`; assert `CanonicalSuspendActivities` contains `WaitForAgentRunActivity`
  (gate `"agent-run"`) **and** `CanonicalReEntryActivities` contains
  `ComputeTaskResumeIndexActivity`. **Covers AC1, AC4.**
- **Full workflow suite** — green, no behavioral regression. **Covers AC6.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `[ResumeBehavior(Both)]` declared | 1 | `SingleIssueCycleResumeDeclarationTests` |
| 2 — allowlist entry removed | 2 | `ResumableStandardStructuralTests` stale-entry check |
| 3 — passes gate with no allowlist entry | 1, 2, 4 | `ResumableStandardStructuralTests` (clause (c) via 40-4's registry) |
| 4 — both canonical registries confirmed | 3 | `SingleIssueCycleResumeDeclarationTests` |
| 5 — declaration honesty (suspend inverse) holds | 1 | `ResumableStandardStructuralTests` `:202-236` |
| 6 — no behavioral change | 4 | Full workflow suite green |

## Dependencies & Sequencing

- **Substrate, landed:** 39-10 — the attribute, the canonical suspend registry, the allowlist and
  the four-clause gate are all in the tree. This story is a consumer, not a co-author.
- **Hard prerequisites:** 40-2 (`WaitForAgentRunActivity` registered + loop node — clause b), 40-4
  (`ComputeTaskResumeIndexActivity` in the graph **and** the clause-(c) `CanonicalReEntryActivities`
  seam — clause c). This story is the LAST of the three to land; it cannot pass earlier (the gate
  enforces the order).
- **Shared file:** `ResumableStandardStructuralTests.cs` — 40-4 rewrites clause (c) (`:252`), this
  story deletes the allowlist entry (`:75`). Trivially mergeable, but 40-4 must go first.
- **In place, verified:** `ResumableStandardStructuralTests` enumerator, `WorkflowTestHelper`.
- **Feeds:** nothing downstream — it is the epic's acceptance gate for resumability wiring (not for
  runtime re-entry; that is 40-7).
- **Sequencing within the story:** 1/2 (atomic) → 3 → 4.

## Risks & Mitigations

- **Landed before 40-2/40-4 ⇒ clause (b)/(c) fail.** Mitigation: this is the gate working; sequence
  40-5 last (execution plan wave). Step 3 verifies preconditions before flipping.
- **40-4 ships the node but not the clause-(c) seam ⇒ AC3 is unreachable.** Mitigation: step 3
  asserts registry membership explicitly, so the failure names the missing seam instead of
  presenting as a mysterious gate failure. This was the defect that made the original AC3
  unpassable — it is now an owned dependency (40-4 AC10), not an assumption.
- **A green gate is read as "re-entry is live".** Mitigation: the Technical Note states the gate
  reads attributes + graphs only; 40-4's Null default means re-entry is inert until 40-7 flips it.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1, 2 | attribute + allowlist removal | 0.5 |
| 3 | precondition verification test (both registries + graph) | 0.5 |
| 4 | run gate + full suite, fix any honesty-inverse surprise | 1.0 |
| **Total** | | **2.0** (story estimate: 2-3 days) |

Unchanged by the review pass — the clause-(c) seam is budgeted in 40-4, not here.
