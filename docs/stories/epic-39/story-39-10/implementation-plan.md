# Implementation Plan — Story 39-10: Resumable-by-Design Standard — Bookmarks + Latest-State Re-Entry + Structural Test

## Scope & Deliverable

When this story is done, "resumable" is an enforced authoring standard, not a per-workflow favor: a standard document (`docs/stories/epic-39/resumable-workflow-standard.md`) defines the two resume modes; every lifecycle workflow carries a reflectable `[ResumeBehavior]` declaration; ONE canonical tenant-folded bookmark builder (`LifecycleBookmarks`) backs every lifecycle suspend point including 39-8's accept gate; a `LifecycleReEntryService` reconstructs a fresh instance's resume position for an issue from 39-11's latest-accepted read plus DCB events (never from Elsa instance internals); idempotent guards in the generic lifecycle make re-entry unable to double-produce, double-review, or double-emit acceptance; and a build-gate structural test (`ContractBindingTests` enumerate-and-assert shape, ratchet allowlist) fails naming any workflow that does not declare-or-comply. A Testcontainers test proves crash re-entry and a resume of a bookmark created before a simulated restart.

## Pre-Reading

- `docs/stories/epic-39/story-39-10/39-10-resumable-by-design-standard-bookmarks-latest-state-re-entry.md` — the story (ACs are source of truth)
- `docs/stories/epic-39/README.md` — pillar 3, "Elsa instance state is an optimization, not the truth"
- `apps/tamma-elsa/src/Tamma.Activities/Clarify/WaitForClarifyingAnswersActivity.cs` + `apps/tamma-elsa/src/Tamma.Activities/Design/WaitForDesignApprovalActivity.cs` — the proven suspend pattern: canonical static bookmark-name builder, `CreateBookmarkArgs` + `AutoBurn`, pure `ReadAnswers`/`ReadDecision` seams
- `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/ClarifyResumeEndpoint.cs` + `DesignResumeEndpoint.cs` — resume side: `BookmarkName(request)` delegation, 404 not-found / 409 collision posture
- `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForMergeApprovalActivity.cs` — `NormalizeSegment` (the shared tenant-segment transform every builder must reuse)
- `apps/tamma-elsa/src/Tamma.Activities/ResumeInput.cs` — `ResumeInput.AsBool`, the #15/#437 coercion helper
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Clarify/ClarifyResumeReadBackTests.cs` + `Design/DesignResumeReadBackTests.cs` — THE serialization-tolerance matrix (boxed bool / `"true"`/`"True"` / `JsonElement`, truthy AND falsy rows) every new read-back inherits
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumeReadTolerantSiblingsTests.cs` — the per-site tolerance-coverage style
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` + `TaxonomyDriftBuildTests.cs` — the enumerate-and-assert build gate: assembly reflection over compiled workflows, justified allowlists, ratchet (`KnownContractViolations` — entries only removed, stale entries fail)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowTestHelper.cs` (`BuildWorkflow`) + `IssueDecompositionWorkflowStructureTests.cs` — graph-walk topology assertions
- `apps/tamma-elsa/src/Tamma.Api/Services/Engine/Replay/ReplayReconstructor.cs` (tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Engine/ReplayReconstructorTests.cs`) — the pure left-fold state-from-events style the resume calculator copies; stays the forensic fallback
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — Story 4-7 query surface (`QueryAsync(tenantId, type, issueNumber, limit)`, `QueryEventsAsync`, `ListByCorrelationIdAsync`)
- `docs/stories/epic-4/story-4-7/4-7-event-query-api-time-travel.md` + `docs/stories/epic-4/story-4-8/4-8-black-box-replay-debugging.md` — the event-query / replay contracts re-entry reads through
- `apps/tamma-elsa/src/Tamma.Activities/Core/EventPersistenceMiddleware.cs` — `context.GetService<T>()` resolution pattern for activity-side services; `TammaEventEmitter` drain
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TriageItemCycleApplyFaultExecutionTests.cs` — real-`IWorkflowRunner` execution harness with capturing event client
- `docs/stories/epic-39/story-39-6/implementation-plan.md` — `DocumentLifecycleWorkflow` graph, `DocumentLifecycleHelper.LifecycleState`, `DocumentEvents.cs`, the shared Testcontainers fixture (its step 10)
- `docs/stories/epic-39/story-39-8/implementation-plan.md` — `WaitForDocumentDecisionActivity.DecisionBookmarkName`, `DocumentDecisionResumeEndpoint`, `APPROVAL.*` events
- `docs/stories/epic-39/story-39-11/39-11-document-store-and-lineage-api.md` — `IDocumentInstanceRepository` + the latest-accepted repository read (AC4 there)
- `docs/stories/epic-39/story-39-12/39-12-pilot-migration-issuedecomposition-onto-the-lifecycle.md` — first allowlist burn-down consumer (its AC6)
- **All story-referenced paths exist.** NOT FOUND (planned by prerequisite stories, no code yet): `apps/tamma-elsa/src/Tamma.Core/Documents/` (39-2), `apps/tamma-elsa/src/Tamma.Activities/Documents/` incl. `WaitForDocumentDecisionActivity`/`DocumentEvents.cs` (39-6/39-8), `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` + `Helpers/DocumentLifecycleHelper.cs` (39-6), `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs` + `IDocumentInstanceRepository` (39-11). See Dependencies & Sequencing.

## Design Decisions

- **D1 — Re-entry splits into a pure calculator (`Tamma.Core`) + an I/O service (`Tamma.Activities`), deviating from the story's "e.g. `Tamma.Api/Services/`" placement.** The lifecycle workflow must consult re-entry at Init, in-engine — and `Tamma.ElsaServer` references only `Tamma.Activities` (→ `Tamma.Core`, `Tamma.Data`), never `Tamma.Api` (verified in the csprojs), so a Tamma.Api-resident service is unreachable from the engine. Therefore: `LifecycleResumeCalculator` (pure left-fold, `ReplayReconstructor` style, zero I/O) lives in `Tamma.Core/Documents/Resume/`; `LifecycleReEntryService` (reads `IDocumentInstanceRepository` + `IEventRepository`, maps rows, calls the calculator) lives in `Tamma.Activities/Documents/` and is DI-registered in BOTH hosts. The story's path is an "e.g."; the component name is kept. `ReplayReconstructor` is not duplicated: the hot path is the 39-11 read + a DOCUMENT./APPROVAL.-only fold (story technical note: full replay is the forensic fallback, served by the existing 4-8 surface in Tamma.Api).
- **D2 — ONE bookmark-composition core, two sanctioned key shapes (story-vs-39-8 tension, resolved).** AC4 wants `(tenantId, issueId, documentType, gate)` → same name; 39-8's accept gate is keyed `(tenantId, sessionId)` for unguessability (its D2). Resolution: `LifecycleBookmarks.Compose(gate, tenantId, segments…)` is THE single core (tenant + every segment through `WaitForMergeApprovalActivity.NormalizeSegment`); on top sit two typed wrappers — `ForStageGate(tenantId, issueId, documentTypeKey, gate)` (the AC4 domain-keyed shape, for lifecycle stage gates that must be recomputable from domain coordinates alone) and `ForDecisionSession(tenantId, sessionId)` (the 39-8 shape; byte-identical to `WaitForDocumentDecisionActivity.DecisionBookmarkName`, which is refactored to delegate). Determinism holds for the session shape too: the decision-session id is persisted in `LifecycleState` and on `APPROVAL.REQUESTED`, so a post-deploy resume recomputes the same name from durable state — "same inputs → same name" with durable inputs. The standard document records both shapes and when each applies; AC8 is satisfied because the accept gate/escalation sink flow through the same core.
- **D3 — Resume declaration = a class attribute, not a runtime descriptor.** 39-6 ships no descriptor object, so the declaration is `[ResumeBehavior(ResumeMode…)]` on the workflow class (the story's first-listed option): `ResumeMode { BookmarkSuspend, LatestStateReEntry, Both }` + `SuspendActivities` (the canonical gate `Type`s used — the "which builder" clause, since each canonical gate type owns exactly one builder). Enum + attribute live in `Tamma.Core/Documents/Resume/` (vocabulary-in-Core, 39-2 pattern; `Type[]` needs no Elsa reference). Reflection-enumerable ⇒ "data a test can enumerate".
- **D4 — Canonical-gate registry as production data.** `LifecycleBookmarks.CanonicalSuspendActivities : IReadOnlyDictionary<Type, string /*gate prefix*/>` maps each sanctioned suspend-activity type to its gate. The structural test walks built graphs for activity nodes whose type is in the registry — no string-matching of bookmark names at test time, and clause (b)'s "non-canonical bookmark name" becomes "suspend activity type not in the registry". Seeded with `WaitForDocumentDecisionActivity` (39-8); legacy waits (Clarify/Design/Merge/…) stay OUT — their workflows are allowlisted, and 39-13+ migrations retire them.
- **D5 — Allowlist scope: every concrete `WorkflowBase` in the ElsaServer assembly, ratchet discipline.** "Lifecycle workflow" is enforced as "every workflow must declare or be allowlisted": the enumerator reflects over all concrete `WorkflowBase` subclasses (the `TaxonomyDriftBuildTests` discovery anchor), so the day 39-12 migrates a workflow it must declare-or-fail. The `LegacyResumeAllowlist` is seeded with all ~30 current workflows, each with a one-line justification + the migration story that burns it down; entries may only be REMOVED; a stale entry (workflow now declares) fails the build (`KnownContractViolations` discipline). `DocumentLifecycleWorkflow` itself declares `Both` from day one and is never allowlisted.
- **D6 — Guards live ONCE in the generic lifecycle, driven by a typed position.** Per the story's guard-placement note: `DocumentLifecycleHelper` (39-6's pure core) gains `ApplyReEntry(LifecycleState, LifecycleResumePosition)` and skip predicates; `DocumentLifecycleWorkflow`'s Init gains one `ComputeReEntryPositionActivity` node (resolves `ILifecycleReEntryService` via `context.GetService<T>()` — the `EventPersistenceMiddleware` pattern) whose output routes `FlowDecision` guards: `Complete` → short-circuit to the accepted terminal (no produce, no review, NO second `DOCUMENT.ACCEPTED`; emits `DOCUMENT.REENTERED` instead); `Review` → skip produce/validate, review the existing revision (body threaded from the store via the activity's `DocumentJson` output); `Accept` → re-publish/re-suspend on the SAME recovered decision-session bookmark; `Produce` → fresh run. Migrated workflows inherit all of it; clause (c) of the structural test checks for this wiring, not hand-rolled guards.
- **D7 — Stub seam for 39-11: `NullLifecycleReEntryService`.** `ILifecycleReEntryService` is this story's interface; until 39-11's repository merges, the default registration is `NullLifecycleReEntryService` (always returns the fresh `Produce` position — today's behavior, zero risk), and unit/integration tests fake `IDocumentInstanceRepository` against 39-11's planned method shape (`GetLatestAcceptedAsync`-style read, its AC4). Re-entry goes live by swapping the DI registration when 39-11 lands — no lifecycle code change.
- **D8 — Crash simulation = never-resume + fresh dispatch against the same store.** AC7's "kills the workflow instance" is realized as: run to mid-point on Testcontainers Postgres with Elsa EF persistence, dispose the host/provider WITHOUT resuming or gracefully suspending, build a NEW provider on the same database, dispatch a FRESH lifecycle instance for the same issue. This is the honest crash shape (the old instance is dead weight; the new one must not depend on it). AC8 additionally proves the bookmark path: the pre-restart bookmark row resumes correctly under the new host via `DocumentDecisionResumeEndpoint.Resume` statics.
- **D9 — `DOCUMENT.REENTERED` joins the 39-6 event catalogue (conscious pin bump).** Re-entry is an operation; operations emit events (platform invariant). One new constant in `DocumentEvents.cs`, data `{resumeAt, skippedStages, basis, existingDocumentId, revision}`, tags `issueId`/`documentType`/`tenantId`/`correlationId`. 39-6's exact-constants pin is updated in the same commit (documented as the ratchet-style conscious edit).
- **D10 — Serialization tolerance applies to the new read-backs, reusing `ResumeInput`.** The only new cross-boundary reads are the workflow-side read of `ComputeReEntryPositionActivity`'s position payload and the re-entry route flags. Both go through pure static seams (`DocumentLifecycleHelper.ReadReEntryPosition(IDictionary<string, object>)` etc.) exercised across the boxed/string/`JsonElement` matrix; any boolean coerces via `ResumeInput.AsBool`, never `is true`. No new bookmark read-back is introduced (the accept gate's is 39-8's, already matrix-tested there).

## Implementation Steps

1. **CREATE `docs/stories/epic-39/resumable-workflow-standard.md`; MODIFY `docs/stories/epic-39/README.md`** (one link line under pillar 3). The standard defines, with file:line-level code references: the two resume modes + when each applies; the canonical builder (`LifecycleBookmarks`, both key shapes per D2, `NormalizeSegment` reuse); the serialization-tolerance requirement (cite `ClarifyResumeReadBackTests` + `ResumeInput.AsBool`, the #15/#437 matrix); the idempotent-step-guard rule; the re-entry read sequence (39-11 latest-accepted → 4-7 event query → typed position; 4-8 replay = forensic fallback); the declaration attribute + structural test contract; the allowlist burn-down rule for 39-12..39-15.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`** (AC4, D2/D4):

   ```csharp
   public static class LifecycleBookmarks
   {
       public static string Compose(string gate, string? tenantId, params string[] segments); // "{gate}-{norm(tenant)}-{norm(seg)}…"
       public static string ForStageGate(string? tenantId, string issueId, string documentTypeKey, string gate)
           => Compose(gate, tenantId, issueId, documentTypeKey);
       public static string ForDecisionSession(string? tenantId, Guid sessionId)
           => Compose("document-decision", tenantId, sessionId.ToString());
       public static IReadOnlyDictionary<Type, string> CanonicalSuspendActivities { get; } // gate registry, D4
   }
   ```

   All segments through `WaitForMergeApprovalActivity.NormalizeSegment`. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/Documents/WaitForDocumentDecisionActivity.cs`** (39-8's): `DecisionBookmarkName` body becomes `LifecycleBookmarks.ForDecisionSession(tenantId, sessionId)` (byte-identical output — pinned by test). Clarify/Design/Merge builders are left untouched (legacy, allowlisted).

3. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Resume/ResumeBehavior.cs`** (AC2, D3):

   ```csharp
   public enum ResumeMode { BookmarkSuspend, LatestStateReEntry, Both }
   [AttributeUsage(AttributeTargets.Class, Inherited = false)]
   public sealed class ResumeBehaviorAttribute(ResumeMode mode) : Attribute
   {
       public ResumeMode Mode { get; } = mode;
       public Type[] SuspendActivities { get; init; } = []; // required non-empty for BookmarkSuspend/Both
   }
   ```

4. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Resume/LifecycleResumePosition.cs` + `LifecycleResumeCalculator.cs`** (AC5 pure half, D1). New types (all wire properties `[JsonPropertyName]`d, `DocumentJson.Options`):

   ```csharp
   public enum LifecycleResumeStage { Produce, Review, Accept, Complete }
   public sealed record LifecycleResumePosition(
       string DocumentTypeKey, LifecycleResumeStage ResumeAt,
       Guid? ExistingDocumentId, int? ExistingRevision,
       Guid? PendingDecisionSessionId,      // recovered from APPROVAL.REQUESTED for Accept re-entry
       string Basis);                       // human-readable derivation ("Decomposition accepted; Plan produced-but-unreviewed")
   public sealed record ResumeEventRow(     // neutral event DTO — Core cannot see Tamma.Data
       string Type, DateTime CreatedAtUtc, Guid? DocumentId, string? DocumentTypeKey, Guid? SessionId, int? Revision);
   public static class LifecycleResumeCalculator
   {   // pure left-fold, ReplayReconstructor style; latestAccepted = 39-11 read result (null = none)
       public static LifecycleResumePosition Reconstruct(
           string documentTypeKey, AcceptedDocumentRef? latestAccepted, IReadOnlyList<ResumeEventRow> orderedEvents);
   }
   ```

   Fold rules: accepted instance exists → `Complete`; `DOCUMENT.PRODUCED.SUCCESS` + `DOCUMENT.VALIDATED.SUCCESS` with no later `DOCUMENT.REVIEWED`/`REVISION_STARTED` → `Review` at that revision; `APPROVAL.REQUESTED` with no `APPROVAL.PROVIDED` → `Accept` with the recovered session id; otherwise `Produce`. Store/stream disagreement (e.g. accepted row but no `DOCUMENT.ACCEPTED` event) → throw `TammaError DOCUMENT.REENTRY.INCONSISTENT_STATE` pointing at the 4-8 replay surface — it never guesses.

5. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/ILifecycleReEntryService.cs`, `LifecycleReEntryService.cs`, `NullLifecycleReEntryService.cs`** (AC5 I/O half, D1/D7):

   ```csharp
   public interface ILifecycleReEntryService
   {
       Task<LifecycleResumePosition> ReconstructAsync(
           Guid? tenantId, string issueId, string documentTypeKey, CancellationToken ct);
   }
   ```

   `LifecycleReEntryService` reads (a) the latest-accepted instance via 39-11's `IDocumentInstanceRepository` (in-process repository method, per 39-11 AC4 — never HTTP) and (b) the issue's `DOCUMENT.*`/`APPROVAL.*` events via `IEventRepository.QueryAsync`/`QueryEventsAsync` (4-7 surface), maps to `ResumeEventRow`, delegates to the calculator; also exposes `GetDocumentBodyAsync(documentId)` for the guard path. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` and `apps/tamma-elsa/src/Tamma.Api/Program.cs`**: register `NullLifecycleReEntryService` as default; the real service behind a config flag until 39-11 merges (D7), then flipped.

6. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/ComputeReEntryPositionActivity.cs`** (D6) — inputs `IssueId`, `DocumentType`, `TenantId`, `CorrelationId`; outputs `PositionJson`, `DocumentJson` (existing body when skipping produce); resolves `ILifecycleReEntryService` via `context.GetService<T>()`; emits `DOCUMENT.REENTERED` via `TammaEventEmitter` ONLY when `ResumeAt != Produce` (a fresh run is not a re-entry). Service missing → `TammaError DOCUMENT.REENTRY.SERVICE_UNREGISTERED` (fail-loud).

7. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocumentLifecycleHelper.cs` and `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs`** (39-6's files; AC6, D6): helper gains `ApplyReEntry(LifecycleState, LifecycleResumePosition)`, `ShouldSkipProduce`, `ShouldSkipReview`, `ShouldShortCircuitAccepted`, and the pure `ReadReEntryPosition(IDictionary<string, object>)` read-back (D10, `ResumeInput`-tolerant). Workflow Init gains the step-6 activity + guard `FlowDecision`s routing per D6; apply `[ResumeBehavior(ResumeMode.Both, SuspendActivities = [typeof(WaitForDocumentDecisionActivity)])]` to the class. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/Documents/DocumentEvents.cs`**: add `Reentered = "DOCUMENT.REENTERED"` (+ `StatusForEvent` → `"success"`); update 39-6's constants pin (D9).

8. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ResumableStandardStructuralTests.cs`** (AC3, D5) — enumerates concrete `WorkflowBase` subclasses of the ElsaServer assembly (the `TaxonomyDriftBuildTests` anchor); per workflow: (a) `[ResumeBehavior]` present XOR `LegacyResumeAllowlist` entry (fail naming the workflow otherwise; stale allowlist entries fail); (b) `BookmarkSuspend`/`Both` ⇒ `WorkflowTestHelper.BuildWorkflow` graph contains ≥1 node whose type is in BOTH the declaration's `SuspendActivities` AND `LifecycleBookmarks.CanonicalSuspendActivities`; declaration-honesty inverse: a canonical suspend node in an undeclared/`LatestStateReEntry`-only workflow fails; (c) `LatestStateReEntry`/`Both` ⇒ graph contains a `ComputeReEntryPositionActivity` node (the descriptor wiring, not per-workflow guards). Allowlist seeded with every current workflow + justification + burn-down story reference.

9. **CREATE the remaining unit tests** — `apps/tamma-elsa/tests/Tamma.Activities.Tests/Documents/LifecycleBookmarksTests.cs`, `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/LifecycleResumeCalculatorTests.cs`, `apps/tamma-elsa/tests/Tamma.Activities.Tests/Documents/LifecycleReEntryServiceTests.cs`, `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/LifecycleReEntryGuardTests.cs`, `LifecycleReEntryReadBackTests.cs` (see Test Plan).

10. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/LifecycleReEntryIntegrationTests.cs`** (AC7/AC8, D8) — extends 39-6's `DocumentLifecycleExecutionTests` Testcontainers fixture (Elsa EF persistence incl. bookmark store on Postgres, stub `llm-call`/`document-review` workflows, capturing publisher/event client, faked `IDocumentInstanceRepository` writes mirroring 39-11's planned lifecycle wiring). Scenarios in Test Plan. Finish with `dotnet ef migrations has-pending-model-changes` (must stay clean) + full `dotnet test`.

## Data & Migrations

None. Re-entry reads 39-11's `document_instances` table (its migration, not this story's) and the existing `domain_events` table; `DOCUMENT.REENTERED` rides the existing drain → `EventRepository` path. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Emits:** `DOCUMENT.REENTERED` (new constant in `Tamma.Activities/Documents/DocumentEvents.cs`; tags `issueId`, `documentType`, `correlationId`, `tenantId`; data `resumeAt`, `skippedStages`, `basis`, `existingDocumentId`, `revision`) — D9's conscious pin bump.
- **Consumes (re-entry read):** `DOCUMENT.PRODUCED.SUCCESS`, `DOCUMENT.VALIDATED.SUCCESS`/`FAILED`, `DOCUMENT.REVIEW_REQUESTED`, `DOCUMENT.REVIEWED`, `DOCUMENT.REVISION_STARTED`, `DOCUMENT.ACCEPTED`, `DOCUMENT.REJECTED`, `DOCUMENT.ESCALATED` (39-6) and `APPROVAL.REQUESTED`/`APPROVAL.PROVIDED` (39-8, for Accept-stage session recovery). No other family is read; `ESCALATION.*` stays the exception surface.

