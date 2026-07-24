# Story 40-5: `[ResumeBehavior]` on `SingleIssueCycleWorkflow` + Allowlist Burn-Down

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

As a **platform maintainer relying on the build gate to answer "is this workflow resumable?"**,
I want `SingleIssueCycleWorkflow` to **statically declare its resume behavior and pass 39-10's
structural test without an allowlist entry**,
So that the coding cycle's resumability is enforced by CI — not by trusting that 40-2/40-3/40-4
were wired correctly — and the epic's central claim ("the coding step is resumable by design")
is machine-checked.

## Priority

P0 — This is the acceptance gate that makes 40-2/40-3/40-4 real rather than aspirational. It is
the first Epic-40 consumer of 39-10's `ResumableStandardStructuralTests` + `LegacyResumeAllowlist`
burn-down, exactly as 39-12 is the first Epic-39 consumer.

## Architectural Context (READ FIRST)

**39-10 has LANDED, and `SingleIssueCycleWorkflow` sits on its legacy allowlist.** The allowlist is
a private static field inside the test fixture
`apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs:43` —
the only file in the solution that names the symbol — so this story's burn-down is a
**tests-project** edit, not a production-code one.

The shipped entry (`:75`) reads, verbatim:

```
["SingleIssueCycleWorkflow"] = "issue-cycle orchestration composite, delegates to sub-workflows (burn-down: 39-14+).",
```

