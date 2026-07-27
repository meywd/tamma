# Implementation Plan — Story 41-17: Standalone Code Review & PR Triage

> **This story is two independently-shippable halves and this plan is written as two.**
> **Half A (`diff-review`)** needs no new taxonomy cell and no new enabler — it is startable
> today, in parallel with Wave 0. **Half B (`pr-triage-sweep`)** is blocked on two things that do
> not exist: 41-1a's `(senior_developer, triage-pr)` cell **and** the tenant-aware
> scheduled-trigger seam, which **no story in Epic 41 owns**. Do not schedule them as one unit;
> do not start Half B expecting to finish it.

## Scope & Deliverable

**Half A — `diff-review`.** A new thin binding over `document-lifecycle` with
`DefinitionId = "diff-review"` that produces a validated `Review` whose `subject.kind = "diff"`
(repository + `prNumber`|`commitSha`), on the produce cell `(senior_developer, code-review)`.
`(senior_developer, code-review)` moves out of `ContractBindingTests.IntentionallyUnbound` into
`Bindings` with authority `ReviewDocumentType.Validate`; `Prompts/senior_developer/code-review.md`
is rewritten to instruct the canonical `Review` wire; `CodeReviewWorkflow`'s mentor-feedback input
becomes the *rendered* validated `Review` instead of the raw model text. The provisional
`("code-review" → review)` registry row is reconciled to a non-provisional `("diff-review" → review)`
row and the declared-edge pin is bumped. `CodeReviewWorkflow` itself — its `DefinitionId`, its two
dispatch sites, its resume allowlist entry — is **not** touched.

**Half B — `pr-triage-sweep`.** A second thin binding, `DefinitionId = "pr-triage-sweep"`,
producing one `TriageDecision` per open PR from the `(senior_developer, triage-pr)` cell, driven by
a tenant-scoped, per-window, durably-idempotent scheduled trigger. Half B ships **only** when 41-1a
has minted the cell and someone has built the scheduler seam.

When both halves are done: code review of an arbitrary diff is a first-class typed document
independent of the mentorship engine, and the open-PR queue is a routed, audited, per-tenant sweep.

## Pre-Reading

