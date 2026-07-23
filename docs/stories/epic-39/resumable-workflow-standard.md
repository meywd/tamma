# Resumable-Workflow Authoring Standard (Story 39-10)

**Status:** enforced by a build gate (`ResumableStandardStructuralTests`).
Pillar 3 of Epic 39 ("Resumable by design"). This document is the source of the
_rules_; the structural test is the source of the _enforcement_.

> **"Is this workflow resumable?" is answered by a build gate, not by reading its
> source.** Every concrete `WorkflowBase` in `Tamma.ElsaServer` must either declare
> `[ResumeBehavior(...)]` or sit on a justified, ratchet-style allowlist that
> 39-12..39-15 burn down.

---

## 1. The two resume modes

A lifecycle workflow is resumable in one (or both) of two ways, declared statically via
`[ResumeBehavior(ResumeMode, SuspendActivities = …)]`
(`apps/tamma-elsa/src/Tamma.Core/Documents/Resume/ResumeBehavior.cs`):

| Mode | When it applies | Mechanism |
|---|---|---|
| `BookmarkSuspend` | The workflow must WAIT for external input (a human/orchestrator decision, an answer, an approval). | Suspends on a deterministic, tenant-folded bookmark; a later resume recomputes the same name and continues. |
| `LatestStateReEntry` | The workflow can lose its Elsa instance (crash, pod eviction, deploy, definition-version bump) and must resume from durable truth. | A fresh instance for the same issue reconstructs its position from the document store + DCB events and skips work already accepted. |
| `Both` | Both of the above (the `DocumentLifecycleWorkflow` posture). | The accept gate suspends on a bookmark AND Init consults the re-entry reconstructor. |

**Elsa instance state is an optimization, not the truth.** If Elsa's persisted instance
resumes cleanly, good — but correctness must hold even when the instance is gone.
Document lineage + events are the durable truth (the same DCB principle as Story 37-1's
"projection is rebuildable").

---

## 2. The ONE canonical bookmark builder

All lifecycle suspend points build names through `LifecycleBookmarks`
(`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`), never by hand.

- **Core:** `Compose(gate, tenantId?, params segments)` — `{gate}-{norm(tenant)}-{norm(seg)}…`,
  every segment (the tenant included) run through
  `WaitForMergeApprovalActivity.NormalizeSegment`
  (`apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForMergeApprovalActivity.cs:136`). The
  tenant fold is the IDOR guard: a resume caller scoped to tenant A computes a name keyed
  by tenant A and can never resolve tenant B's bookmark (a cross-tenant attempt 404s).
- **Two sanctioned key shapes:**
  - `ForStageGate(tenantId, issueId, documentTypeKey, gate)` — the domain-keyed shape
    (AC4), recomputable from durable domain coordinates alone. Use for stage gates.
  - `ForDecisionSession(tenantId, sessionId)` — the 39-8 accept-gate shape where the
    128-bit session id carries unguessability. Byte-identical to
    `WaitForDocumentDecisionActivity.DecisionBookmarkName`
    (`…/Documents/WaitForDocumentDecisionActivity.cs:127`), which now delegates here (pinned
    by `LifecycleBookmarksTests.ForDecisionSession_IsByteIdenticalTo_DecisionBookmarkName`).
    Determinism still holds: the session id is persisted in `LifecycleState` and on
    `APPROVAL.REQUESTED`, so a post-deploy resume recomputes the same name.
- **Canonical-gate registry:** `LifecycleBookmarks.CanonicalSuspendActivities`
  (`Type → gate prefix`) is production data the structural test walks. A canonical suspend
  node in an undeclared workflow fails the build (declaration honesty). Legacy
  Clarify/Design/Merge waits are deliberately OUT — their workflows are allowlisted and
  39-13+ migrations retire them.

**Selection rule:** domain-keyed (`ForStageGate`) for a stage gate that must be
recomputable from `(tenantId, issueId, documentType, gate)`; session-keyed
(`ForDecisionSession`) where unguessability is load-bearing (the accept gate / escalation
sink).

---

## 3. Serialization tolerance is not optional (the #15/#437 lesson)

Every resume/re-entry read-back MUST be tolerant of a serializing runtime. The in-process
runtime hands a value back as a boxed CLR type; a distributed dispatcher round-trips it to
a `string` or `JsonElement`. A bare `is true` pattern silently takes the WRONG branch under
serialization while still returning HTTP 200 — a silent mis-branch.

- Coerce booleans via `ResumeInput.AsBool`
  (`apps/tamma-elsa/src/Tamma.Activities/ResumeInput.cs`), NEVER `is true`.
