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

**39-10 seeds `SingleIssueCycleWorkflow` in the legacy allowlist.** 39-10's D5 seeds
`LegacyResumeAllowlist` with *"all ~30 current workflows, each with a one-line justification +
the migration story that burns it down."* `SingleIssueCycleWorkflow` is one of them — allowlisted
because, at 39-10's landing, it was the non-durable inline-monitor cycle. Its allowlist entry
names **Epic 40 as the burn-down story**.

**The gate has three clauses** (39-10 AC3 / D4 / D5), checked by
`ResumableStandardStructuralTests` (`apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/`):

- (a) every concrete `WorkflowBase` has a `[ResumeBehavior]` XOR a `LegacyResumeAllowlist` entry
  (a stale entry — workflow now declares — fails the build);
- (b) `BookmarkSuspend`/`Both` ⇒ the graph contains a node whose type is in BOTH the declaration's
  `SuspendActivities` AND `LifecycleBookmarks.CanonicalSuspendActivities`;
- (c) `LatestStateReEntry`/`Both` ⇒ the graph contains the re-entry compute node.

By the time this story runs, 40-2 has registered `WaitForAgentRunActivity` in
`CanonicalSuspendActivities` and switched the loop node to it; 40-4 has added
`ComputeTaskResumeIndexActivity` to the graph. So the cycle now genuinely satisfies both (b) and
(c) — this story flips the declaration and removes the allowlist entry, and the gate proves it.

## Acceptance Criteria

1. **`[ResumeBehavior(Both)]` declared.** `SingleIssueCycleWorkflow` carries
   `[ResumeBehavior(ResumeMode.Both, SuspendActivities = [typeof(WaitForAgentRunActivity)])]`
   (39-10's attribute). `Both` because the cycle both suspends on the durable agent-run bookmark
   (40-2) and re-enters from latest git/event state (40-4).

2. **Allowlist entry removed.** `SingleIssueCycleWorkflow`'s entry in `LegacyResumeAllowlist` is
   deleted. Per 39-10's ratchet, a stale entry (workflow now declares) fails the build — so the
   removal is mandatory, not optional.

3. **Structural test passes with no allowlist entry.** `ResumableStandardStructuralTests`' clauses
   (a)/(b)/(c) all pass for `SingleIssueCycleWorkflow`: it declares; its graph contains
   `WaitForAgentRunActivity` (canonical suspend, registered by 40-2); its graph contains
   `ComputeTaskResumeIndexActivity` (re-entry node, added by 40-4). This is the burn-down proof.

4. **Canonical registry entry confirmed.** `WaitForAgentRunActivity` is in
   `LifecycleBookmarks.CanonicalSuspendActivities` with gate `"agent-run"` (landed by 40-2;
   asserted here so the dependency is explicit and a regression fails this story's test).

5. **Declaration honesty holds.** The 39-10 inverse checks pass: the cycle does not declare
   `BookmarkSuspend`-only while containing the re-entry node, nor `LatestStateReEntry`-only while
   containing a canonical suspend node — `Both` is the truthful declaration and the test confirms
   consistency.

6. **No behavioral change.** This story adds an attribute and deletes an allowlist line; it changes
   no runtime wiring (that is 40-2/40-3/40-4). The full workflow test suite stays green.

## Technical Notes

- **This story is deliberately thin and gated on 40-2 + 40-4.** It cannot pass until the loop node
  is `WaitForAgentRunActivity` (40-2) AND the re-entry node is present (40-4). Attempting it
  earlier fails clause (b) or (c) — which is the gate working as designed.
- **`Both`, not `BookmarkSuspend`.** The cycle has *two* resumability properties (suspend +
  crash re-entry); declaring only one would fail the honesty inverse (AC5) once the other node is
  in the graph.
- **If 39-10's attribute/enum names differ at merge**, adopt the exact 39-10 shipped names — this
  story tracks 39-10's contract, it does not define it.

## Dependencies

- **Story 39-10 — HARD.** `ResumeBehaviorAttribute`/`ResumeMode`, `CanonicalSuspendActivities`,
  `LegacyResumeAllowlist`, `ResumableStandardStructuralTests`. This story is a consumer.
- **Story 40-2 — HARD.** `WaitForAgentRunActivity` + its `CanonicalSuspendActivities` registration
  + the loop node swap (clause b).
- **Story 40-4 — HARD.** `ComputeTaskResumeIndexActivity` in the graph (clause c).
- **Existing:** the structural test enumerator (`TaxonomyDriftBuildTests` anchor), `WorkflowTestHelper`.

## Estimated Effort

2-3 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