## Test Plan

All NUnit + FluentAssertions (+ Moq fakes; Testcontainers for step 10).

- **`LifecycleBookmarksTests`** (unit) — determinism (same inputs twice → byte-identical), tenant folding (tenant A ≠ tenant B names for identical remaining inputs), null tenant → `none` segment, hostile-character normalization via `NormalizeSegment`, `ForDecisionSession` byte-parity pin against `WaitForDocumentDecisionActivity.DecisionBookmarkName`, registry non-empty + every registry type is an Elsa `Activity`. **Covers AC4 (builder half), AC8 (name-parity precondition).**
- **`LifecycleResumeCalculatorTests`** (unit, pure) — the position matrix: no rows/no events → `Produce`; accepted → `Complete`; produced+validated, unreviewed → `Review` at same revision; revision-in-flight (`REVISION_STARTED` last) → `Produce`-of-revision; `APPROVAL.REQUESTED` unanswered → `Accept` with recovered session id; store/stream disagreement → `TammaError DOCUMENT.REENTRY.INCONSISTENT_STATE`; determinism (same slice twice → equal result). **Covers AC5.**
- **`LifecycleReEntryServiceTests`** (unit, Moq'd `IDocumentInstanceRepository` + `IEventRepository`) — reads latest-accepted via the repository method (never HTTP), passes ordered event rows, `NullLifecycleReEntryService` always yields `Produce`. **Covers AC5 (I/O half), D7.**
- **`LifecycleReEntryGuardTests`** (unit, Elsa-free) — drives `DocumentLifecycleHelper` twice over the same accepted lineage: second pass yields zero produce dispatch decisions, zero review dispatch decisions, and an event plan containing NO second `DOCUMENT.ACCEPTED` (exactly `DOCUMENT.REENTERED`). **Covers AC6.**
- **`LifecycleReEntryReadBackTests`** (unit, `ClarifyResumeReadBackTests` shape) — `ReadReEntryPosition` across the matrix: position payload as string AND `JsonElement`; every boolean flag as boxed bool / `"true"`/`"True"` / `JsonElement`, truthy AND falsy rows; missing key fail-closed to fresh `Produce`. **Covers AC4 (matrix clause).**
- **`ResumableStandardStructuralTests`** (build gate) — clauses (a)/(b)/(c) + declaration honesty + allowlist ratchet (stale entry fails; adding is a reviewed diff on a justified list); `DocumentLifecycleWorkflow` asserted present and declaring `Both` (so AC2 is proven on a real workflow, not just enforceable). **Covers AC2, AC3.**
- **`LifecycleReEntryIntegrationTests`** (Testcontainers Postgres) — (i) AC7: run lifecycle to produced+validated (review pending), kill per D8, fresh dispatch for the same issue → asserts re-enter at `Review` of the same revision, zero new `DOCUMENT.PRODUCED.*`, run to completion, final stream contains EXACTLY ONE `DOCUMENT.ACCEPTED` for the document; variant: kill after acceptance → fresh dispatch short-circuits to `Complete` with `DOCUMENT.REENTERED` and no duplicate acceptance. (ii) AC8: suspend on the accept gate, dispose the host, new provider on the same store, resume via `DocumentDecisionResumeEndpoint.Resume` statics with `Accept` → workflow continues on the Accept branch (and a control resume with `Escalate` takes the escalate branch). **Covers AC7, AC8, AC6 (event-stream half).**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — authoring standard doc, linked, code-referenced | 1 | Reviewer check: doc exists, README links it, every cited path/type resolves (spot-check against this plan's Pre-Reading) |
| 2 — static, enumerable resume declaration | 3, 7 | `ResumableStandardStructuralTests` (attribute enumerated + `DocumentLifecycleWorkflow` declares `Both`) |
| 3 — structural build gate with ratchet allowlist | 8 | `ResumableStandardStructuralTests` clauses (a)/(b)/(c), honesty check, stale-entry failure |
| 4 — one canonical tenant-folded builder + tolerance matrix on new read-backs | 2, 7 (D2/D10) | `LifecycleBookmarksTests` (parity, folding), `LifecycleReEntryReadBackTests` (matrix) |
| 5 — typed re-entry reconstruction, never from Elsa internals | 4, 5 (D1) | `LifecycleResumeCalculatorTests` (matrix + inconsistency fail-loud), `LifecycleReEntryServiceTests` |
| 6 — idempotent guards, no double produce/review/acceptance | 6, 7 (D6) | `LifecycleReEntryGuardTests` (twice-over-same-lineage), integration (i) exactly-one-acceptance assert |
| 7 — crash → fresh instance re-enters correctly (integration) | 10 (D8) | `LifecycleReEntryIntegrationTests` scenario (i) both variants |
| 8 — 39-8 suspends resumable across restart via the same mechanism | 2 (delegation), 10 | `LifecycleBookmarksTests` byte-parity pin; `LifecycleReEntryIntegrationTests` scenario (ii) |

## Dependencies & Sequencing

- **Hard prerequisites:** 39-6 (`DocumentLifecycleWorkflow` + `DocumentLifecycleHelper` + `DocumentEvents` — steps 6–7 modify them; blocking) and 39-8 (`WaitForDocumentDecisionActivity` + `DocumentDecisionResumeEndpoint` — step 2 refactors the builder, AC8 resumes through it; blocking). Neither is implemented yet — do not start steps 2/6/7 before they compile. 39-2 underpins both (`Tamma.Core/Documents` namespace).
- **Parallel-with-contract:** 39-11 — blocking only for the LIVE re-entry read; developed against its planned `IDocumentInstanceRepository` shape with fakes, `NullLifecycleReEntryService` as the shipping default until it merges (D7). Coordinate the latest-accepted method name in lockstep (one signature, agreed in both plans).
- **Stubbed, not pulled in:** 39-17/39-18 (the "orchestrator" in tests is a resume caller, exactly as in 39-8's plan); 39-12 (first burn-down consumer — this story ships the gate it must pass).
- **In place, verified:** Elsa 3 bookmarks + EF persistence, `NormalizeSegment`, `ResumeInput`, 4-7 `IEventRepository` query surface, 4-8 `ReplayReconstructor` (forensic fallback only), `WorkflowTestHelper`, the drift-test enumeration precedents.
- **Feeds:** 39-12..39-15 (declare-or-fail + allowlist burn-down), 39-9 (repair ring runs inside the same guarded lifecycle).
- **Sequencing within the story:** 1 → 2/3/4 (parallel) → 5/6 → 7 → 8/9 → 10.

## Risks & Mitigations

- **Prerequisite stack (39-6/39-8) is plan-only.** Largest schedule risk; steps 6–7 edit files that don't exist yet. Mitigation: steps 1–5 + 8–9 depend only on 39-2's namespace and this story's own types; every consumed 39-6/39-8 name is pinned in their plans (drift = mechanical rename); the structural test can land dark (allowlist covers everything) even before the lifecycle merges.
- **Two bookmark key shapes read as two standards (D2).** Mitigation: ONE `Compose` core, both wrappers tested through it, and the standard document states the selection rule (domain-keyed for stage gates, session-keyed where unguessability is load-bearing); the byte-parity pin stops silent divergence from 39-8.
- **Structural test false confidence via the allowlist.** A seeded-with-everything allowlist enforces nothing until burn-down starts. Mitigation: ratchet semantics (add = reviewed justified diff, stale = failure) + 39-12 AC6 explicitly requires passing WITHOUT an entry — the gate bites on the very first migration.
- **Re-entry mis-reconstruction is worse than no re-entry** (skipping a produce that was NOT accepted). Mitigation: the calculator is pure with an exhaustive position matrix; disagreement fails loud (`INCONSISTENT_STATE`), never guesses; `NullLifecycleReEntryService` is the safe default so a bad read can be disabled by DI swap without touching the lifecycle.
- **Integration flakiness (host dispose/rebuild on shared Postgres).** Mitigation: the 39-6 fixture is already container-per-fixture; scenario (ii) reuses 39-8's proven resume statics; keep scenarios to the AC list only.
- **Story-vs-canon tension:** none beyond D2 (AC4's builder tuple vs 39-8's session-keyed gate — resolved with the story's determinism intent preserved; recorded in the standard doc). D1 deviates from a story "e.g." placement for project-graph reasons, not from a requirement.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | Standard document + README link | 0.75 |
| 2 | `LifecycleBookmarks` + 39-8 builder delegation + registry | 0.5 |
| 3 | `ResumeBehaviorAttribute` + `ResumeMode` | 0.25 |
| 4 | `LifecycleResumePosition` + pure calculator | 1.0 |
| 5 | Re-entry service + null seam + DI registration (both hosts) | 0.5 |
| 6–7 | `ComputeReEntryPositionActivity` + lifecycle guard wiring + `DOCUMENT.REENTERED` | 1.0 |
| 8 | Structural test + seeded allowlist | 0.75 |
| 9 | Unit test suites (bookmarks, calculator, service, guards, read-back) | 1.0 |
| 10 | Testcontainers crash/restart scenarios | 1.0 |
| — | 39-8/39-11 lockstep coordination, review polish | 0.25 |
| **Total** | | **7.0** (story estimate: 5–7 days) |