- `docs/stories/epic-41/story-41-17/41-17-standalone-code-review-and-pr-triage.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/README.md` — rule 1's six thinness clauses (a)–(f); the Wave-0 table row for the scheduler seam (now story 41-30); the Dependencies bullet "Scheduled workflows have no reusable pattern"
- `docs/stories/epic-39/story-39-12/implementation-plan.md` and `docs/stories/epic-39/story-39-15` — the binding recipe (D1 byte-stable surface, D2 typed-routing-only, D3 legacy event mirroring, D5 drift-guard migration, D7 resume declaration)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` — **the reference shape**; copy its skeleton verbatim (ReadInputs → ComputeReEntryPosition → ReadPositionStage → FreshRun → Fetch* → DispatchLifecycle → ReadLifecycleExit → ExposeOutput; zero `Finish`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — **the reference structure-test set**; the six clauses in executable form
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Review.cs` (`ReviewSubject` at `:153-161`, `ReviewIssue`, `ReviewDecision`) + `ReviewDocumentType.cs` (violation constants at `:16-38`; the `APPROVE_WITH_BLOCKING_ISSUES` rule at `:35`/`:94`; the fixture pair at `:185` / `:211`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs` (`OUT_OF_VOCABULARY` `:146`, `REASONING_REQUIRED` `:149`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs` — **read this before writing a line of Half A**: `DiffSubjectKind` (`:33`), the 5-role diff map (`DiffReviewAction`, `s_diffRoster` `:73`), `Resolve(role, override, subjectKind, docTypeKey)` (`:108`), `AllDispatchablePairs` (`:178`)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentReviewWorkflow.cs:236` — where the review-stage subject kind is **hardcoded** to `document`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs` — the incumbent: `DefinitionId` `:56`, `BindInputs` `:108-131`, `AnalyzeChanges` `:274-301`, `StoreAnalysis` `:303-308`, the mentor-feedback `analysis` variable `:327`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:596-611` and `MentorshipWorkflow.cs:402` — the two live `code-review` dispatch sites
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — `Bindings` `:82-250`, `IntentionallyUnbound` `:286-354` (the `(senior_developer, code-review)` entry at `:293-295`), `ReviewProducerDispatchablePairs` `:505-544`, the 16-pair pin `:592-601`, the universal authority pin `:626-652`, the both-classified guard `:713-720`, the staleness guard `:724-737`
- `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174` (`BuildSeed`, the provisional `code-review` row at `:158`) + `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` (`HaveCount(16)`)
- `apps/tamma-elsa/src/Tamma.Activities/Review/CodeReviewEvents.cs` — the **existing** `CODE_REVIEW.*` family (read D6 before naming an event)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` (`FireAtMinute` `:34`, `_lastFired` `:83`, `ComputeAdvisoryLockKey(year, dayOfYear, hour)` `:241`, hardcoded target `:198-200`) and `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Rotation/SecretAutoRotationScheduler.cs` — the two existing schedulers, **neither reusable**; see D8
- **NOT FOUND (must be built elsewhere before Half B compiles):** the `(senior_developer, triage-pr)` cell (41-1a) and any tenant-aware scheduled-trigger seam (story 41-30, not yet built)

## Corrections to the story

The story was drafted against a snapshot. Verified against the tree today:

1. **CONFIRMED, all of it.** Every file:line the story cites resolves: `CodeReviewWorkflow.cs:56`
   (`DefinitionId = "code-review"`), the two dispatch sites (`SingleIssueCycleWorkflow.cs:601`,
   `MentorshipWorkflow.cs:402`), the input-shape mismatch (`BindInputs` at `:114-128` reads
   `RepositoryUrl`/`repositoryUrl` and never `repository`/`prNumber`/`conventions`, so the
   SingleIssueCycle site really does run with empty `StoryId`/`JuniorId`/`RepositoryUrl` — the
   pre-existing defect is real), `DocumentTypeRegistry.cs:158` (the provisional `code-review` row),
   `ContractBindingTests.cs:293-295` and `:713-720`, `ReviewDocumentType.cs:35` +
   `:185`/`:211`, `TriageDecision.cs:146-149`, `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)`,
   the whole scheduler indictment (`:34`, `:83`, `:241`, hardcoded target). The `triage-pr` wire
   really is absent from `AgentAction.cs` and from `SeniorDeveloper`'s eligible set
   (`RolePhaseMap.cs:80-92`). Nothing in the Dependencies section needs softening.
   Only `ReviewDocumentType.cs:17-32` has drifted — the constants now span `:16-38`.

2. **NEW — half of Half A already exists, and the story does not know it.** Story 39-7 (D3/D9)
   already shipped the **diff review subject kind and the 5-role diff-review action map**:
   `ReviewerSelectionHelper.DiffSubjectKind = "diff"` (`:33`), `s_diffRoster` (`:73` —
   senior_developer, developer, architect, security, tester), `DiffReviewAction` (senior_developer +
   developer → `code-review`, architect → `code-review-architecture`, security →
   `code-review-security`, tester → `code-review-coverage`), and `Resolve(..., subjectKind, ...)`
   dispatching on it (`:134-140`). Those four specialisation pairs are already classified in
   `ContractBindingTests.ReviewProducerDispatchablePairs` (`:519-530`) as *"diff-review producer
   pair (D3 diff map)"*, and `ReviewerSelectionHelper_AllDispatchablePairs_HasSixteenEligiblePairs`
   (`:592`) pins them. **Consequence:** the story's Scope line "developer/security/tester lenses
   available via panel policy" is already true at the helper level and needs no new code. What
   41-17 adds is the *producing workflow*, not the lens map.

3. **NEW — but the diff lens is currently DEAD CODE, and this story must not pretend otherwise.**
   `DocumentReviewWorkflow.BuildSubject` (`:234-240`) **hardcodes**
   `Kind = ReviewerSelectionHelper.DocumentSubjectKind`. Since `document-lifecycle` reaches the
   reviewer only through `document-review`, no lifecycle-driven review can ever carry a `diff`
   subject, so `DiffReviewAction` is unreachable from the lifecycle today. This is not a problem for
   41-17 as designed — see **D2**: the diff-ness of `diff-review` lives in the **produced document's
   payload** (`Review.subject.kind = "diff"`), which the *produce* step emits and
   `ReviewDocumentType.Validate` checks. The lifecycle's own REVIEW stage reviews that `Review`
   document as a `document` subject through the plan-review lens. The story's AC5 ("changing the
   configured reviewer role changes the dispatched pair") is satisfied by the existing
   document-review roster mechanism, **not** by the diff map. Record the dead-lens fact; do not fix
   it here (fixing it means threading a subject kind through `DocumentLifecycleWorkflow`, which is a
   generic-layer change with no consumer).

4. **NEW — the `CODE_REVIEW.*` event family is already taken.** The story proposes
   `CODE_REVIEW.STARTED` / `CODE_REVIEW.VERDICT`. `apps/tamma-elsa/src/Tamma.Activities/Review/CodeReviewEvents.cs`
   already owns `CODE_REVIEW.*` for the incumbent PR-lifecycle workflow (`PR_CREATED.SUCCESS`,
   `GUIDANCE_DELIVERED.*`, `ITERATION.STARTED`, `MERGED.*`, `ESCALATED`, `ESCALATION_RESOLVED`,
   `FAILED`) with a pinned `StatusForEvent` switch. Adding two members to that family would make
   `CODE_REVIEW.*` mean two different aggregates on one stream and would break the family's
   dashboard semantics. **Correction: mint `DIFF_REVIEW.*`** (`DIFF_REVIEW.STARTED`,
   `DIFF_REVIEW.VERDICT`, `DIFF_REVIEW.FAILED`) in a new `Tamma.Activities/Review/DiffReviewEvents.cs`.
   The story's "Events" section is amended accordingly.

5. **NEW — AC1's `SUBJECT_INCOMPLETE`/`SUBJECT_UNKNOWN_KIND`/`ISSUE_MISSING_FIX` and AC2's
   `APPROVE_WITH_BLOCKING_ISSUES` are ALREADY IMPLEMENTED AND ALREADY FIXTURED.**
   `ReviewDocumentType` ships all four constants and `Examples` already contains the exact fixture
   pair the story names (`valid-request-changes-with-blocking-issue` `:185`,
   `invalid-approve-with-blocking-issue` `:211`, asserting `ApproveWithBlockingIssues` at `:223`).
   AC1/AC2 therefore require **no new validator code** — they are satisfied by *binding* the cell to
   `ReviewDocumentType.Validate` and adding the missing negative fixtures for the two subject codes
   if absent. Budget accordingly: this is the cheapest part of the story, not the expensive part.

6. **NEW — AC6's "moves from `IntentionallyUnbound` into `Bindings`" has a second, unstated
   consequence.** `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` (`:655`) and
   `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` (`:626`) mean the
   move is not optional bookkeeping: once `(senior_developer, code-review)` is a document producer it
   **must** be in `Bindings` with a `*DocumentType.Validate` authority, and it must **not** remain
   allowlisted. Additionally `ReviewProducerDispatchablePairs_HasNoStaleEntries` (`:567`) forbids
   overlap between that table and `Bindings` — `(senior_developer, code-review)` is currently in
   `IntentionallyUnbound` (not in `ReviewProducerDispatchablePairs`), so the move is a clean
   one-table hop, but verify the invariant after the edit.

7. **NEW — the effort split in the story understates Half A.** The story says "≈3 days for the
   code-review half". Rewriting `Prompts/senior_developer/code-review.md` to the canonical `Review`
   wire is a **breaking prompt change for the incumbent `CodeReviewWorkflow`**, which today posts
   that model output as prose to a junior developer. Half A therefore also owns a render step and a
   regression test in a workflow it is otherwise forbidden from touching. See D5. Revised: 3.5–4 days.

## Design Decisions

- **D1 — Two new DefinitionIds, zero rewiring, and the incumbent is left strictly alone.** The story's
  decision is upheld and the reason verified: `code-review`'s two dispatch sites pass mutually
  incompatible input shapes (`SingleIssueCycleWorkflow.cs:596-611` sends
  `repository`/`prNumber`/`branchName`/`conventions`/`tenantId` fire-and-forget; `MentorshipWorkflow.cs:402`
  sends `SessionId`/`StoryId`/`JuniorId` and waits), so rebinding the id in place would silently
  rewire both. `diff-review` and `pr-triage-sweep` are new ids with no incumbent callers.
  `CodeReviewWorkflow` keeps its `LegacyResumeAllowlist` entry
  (`ResumableStandardStructuralTests.cs`, `"CodeReviewWorkflow"` = "code-review leaf, runs to
  completion (burn-down: 39-14+)") — this story does not burn it down. The pre-existing empty-input
  defect at the SingleIssueCycle site is **recorded in `.dev/bugs/` and not fixed here**.

- **D2 — "Diff" lives in the produced payload, not in the lifecycle's review subject.** Per
  Correction 3: the binding hands the produce cell a `producerVariablesJson` carrying
  `repository` + `prNumber`|`commitSha` + the diff text, and the cell emits a `Review` whose
  `subject` is `{ kind:"diff", repository, prNumber|commitSha }`. `ReviewDocumentType.Validate`
  enforces `SUBJECT_UNKNOWN_KIND` / `SUBJECT_INCOMPLETE` on it. The lifecycle's REVIEW stage then
  critiques that `Review` **document** through `ReviewerSelectionHelper.Resolve(role, null,
  "document", "review")` → `RolePhaseMap.GetPanelActionForRole(role, "review")` →
  `GetReviewActionForRole` (`RolePhaseMap.cs:376-387`) — the plan-review lens, which covers all 7
  non-`tech_writer` roles and needs **no new selector arm**. Consequence: 41-17 does **not** need
  41-1a's review-selector work, and does not touch `DocumentReviewWorkflow`.

- **D3 — Reviewer-role selection is acceptance-rules policy, verbatim (AC5).** The binding names no
  reviewer cell and contains no role literal in its graph; it forwards an optional
  `acceptanceRulesJson` input to the lifecycle, exactly as `TaskCreationWorkflow` does
  (`:69`, `:195`). `AcceptanceDefaults.For(DocumentTypeKey.Review)` supplies the default roster.
  AC5's integration test flips the configured reviewer role in the rules JSON and asserts the
  dispatched `(role, action)` changes — the mechanism is 39-5/39-7's, untouched.

- **D4 — Bind the existing cell; mint no second one.** `(senior_developer, code-review)` is the
  correct produce cell and it already exists in `AgentAction.cs:53` and in `SeniorDeveloper`'s
  eligible set (`RolePhaseMap.cs:85`), with a shipped prompt file at
  `src/Tamma.Api/Prompts/senior_developer/code-review.md`. **Half A therefore adds ZERO taxonomy
  cells** — no `AgentAction` member, no `RolePhaseMap` edit, no new prompt file, and no bump of
  `AgentActionTests.cs:38` `Be(80)` / `RolePhaseMapTests.cs:64` `HaveCount(80)`. This is precisely
  why Half A is Wave-0-independent.

- **D5 — The prompt rewrite is a breaking change to a workflow this story may not restructure, so it
  ships with a render seam.** `Prompts/senior_developer/code-review.md` is rewritten to instruct the
  canonical `Review` wire (`"subject"`, `"decision"`, `"summary"`, `"issues"` with
  `"severity"`/`"category"`/`"description"`/`"suggestedFix"`). `CodeReviewWorkflow.StoreAnalysis`
  (`:303-308`) currently stores that reply verbatim into `analysisText`, which feeds the
  `mentor-feedback` call's `analysis` variable (`:327`). Half A adds a **pure static renderer**
  `ReviewProse.Render(string reviewJson) → string` in
  `Tamma.ElsaServer/Workflows/Helpers/` (deterministic markdown: decision line, summary, then one
  bullet per issue with severity/category/fix; unparseable JSON → the raw text unchanged, so the
  mentorship path can never go blank) and `StoreAnalysis` calls it. This is a **three-line change
  inside a 824-line workflow** — a value transform on one variable, not a restructuring; D1's
  "leave the incumbent alone" is about its graph, its id and its callers, all of which are
  untouched. AC6's "a test asserts the mentorship path never receives raw JSON" pins it.

- **D6 — New event family `DIFF_REVIEW.*`, not `CODE_REVIEW.*` (Correction 4).** New file
  `apps/tamma-elsa/src/Tamma.Activities/Review/DiffReviewEvents.cs` in the `ResearchEvents` /
  `DecompositionEvents` shape: `Started` = `DIFF_REVIEW.STARTED`, `Verdict` = `DIFF_REVIEW.VERDICT`
  (data `decision`, `issueCount`, `blockingCount`, `documentId`), `Failed` = `DIFF_REVIEW.FAILED`
  (LOUD, on lifecycle `rejected`/`escalated`, detail naming the typed outcome), plus the house
  `ParseTenantId` + `StatusForEvent` statics. Half B mints `PrTriageEvents` = `PR_TRIAGE.SWEEP.STARTED`
  / `.ITEM` / `.COMPLETED` in the same shape. `CodeReviewEvents.cs` is **diff-empty**. The generic
  `DOCUMENT.*`/`APPROVAL.*`/`ESCALATION.*` families are emitted by the lifecycle machinery, not by
  this binding.

- **D7 — Resume declarations: `diff-review` = `LatestStateReEntry`, `pr-triage-sweep` =
  `LatestStateReEntry`; neither is `Both`.** The story's AC7 says the review half declares `Both`.
  **Deviate, with reason:** `Both` requires the workflow's *own* graph to contain a canonical
  bookmark-suspend node (`ResumableStandardStructuralTests` clause (b) — `SuspendActivities` must be
  non-empty and intersect `LifecycleBookmarks.CanonicalSuspendActivities`). A thin binding never
  suspends: the accept-gate suspend happens inside the dispatched `document-lifecycle` child, which
  the parent waits on with `WaitForCompletion=true`. Declaring `Both` would be dishonest and would
  fail the gate. Every landed thin binding declares `LatestStateReEntry`
  (`TaskCreationWorkflow.cs:47`, `ResearchWorkflow.cs:35`, `IssueDecompositionWorkflow`) — 39-12 D7
  states this explicitly. Both bindings carry a `ComputeReEntryPositionActivity` node (clause (c))
  and neither takes a `LegacyResumeAllowlist` entry.

- **D8 — Half B does not build the scheduler seam; it consumes one and stays dark until it exists.**
  Verified: there are **two** schedulers in the tree and neither is reusable.
  `HourlyAnalyticsRollupScheduler` hardcodes its target (`:198-200`), exposes a single
  `FireAtMinute` int (`:34`), keeps last-fired in a process field (`_lastFired`, `:83`), and locks on
  `(year, dayOfYear, hour)` with **no tenant component** (`:241`) — one tenant's leader suppresses
  every other tenant's fire. `SecretAutoRotationScheduler` (Story 29-6) is *closer* — its idempotency
  is a **durable per-row `NextRotationDueAt`** plus a per-secret concurrency guard via
  `IRotationTriggerService`, which is exactly the right shape — but it too hardcodes one target
  workflow (`rotate-secret`), is opt-in-disabled by default, and is not tenant-partitioned. Half B's
  plan is therefore: **define the consumed interface, implement against it, and ship the binding
  without a trigger.** `IScheduledSweepTrigger` (tenant id, window key, target definition id,
  cron/window shape, durable last-fired) is a *dependency declaration in this plan*, not a
  deliverable of this story. Until it lands, `pr-triage-sweep` is dispatchable manually/by API and
  **AC4 is unreachable** — the story says so and this plan does not paper over it.

- **D9 — Half B is fail-closed per item via one lifecycle instance per PR, not a loop inside one
  document.** The sweep enumerates open PRs, then dispatches one `document-lifecycle` child per PR
  (`WaitForCompletion=true`, sequential) producing one `TriageDecision` each, emitting
  `PR_TRIAGE.SWEEP.ITEM` per PR with its outcome. A failed PR emits `.ITEM` with the failure detail
  and the loop continues — no `Finish`, no bespoke terminal. Per-PR idempotency is the lifecycle's
  own re-entry, keyed on a producer-scoped issue id (`TaskCreationWorkflow`'s D2 pattern:
  `CreationBindingHelper.ScopeIssueId(issueId, "pr-triage")` → `{repo}#pr-{n}#pr-triage`), so a
  process kill mid-sweep re-enters at `Complete` for already-triaged PRs and at `Produce` for the
  rest. That is what makes AC4's "no PR double-triaged and none dropped" mechanical rather than
  aspirational — **but it still needs the trigger to guarantee at-most-once-per-window across the
  fleet.**

- **D10 — Delivery of the review back to the PR rides the existing generic seam.** The lifecycle
  already exposes `deliveryWorkflowDefinitionId` + `repository` + `issueNumber`
  (`DocumentLifecycleWorkflow.cs:139-141`, `:200-202`, `hasDeliveryGate` `:624`, `DispatchDelivery`
  `:644-653`), used by `DesignDeliveryWorkflow` (`design-proposal-delivery`). If posting the review
  comment to the PR is wanted, add a `diff-review-delivery` leaf and pass its id — **no bespoke
  terminal in the binding**. Optional; not an AC. Keep it out of the first cut if time is tight.

## Implementation Steps

### Half A — `diff-review` (startable now)

1. **Precondition check (no code).** `dotnet build` green; confirm in tree: `ReviewDocumentType`
   registered (`DocumentTypeRegistry.cs:36`), `DocumentLifecycleWorkflow` + `document-review` +
   `ComputeReEntryPositionActivity` + `LifecycleBindingHelper` + `FetchLatestAcceptedDocumentActivity`
   all present, `TaskCreationWorkflow` compiles as the template. All verified present at plan time.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Review/DiffReviewEvents.cs`** (D6) — three
   constants + `ParseTenantId` + `StatusForEvent`, copying `ResearchEvents.cs` verbatim in shape.
   **CREATE `apps/tamma-elsa/src/Tamma.Activities/Review/EmitDiffReviewEventActivity.cs`** if the
   house pattern requires a per-family emitter (mirror `EmitResearchEventActivity`); otherwise reuse
   the generic emitter the sibling bindings use.

3. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DiffReviewBindingHelper.cs`** —
   pure, Elsa-free, total, fail-closed (the `CreationBindingHelper` posture):

   ```csharp
   public static class DiffReviewBindingHelper
   {
       // Compose the diff subject the produce cell must echo; used to build producerVariablesJson.
       public static string BuildSubjectJson(string repository, int? prNumber, string? commitSha);
       // Producer-scoped resume anchor so a re-review of the same PR does not collide with
       // other 'review' documents on the same issue (TaskCreationWorkflow D2 pattern).
       public static string ScopeSubjectId(string repository, int? prNumber, string? commitSha);
       // Typed reads off the accepted Review payload for outputs + the VERDICT event.
       public static (string Decision, int IssueCount, int BlockingCount) ReadVerdict(string documentJson);
       public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit);
   }
   ```

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewProse.cs`** (D5) —
   `public static string Render(string reviewJson)`: deterministic markdown; unparseable → input
   returned unchanged. Pure, no Elsa, no I/O.

5. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/code-review.md`** (AC6) — front
   matter unchanged in shape (bump `version`); body instructs the canonical `Review` wire. Embed
   `ReviewDocumentType.RenderContract()`'s field set by hand (no 39-16 generated-region marker
   exists in any prompt file — verified — so this is a hand edit, exactly as 41-29's Phase 1 step 4
   records for the plan templates). Must literally contain the token groups step 8 pins.

6. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs`** (D5, AC6) —
   `StoreAnalysis` (`:303-308`) sets `analysisText` to `ReviewProse.Render(<llm reply>)` instead of
   the raw reply. **Nothing else in this file changes**: not the `DefinitionId`, not the graph, not
   the inputs, not its allowlist entry.

7. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DiffReviewWorkflow.cs`** — the binding.
   Copy `TaskCreationWorkflow.cs`'s skeleton; `builder.DefinitionId = "diff-review"`,
   `builder.Version = WorkflowVersions.ComputedVersion`,
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (D7). Inputs: `repository`, `prNumber`,
   `commitSha`, `diffText`/`diffRef`, `issueId?`, `tenantId`, `correlationId?`,
   `acceptanceRulesJson?`, `conventions?`. Graph:
   `ReadInputs → ComputeReEntryPosition → ReadPositionStage → FreshRun(FlowDecision)`
   → *(True)* `EmitDiffReviewStarted` → `FetchAcceptedPlanOrCriteria` (optional
   `FetchLatestAcceptedDocumentActivity` for the `consumes` lineage) → join
   → `DispatchLifecycle` (`document-lifecycle`, `WaitForCompletion=true`) with
   `documentType = "review"`, `producerRole = AgentRole.SeniorDeveloper.ToWire()`,
   `producerAction = AgentAction.CodeReview.ToWire()`, `producerVariablesJson` carrying
   subject/diff/plan/criteria, a **declared** `feedbackVariableName` naming a variable the rewritten
   prompt actually declares (clause (e) — verify against the front matter, this is the render-drop
   lesson), plus `issueId` (the scoped id), `correlationId`, `tenantId`, `acceptanceRulesJson`
   → `ReadLifecycleExit` (`LifecycleBindingHelper.ReadLifecycleResult` + `IsAccepted`)
   → `Verdict(FlowDecision)` → `EmitDiffReviewVerdict` / `EmitDiffReviewFailed` → `ExposeOutput`.
   **Zero `Finish`. Zero `DispatchWorkflow("llm-call")`. Exactly one `DispatchWorkflow` whose
   literal definition id is `document-lifecycle`** (plus, if D10's delivery is included, it is passed
   as an *input*, not a second dispatch). No `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`
   variables.

8. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**
   (AC6, Correction 6) — **delete** the `(senior_developer, code-review)` entry from
   `IntentionallyUnbound` (`:293-295`); **add** to `Bindings`:

   ```csharp
   // Story 41-17 (Half A) — DiffReviewWorkflow binds (senior_developer, code-review) as the
   // produce step of its document-lifecycle binding; shape authority is
   // Tamma.Core/Documents/Types/ReviewDocumentType.cs (ReviewDocumentType.Validate).
   [("senior_developer", "code-review")] = new("ReviewDocumentType.Validate",
   [
       One("\"subject\""), One("\"kind\""), One("\"decision\""), One("\"summary\""),
       One("\"issues\""), One("\"severity\""), One("\"category\""), One("\"suggestedFix\""),
   ]),
   ```

   Then verify by running the suite: `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` clause (b)
   (no both-classified contradiction) and clause (c) (not stale — `CodeReviewWorkflow` still emits
   the pair from its compiled `AnalyzeChanges` site, so the entry is live),
   `UniversalPin_EveryBindingAuthority_...` (authority ends in `DocumentType.Validate` ✓),
   `EveryReviewProducerDispatchablePair_IsClassified` (now satisfied via `Bindings` ✓),
   `ReviewProducerDispatchablePairs_HasNoStaleEntries` (no overlap introduced ✓),
   `ReviewerSelectionHelper_AllDispatchablePairs_HasSixteenEligiblePairs` (**unchanged at 16** — this
   story adds no dispatchable reviewer pair).

9. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (AC7) — replace the
   provisional row at `:158`
   (`new WorkflowDocumentInterface("code-review", empty, DocumentTypeKey.Review, true)`) with a
   non-provisional `("diff-review", consumes [Plan] (or empty), produces Review, false)` row, with a
   comment naming this story. **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`**
   — `HaveCount(16)` → `HaveCount(16)` **if** the `code-review` row is *replaced* (net zero), or
   → `HaveCount(17)` **if** the `code-review` row is retained alongside. **Decision: replace.** The
   story is explicit that the provisional row is a 39-1 seed guess for a workflow that produces no
   document, so it is reconciled away, and the count stays 16 with a comment recording the swap. Half
   B's `pr-triage-sweep` row is the one that takes the count to 17.

10. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`** —
    add `"DiffReviewWorkflow"` to `ExpectedContributingWorkflows` (`:123+`) with a comment
    ("Story 41-17: the (senior_developer, code-review) pair rides its document-lifecycle binding,
    discovered by the lifecycle-binding walk"). `MinExpectedDispatchPairs` (`:110`, currently 21)
    needs no change — the pair count does not fall.

11. **CREATE the tests** — see Test Plan. Finish with full `dotnet test` and
    `dotnet ef migrations has-pending-model-changes` (must stay clean).

### Half B — `pr-triage-sweep` (BLOCKED; do not start before its two gates clear)

12. **GATE (no code until both are true):** (a) 41-1a has landed `(senior_developer, triage-pr)` —
    `AgentAction.cs` member + `RolePhaseMap` `SeniorDeveloper` set + `Prompts/senior_developer/triage-pr.md`
    + the two count pins (`AgentActionTests.cs:38`, `RolePhaseMapTests.cs:64`) bumped in the same
    change; (b) a tenant-aware scheduled-trigger seam exists per D8. **If (b) is still unbuilt when
    Half A ships, file it as an epic-level blocker and stop.**

13. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Review/PrTriageEvents.cs`** — `PR_TRIAGE.SWEEP.STARTED`
    / `.ITEM` / `.COMPLETED` + `ParseTenantId` + `StatusForEvent` (`.ITEM` with a failure detail is
    error-status).

14. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/PrTriageBindingHelper.cs`** —
    pure: `ScopeIssueId(repository, prNumber)`, `BuildProducerVariables(prJson)`,
    `ReadClassification(documentJson)`, `BuildItemDetail(exit)`.

15. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PrTriageSweepWorkflow.cs`** —
    `DefinitionId = "pr-triage-sweep"`, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (D7).
    Graph: `ReadInputs → EmitSweepStarted → ListOpenPRs` (existing Git-platform activity)
    `→ hasMorePrs(FlowDecision) → extractCurrentPr → ComputeReEntryPosition(scoped)
    → DispatchLifecycle(document-lifecycle, documentType="triage-decision",
    producer=(senior_developer, triage-pr)) → ReadItemExit → EmitSweepItem → incrementPr` (loop)
    `→ EmitSweepCompleted → ExposeOutput`. Zero `Finish`; the per-item failure edge rejoins
    `EmitSweepItem`, never a terminal (D9).

16. **MODIFY `DocumentTypeRegistry.BuildSeed`** — add `("pr-triage-sweep", empty, TriageDecision,
    false)`; **bump `WorkflowInterfaceGraphTests.cs:45` 16 → 17** in the same change (rule 1
    clause (f) — one conscious bump per new producing workflow).

17. **MODIFY `ContractBindingTests.Bindings`** — add `(senior_developer, triage-pr)` with authority
    `TriageDecisionDocumentType.Validate` and the closed-enum token groups
    (`"priority"`, `"type"`, `"complexity"`, `"automation"`, `"reasoning"` — copy the
    `(product_owner, triage-intake)` entry at `:192-196`). **MODIFY `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`**
    — add `"PrTriageSweepWorkflow"`.

18. **WIRE the trigger** — register `pr-triage-sweep` with the seam from step 12(b), per tenant, with
    the persisted window key. **CREATE the tests** in Test Plan's Half B section.

## Data & Migrations

None in either half. `Review` and `TriageDecision` documents persist through 39-11's existing
`document_instances` table; `DIFF_REVIEW.*` / `PR_TRIAGE.*` ride the existing `TammaEventEmitter` →
`EventPersistenceMiddleware` → `EventRepository` → `domain_events` drain.
`dotnet ef migrations has-pending-model-changes` must stay clean.
*(If the scheduler seam of D8 introduces a persisted last-fired table, that migration belongs to the
seam's owning story, not here.)*

## Events

- **Half A emits (new constants, `Tamma.Activities/Review/DiffReviewEvents.cs`):**
  `DIFF_REVIEW.STARTED` (fresh runs only — a re-entry is not a new review),
  `DIFF_REVIEW.VERDICT` (on lifecycle `accepted`; data `decision`, `issueCount`, `blockingCount`,
  `documentId`), `DIFF_REVIEW.FAILED` (LOUD, on `rejected`/`escalated`, detail naming the typed
  outcome wire). Tags: `repository`, `prId`/`prNumber`, `issueId`, `tenantId`, `correlationId`.
- **Half B emits (new constants, `PrTriageEvents.cs`):** `PR_TRIAGE.SWEEP.STARTED`,
  `PR_TRIAGE.SWEEP.ITEM` (one per PR, success or failure, carrying the PR number + outcome),
  `PR_TRIAGE.SWEEP.COMPLETED` (counts). Tags: `repository`, `prId`, `tenantId`.
- **Emitted by the machinery both bindings wire in (not by this story's code):** the whole
  `DOCUMENT.*` family (`PRODUCED`/`VALIDATED`/`REVIEW_REQUESTED`/`REVIEWED`/`REVISION_STARTED`/
  `ACCEPTED`/`REJECTED`/`ESCALATED`/`REENTERED`), `APPROVAL.REQUESTED`/`PROVIDED`,
  `ESCALATION.TRIGGERED`.
- **`CodeReviewEvents.cs` is diff-empty.** (D6.)

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suites).

**Half A**

- **`DiffReviewWorkflowStructureTests`** (new, modelled line-for-line on
  `TaskCreationWorkflowStructureTests`) — the six thinness clauses as executable pins:
  (a) exactly one `DispatchWorkflow`, `StructureWalk.LiteralDefId(...) == "document-lifecycle"`;
  (b) zero `DispatchWorkflow` with literal def id `llm-call`; (c) `OfType<Finish>()` empty;
  (d) variable names contain none of `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`;
  (e) `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains
  `(DiffReviewWorkflow, DispatchLifecycle, senior_developer, code-review)` and
  `MaterializeDispatchInput` yields `documentType == "review"` plus a **declared**
  `feedbackVariableName`; (f) `DefinitionId == "diff-review"`, threads `TenantId`, carries exactly
  one `ComputeReEntryPositionActivity`, no `Wait*` activity, and
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. **Covers AC1 (structure), AC7.**
- **`DiffReviewBindingHelperTests`** — `BuildSubjectJson` round-trips through
  `ReviewDocumentType.Validate` for the PR-number and commit-sha shapes; `ScopeSubjectId`
  determinism + collision-freedom across two PRs on one repo; `ReadVerdict` on a valid payload /
  unreadable JSON → fail-closed zeros; `BuildFailureDetail` names each reachable
  `DocumentLifecycleOutcome` wire (`review-undecidable`, `ambiguity-above-threshold`,
  `rounds-exhausted`, `validation-exhausted`) + `rejected`.
- **`ReviewProseTests`** (D5, AC6) — a valid `Review` renders decision + summary + one bullet per
  issue, deterministically (same input twice → byte-identical); malformed JSON returns the input
  unchanged; **the output contains no `{`/`"` JSON scaffolding** — the "mentorship path never
  receives raw JSON" assertion.
- **`ReviewDocumentType` fixture additions** (`Tamma.Core.Tests`) — confirm the four AC1/AC2 codes
  each have a rejecting fixture asserting the **code**, not just invalidity:
  `SUBJECT_UNKNOWN_KIND`, `SUBJECT_INCOMPLETE` (diff subject with neither `prNumber` nor
  `commitSha`), `ISSUE_MISSING_FIX`, and the already-shipped
  `invalid-approve-with-blocking-issue` → `APPROVE_WITH_BLOCKING_ISSUES` pair (`:211`/`:223`). Add
  only what is missing. **Covers AC1, AC2.**
- **Drift-guard runs (steps 8–10, self-verifying)** — `ContractBindingTests` fully green with the
  cell moved: no contradiction, no staleness, universal-authority pin satisfied, the 16-pair pin
  unchanged; `ResumableStandardStructuralTests` green with **no** `DiffReviewWorkflow` allowlist
  entry; `WorkflowInterfaceGraphTests` green at the recorded count. **Covers AC6, AC7.**
- **`DiffReviewLifecycleExecutionTests`** (Testcontainers, on the shared 39-6/39-10 fixture) —
  (a) happy path: scripted valid `Review` draft with `decision=request-changes` + a critical issue →
  review approve → orchestrator-side `Accept` resume → outputs + `DIFF_REVIEW.VERDICT`; store asserts
  the accepted `Review` is readable by `repository`/`prNumber` lineage through 39-11.
  (b) the flagship rule end-to-end: a draft with `decision=approve` **and** a critical issue is
  rejected by validation with `APPROVE_WITH_BLOCKING_ISSUES` and drives a repair/revise round —
  proving no downgrade-to-concerns path exists. **Covers AC2.**
  (c) **AC5**: two runs with different `acceptanceRulesJson` reviewer roles; assert the dispatched
  reviewer `(role, action)` differs, and grep the built graph for role literals → none.
  (d) crash after acceptance → fresh `diff-review` dispatch for the same subject re-enters at
  `Complete`, exactly one `DOCUMENT.ACCEPTED` and one `DIFF_REVIEW.VERDICT` on the stream.
- **`CodeReviewWorkflow` regression** — the existing `CodeReviewWorkflow` structure/execution tests
  stay green unmodified; add one assertion that its `mentor-feedback` dispatch's `analysis` value is
  the rendered prose. **Covers AC6 (second half).**

**Half B** (unrunnable until step 12's gates clear)

- **`PrTriageSweepWorkflowStructureTests`** — the same six clauses; plus: the per-item failure edge
  reaches `EmitSweepItem`, never a terminal; zero `Finish`; the loop's dispatch materialises
  `(senior_developer, triage-pr)` + `documentType == "triage-decision"`.
- **`TriageDecision` closed-enum fixtures** (AC3) — out-of-vocabulary priority/type/complexity/
  automation → `OUT_OF_VOCABULARY` naming the field; classification with no reasoning →
  `REASONING_REQUIRED`. (Both codes already exist at `TriageDecision.cs:146`/`:149`; confirm the
  fixtures exist, add what is missing.)
- **`PrTriageSweepDurabilityTests`** (Testcontainers, **AC4**) — three PRs; kill the process after
  the second `PR_TRIAGE.SWEEP.ITEM`; re-dispatch the sweep for the same window; assert exactly three
  `TriageDecision` documents exist, exactly three `.ITEM` events, no PR triaged twice and none
  dropped. A second scenario injects a per-PR failure and asserts the sweep continues to completion
  with an error-status `.ITEM` for that PR. **This test requires the trigger seam for its
  "once per window per tenant across a restart" half; without it, only the per-item half is
  provable, and that limitation is recorded in the story.**

## Definition of Done

| AC | Half | Satisfied by step(s) | Verified by |
|---|---|---|---|
| 1 — diff subject validated, three codes | A | 3, 7 (D2) | `ReviewDocumentType` fixtures; `DiffReviewBindingHelperTests`; structure tests |
| 2 — `APPROVE_WITH_BLOCKING_ISSUES`, no downgrade path | A | 5, 7 | existing `:211`/`:223` fixture + execution scenario (b) |
| 3 — `TriageDecision` closed enums | B | 17 | `TriageDecision` fixtures |
| 4 — tenant-scoped, per-window, durable, fail-closed sweep | B | 15, 18 | `PrTriageSweepDurabilityTests` — **partially unreachable without the scheduler seam (D8)** |
| 5 — reviewer role from rules, no literal in the graph | A | 7 (D3) | execution scenario (c) + graph literal grep |
| 6 — cell moves to `Bindings`, guard green, prose not raw JSON | A | 5, 6, 8 (D5) | `ContractBindingTests` full suite; `ReviewProseTests`; `CodeReviewWorkflow` regression |
| 7 — resume declared, 39-10 green without allowlist, registry reconciled, edge pin | A + B | 7, 9, 15, 16 (D7) | `ResumableStandardStructuralTests`; `WorkflowInterfaceGraphTests` |

## Risks & Mitigations

- **The prompt rewrite breaks the mentorship guidance quality (D5).** The junior-facing guidance
  currently reads whatever the model wrote; after the rewrite it reads rendered structure.
  Mitigation: `ReviewProse.Render` falls back to the raw text on unparseable input, so the path can
  never go blank; the render is deterministic and reviewed as prose in the PR; a `.dev/findings/`
  entry records the before/after for a real PR.
- **Half B is scheduled as if it were startable.** This is the single largest planning risk in the
  story. Mitigation: this plan splits the deliverable, the DoD table marks AC4 partially unreachable,
  and step 12 is a hard gate. **Do not merge a `pr-triage-sweep` that fakes a trigger with a
  `HourlyAnalyticsRollupScheduler` clone** — that reintroduces the exact tenant-suppression bug the
  README documents at `:241`.
- **`ContractBindingTests` has five interlocking guards and the cell move touches four of them
  (Correction 6).** Mitigation: step 8 lists every guard to re-run by name; run the whole fixture,
  not the one test.
- **The dead diff lens (Correction 3) invites a scope creep into `DocumentReviewWorkflow`.**
  Mitigation: D2 makes the diff-ness a payload property; threading a subject kind through the
  lifecycle is explicitly out of scope and would be a generic-layer change with no consumer.
- **The edge-count pin direction is easy to get wrong (step 9).** Replacing the provisional
  `code-review` row keeps the count at 16; Half B takes it to 17. Mitigation: the comment in
  `WorkflowInterfaceGraphTests` records both moves; the pin is a conscious edit per rule 1(f).
- **Story-vs-code tensions:** D7 (resume mode `LatestStateReEntry`, not the story's `Both`) and D6
  (`DIFF_REVIEW.*`, not `CODE_REVIEW.*`) both deviate from story text with reasons recorded above.
  Neither weakens an AC.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition check + `DiffReviewEvents` (+ emitter) | 0.25 |
| 3–4 | `DiffReviewBindingHelper` + `ReviewProse` | 0.5 |
| 5–6 | Prompt rewrite + `CodeReviewWorkflow` render seam | 0.5 |
| 7 | `DiffReviewWorkflow` binding | 0.75 |
| 8–10 | Contract/registry/drift migrations (4 guards + edge pin) | 0.5 |
| 11 | Structure + helper + fixture tests | 0.5 |
| 11 | Testcontainers execution scenarios (a)–(d) | 0.75 |
| **Half A total** | | **3.75** |
| 13–14 | `PrTriageEvents` + `PrTriageBindingHelper` | 0.5 |
| 15 | `PrTriageSweepWorkflow` binding + per-PR loop | 1.0 |
| 16–17 | Registry row + edge-pin bump + contract binding | 0.25 |
| 18 | Trigger wiring + durability tests | 0.75 |
| **Half B total** (*after* its blockers clear) | | **2.5** |
| **Total** | | **6.25** (story estimate: 5–6 days — revised up per Correction 7) |

## Blocks / Blocked by

**Half A — `diff-review`**
- **Blocked by:** Epic 39 (`Review` type, `document-lifecycle`, `document-review`, acceptance rules,
  39-10 resume standard, 39-11 store) — **all landed and verified in tree.** Nothing else. Half A is
  startable today and is one of only two Wave-0-independent work items in Epic 41 (the other is
  **41-29**).
- **Blocks:** nothing hard. Complements **41-15** (acceptance verification also produces a `Review`)
  and **41-28** (design/a11y review reuses the same `Review` recipe); both benefit from Half A
  landing first as the reference `Review`-producing binding.

**Half B — `pr-triage-sweep`**
- **Blocked by:** **41-1a** (the `(senior_developer, triage-pr)` cell — absent from `AgentAction.cs`
  and from `RolePhaseMap.cs:80-92`, verified), **and the tenant-aware scheduled-trigger seam, which
  NO story in Epic 41 owns** (README Wave-0 table, owner "none — must be written"). The same seam
  blocks **41-5**, **41-7**, **41-11**, **41-16**, **41-20** and **41-23**; whoever writes it
  unblocks all seven at once.
- **Blocks:** nothing in Epic 41 directly.

**Shared-file register (coordinate before editing):**
`ContractBindingTests.cs` (also edited by 41-1a, 41-18, 41-19, 41-20, 41-21),
`DocumentTypeRegistry.BuildSeed` + `WorkflowInterfaceGraphTests.cs:45` (edited by **every**
producing-workflow story in Epic 41 — the pin is a serialized, one-per-story bump),
`TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (same).
