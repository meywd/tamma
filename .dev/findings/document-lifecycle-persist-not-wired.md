# Finding: DocumentLifecycleWorkflow does not persist document_instances during a live run

**Date**: 2026-07-23
**Author**: Claude (agent)
**Type**: 🚨 Known Issue
**Category**: Integration

## 📋 Summary

`DocumentLifecycleWorkflow` (39-6) drives produce → validate → review → revise → accept and emits
the `DOCUMENT.*` event family, but it **never calls `PersistDocumentInstanceActivity`** (39-11). The
persist activity exists and is DI-registered, and the read side (`IDocumentInstanceRepository`) is
consumed by `LifecycleReEntryService`, but nothing in the lifecycle graph writes a
`document_instances` row. A live produce→accept run therefore persists **no** document rows — only
the event stream and the workflow-instance state are durable.

## 🔍 Context

Discovered while implementing **Story 39-12** (pilot migration of `IssueDecompositionWorkflow` onto
the lifecycle). 39-12 AC5 requires reading the accepted `Decomposition` instance back through the
39-11 store after a live run. That read returns nothing because the write half was never wired into
the generic lifecycle.

### Related Components
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` (no `PersistDocumentInstance*` reference)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/PersistDocumentInstanceActivity.cs` (defined, DI-registered, uncalled)
- `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleReEntryService.cs` (read side, works when the store is seeded)
- Epic 39, Stories 39-6 (lifecycle) and 39-11 (store)

## 💡 The Finding

39-11 shipped the engine→store **seam** (the activity + repository + migration) but deferred the
per-transition `persist + emit` wiring into the lifecycle graph — its own doc-comment defers this.
39-6's graph was authored before/alongside 39-11 and never took the dependency. The result is a
split-brain: the DCB event stream records the full lifecycle, but the queryable
`document_instances` projection stays empty on a live run. Any consumer that expects to read the
latest accepted document from the store (Stories 2-15 / 2-16, and 39-12 AC5) sees nothing until the
persist step is added.

### Why Does This Matter?

The store is the intended **read model** for accepted documents. Without the write wiring, the store
is only ever populated by tests that seed it directly, and the "typed documents persisted with
lineage" promise of Epic 39 is not met end-to-end at runtime.

## 📊 Details

- `grep -c PersistDocumentInstance DocumentLifecycleWorkflow.cs` → **0**.
- The activity is registered in `Tamma.ElsaServer/Program.cs` and referenced only by
  `LifecycleReEntryService` (read path) — never scheduled by the lifecycle graph.
- 39-12's crash/re-entry execution scenarios (e)/(f) **seed** the store (the 39-10 re-entry test
  pattern) so they exercise the read path; the happy-path scenario (a)'s live store-read assertion
  is the half that cannot pass until this is fixed.

## ✅ Action Items

- [ ] Wire `PersistDocumentInstanceActivity` into `DocumentLifecycleWorkflow` at each document
      transition (produced / validated / reviewed / revised / accepted / rejected / escalated), so a
      live run projects `document_instances` rows with lineage. This is a **39-6 + 39-11** change,
      not a per-binding (39-12) change — do not patch it inside `IssueDecompositionWorkflow`.
- [ ] Once wired, re-enable 39-12 execution scenario (a)'s live store-read assertion (currently the
      re-entry scenarios seed the store to cover the read path).

## 🔗 Related
- Story: `docs/stories/epic-39/story-39-12/implementation-plan.md` (Dependencies & Sequencing — "if
  39-11 lands store-only, the wiring gap is filed against 39-11/39-6, not patched here")
- Story: `docs/stories/epic-39/story-39-11/39-11-document-store-and-lineage-api.md`

## 📊 Impact Assessment

**Severity**: 🟠 High — blocks the store read-model promise end-to-end at runtime; does not block
39-12's own gates (structure/resume/events all pass; the pilot is proven on the event stream).

---

## ✅ Resolution (2026-07-23)

Wired `PersistDocumentInstanceActivity` into `DocumentLifecycleWorkflow`. Because the store is
insert-only (PK = `envelope.Id`), each distinct envelope is persisted **exactly once**: at
revise-start (before it is superseded) and at the terminal transition (accepted/rejected/escalated,
the escalate persist gated for the no-draft case). A pre-minted `UuidV7` workflow variable
(`TransitionEventId`) links each transition's `DOCUMENT.*` emit and its adjacent persist via
`CorrelatingEventId` (the AC7 seam), resume-deterministic across suspend/resume, and inherits the
39-10 re-entry gating (no double-persist on crash/re-entry). Fail-loud preserved. No schema change.
Structure test pins the persist node set + mint→emit→persist adjacency.

**Residual (deliberately not gold-plated):** the store projects terminal state + the superseded
revision chain, NOT a row per intermediate produced→validated→reviewed state — consistent with the
store's "read-optimized product layer, stream wins" doctrine. Per-transition status rows, if ever
wanted, are a clean follow-up via a `SetDocumentInstanceStatus` activity over the existing
`/api/engine/documents/{id}/status` endpoint. The `[Explicit]` execution-test harness still seeds the
store (its `CapturingHandler` stub records the persist POST but doesn't feed the read fake) — a
harness-only follow-up; the wiring itself is proven by the structure-test pins.

---

**Status**: ✅ Resolved
**Last Updated**: 2026-07-23
