# Bug: a repair after a revise mints a draft with a null supersedes edge

**Date**: 2026-07-24
**Reporter**: Claude (agent)
**Status**: ✅ Resolved — 2026-07-24 (confirmed reproducible in the code as written, then fixed)
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

## Resolution (2026-07-24)

Confirmed as filed: `IngestDraft` derived the edge as `isRevise ? state.Current?.Id : null`, and
both `IngestProduce` and `IngestRepair` passed `isRevise: false`, so every repair draft — including
one produced inside a revise round — was minted with a null edge.

**What shipped** — the supersedes derivation moved out of the graph into the pure decision core
(D1), as a three-valued turn instead of a boolean:

- `DocumentLifecycleHelper.DraftOrigin { Produce, Repair, Revise }` +
  `DocumentLifecycleHelper.ResolveSupersedes(state, origin)`:
  - `Produce` → `null` (starts the chain) — unchanged,
  - `Revise` → `state.Current?.Id` (extends the chain) — unchanged,
  - `Repair` → `state.Current?.SupersedesDocumentId` (**inherits** the position of the draft it
    replaces) — the fix.
- `DocumentLifecycleWorkflow.IngestDraft` takes a `DraftOrigin` instead of `bool isRevise`; the
  three ingest sites pass `Produce` / `Repair` / `Revise`. Every non-repair path is byte-for-byte
  the previous behaviour.

**Why the inherited edge cannot collide with the unique filtered index.** The lifecycle persists
an envelope at exactly two kinds of site: `PersistRevised` (the current draft, as a revise is about
to supersede it) and the terminal `Persist{Accepted|Rejected|Escalated}`. A draft that a repair
replaces reaches *neither* — the repair happens between VALIDATE and any persist — so it is never
written to `document_instances`. The inherited prior therefore gains exactly ONE successor row (the
surviving repaired draft), no matter how many repair turns run in the round. A repair of a first
draft inherits `null` and still supersedes nothing.

**Coverage** (`tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleHelperTests.cs`, fast gate):
`ResolveSupersedes_ProduceStartsTheChain_ReviseExtendsIt`,
`ResolveSupersedes_RepairOfAFirstDraft_SupersedesNothing`,
`ResolveSupersedes_RepairInsideAReviseRound_InheritsTheChainPosition`,
`ConsecutiveRepairsInsideAReviseRound_AllInheritTheSameEdge`, and the lifecycle-level
`ProduceReviewReviseRepairAccept_PersistedChainIsUnbroken` — which drives the graph's stage order
and collects the envelopes at the two persist sites, asserting one unbroken chain, that the
replaced revision is absent from the store, and that no prior gains two successors. Verified to
FAIL on the pre-fix expression (three of the five red when `ResolveSupersedes` is temporarily
reverted to `origin == Revise ? Current?.Id : null`).

**Left for follow-up.** The end-to-end runtime proof lives in `DocumentLifecycleExecutionTests`
(`[Explicit]`, CI-Postgres jobs). A scenario there — script `valid → concerns review → invalid
revision → repair → approve` and assert the published `AcceptanceRequest.Lineage` (and the
`PersistDocumentInstanceActivity` HTTP bodies the harness already captures) carry the inherited
edge — would additionally prove the *routing*: that the repair ring really is re-entered after a
revise and that the real `document_instances` insert accepts the inherited edge without a `23505`.
The pure pin cannot see either. That fixture does not run outside the CI Postgres jobs (it faults
with `WorkflowGraphNotFoundException` locally), so it was not extended here.

## Related
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` (`IngestDraft`)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/DocumentInstanceRepository.cs` (chain invariant)
- `.dev/findings/assessment-family-policy-gaps.md` (39-13 follow-ups)
- `.dev/findings/document-lifecycle-persist-not-wired.md` (the persist wiring this rides on)

---

**Last Updated**: 2026-07-24 (resolved)