- Read the position payload tolerant of `string` AND `JsonElement`.
- Every new read-back inherits the matrix from
  `apps/tamma-elsa/tests/Tamma.Activities.Tests/Clarify/ClarifyResumeReadBackTests.cs`
  (boxed bool / `"true"`/`"True"` / `JsonElement`, truthy AND falsy rows, missing key
  fail-closed). 39-10's is
  `…/Workflows/LifecycleReEntryReadBackTests.cs` over
  `DocumentLifecycleHelper.ReadReEntryPosition`.

---

## 4. Idempotent step guards (no double-produce / double-review / double-accept)

Guards live ONCE in the generic lifecycle (39-6), not per migrated workflow — migrations
inherit them. `DocumentLifecycleHelper`
(`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocumentLifecycleHelper.cs`) carries
`ShouldSkipProduce` / `ShouldSkipReview` / `ShouldShortCircuitAccepted` / `ApplyReEntry`;
`DocumentLifecycleWorkflow`'s Init wires ONE `ComputeReEntryPositionActivity` node whose
typed position routes `FlowDecision` guards:

- `Complete` → short-circuit to the accepted terminal — no produce, no review, and NO second
  `DOCUMENT.ACCEPTED`; it emits `DOCUMENT.REENTERED` instead.
- `Review` → skip produce/validate, review the existing revision (body threaded from the
  store).
- `Accept` → re-suspend on the SAME recovered decision-session bookmark.
- `Produce` → run fresh.

An already-accepted document is a no-op for produce AND review (no duplicate LLM spend, no
duplicate acceptance events). Proven by `LifecycleReEntryGuardTests` (twice over the same
accepted lineage) and the integration suite's exactly-one-acceptance assertion.

---

## 5. The re-entry read sequence

Re-entry is a **read model, not a replay-everything**. `LifecycleReEntryService`
(`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleReEntryService.cs`) reads, in order:

1. **39-11 latest-accepted** — `IDocumentInstanceRepository.GetLatestAcceptedAsync(tenantId,
   issueId, ct)` (in-process, never HTTP): the single indexed read that answers most of the
   position question ("what is already accepted?").
2. **4-7 event query** — `IEventRepository.QueryEventsAsync` over the issue's `DOCUMENT.*` /
   `APPROVAL.*` slice, mapped to the neutral `ResumeEventRow` DTO, to fill in sub-stages
   (produced-but-unreviewed, unanswered approval + recovered session id).
3. **Typed position** — the pure `LifecycleResumeCalculator.Reconstruct(...)`
   (`apps/tamma-elsa/src/Tamma.Core/Documents/Resume/LifecycleResumeCalculator.cs`) folds
   both into a `LifecycleResumePosition` in the `ReplayReconstructor` left-fold style.

The full `ReplayReconstructor`
(`apps/tamma-elsa/src/Tamma.Api/Services/Engine/Replay/ReplayReconstructor.cs`) stays the
**forensic fallback** for edge/audit cases — NOT the hot path.

**It never guesses.** Store/stream disagreement (an accepted row with no `DOCUMENT.ACCEPTED`
event, or the converse) throws `DOCUMENT.REENTRY.INCONSISTENT_STATE` and points at the 4-8
replay surface. Skipping a produce that was not accepted is worse than not skipping.

---

## 6. The declaration attribute + structural test contract

- **Declaration:** `[ResumeBehavior(ResumeMode.X, SuspendActivities = new[] { typeof(Y) })]`
  on the workflow class. `SuspendActivities` is REQUIRED non-empty for
  `BookmarkSuspend`/`Both` (the "which builder" clause — each canonical gate type owns one
  bookmark builder). It is reflection-enumerable data, not a doc comment.
- **Gate** (`…/Workflows/ResumableStandardStructuralTests.cs`) — enumerates every concrete
  `WorkflowBase` and asserts:
  - (a) `[ResumeBehavior]` XOR a `LegacyResumeAllowlist` entry (stale entries fail);
  - (b) `BookmarkSuspend`/`Both` ⇒ the built graph has a node whose type is in BOTH the
    declaration's `SuspendActivities` AND `LifecycleBookmarks.CanonicalSuspendActivities`
    (and the inverse honesty check);
  - (c) `LatestStateReEntry`/`Both` ⇒ the graph has a `ComputeReEntryPositionActivity` node.
  `DocumentLifecycleWorkflow` declares `Both` and is NEVER allowlisted.

---

## 7. Allowlist burn-down (39-12..39-15)

`LegacyResumeAllowlist` is seeded with every current legacy workflow + a one-line
justification + the migration story that retires it. **Ratchet discipline** (the
`KnownContractViolations` pattern): entries may only be REMOVED; the day a workflow starts
declaring `[ResumeBehavior]`, its stale entry fails the build. 39-12's pilot migration
(IssueDecomposition) must pass the gate WITHOUT an allowlist entry — so the gate bites on the
very first migration. Each subsequent migration (39-13..39-15) shrinks the list until it is
empty and every workflow is resumable by construction.
