# Implementation Plan — Story 40-5: `[ResumeBehavior]` on `SingleIssueCycleWorkflow` + Allowlist Burn-Down

## Scope & Deliverable

When this story is done, `SingleIssueCycleWorkflow` declares
`[ResumeBehavior(ResumeMode.Both, SuspendActivities = [typeof(WaitForAgentRunActivity)])]`, its
entry in 39-10's `LegacyResumeAllowlist` is removed, and `ResumableStandardStructuralTests`
passes all three clauses for it with **no allowlist entry** — the first Epic-40 burn-down,
machine-proving that the coding cycle is resumable by design. No runtime wiring changes here
(that is 40-2/40-3/40-4); this is the declaration + gate flip.

## Pre-Reading

- `docs/stories/epic-40/story-40-5/40-5-resume-behavior-declaration-and-allowlist-burndown.md` — this story (ACs are source of truth)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — D3 (`ResumeBehaviorAttribute`/`ResumeMode`), D4 (`CanonicalSuspendActivities`), D5 (`LegacyResumeAllowlist` ratchet), step 8 (`ResumableStandardStructuralTests` clauses a/b/c + honesty inverse)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Resume/ResumeBehavior.cs` — the attribute + enum (39-10)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs` — `CanonicalSuspendActivities` (40-2 adds `WaitForAgentRunActivity`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs` — the gate + the `LegacyResumeAllowlist` seed (39-10) that names Epic 40 as burn-down for `SingleIssueCycleWorkflow`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — the class to annotate; the loop now containing `WaitForAgentRunActivity` (40-2) + `ComputeTaskResumeIndexActivity` (40-4)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowTestHelper.cs` — `BuildWorkflow` graph-walk used by the gate
- **NOT FOUND (prerequisite):** all of 39-10's resume infrastructure, 40-2's `WaitForAgentRunActivity`, 40-4's `ComputeTaskResumeIndexActivity`. See Dependencies & Sequencing.

## Design Decisions

- **D1 — `Both`, `SuspendActivities = [typeof(WaitForAgentRunActivity)]`.** The cycle satisfies
  both resume modes (durable suspend on the agent-run bookmark + crash re-entry from git/events),
  so `Both` is the only honest declaration; `SuspendActivities` lists the single canonical suspend
  type the graph contains. (The cycle also contains `WaitForCIResultsActivity` and
  `WaitForPRMergedActivity` — legacy waits NOT in `CanonicalSuspendActivities`; they are not listed
  because the resumable *standard* is about the lifecycle-canonical suspend points. If 39-10's
  clause (b) requires *every* suspend node be canonical, that is a broader migration out of scope —
  D2 addresses.)
- **D2 — Scope check against 39-10 clause (b)'s exact wording.** 39-10 D4/step-8 phrase clause (b)
  as "contains ≥1 node whose type is in BOTH the declaration's `SuspendActivities` AND
  `CanonicalSuspendActivities`" — an existence check, not a for-all. So listing
  `WaitForAgentRunActivity` suffices; the legacy `WaitForCIResults`/`WaitForPRMerged` waits do not
  need to be canonical for this story to pass. This is confirmed against 39-10's plan; if 39-10
  shipped a stricter for-all clause, this story escalates to also register those waits (a scope
  bump recorded here, not silently absorbed).
- **D3 — Removal is mandatory (ratchet), not cleanup.** 39-10's `KnownContractViolations`
  discipline fails the build on a *stale* allowlist entry (workflow now declares). So deleting
  `SingleIssueCycleWorkflow`'s line is forced by the gate the moment AC1 lands — the two edits are
  one atomic change.
- **D4 — Track 39-10's shipped names.** If the attribute/enum/registry names differ from the plan
  at merge, adopt the shipped names verbatim. This story owns no contract; it consumes 39-10's.

## Implementation Steps

1. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`** — add
   `[ResumeBehavior(ResumeMode.Both, SuspendActivities = [typeof(WaitForAgentRunActivity)])]` to
   the class (D1). Add the `using` for the 39-10 attribute namespace.

2. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs`
   (or wherever `LegacyResumeAllowlist` is seeded)** — remove the `SingleIssueCycleWorkflow` entry
   (D3).

3. **VERIFY clause (b)/(c) preconditions** — confirm (assert in a small dedicated test if not
   already covered) that `WaitForAgentRunActivity` is in `CanonicalSuspendActivities` (40-2) and
   that the built `SingleIssueCycleWorkflow` graph contains both `WaitForAgentRunActivity` and
   `ComputeTaskResumeIndexActivity` (40-4). If either is missing, this story is blocked on that
   story (the gate working as designed).

4. **RUN `ResumableStandardStructuralTests`** — all clauses green for `SingleIssueCycleWorkflow`
   with no allowlist entry (AC3). Run the full workflow suite (AC6). Finish with `dotnet test`.

## Data & Migrations

None. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

None. No runtime path changes.

## Test Plan

All NUnit + FluentAssertions.

- **`ResumableStandardStructuralTests` (existing, 39-10)** — now asserts `SingleIssueCycleWorkflow`
  passes clauses (a)/(b)/(c) + honesty inverse with no allowlist entry; a stale entry (if not
  removed) fails. **Covers AC2, AC3, AC5.**
- **`SingleIssueCycleResumeDeclarationTests`** (new, small) — reflect the `[ResumeBehavior]` on
  `SingleIssueCycleWorkflow`: `Mode == Both`, `SuspendActivities` contains
  `WaitForAgentRunActivity`; assert `CanonicalSuspendActivities` contains `WaitForAgentRunActivity`
  (gate `"agent-run"`). **Covers AC1, AC4.**
- **Full workflow suite** — green, no behavioral regression. **Covers AC6.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `[ResumeBehavior(Both)]` declared | 1 | `SingleIssueCycleResumeDeclarationTests` |
| 2 — allowlist entry removed | 2 | `ResumableStandardStructuralTests` stale-entry check |
| 3 — passes gate with no allowlist entry | 1, 2, 4 | `ResumableStandardStructuralTests` |
| 4 — canonical registry entry confirmed | 3 | `SingleIssueCycleResumeDeclarationTests` |
| 5 — declaration honesty holds | 1 | `ResumableStandardStructuralTests` honesty inverse |
| 6 — no behavioral change | 4 | Full workflow suite green |

## Dependencies & Sequencing

- **Hard prerequisites:** 39-10 (the attribute + gate + allowlist), 40-2 (`WaitForAgentRunActivity`
  registered + loop node — clause b), 40-4 (`ComputeTaskResumeIndexActivity` in graph — clause c).
  This story is the LAST of the four to land; it cannot pass earlier (the gate enforces order).
- **In place, verified:** `ResumableStandardStructuralTests` enumerator, `WorkflowTestHelper`.
- **Feeds:** nothing downstream — it is the epic's acceptance gate for resumability.
- **Sequencing within the story:** 1/2 (atomic) → 3 → 4.

## Risks & Mitigations

- **Landed before 40-2/40-4 ⇒ clause (b)/(c) fail.** Mitigation: this is the gate working; sequence
  40-5 last (execution plan wave). Step 3 verifies preconditions before flipping.
- **39-10 shipped a stricter for-all clause (b) than planned.** Mitigation: D2 records the check
  against 39-10's actual wording; a stricter clause escalates to a recorded scope bump (register
  the legacy waits), not a silent workaround.
- **Attribute/enum name drift from 39-10.** Mitigation: D4 tracks shipped names; mechanical rename.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1, 2 | attribute + allowlist removal | 0.5 |
| 3 | precondition verification test | 0.5 |
| 4 | run gate + full suite, fix any honesty-inverse surprise | 1.0 |
| **Total** | | **2.0** (story estimate: 2-3 days) |