**Corrected** *(earlier drafts of this story claimed the entry "names Epic 40 as the burn-down
story" — it does not; it nominates **39-14+**).* 39-14/39-15 burned down the cycle's
sub-workflows (`TaskCreationWorkflow`, `TestCaseCreationWorkflow`, the triage trio, …) and left the
cycle itself allowlisted, so Epic 40 is the de-facto burn-down owner by inheritance. Epic 40's own
reading of *why* it is still there — the coding step is the last non-durable, inline-monitor step
(`ExecuteAgentActivity` at `SingleIssueCycleWorkflow.cs:571`) — is this epic's analysis, not a
quote from the entry.

**The gate has four live clause groups** (39-10 AC3 / D4 / D5) — plus two pins this story does not
move: the 39-15 universal pin that no document *producer* remains allowlisted (`:266`; the cycle
dispatches sub-workflows, not `document-lifecycle`, so it is unaffected either way) and the
`DocumentLifecycleWorkflow` AC2 pin (`:294`). All in `ResumableStandardStructuralTests`:

- (a) `EveryWorkflow_DeclaresResumeBehavior_XorIsAllowlisted` (`:107`) + the stale-entry ratchet
  (`:133`) — declare XOR allowlist; a workflow that starts declaring must lose its entry;
- (b) `EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode` (`:158`) — `BookmarkSuspend`/`Both` ⇒
  the graph contains ≥1 node whose type is in BOTH the declaration's `SuspendActivities` AND
  `LifecycleBookmarks.CanonicalSuspendActivities` (an **existence** check, `:177-189`);
- (b-inverse) `CanonicalSuspendNode_AppearsOnlyInDeclaredWorkflows` (`:202`) — a canonical suspend
  node may appear only in a workflow that declares and lists it;
- (c) `EveryReEntryWorkflow_HasAComputeReEntryNode` (`:240`) — `LatestStateReEntry`/`Both` ⇒ a
  re-entry node in the graph. **As shipped, `:252` tests exact type identity against the single
  hardcoded `ComputeReEntryPositionActivity`**, which is document-coupled and unusable for code
  (epic README, "NOT a dependency"). 40-4 (AC10) replaces that with membership of the
  `CanonicalReEntryActivities` registry and registers `ComputeTaskResumeIndexActivity` in it.

By the time this story runs, 40-2 has registered `WaitForAgentRunActivity` in
`CanonicalSuspendActivities` and switched the loop node to it; 40-4 has added
`ComputeTaskResumeIndexActivity` to the graph **and widened clause (c) so the gate can see it**.
Only then does the cycle genuinely satisfy (b) and (c) — this story flips the declaration and
removes the allowlist entry, and the gate proves it.

## Acceptance Criteria

1. **`[ResumeBehavior(Both)]` declared.** `SingleIssueCycleWorkflow` carries
   `[ResumeBehavior(ResumeMode.Both, SuspendActivities = new[] { typeof(WaitForAgentRunActivity) })]`
   (39-10's attribute, `Tamma.Core/Documents/Resume/ResumeBehavior.cs:39`). `Both` because the
   cycle both suspends on the durable agent-run bookmark (40-2) and re-enters from latest
   git/event state (40-4). Use the `new[] { … }` array-creation form — every shipped declaration
   does (e.g. `DocumentLifecycleWorkflow.cs:54`, `ClarifyingQuestionsWorkflow.cs:40`), and an
   attribute argument must be a constant / `typeof` / array-creation expression.

2. **Allowlist entry removed.** The `["SingleIssueCycleWorkflow"] = …` line at
   `ResumableStandardStructuralTests.cs:75` is deleted. Per the ratchet (`:133`), a stale entry
   (workflow now declares) fails the build — so the removal is mandatory, not optional.

3. **Structural test passes with no allowlist entry.** Every clause passes for
   `SingleIssueCycleWorkflow`: it declares (a); its graph contains `WaitForAgentRunActivity`, which
   is in its declaration's `SuspendActivities` **and** in `CanonicalSuspendActivities` (b, the
   intersection at `:177-189`); and clause (c) resolves because the re-entry-activity **set** —
   `CanonicalReEntryActivities`, landed by 40-4 AC10 — contains `ComputeTaskResumeIndexActivity`,
   which is in the graph. *(Corrected: before 40-4's seam, clause (c) tested exact identity against
   `ComputeReEntryPositionActivity` only (`:252`), so this AC could not pass as originally written.)*
   This is the burn-down proof.

4. **Both canonical registries confirmed.** `WaitForAgentRunActivity` is in
   `LifecycleBookmarks.CanonicalSuspendActivities` with gate `"agent-run"` (landed by 40-2) **and**
   `ComputeTaskResumeIndexActivity` is in `CanonicalReEntryActivities` (landed by 40-4). Asserted
   here so both dependencies are explicit and a regression in either reddens this story's test
   rather than silently degrading the gate.

5. **Declaration honesty holds — for the check that exists.** The shipped inverse
   (`CanonicalSuspendNode_AppearsOnlyInDeclaredWorkflows`, `:202-236`) passes: the cycle does not
   contain a canonical suspend node without declaring `BookmarkSuspend`/`Both` and listing it — so
   a `LatestStateReEntry`-only declaration would fail once `WaitForAgentRunActivity` is in the
   graph, and `Both` is the truthful declaration. *(Corrected: the gate has NO re-entry inverse —
   nothing fails a `BookmarkSuspend`-only declaration that contains a re-entry node. Earlier
   drafts claimed that check passes; it does not exist, and adding it is explicitly out of 40-4's
   seam scope. Declaring `Both` makes the question moot for this workflow.)*

6. **No behavioral change.** This story adds an attribute and deletes an allowlist line; it changes
   no runtime wiring (that is 40-2/40-3/40-4) and no gate code (that is 40-4 AC10). The full
   workflow test suite stays green.

## Technical Notes

- **This story is deliberately thin and gated on 40-2 + 40-4.** It cannot pass until the loop node
  is `WaitForAgentRunActivity` (40-2) AND the re-entry node is present **and representable in the
  gate** (40-4's node + clause-(c) seam). Attempting it earlier fails clause (b) or (c) — which is
  the gate working as designed.
- **`Both`, not `BookmarkSuspend`.** The cycle has *two* resumability properties (suspend +
  crash re-entry). `LatestStateReEntry`-only would fail the shipped suspend-node inverse (AC5)
  once `WaitForAgentRunActivity` is in the graph; `BookmarkSuspend`-only would not fail any
  shipped check but would be a false declaration — declare `Both`.
- **`Both` is a claim about wiring, not about runtime efficacy.** The gate reads attributes and
  built graphs only; it never reads DI. Under 40-4's shipped default the re-entry service is the
  Null seam, so a green gate here means "the cycle is wired to re-enter", not "re-entry is on".
  40-7 flips that default (40-4 AC6). Same posture `DocumentLifecycleWorkflow` shipped under
  39-10 D7 before 39-11 landed — accepted precedent, stated so nobody reads the gate as proof of
  behavior.
- **The 39-10 contract is shipped and fixed, no name drift to hedge against** *(Corrected: earlier
  drafts hedged "if 39-10's names differ at merge")*: `Tamma.Core.Documents.Resume.ResumeMode
  { BookmarkSuspend, LatestStateReEntry, Both }` (`ResumeBehavior.cs:11`) and
  `ResumeBehaviorAttribute(ResumeMode mode)` with `Type[] SuspendActivities { get; init; }`
  (`:39-54`).

## Dependencies

- **Story 39-10 — LANDED (this story is its consumer).** `ResumeBehaviorAttribute`/`ResumeMode`
  (`Tamma.Core/Documents/Resume/ResumeBehavior.cs:11`, `:39`), `CanonicalSuspendActivities`
  (`LifecycleBookmarks.cs:98-105`), `LegacyResumeAllowlist` +
  `ResumableStandardStructuralTests` (`…/ResumableStandardStructuralTests.cs:43`, `:34`).
  *(Corrected: earlier drafts listed 39-10 as an unlanded hard prerequisite.)*
- **Story 40-2 — HARD.** `WaitForAgentRunActivity` + its `CanonicalSuspendActivities` registration
  + the loop node swap (clause b).
- **Story 40-4 — HARD, on two counts.** `ComputeTaskResumeIndexActivity` in the graph, **and** the
  clause-(c) `CanonicalReEntryActivities` seam (40-4 AC10) without which this story's AC3 cannot
  pass. Merge order: 40-4 → 40-5 (both edit `ResumableStandardStructuralTests.cs`).
- **Existing:** the structural test enumerator (`TaxonomyDriftBuildTests` anchor), `WorkflowTestHelper`.

## Estimated Effort

2-3 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
| 2026-07-24 | 1.1.0   | Review pass: corrected the allowlist entry's text/owner (39-14+, not Epic 40) and its location (test fixture); AC3 now depends on 40-4's clause-(c) registry rather than a hardcoded type; AC4 covers both registries; AC5 limited to the inverse that actually exists; attribute syntax fixed to the shipped `new[] { … }` form; 39-10 recorded as landed | Claude |
