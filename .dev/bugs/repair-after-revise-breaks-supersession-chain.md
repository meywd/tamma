# Bug: a repair after a revise mints a draft with a null supersedes edge

**Date**: 2026-07-24
**Reporter**: Claude (agent)
**Status**: 🔍 Open — not introduced by, and not fixed by, the 39-13 follow-up work
**Severity**: 🟡 Medium — silent lineage corruption, no fail-loud signal
**Area**: Epic 39 document lifecycle / store (39-6 + 39-11)

## Summary

In `DocumentLifecycleWorkflow`, `IngestDraft` derives the produced envelope's
`SupersedesDocumentId` **solely from `isRevise`**: a revise supersedes the reviewed draft, and
every other branch supersedes nothing. But `isRevise: false` does **not** mean "first draft" — a
deterministic **validation repair** also takes that branch, and repairs occur *inside* revise
rounds too.

So the sequence `produce → review → revise → (validate fails) → repair` mints the repair draft with
`SupersedesDocumentId = null`, **breaking that round's supersession chain**. The repaired revision
becomes an orphan rather than the successor of the draft it replaced.

## Why it matters

- The store's chain is the lineage consumers walk (`document_instances`, the 39-11 lineage query).
  An orphaned link means a revision history that silently under-reports what superseded what.
- It is **silent**. `DocumentInstanceRepository.InsertAsync` only faults on a *duplicate* supersedes
  edge (unique filtered index on `supersedes_document_id` → `23505`); a **null** edge inserts
  happily. Nothing detects the gap.
- Time-travel debugging and any "show me how this document evolved" surface are affected.

## Discovery

Found while analysing whether Clarification Run B's cross-run `supersedesDocumentId` could be
threaded into the envelope (39-13 follow-up item 4). That investigation concluded the cross-run
edge must NOT be added — and surfaced this pre-existing repair-branch defect as a separate issue.
See `.dev/findings/assessment-family-policy-gaps.md` #4 for the cross-run reasoning.

## Suggested fix

On the repair branch, `IngestDraft` should **inherit** the current chain position rather than
resetting it — i.e. carry `state.Current?.SupersedesDocumentId` through, so a repair produced
during a revise round keeps pointing at the draft the revise superseded. Take care that:

- a repair of a *first* draft still supersedes nothing (inheriting `null` is correct there); and
- the inherited edge does not collide with the unique filtered index — a repair *replaces* the
  revision it repairs in the chain, it does not add a second successor to the same prior.

Add a store-level test covering `produce → review → revise → repair → accept` and asserting the
chain is unbroken end-to-end.

## Related
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` (`IngestDraft`)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/DocumentInstanceRepository.cs` (chain invariant)
- `.dev/findings/assessment-family-policy-gaps.md` (39-13 follow-ups)
- `.dev/findings/document-lifecycle-persist-not-wired.md` (the persist wiring this rides on)

---

**Last Updated**: 2026-07-24
