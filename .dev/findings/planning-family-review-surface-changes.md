# Finding: Planning-family migration (39-14) — review-surface behavior changes

**Date**: 2026-07-23
**Author**: Claude (agent)
**Type**: 📚 Lesson Learned
**Category**: Architecture

## 📋 Summary

Moving `PlanGeneration` + `PlanReview` onto the document lifecycle (Story 39-14) changed two
observable behaviors of the plan-review surface: `defer`/`split` verdicts retire, and `PlanReview`
becomes a store read-through shim (review already happened inside the Plan lifecycle). Both are
intentional; recorded here so operators/reviewers aren't surprised.

## 🔍 Context

Discovered/decided while implementing **Story 39-14** (D1, D2, D5). The legacy `PlanReviewWorkflow`
ran a bespoke 3-phase debate (7 role reviews + 7 rebuttals + a PO decision) whose PO phase could emit
`defer`/`split`. The unified lifecycle puts review + revise inside `document-lifecycle` via 39-7's
panel producers; the accept decision is the orchestrator's (the acceptor is an actor, not a branch).

### Related Components
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/{PlanGenerationWorkflow,PlanReviewWorkflow}.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` (the untouched parent)
- Epic 39, Stories 39-14 / 39-4 / 39-7 / 39-17

## 💡 The Findings

### 1. `defer` / `split` retire from the review verdict surface (D2)
The unified `Review` decision enum (39-4) has no `defer`/`split` members. The `PlanReview` shim always
outputs empty `deferred`/`split` (`"[]"`); `SingleIssueCycleWorkflow`'s `NeedsModification`/`Defer`/
`Split` branches stay compiled but become **unreachable** from the shim (it maps accepted→`"approved"`,
everything else→`"needsHuman"`). Scope routing (defer/split) is the orchestrator's job. **If product
wants defer/split back, it lands as an orchestrator accept-decision capability (39-17), never as a
reviewer verdict.**

### 2. `PlanReview` is now a deterministic read-through shim (D1)
`PlanReviewWorkflow` (DefinitionId `plan-review`, kept for the SingleIssueCycle call site) makes **no**
llm-call. It reads the latest accepted `Plan` + its `Review` lineage from the document store and maps
to the legacy output shape. Review actually happens inside the Plan lifecycle (panel producers =
lifecycle revise rounds). This avoids a double-review; the call site is unchanged and byte-stable.

### 3. Round count reconstruction moved (D5, survey finding)
The two workflows never emitted dedicated `PLAN.*` DCB constants — their only in-workflow emissions
were `CONTEXT.STORE_ROLE.*` (from `StoreRoleFindingActivity`). The binding keeps ONE aggregate-review
store node (`Role = "plan-review"`) so that family continues and the KB keeps receiving review content.
Round count, previously reconstructable only from `po-decision-round-{N}` role strings, is now
reconstructable from `DOCUMENT.REVISION_STARTED`/`DOCUMENT.REVIEWED` round tags.

### 4. The per-type acceptance-rules surface gained an autonomy floor (2026-07-24 update)
`AcceptanceRules` now carries an optional `AcceptorRequirement` (`any` | `human`) — the per-type
autonomy floor filed back from 39-13 D4 (see the sibling finding). It does NOT touch the plan/review
panel defaults recorded above: `plan` and `review` keep the 7-role majority panel and
`AcceptorRequirement.Any`; only `design` ships `human`. Relevant here because it lands in the same
`AcceptanceDefaults.For` per-type table this finding's panel defaults live in, and because **39-17
now has two per-type policy inputs to honor, not one** — the autonomy dial AND the acceptor floor —
alongside whatever it does about defer/split (item 1).

## ✅ Action Items
- [ ] If defer/split scope routing is still wanted, implement it as a 39-17 orchestrator accept-decision
      capability (not a reviewer verdict).
- [ ] Dashboards/consumers that read `deferred`/`split` from `plan-review` output should treat them as
      always-empty and migrate to the orchestrator's routing signal.
- [ ] When 39-17 lands, have its routing honor `AcceptanceRules.AcceptorRequirement` (`human` ⇒ never
      `AcceptanceRouting.DecideSelf`). Nothing reads the field today — see
      `.dev/findings/assessment-family-policy-gaps.md` #2.

## 🔗 Related
- `.dev/findings/assessment-family-policy-gaps.md` (39-13 sibling behavior changes; the four filed-back
  items there are now resolved, with the `AcceptorRequirement` consumer wiring left to 39-17)
- `.dev/findings/document-lifecycle-persist-not-wired.md` (AC-store gap affecting execution tests)
- `docs/stories/epic-39/story-39-14/implementation-plan.md` (D1, D2, D5)

## 📊 Impact Assessment
**Severity**: 🟡 Medium — no 39-14 gate is affected; the parent's compat surface is pinned by
execution-test scenario (h). The defer/split retirement is the most consumer-visible.

---

**Status**: 🔍 Needs Review
**Last Updated**: 2026-07-24
