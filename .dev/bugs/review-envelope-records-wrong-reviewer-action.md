# Bug: the Review envelope recorded a different reviewer action than the one actually used

**Date**: 2026-07-25
**Status**: ✅ Fixed
**Severity**: 🟡 Medium — wrong provenance on a persisted audit document; no wrong routing
**Introduced by**: Story 39-15
**Found by**: the Epic 41/42 implementation-planning pass

## Summary

Two code paths choose the reviewer's action, and since Story 39-15 they disagreed for
`triage-decision` documents:

| Path | Call | Doc-type aware? |
|---|---|---|
| Reviewer **selection** — `ReviewerSelectionHelper.ResolveDocumentAction` (`:153-168`) | `RolePhaseMap.GetPanelActionForRole(role, documentTypeKey)` | **Yes** |
| Review envelope **provenance** — `DocumentLifecycleWorkflow.BuildReviewEnvelope` (`:1212`) | `RolePhaseMap.GetReviewActionForRole(role)` | No |

`GetPanelActionForRole` returns `GetTriageActionForRole(role)` when the document type key is
`triage-decision`, and `GetReviewActionForRole(role)` otherwise (`RolePhaseMap.cs:430-433`). So for
a `triage-decision` review the reviewer critiqued the draft through their **triage** lens, while the
`Review` document persisted for that critique recorded its producer action as the **plan/task
review** lens.

The review itself was correct — the right reviewer ran with the right prompt. What was wrong is the
audit record of which lens produced it.

## Why it happened

Story 39-15 needed a doc-type-aware panel action so a `triage-decision` draft would be critiqued
through each role's triage lens. `GetPanelActionForRole` was added for that and wired into
`ReviewerSelectionHelper` — the *selection* site. `BuildReviewEnvelope`, which independently
reconstructs the same `(role, action)` pair to stamp the document's `DocumentProducer`, was not
updated. Two call sites, one changed.

Nothing caught it because the two sites are in different assemblies, no test asserts that the
envelope's recorded action equals the action the selector chose, and both values are legal members
of the same enum — so every schema and taxonomy check passes.

## Fix

`BuildReviewEnvelope` now calls `GetPanelActionForRole(reviewerRole, state.TypeKey)` — the same call
with the same arguments as the selection path. `LifecycleState.TypeKey` is the document type key
already carried in lifecycle state, so no new plumbing was needed.

## Lesson

**When a derived value is reconstructed at a second site, the two sites are a lockstep pair even
though nothing in the type system says so.** The safer shape is to compute the pair once at
selection time and carry it forward in lifecycle state, rather than deriving it twice from a role.
That refactor is larger than this fix and is worth considering when `BuildReviewEnvelope` is next
touched.

Corollary for reviewers: a "single source of truth" comment on the *map* (`PanelReviewWorkflow.cs:46`,
`ReviewerSelectionHelper.cs:10` both say it) does not make the *callers* consistent. The map was
never the problem.

## Related

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs:1212`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs:153-168`
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:430-433`
- `docs/stories/epic-39/story-39-15/` (introducing story)
