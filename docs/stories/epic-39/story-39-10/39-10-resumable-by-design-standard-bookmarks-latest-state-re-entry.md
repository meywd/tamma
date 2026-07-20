# Story 39-10: Resumable-by-Design Standard — Bookmarks + Latest-State Re-Entry + Structural Test

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

As a **platform operator** (and the orchestrator running unattended in full-auto mode),
I want every lifecycle workflow to be resumable **by construction** — suspending on a deterministic bookmark when it needs external input, and re-entering from the latest accepted state after a crash/restart instead of starting from scratch,
So that a deploy, a pod eviction, or a week-long human approval never costs a workflow its accumulated work, and "is this workflow resumable?" is answered by a build gate rather than by reading its source.

## Priority

P0 — Third pillar of Epic 39 ("Resumable by design", epic README). 39-12's pilot migration must land on a workflow that already knows what "resumable" means, and the supervised accept gate (39-8) is a suspend that only makes sense if resume is guaranteed. This story turns the existing DesignProposal/Clarify pattern from a per-workflow favor into an authoring standard.

## Architectural Context (READ FIRST)

Two resume modes exist today, both proven but neither standardized:

**(a) Bookmark suspend/resume (the pattern to generalize):**
- `apps/tamma-elsa/src/Tamma.Activities/Clarify/WaitForClarifyingAnswersActivity.cs` and `apps/tamma-elsa/src/Tamma.Activities/Design/WaitForDesignApprovalActivity.cs` — the two existing suspend activities, resumed via `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/ClarifyResumeEndpoint.cs` / `DesignResumeEndpoint.cs`.
- **Bookmark names are tenant-folded and deterministic** — the canonical bookmark-name builder gives suspend/resume parity (same inputs → same name) with the tenant id folded in so tenant A can never resume tenant B's bookmark.
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Clarify/ClarifyResumeReadBackTests.cs` — **the serialization-tolerance matrix**. The #15/#437 lesson: the in-process runtime hands the resumed input back as a boxed `bool`, but a distributed dispatcher round-trips it to a `string` or `JsonElement`; a bare `is true` pattern silently takes the wrong branch under serialization while returning HTTP 200. Every resume read-back MUST be coercion-tolerant across the boxed/string/JsonElement matrix, and every new suspend point inherits this test shape (see also `Design/DesignResumeReadBackTests.cs`).

**(b) Crash/restart re-entry (the pattern to introduce):**
- Position is reconstructed from **persisted document lineage + DCB events** — not from Elsa's serialized instance state alone.
- `docs/stories/epic-4/story-4-7/4-7-event-query-api-time-travel.md` — the event query API (query by tags/`issueId`, time-travel reads) used to fetch the issue's event history.
- `docs/stories/epic-4/story-4-8/4-8-black-box-replay-debugging.md` + `apps/tamma-elsa/src/Tamma.Api/Services/Engine/Replay/ReplayReconstructor.cs` (tests: `tests/Tamma.Api.Tests/Engine/ReplayReconstructorTests.cs`) — the existing state-from-events reconstructor; re-entry reuses this machinery rather than inventing a second replayer.
- **Story 39-11's latest-accepted-state query** is the primary read: "for issue X, what is the latest accepted document of each type?" A lifecycle workflow re-entering for issue X asks that question first and skips every produce step whose output is already accepted.
- **Idempotent step guards**: a lifecycle step that would produce an already-accepted document is a no-op (guarded by the lineage read), so re-entry never double-produces, double-reviews, or double-emits acceptance events.

**Structural-test precedent:** `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` walks compiled workflow graphs by reflection (via `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs`) and fails the build naming the offender — the same enumerate-and-assert shape this story's structural test uses for resume declarations.

## Acceptance Criteria

1. **Authoring standard document.** `docs/stories/epic-39/resumable-workflow-standard.md` (linked from the epic README) defines: the two resume modes (bookmark suspend vs crash re-entry), when each applies, the deterministic tenant-folded bookmark naming rule, the serialization-tolerance requirement for resume read-backs (the boxed-bool/string/JsonElement matrix, citing `ClarifyResumeReadBackTests`), the idempotent-step-guard rule (already-accepted documents are never re-produced), and the re-entry read sequence (39-11 latest-accepted-state → 4-7 event query → resume position). Concrete code references, not prose-only.

2. **Resume declaration surface.** Every lifecycle workflow statically declares its resume behavior — e.g. a `ResumeBehavior` property/attribute on the workflow (or its lifecycle descriptor from 39-6) with values covering at minimum `{ BookmarkSuspend, LatestStateReEntry, Both }` plus, for bookmark mode, the canonical bookmark-name builder it uses. The declaration is data a test can enumerate, not a doc comment.

3. **Structural test (build gate).** A test in `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/` enumerates every lifecycle workflow (reflection over compiled graphs, `ContractBindingTests` / `TaxonomyDriftBuildTests` pattern) and fails — naming the workflow — if it (a) lacks a resume declaration, (b) declares `BookmarkSuspend` but contains no suspend activity or uses a non-canonical bookmark name, or (c) declares re-entry but has no idempotent guard on its produce steps. A justified allowlist (ratchet-style, entries may only be removed) covers legacy workflows not yet migrated (39-12..39-15 burn it down).

4. **Deterministic tenant-folded bookmark names everywhere.** All lifecycle suspend points build bookmark names through ONE shared canonical builder (generalizing the Clarify/Design builder): same `(tenantId, issueId, documentType, gate)` → same name on suspend and on resume; different tenants → disjoint names. Unit tests cover parity and tenant folding; the serialization-tolerance matrix from `ClarifyResumeReadBackTests` is applied to every new resume read-back (boxed bool, `"true"`/`"True"` strings, `JsonElement` — both truthy and falsy rows).

5. **Re-entry reconstruction implemented.** A re-entry component (e.g. `LifecycleReEntryService` in `apps/tamma-elsa/src/Tamma.Api/Services/`) reconstructs a workflow's position for an issue from (a) 39-11's latest-accepted-state query and (b) DCB events via the Story 4-7 query API / `ReplayReconstructor`, returning a typed resume position ("Decomposition accepted, Plan produced-but-unreviewed → re-enter at review of Plan"). It never guesses from Elsa instance internals.

6. **Idempotent step guards.** Lifecycle produce/review/accept steps check the reconstructed state before executing: an already-accepted document short-circuits produce AND review for that document (no duplicate LLM spend, no duplicate `DOCUMENT.*` acceptance events). A unit test drives a lifecycle twice over the same accepted lineage and asserts zero new produce dispatches and zero duplicate acceptance events.

7. **One proven re-entry integration test.** An integration test (Testcontainers Postgres, existing pattern) runs a lifecycle workflow to a mid-point (e.g. document produced and accepted, next stage pending), **kills the workflow instance** (simulated crash — no graceful suspend), starts a fresh instance for the same issue, and asserts: it re-enters at the correct position, does not re-produce the accepted document, completes, and the final event stream contains exactly one acceptance per document.

8. **Escalation suspends are resumable too.** The supervised accept gate and the escalation sink (39-8) suspend via the same canonical bookmark mechanism, so a human answering days later — possibly after a deploy — resumes correctly. A test resumes a bookmark created before a simulated restart (new host, same store) and asserts the workflow continues on the right branch.

## Technical Notes

- **Re-entry is a read model, not a replay-everything.** The 39-11 latest-accepted-state query answers most of the position question in one indexed read; the event query fills in sub-stages (produced-but-unreviewed, review round N). Full `ReplayReconstructor` replay is the fallback for forensic/edge cases, not the hot path.
- **Elsa instance state is an optimization, not the truth.** If Elsa's persisted instance resumes cleanly, fine — but the standard requires correctness even when the instance is gone (crash, store loss, definition version bump). Document lineage + events are the durable truth; this is the same DCB principle as Story 37-1's "projection is rebuildable."
- **Guard placement.** Put idempotent guards in the generic lifecycle (39-6) once, not in each migrated workflow — migrations (39-12..39-15) then inherit them. The structural test's clause (c) checks the lifecycle descriptor wiring, not hand-rolled per-workflow guards.
- **What "lifecycle workflow" means for the allowlist.** At this story's landing only the pilot (39-12) may be on the lifecycle; the structural test still ships with the enumerator + allowlist so every subsequent migration must declare-or-fail from day one.
- Serialization tolerance is not optional polish — #15/#437 was a silent wrong-branch-with-HTTP-200 bug. Reuse the coercion helpers rather than re-implementing `is true` checks.

## Dependencies

- **Story 39-6 (DocumentLifecycleWorkflow)** — the lifecycle descriptor carries the resume declaration and the step guards. Blocking.
- **Story 39-11 (Document Store & Lineage API)** — the latest-accepted-state query is the re-entry read. Blocking for AC5/AC7 (can be developed in parallel against its contract).
- **Story 39-8 (Escalation & Approval Surface)** — its suspend points adopt the canonical bookmark builder (AC8).
- **Existing:** Elsa 3 bookmarks; Clarify/Design suspend-resume endpoints + read-back tests; Story 4-7 event query API; Story 4-8 `ReplayReconstructor`.
- **Consumed by 39-12..39-15** — every migration must satisfy the structural test and shrink the allowlist.

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
