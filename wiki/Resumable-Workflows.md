---
title: "Resumable Workflows (Authoring Standard)"
---

# Resumable Workflows

_Epic 39, Pillar 3 (Story 39-10). Resumable by design, enforced by a build gate._

Most legacy Tamma workflows restarted from scratch if they stopped. Epic 39 makes
resumability an **authoring standard, not a per-workflow favor**: every concrete
`WorkflowBase` in `Tamma.ElsaServer` must either declare `[ResumeBehavior(...)]` or sit on
a justified, ratchet-style allowlist that the 39-12…39-15 migrations burn down.

> **"Is this workflow resumable?" is answered by a build gate, not by reading its
> source.** `ResumableStandardStructuralTests` enumerates every workflow by reflection and
> fails the build on any that neither declares nor is allowlisted.

---

## 1. The two resume modes

Declared statically via `[ResumeBehavior(ResumeMode, SuspendActivities = …)]`
(`apps/tamma-elsa/src/Tamma.Core/Documents/Resume/ResumeBehavior.cs`):

| Mode | When it applies | Mechanism |
|---|---|---|
| `BookmarkSuspend` | The workflow must WAIT for external input (a human/orchestrator decision, an answer, an approval). | Suspends on a deterministic, tenant-folded bookmark; a later resume recomputes the same name and continues. |
| `LatestStateReEntry` | The workflow can lose its Elsa instance (crash, pod eviction, deploy, definition-version bump) and must resume from durable truth. | A fresh instance for the same issue reconstructs its position from the document store + DCB events and skips work already accepted. |
| `Both` | Both of the above — the `DocumentLifecycleWorkflow` posture. | The accept gate suspends on a bookmark AND Init consults the re-entry reconstructor. |

**Elsa instance state is an optimization, not the truth.** If Elsa's persisted instance
resumes cleanly, good — but correctness must hold even when the instance is gone. Document
lineage + DCB events are the durable truth.

---

## 2. Latest-state re-entry

Re-entry is a **read model, not a replay-everything**. `DocumentLifecycleWorkflow` wires
one `ComputeReEntryPositionActivity`
(`apps/tamma-elsa/src/Tamma.Activities/Documents/ComputeReEntryPositionActivity.cs`) at
Init, whose typed position routes idempotent `FlowDecision` guards:

- `Complete` → short-circuit to the accepted terminal — no produce, no review, and **no
  second `DOCUMENT.ACCEPTED`**; it emits `DOCUMENT.REENTERED` instead.
- `Review` → skip produce/validate, review the existing revision.
- `Accept` → re-suspend on the SAME recovered decision-session bookmark.
- `Produce` → run fresh.

`LifecycleReEntryService`
(`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleReEntryService.cs`) reads, in
order:

1. **Latest-accepted** — `IDocumentInstanceRepository.GetLatestAcceptedAsync(tenantId,
   issueId, ct)` (in-process, never HTTP): the single indexed read answering "what is
   already accepted?".
2. **Event query** — `IEventRepository.QueryEventsAsync` over the issue's `DOCUMENT.*` /
   `APPROVAL.*` slice, to fill in sub-stages (produced-but-unreviewed, unanswered approval
   + recovered session id).
3. **Typed position** — the pure `LifecycleResumeCalculator.Reconstruct(...)`
   (`apps/tamma-elsa/src/Tamma.Core/Documents/Resume/LifecycleResumeCalculator.cs`) folds
   both into a `LifecycleResumePosition`.

**It never guesses.** Store/stream disagreement throws
`DOCUMENT.REENTRY.INCONSISTENT_STATE` rather than skipping a produce that was not accepted.
An already-accepted document is a no-op for produce AND review — no duplicate LLM spend, no
duplicate acceptance events.

---

## 3. Bookmark suspend

All lifecycle suspend points build bookmark names through `LifecycleBookmarks`
(`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`), never by hand.
Every segment — the tenant included — is normalized, and the **tenant fold is the IDOR
guard**: a resume caller scoped to tenant A computes a name keyed by tenant A and can never
resolve tenant B's bookmark (a cross-tenant attempt 404s). Two sanctioned key shapes:

- `ForStageGate(tenantId, issueId, documentTypeKey, gate)` — domain-keyed, recomputable
  from durable coordinates alone. Used for stage gates.
- `ForDecisionSession(tenantId, sessionId)` — the accept-gate shape where a 128-bit session
  id carries unguessability; determinism holds because the session id is persisted in
  `LifecycleState` and on `APPROVAL.REQUESTED`.

**Serialization tolerance is not optional.** A distributed dispatcher round-trips a resume
value to a `string` or `JsonElement`; a bare `is true` pattern silently takes the wrong
branch while still returning HTTP 200. Read booleans via `ResumeInput.AsBool`, never
`is true`, and read position payloads tolerant of both `string` and `JsonElement`.

---

## 4. The declaration + structural test contract

- **Declaration:** `[ResumeBehavior(ResumeMode.X, SuspendActivities = new[] { typeof(Y) })]`
  on the workflow class. `SuspendActivities` is required non-empty for
  `BookmarkSuspend`/`Both`. It is reflection-enumerable data, not a doc comment.
- **Gate** (`ResumableStandardStructuralTests`) asserts, over every concrete
  `WorkflowBase`:
  - (a) `[ResumeBehavior]` XOR a `LegacyResumeAllowlist` entry (stale entries fail);
  - (b) `BookmarkSuspend`/`Both` ⇒ the built graph has a node whose type is in BOTH the
    declaration's `SuspendActivities` AND `LifecycleBookmarks.CanonicalSuspendActivities`
    (plus the inverse honesty check);
  - (c) `LatestStateReEntry`/`Both` ⇒ the graph has a `ComputeReEntryPositionActivity` node.

`DocumentLifecycleWorkflow` declares `Both` and is never allowlisted.

---

## 5. Allowlist burn-down

`LegacyResumeAllowlist` was seeded with every legacy workflow plus a one-line justification
and the migration story that retires it. **Ratchet discipline**: entries may only be
removed — the day a workflow starts declaring `[ResumeBehavior]`, its stale entry fails the
build. The 39-12 pilot (issue-decomposition) passed the gate with **no** allowlist entry, so
the gate bit on the very first migration; each subsequent migration (39-13…39-15) shrank the
list until it holds **only non-producer workflows**. Every document-producing workflow is now
resumable by construction.

---

## See also

- [Document Lifecycle](Document-Lifecycle) — the lifecycle these guarantees protect
- [Workflow: Document Lifecycle](Workflow-Document-Lifecycle) — the reference implementation
- [Architecture](Architecture) — the DCB event store the re-entry read reconstructs from
