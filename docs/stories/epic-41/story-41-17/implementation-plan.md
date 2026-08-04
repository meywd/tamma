# Implementation Plan — Story 41-17: Standalone Code Review & PR Triage

> **This story is two independently-shippable halves and this plan is written as two.**
> **Half A (`diff-review`)** needs no new taxonomy cell and no new enabler — it is startable
> today, in parallel with Wave 0. ~~**Half B (`pr-triage-sweep`)** is blocked on two things that do
> not exist: 41-1a's `(senior_developer, triage-pr)` cell **and** the tenant-aware
> scheduled-trigger seam, which **no story in Epic 41 owns**.~~ Do not schedule them as one unit;
> do not start Half B expecting to finish it.

---

## AMENDMENT (2026-08-01) — READ BEFORE FOLLOWING THIS PLAN

> The story file is now split into **41-17A** / **41-17B** and carries the authoritative amendment
> log. Five things in *this plan* are wrong or stale against the tree as of 2026-08-01. Nothing is
> deleted below; each is corrected in place at the point of use.
>
> | # | Where | Was | Is |
> |---|---|---|---|
> | **P1** | **D5 + steps 4, 6; Test Plan `ReviewProseTests`; DoD row 6** | rewrite the prompt, render the validated `Review` to prose, feed it to `mentor-feedback` via `analysisText` | **`ReviewProse` is deleted from scope.** `Prompts/senior_developer/mentor-feedback.md:2` declares `variables: role, prDescription, diff, conventions` — **not** `analysis` — so the value `StoreAnalysis` sets is dropped at render and the mentorship path reads nothing from it. The seam fixes a data flow that does not exist. Replaced by **D11**. |
> | **P2** | Correction 1 ("CONFIRMED, all of it") + Pre-Reading + steps 8–10 | a list of file:line citations | **most of the cited line numbers have drifted**, and one Dependencies claim is now false. Corrected table below. |
> | **P3** | step 9 | `HaveCount(16)` → stays 16 | the pin is **`HaveCount(18)`** at `WorkflowInterfaceGraphTests.cs:52` (41-2 took 16→17, 41-9 17→18). It stays **18** for Half A. The step also **misses a required edit**: `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:103-137`) is **bidirectional** — a non-provisional seed row absent from its `reconciled` array fails the build, so `"diff-review"` must be added there. |
> | **P4** | step 12 gate (a); D8; "Blocks / Blocked by" | 41-1a has not landed the `(senior_developer, triage-pr)` cell; the count pins are `Be(80)`/`HaveCount(80)` | **41-1a has landed.** `AgentAction.cs:66`, `RolePhaseMap.cs:98`, `Prompts/senior_developer/triage-pr.md`, and `TemplateExampleConformanceTests.ConformingUnboundCells:312` (`triage-pr → triage-decision`, template already on the `TriageDecision` wire). The pins are **96** (`AgentActionTests.cs:42`, `RolePhaseMapTests.cs:74`), and they live in **`Tamma.Api.Tests/Agents/`**, not `Tamma.Core.Tests`. Gate (a) is **discharged**; only gate (b) remains. |
> | **P5** | step 15 (`ListOpenPRs` "existing Git-platform activity"); Est. Effort | listing open PRs is free | **it does not exist.** `IGitPlatformClient` (`Tamma.Platforms.Abstractions/IGitPlatformClient.cs:29-120`) has 12 methods and none lists a repo's PRs; the nearest thing is `IGitHubActionsClient.ListPullRequestsForHeadAsync` (`:84`), keyed by head branch and GitHub-only. **No Elsa activity consumes `IGitPlatformClient` at all** — its only non-`Tamma.Platforms*` references are in test projects — and `Tamma.Activities.csproj:40-48` references no platform project. Half B owns a new interface method + 4 driver impls + an activity seam + 3 contract suites: **+1.25–1.5 days**. See **D12**. |
>
> **Line-number drift (P2), corrected:**
>
> | Citation in this plan | Correct today |
> |---|---|
> | `DocumentTypeRegistry.cs:158` (provisional `code-review` row) | **`:172`** |
> | `ContractBindingTests.cs:293-295` (`IntentionallyUnbound` entry) | **`:375-377`** |
> | `ContractBindingTests.cs:713-720` (both-classified guard) | **`:1043-1051`** (inside `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`, `:1012`) |
> | `ContractBindingTests.cs:82-250` (`Bindings`) / `:286-354` (`IntentionallyUnbound`) | **`:94-…`** / **`:368-…`** |
> | `ContractBindingTests.cs:505-544` (`ReviewProducerDispatchablePairs`) | **`:587-…`**; classification test at `:640`, staleness at `:660` |
> | `ContractBindingTests.cs:592-601` "the 16-pair pin" | **`:685`, and it is `…_HasEighteenEligiblePairs` / `HaveCount(18)`** — 41-1a added `(tech_writer, review-docs)` + `(ux_designer, review-design)`. Half A still does not move it. |
> | `ContractBindingTests.cs:626-652` / `:655` (universal pins) | **`:957`** / **`:986`** |
> | `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)` | **`:52` `HaveCount(18)`** |
> | `ReviewDocumentType.cs` fixture pair `:185`/`:211` | **`:188`/`:214`**; the `ApproveWithBlockingIssues` assertion is at **`:226`**; the rule is at **`:91-100`** |
> | `ReviewerSelectionHelper.cs` `s_diffRoster :73` / `Resolve :108` / `AllDispatchablePairs :178` | **`:83`** / **`:118`** / **`:189`**; `DiffReviewAction` at `:44`; `DiffSubjectKind` at `:33` still exact |
> | `RolePhaseMap.cs:80-92` (`SeniorDeveloper` set) | **`:84-98`**; `CodeReview` at `:89`, `SummarizeTechnical` at `:93`, `TriagePr` at `:98` |
> | `AgentAction.cs:53` (`CodeReview`) | **`:58`** |
> | `HourlyAnalyticsRollupScheduler.cs` `:34`/`:83`/`:241`/`:198-200` | **`:35`**/**`:84`**/**`:242`**/**`:199-200`** |
> | `CodeReviewWorkflow.cs` `AnalyzeChanges :274-301` / `StoreAnalysis :303-308` / mentor `analysis :327` | **`:276-297`** / **`:299-306`** / **`:326-331`** |
>
> `Review.cs:153-161`, `TriageDecision.cs:146`/`:149`, `CodeReviewWorkflow.cs:56`,
> `SingleIssueCycleWorkflow.cs:601`, `MentorshipWorkflow.cs:402`, `TaskCreationWorkflow.cs:47`,
> `DocumentReviewWorkflow.cs:236` still resolve exactly as cited.
>
> **Also wrong, minor:** D8 says "there are **two** schedulers in the tree". There are **three** —
> `HourlyAnalyticsRollupScheduler`, `SecretAutoRotationScheduler`, and
> `Tamma.Api/Services/Audit/AuditChainCheckpointScheduler.cs`. None is tenant-partitioned or
> multi-target, so D8's conclusion stands; only the count was wrong.

---

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
- ~~**NOT FOUND (must be built elsewhere before Half B compiles):** the `(senior_developer, triage-pr)` cell (41-1a) and any tenant-aware scheduled-trigger seam (story 41-30, not yet built)~~
  > **Amendment (2026-08-01).** **NOT FOUND today:** a tenant-aware scheduled-trigger seam (41-30, not
  > yet built) — one item, not two. The `(senior_developer, triage-pr)` cell **exists** (P4).
  > **Also NOT FOUND, and newly this story's problem (P5):** any way to list a repository's open PRs —
  > `IGitPlatformClient.cs:29-120` has no such method, and **no Elsa activity consumes
  > `IGitPlatformClient` at all**. See **D12**.
- **Add to Pre-Reading for Half A (Amendment P1):** `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/code-review.md`
  (`:2` — the declared `variables:` line), `.../mentor-feedback.md` (`:2` — same defect, not fixed
  here), `.../summarize-technical.md` (`:2` — the repoint target),
  `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs:559-589` (`Render` — an
  unsupplied placeholder survives as a literal token and unresolved is non-fatal), and
  `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewProducerHelper.cs:196-201` (the
  house statement of the render rule). **Read these before touching any prompt.**

## Corrections to the story

The story was drafted against a snapshot. Verified against the tree today:

> **Amendment (2026-08-01) — Correction 1's headline "CONFIRMED, all of it" no longer holds.** Two
> claims in it are now false: the `triage-pr` wire **is** present (P4), and most of the cited line
> numbers have drifted (P2 table above). The *substance* — the incumbent's input-shape mismatch, the
> provisional registry row, the scheduler indictment — is re-verified and still true. Correction 7's
> effort revision is also superseded: see the re-costed table under **Est. Effort**.

1. ~~**CONFIRMED, all of it.**~~ Every file:line the story cites resolves: `CodeReviewWorkflow.cs:56`
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
   > **Amendment (2026-08-01) — substance confirmed, citations drifted.** `DiffSubjectKind` is still
   > `:33`; `s_diffRoster` is **`:83`** (not `:73`); `DiffReviewAction` is `:44` and its dispatch arm
   > **`:146-147`** (not `:134-140`); the classification block is **`:601-612`** (not `:519-530`); the
   > pin is **`:685`** and is now `…_HasEighteenEligiblePairs` / `HaveCount(18)`, because 41-1a added
   > `(tech_writer, review-docs)` and `(ux_designer, review-design)` to the **document** roster. The
   > five diff pairs are unchanged and Half A still does not move the pin.

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
   pair the story names (`valid-request-changes-with-blocking-issue` ~~`:185`~~ **`:188`**,
   `invalid-approve-with-blocking-issue` ~~`:211`~~ **`:214`**, asserting `ApproveWithBlockingIssues`
   at ~~`:223`~~ **`:226`**; the rule itself is at **`:91-100`**).
   AC1/AC2 therefore require **no new validator code** — they are satisfied by *binding* the cell to
   `ReviewDocumentType.Validate` and adding the missing negative fixtures for the two subject codes
   if absent. Budget accordingly: this is the cheapest part of the story, not the expensive part.

6. **NEW — AC6's "moves from `IntentionallyUnbound` into `Bindings`" has a second, unstated
   consequence.** `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` (~~`:655`~~ **`:986`**) and
   `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` (~~`:626`~~ **`:957`**)
   mean the
   move is not optional bookkeeping: once `(senior_developer, code-review)` is a document producer it
   **must** be in `Bindings` with a `*DocumentType.Validate` authority, and it must **not** remain
   allowlisted. Additionally `ReviewProducerDispatchablePairs_HasNoStaleEntries` (~~`:567`~~ **`:660`**) forbids
   overlap between that table and `Bindings` — `(senior_developer, code-review)` is currently in
   `IntentionallyUnbound` (not in `ReviewProducerDispatchablePairs`), so the move is a clean
   one-table hop, but verify the invariant after the edit.

7. **NEW — the effort split in the story understates Half A.** The story says "≈3 days for the
   code-review half". Rewriting `Prompts/senior_developer/code-review.md` to the canonical `Review`
   wire is a **breaking prompt change for the incumbent `CodeReviewWorkflow`**, which today posts
   that model output as prose to a junior developer. Half A therefore also owns a ~~render step~~
   **cell repoint** and a regression test in a workflow it is otherwise forbidden from touching. See
   ~~D5~~ **D11**. Revised: ~~3.5–4~~ **4–4.5** days.

8. **NEW (Amendment, 2026-08-01) — the incumbent's dispatch of this cell is already blind, and that
   changes the design.** `code-review.md:2` declares `variables: role, prDescription, diff, conventions`;
   `CodeReviewWorkflow.cs:286-296` supplies only the **undeclared** `reviewCommentsJson`;
   `LlmCallWorkflow.cs:146-158` back-fills only `role`/`conventions`; `PromptStoreService.Render`
   (`:568-583`) leaves `{{diff}}` as a literal token and treats unresolved as non-fatal. The workflow
   has no diff to supply (`:70-96`) — `reviewCommentsJson` is the *human* reviewer's comments
   (`:242-254`). The same defect repeats at the next call: `mentor-feedback.md:2` does not declare
   `analysis`, which `:326-331` supplies. **Consequence: D5 is deleted and D11 replaces it.**

9. **NEW (Amendment, 2026-08-01) — half of Half B's prompt work is already done.** 41-1a shipped
   `Prompts/senior_developer/triage-pr.md` on the `TriageDecision` wire and
   `TemplateExampleConformanceTests.ConformingUnboundCells:312` already pins it there. Step 17 is a
   `Bindings` insert only — no prompt rewrite, and no `PendingProducerCells` graduation (that table,
   `:741-819`, holds only 41-1b's six types and never held `triage-pr`).

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
  correct produce cell and it already exists in ~~`AgentAction.cs:53`~~ **`AgentAction.cs:58`** and in
  `SeniorDeveloper`'s eligible set (~~`RolePhaseMap.cs:85`~~ **`RolePhaseMap.cs:89`**), with a shipped
  prompt file at `src/Tamma.Api/Prompts/senior_developer/code-review.md`. **Half A therefore adds ZERO
  taxonomy cells** — no `AgentAction` member, no `RolePhaseMap` edit, no new prompt file, and no bump
  of ~~`AgentActionTests.cs:38` `Be(80)` / `RolePhaseMapTests.cs:64` `HaveCount(80)`~~
  **`Tamma.Api.Tests/Agents/AgentActionTests.cs:42` `Be(96)` /
  `Tamma.Api.Tests/Agents/RolePhaseMapTests.cs:74` `HaveCount(96)`**. This is precisely why Half A is
  Wave-0-independent.
  > **Amendment (2026-08-01) — D4 SURVIVES D11.** The repoint target
  > `(senior_developer, summarize-technical)` already exists (`AgentAction.cs:61`,
  > `RolePhaseMap.cs:93`, `Prompts/senior_developer/summarize-technical.md`), so D11 mints no cell
  > either and the 96-member pins still do not move. *(The count was 80 when this plan was written; it
  > is 96 today — 41-1a/41-1b landed. Either way, Half A does not touch it.)*

- **D5 — SUPERSEDED BY D11 (Amendment P1, 2026-08-01). Do not implement the render seam.** Kept
  verbatim below because it is the decision that was wrong and a future reader must see why.

  > **Why D5 fails.** (i) Its consumer does not exist: `mentor-feedback.md:2` declares
  > `variables: role, prDescription, diff, conventions` and never `analysis`, and
  > `PromptStoreService.Render` (`:568-583`) drops a supplied-but-undeclared key — the codebase says
  > so itself in `ReviewProducerHelper.cs:196-201`. So `analysisText` has been dead since it was
  > written, and AC6's "a test asserts the mentorship path never receives raw JSON" pins nothing.
  > (ii) Worse, D5's premise is inverted. It treats the prompt rewrite as a *quality* risk to
  > mentoring prose. The real risk is that the incumbent's `AnalyzeChanges` dispatch
  > (`CodeReviewWorkflow.cs:286-296`) supplies exactly one variable, `reviewCommentsJson`, which
  > `code-review.md` **does not declare** — while the template renders `## Diff\n{{diff}}` from a
  > variable nobody supplies and `LlmCallWorkflow.cs:146-158` back-fills only `role`/`conventions`.
  > An unresolved placeholder is left as the **literal token** and is non-fatal (`PromptStoreService.cs:568-583`;
  > the count is only surfaced at `:653` and `PromptEndpoints.cs:448`). The site has no diff to
  > supply in the first place (`CodeReviewWorkflow.cs:70-96`; `reviewCommentsJson` is the *human*
  > reviewer's comments from `MonitorReviewActivity`, `:242-254`). So the model there already
  > reviews a diff it cannot see — and after the rewrite it would return a schema-valid, fully
  > hallucinated `Review` that D5's renderer would hand to a junior as structured mentoring.
  > Making unvalidated noise into validated fabrication is a regression, not a fix.

  ~~`Prompts/senior_developer/code-review.md` is rewritten to instruct the
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
  untouched. AC6's "a test asserts the mentorship path never receives raw JSON" pins it.~~

- **D11 — REPLACES D5 (Amendment P1, 2026-08-01): rewrite the template AND take the incumbent off the
  cell. Decided, not deferred.** The story file carries the full reasoning under
  *§ Prompt-rewrite decision*; this is the buildable form.

  1. **Rewrite** `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/code-review.md` to the
     canonical `Review` wire (`"subject"`/`"kind"`, `"decision"`, `"summary"`, `"issues"` with
     `"severity"`/`"category"`/`"description"`/`"suggestedFix"`), declaring the variables the
     `diff-review` binding actually supplies (`role`, `prDescription`, `diff`, `conventions`, plus
     whatever `feedbackVariableName` carrier the binding names — clause (e)). It becomes the
     `diff-review` produce template and nothing else.
  2. **Repoint** `CodeReviewWorkflow.AnalyzeChanges` (`:276-297`) from
     `(senior_developer, code-review)` to **`(senior_developer, summarize-technical)`**, supplying that
     template's **declared** variables: `workItemJson` (the PR identity the workflow already holds —
     repository/PR number/branch), `findings` (`reviewCommentsJson`, the human comments it actually
     has) and `audience` (the junior's skill level as prose). Evidence this is the right target:
     `summarize-technical.md:2` declares `role, workItemJson, findings, audience`; the action is in
     `SeniorDeveloper`'s eligible set (`RolePhaseMap.cs:93`); it is already classified
     "free-text technical summary; no document type claims it" in
     `TemplateExampleConformanceTests.cs:438`; and **nothing dispatches it today** (verified: the only
     `SummarizeTechnical` references are `ActionCatalog.Descriptors.cs:121`, `AgentAction.cs:61`,
     `RolePhaseMap.cs:93`), so a new `IntentionallyUnbound` entry is required for it.
     **Zero taxonomy cells are minted** — the 96-member pins do not move, so D4 survives intact.
  3. **Delete** `ReviewProse` (step 4), the `StoreAnalysis` render change (step 6 as written),
     `ReviewProseTests`, and AC6's "never receives raw JSON" clause.

  *This is not the rewiring D1 forbids.* D1 protects `code-review`'s `DefinitionId`, graph shape,
  inputs and its two external callers — all untouched. Changing one internal `llm-call` dispatch's
  `role`/`action`/`variables` dictionary is the same edit class D5 already accepted for
  `StoreAnalysis`, and it is the house precedent, stated in the tree at
  `TemplateExampleConformanceTests.cs:343-345`: 39-15 D5 split `(developer, triage-context-scan)` off
  *"precisely so a document producer never shares a cell with a free-text scan."*
  *The junior loses nothing:* `DeliverGuidanceActivity` already receives `ReviewCommentsJson` verbatim
  (`CodeReviewWorkflow.cs:354`).

  **Left open with an owner, deliberately:** `mentor-feedback.md` is itself a code-review JSON template
  (`:2` declares `role, prDescription, diff, conventions`; `:25-45` emits the legacy code-review wire)
  fed two undeclared variables (`analysis`, `skillLevel`) by `CodeReviewWorkflow.cs:326-331`. That is a
  pre-existing defect in a workflow this story does not own, and fixing it means deciding what a
  mentoring prompt should say. **File it in `.dev/bugs/`; do not fix it here.** Named so nobody reads
  41-17A as having left the mentorship path healthy.

- **D12 — NEW (Amendment P5): Half B owns the open-PR platform surface.** Step 15's "`ListOpenPRs`
  (existing Git-platform activity)" does not exist in any form. Half B builds:
  (a) `IGitPlatformClient.ListOpenPullRequestsAsync` — paged, `PlatformResult`-returning, same
  never-throw-on-platform-error posture as the other 12 methods
  (`IGitPlatformClient.cs:11-15`), added next to `ListPullRequestFilesAsync` (`:71`);
  (b) implementations in **all four** clients — `GiteaPlatformClient` (backs Gitea *and* Forgejo),
  `GitLabPlatformClient`, `GitHubPlatformClient`, and `NullGitPlatformDriver`'s inner client. GitHub
  needs a widened inner seam first: `GitHubPlatformClient` delegates to `IGitHubActionsClient` and
  returns `ServiceUnavailable` for anything not on it (`:107-113`, `:117-134`, `:162-171`), so a new
  method lands on `IGitHubActionsClient` + `OctokitGitHubActionsClient` + `NullGitHubActionsClient`;
  (c) an Elsa activity — and it may **not** take a direct dependency on the platform projects
  (`Tamma.Activities.csproj:40-48` references only `Tamma.Core`, `Tamma.Data`,
  `Tamma.Activities.Guardrails`; no activity anywhere consumes `IGitPlatformClient`). Use the house
  seam pattern: interface in `Tamma.Activities`, implementation in `Tamma.Api` over `IPlatformResolver`
  (`Tamma.Platforms/PlatformResolver.cs:68-139`), exactly as `IGitHubActionsClient` /
  `OctokitGitHubActionsClient` do;
  (d) contract-suite updates in the same change: `GiteaPlatformClientTests`, `ForgejoContractTests`
  (doc-comment count 12 → 13, `:16-18`), `GiteaIntegrationTests` (17 → 18 methods, `:19-27`).
  A driver without the capability returns `capability_unsupported` (the `CreatePullRequestReviewCommentAsync`
  posture, `IGitPlatformClient.cs:74-80`) — an empty list must mean "no open PRs", or the sweep
  silently no-ops and AC4 passes vacuously. **+1.25–1.5 days on Half B.**

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

  > **Amendment (2026-08-01) — D7 was right and the story has been corrected to match, so this is no
  > longer a deviation.** Verified: `ResumeBehaviorAttribute` requires non-empty `SuspendActivities`
  > for `Both` (`ResumeBehavior.cs:33-35`) and
  > `ResumableStandardStructuralTests.EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode`
  > (`:158-197`) fails a `Both` declaration whose named activities are empty, non-canonical, or absent
  > from the built graph; its inverse `CanonicalSuspendNode_AppearsOnlyInDeclaredWorkflows` (`:201-235`)
  > closes the other direction. Sixteen landed workflows declare `LatestStateReEntry`; the only two
  > `Both` declarations are `DocumentLifecycleWorkflow.cs:54` and `ClarifyingQuestionsWorkflow.cs:40`,
  > each naming a real canonical suspend activity. Epic 41 README rule 5 already states the rule.
  > The story's AC7 clause 1 (which said `Both`) is superseded — see the story's Amendment A2.

- **D8 — Half B does not build the scheduler seam; it consumes one and stays dark until it exists.**
  Verified: there are ~~**two**~~ **three** schedulers in the tree (the third is
  `Tamma.Api/Services/Audit/AuditChainCheckpointScheduler.cs`) and **none** is reusable.
  `HourlyAnalyticsRollupScheduler` hardcodes its target (`:199-200`), exposes a single
  `FireAtMinute` int (`:35`), keeps last-fired in a process field (`_lastFired`, `:84`), and locks on
  `(year, dayOfYear, hour)` with **no tenant component** (`:242`) — one tenant's leader suppresses
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

4. ~~**CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewProse.cs`** (D5) —
   `public static string Render(string reviewJson)`: deterministic markdown; unparseable → input
   returned unchanged. Pure, no Elsa, no I/O.~~
   > **Amendment (2026-08-01) — DELETED (D11).** Its consumer does not exist; see D5's superseded note.

5. **REWRITE `apps/tamma-elsa/src/Tamma.Api/Prompts/senior_developer/code-review.md`** (AC6, D11.1) —
   front matter shape unchanged (bump `version`); **the declared `variables:` list must match what the
   `diff-review` binding supplies** — the current list is `role, prDescription, diff, conventions`
   (`:2`) and it must keep whatever carrier step 7 names as `feedbackVariableName`. Body instructs the
   canonical `Review` wire. Embed `ReviewDocumentType.RenderContract()`'s field set by hand (no 39-16
   generated-region marker exists in any prompt file — verified — so this is a hand edit, exactly as
   41-29's Phase 1 step 4 records for the plan templates). Must literally contain the token groups
   step 8 pins.
   **Also (new, Amendment P1):** move
   `TemplateExampleConformanceTests.IntentionallyUnboundCells[("senior_developer","code-review")]`
   (`:397`) into the bound/conforming set as a `"review"` producer — that table's own doc requires the
   move and the template rewrite in the same change (`:334-337`), and its current justification
   ("raw text kept by CodeReviewWorkflow.StoreAnalysis") stops being true at step 6.

6. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs`** (D11.2, AC6) —
   ~~`StoreAnalysis` (`:303-308`) sets `analysisText` to `ReviewProse.Render(<llm reply>)` instead of
   the raw reply.~~
   > **Amendment (2026-08-01) — REPLACED (D11).** `StoreAnalysis` (`:299-306`) is left alone. Instead
   > `AnalyzeChanges` (`:276-297`) is repointed to `(senior_developer, summarize-technical)` and its
   > `["variables"]` dictionary is rewritten to that template's **declared** keys — `workItemJson`,
   > `findings`, `audience` (`summarize-technical.md:2`). Delete the stale inline comment at `:289`
   > claiming *"The template renders `{{reviewCommentsJson}}`"* — it never did.

   **Nothing else in this file changes**: not the `DefinitionId`, not the graph, not the inputs, not
   its `LegacyResumeAllowlist` entry, not its two external callers.

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

   > **Amendment (2026-08-01) — one addition and one correction.**
   > **Addition (D11.2):** also **add** an `IntentionallyUnbound` entry for the newly-dispatched
   > `(senior_developer, summarize-technical)` pair ("free-text technical summary of the human
   > reviewer's comments; `CodeReviewWorkflow.StoreAnalysis` keeps the raw text and it is not
   > sliced"), or clause (a) of `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` (`:1024-1041`)
   > fails on an unclassified pair.
   > **Correction to the staleness reasoning below:** after step 6 the incumbent no longer emits
   > `(senior_developer, code-review)`, so the `Bindings` entry stays live **only** because
   > `DiffReviewWorkflow`'s lifecycle binding emits it — `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs()`
   > (`:441-451`) concatenates `ScanLifecycleBindingDispatches()`, so this holds, but it now depends on
   > step 7 and step 10 landing in the same change. Do not land step 6 and step 8 without step 7.

   Then verify by running the suite: `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` (`:1012`)
   clause (b) (no both-classified contradiction, `:1043-1051`) and clause (c) (not stale, `:1053-1067`
   — see the correction above),
   `UniversalPin_EveryBindingAuthority_...` (`:957`; authority ends in `DocumentType.Validate` ✓),
   `EveryReviewProducerDispatchablePair_IsClassified` (`:640`; now satisfied via `Bindings` ✓),
   `ReviewProducerDispatchablePairs_HasNoStaleEntries` (`:660`; no overlap introduced ✓),
   ~~`ReviewerSelectionHelper_AllDispatchablePairs_HasSixteenEligiblePairs` (**unchanged at 16**~~
   **`ReviewerSelectionHelper_AllDispatchablePairs_HasEighteenEligiblePairs` (`:685`, unchanged at
   `HaveCount(18)`** — 41-1a took it 16 → 18; this story adds no dispatchable reviewer pair).

9. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (AC7) — replace the
   provisional row at ~~`:158`~~ **`:172`**
   (`new WorkflowDocumentInterface("code-review", empty, DocumentTypeKey.Review, true)`) with a
   non-provisional `("diff-review", consumes [Plan] (or empty), produces Review, false)` row, with a
   comment naming this story. **Decision: replace** — the provisional row is a 39-1 seed guess for a
   workflow that produces no document, so it is reconciled away.

   > **Amendment (2026-08-01) — the pin number was stale and one required edit was missing (P3).**
   > ~~"`WorkflowInterfaceGraphTests.cs:45` — `HaveCount(16)` → `HaveCount(16)` … Half B's
   > `pr-triage-sweep` row is the one that takes the count to 17."~~
   > The pin is **`WorkflowInterfaceGraphTests.cs:52`, `HaveCount(18)`** — 41-2 took it 16 → 17 and
   > 41-9 took it 17 → 18. Half A **replaces**, so it **stays 18** and the only edit is a comment
   > recording the swap. Half B takes it **18 → 19**.
   > **Missing edit:** `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:103-137`)
   > holds a `reconciled` array that is explicitly **BIDIRECTIONAL** ("everything listed must be
   > `!Provisional` AND everything unlisted must be `Provisional`", `:131-133`). Add `"diff-review"`
   > to it, or the build fails on the new non-provisional row. Half B adds `"pr-triage-sweep"`.

10. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`** —
    add `"DiffReviewWorkflow"` to `ExpectedContributingWorkflows` (`:123+`) with a comment
    ("Story 41-17: the (senior_developer, code-review) pair rides its document-lifecycle binding,
    discovered by the lifecycle-binding walk"). `MinExpectedDispatchPairs` (`:110`, currently 21)
    needs no change — the pair count does not fall.

11. **CREATE the tests** — see Test Plan. Finish with full `dotnet test` and
    `dotnet ef migrations has-pending-model-changes` (must stay clean).

### Half B — `pr-triage-sweep` (BLOCKED; do not start before its ~~two~~ **remaining** gates clear)

12. **GATE (no code until both are true):** ~~(a) 41-1a has landed `(senior_developer, triage-pr)` —
    `AgentAction.cs` member + `RolePhaseMap` `SeniorDeveloper` set + `Prompts/senior_developer/triage-pr.md`
    + the two count pins (`AgentActionTests.cs:38`, `RolePhaseMapTests.cs:64`) bumped in the same
    change;~~ (b) a tenant-aware scheduled-trigger seam exists per D8. **If (b) is still unbuilt when
    Half A ships, file it as an epic-level blocker and stop.**

    > **Amendment (2026-08-01) — gate (a) is DISCHARGED (P4).** 41-1a landed:
    > `AgentAction.cs:66` (`[Wire("triage-pr")] TriagePr`, comment "Story 41-1a — 41-17's PR-triage
    > cell"), `RolePhaseMap.cs:98` (in `SeniorDeveloper`'s set), `Prompts/senior_developer/triage-pr.md`
    > ships, and `TemplateExampleConformanceTests.ConformingUnboundCells:312` already declares
    > `("senior_developer","triage-pr") → "triage-decision"` — i.e. the template already instructs the
    > `TriageDecision` wire and its worked example validates, so step 17's prompt work is **already
    > done**. The count pins named above were wrong twice over: they are **96**, not 80, and they live
    > at `Tamma.Api.Tests/Agents/AgentActionTests.cs:42` and
    > `Tamma.Api.Tests/Agents/RolePhaseMapTests.cs:74`, not in `Tamma.Core.Tests`.
    > **`(senior_developer, triage-pr)` is NOT in `ContractBindingTests.PendingProducerCells`** (`:741-819`)
    > — that table holds only 41-1b's six types — so step 17 is a plain `Bindings` insert with no
    > graduation to perform.
    > **New gate (c) — Half B's own deliverable, not a blocker:** the open-PR platform surface of
    > **D12** must be built before step 15 can enumerate anything.

13. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Review/PrTriageEvents.cs`** — `PR_TRIAGE.SWEEP.STARTED`
    / `.ITEM` / `.COMPLETED` + `ParseTenantId` + `StatusForEvent` (`.ITEM` with a failure detail is
    error-status).

14. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/PrTriageBindingHelper.cs`** —
    pure: `ScopeIssueId(repository, prNumber)`, `BuildProducerVariables(prJson)`,
    `ReadClassification(documentJson)`, `BuildItemDetail(exit)`.

14b. **NEW (Amendment P5, D12) — BUILD the open-PR platform surface before step 15.**
    (a) add `ListOpenPullRequestsAsync` to `Tamma.Platforms.Abstractions/IGitPlatformClient.cs`;
    (b) implement it in `GiteaPlatformClient` (serves Gitea + Forgejo), `GitLabPlatformClient`,
    `GitHubPlatformClient` (which first needs the method on `IGitHubActionsClient` +
    `OctokitGitHubActionsClient` + `NullGitHubActionsClient`, because it can only delegate), and
    `NullGitPlatformDriver`'s inner client;
    (c) add the Elsa activity behind a `Tamma.Activities`-side seam interface implemented in
    `Tamma.Api` over `IPlatformResolver` — **not** a direct platform reference
    (`Tamma.Activities.csproj:40-48`);
    (d) update `GiteaPlatformClientTests`, `ForgejoContractTests` (`:16-18`, 12 → 13) and
    `GiteaIntegrationTests` (`:19-27`, 17 → 18).
    Capability-absent ⇒ `capability_unsupported`, never an empty list.

15. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PrTriageSweepWorkflow.cs`** —
    `DefinitionId = "pr-triage-sweep"`, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (D7).
    Graph: `ReadInputs → EmitSweepStarted → ListOpenPRs` (~~existing~~ **the step-14b** Git-platform activity)
    `→ hasMorePrs(FlowDecision) → extractCurrentPr → ComputeReEntryPosition(scoped)
    → DispatchLifecycle(document-lifecycle, documentType="triage-decision",
    producer=(senior_developer, triage-pr)) → ReadItemExit → EmitSweepItem → incrementPr` (loop)
    `→ EmitSweepCompleted → ExposeOutput`. Zero `Finish`; the per-item failure edge rejoins
    `EmitSweepItem`, never a terminal (D9).

16. **MODIFY `DocumentTypeRegistry.BuildSeed`** — add `("pr-triage-sweep", empty, TriageDecision,
    false)`; **bump ~~`WorkflowInterfaceGraphTests.cs:45` 16 → 17~~ `WorkflowInterfaceGraphTests.cs:52`
    18 → 19** in the same change (rule 1 clause (f) — one conscious bump per new producing workflow),
    **and add `"pr-triage-sweep"` to the bidirectional `reconciled` array at `:110-137`** (Amendment P3).

17. **MODIFY `ContractBindingTests.Bindings`** — add `(senior_developer, triage-pr)` with authority
    `TriageDecisionDocumentType.Validate` and the closed-enum token groups
    (`"priority"`, `"type"`, `"complexity"`, `"automation"`, `"reasoning"` — copy the
    `(product_owner, triage-intake)` entry at ~~`:192-196`~~ **`:204-208`**). **MODIFY `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`**
    — add `"PrTriageSweepWorkflow"`. *(No prompt work: `triage-pr.md` already instructs the
    `TriageDecision` wire — Amendment P4.)*

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
- ~~**`ReviewProseTests`** (D5, AC6) — a valid `Review` renders decision + summary + one bullet per
  issue, deterministically (same input twice → byte-identical); malformed JSON returns the input
  unchanged; **the output contains no `{`/`"` JSON scaffolding** — the "mentorship path never
  receives raw JSON" assertion.~~
  > **Amendment (2026-08-01) — DELETED with `ReviewProse` (D11). Replaced by the two tests below.**
- **`CodeReviewWorkflowCellRepointTests`** (D11.2, AC6 / story AC6) — assert
  `TaxonomyDriftBuildTests` discovers `(CodeReviewWorkflow, AnalyzeChanges, senior_developer,
  summarize-technical)` and **no longer** discovers `(CodeReviewWorkflow, …, senior_developer,
  code-review)`; assert `DiffReviewWorkflow` is the workflow that now emits the latter. The existing
  `CodeReviewWorkflowStructureTests` (17 tests) must stay green **unmodified** — the graph, node ids,
  terminals and `DefinitionId` are untouched.
- **`CodeReviewWorkflowDeclaredVariableTests`** (story AC6b, the render lesson) — for each of the
  workflow's two `llm-call` dispatches, materialise the `["variables"]` dictionary and assert every
  key is in the target template's declared `variables` front matter, read via
  `SystemPrompts.GetRoleAction(role, action)`. The `mentor-feedback` dispatch is carved out by an
  explicit, bug-referenced exclusion (its `analysis`/`skillLevel` keys are a pre-existing defect this
  story does not fix — D11's "left open" note); the `summarize-technical` dispatch must pass with no
  carve-out. Precedent for the assertion shape:
  `AcceptanceCriteriaAuthoringWorkflowStructureTests.cs:92-95`.
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
  stay green unmodified; ~~add one assertion that its `mentor-feedback` dispatch's `analysis` value is
  the rendered prose.~~ **Covers AC6 (second half).**
  > **Amendment (2026-08-01).** The struck assertion cannot mean anything: `mentor-feedback.md:2` does
  > not declare `analysis`, so whatever `StoreAnalysis` writes is dropped at render. The second half of
  > AC6 is now covered by `CodeReviewWorkflowCellRepointTests` +
  > `CodeReviewWorkflowDeclaredVariableTests` above.

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
| 2 — `APPROVE_WITH_BLOCKING_ISSUES`, no downgrade path | A | 5, 7 | existing `:214`/`:226` fixture + execution scenario (b) |
| 3 — `TriageDecision` closed enums | B | 17 | `TriageDecision` fixtures |
| 4 — tenant-scoped, per-window, durable, fail-closed sweep | B | 14b, 15, 18 | `PrTriageSweepDurabilityTests` — **partially unreachable without the scheduler seam (D8)** |
| 4b — open-PR platform surface *(NEW, Amendment P5)* | B | 14b (D12) | driver unit tests + `ForgejoContractTests` + `GiteaIntegrationTests` |
| 5 — reviewer role from rules, no literal in the graph | A | 7 (D3) | execution scenario (c) + graph literal grep |
| 6 — cell moves to `Bindings`, guard green, ~~prose not raw JSON~~ **incumbent off the cell** | A | 5, 6, 8 (~~D5~~ **D11**) | `ContractBindingTests` full suite; ~~`ReviewProseTests`~~ `CodeReviewWorkflowCellRepointTests`; `CodeReviewWorkflow` regression |
| 6b — every supplied variable is declared *(NEW, Amendment P1)* | A | 5, 6 (D11) | `CodeReviewWorkflowDeclaredVariableTests` |
| 7 — resume declared, 39-10 green without allowlist, registry reconciled, edge pin **stays 18 for A / 18→19 for B** | A + B | 7, 9, 15, 16 (D7) | `ResumableStandardStructuralTests`; `WorkflowInterfaceGraphTests` (both `Declared_edge_count_is_pinned` **and** `Seeded_declarations_are_provisional_except_reconciled_bindings`) |
| 8 — template conformance table move *(NEW, Amendment P1)* | A | 5 | `TemplateExampleConformanceTests` |

## Risks & Mitigations

- ~~**The prompt rewrite breaks the mentorship guidance quality (D5).** The junior-facing guidance
  currently reads whatever the model wrote; after the rewrite it reads rendered structure.
  Mitigation: `ReviewProse.Render` falls back to the raw text on unparseable input, so the path can
  never go blank; the render is deterministic and reviewed as prose in the PR; a `.dev/findings/`
  entry records the before/after for a real PR.~~
  > **Amendment (2026-08-01) — this risk was mis-stated and its mitigation was inert.** The real risk
  > was never guidance *quality*; it was that the rewritten template would be rendered for a caller
  > that supplies neither `diff` nor `prDescription`, producing a schema-valid hallucinated `Review`
  > delivered to a junior as structured mentoring. D11 removes the risk by construction (the incumbent
  > moves off the cell) rather than mitigating it. **Residual risk:** the incumbent's LLM leg changes
  > from an ungrounded code-review JSON blob to a technical summary of the review comments it actually
  > holds — a behavior change, recorded in `.dev/findings/` with a before/after on a real PR, and
  > bounded by the fact that `DeliverGuidanceActivity` already passes the raw `ReviewCommentsJson`
  > through (`CodeReviewWorkflow.cs:354`).
- **NEW (Amendment P5) — Half B's platform surface touches four drivers and three contract suites.**
  Adding a method to `IGitPlatformClient` is a breaking interface change for every implementer, and
  `GitHubPlatformClient` cannot satisfy it without widening `IGitHubActionsClient` first. Mitigation:
  D12 lists all four implementers and all three suites; budget it as its own line item, not as
  "call the existing activity".
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

> **Amendment (2026-08-01) — re-costed. Half A 3.75 → 4.25; Half B 2.5 → 4.0. The two are now
> separately-scheduled stories (41-17A / 41-17B), not "halves" of one 6.25-day unit.**

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition check + `DiffReviewEvents` (+ emitter) | 0.25 |
| 3 ~~–4~~ | `DiffReviewBindingHelper` ~~+ `ReviewProse`~~ *(step 4 deleted, D11)* | 0.25 |
| 5–6 | Prompt rewrite + **cell repoint + conformance-table move** (D11) | 0.75 |
| 7 | `DiffReviewWorkflow` binding | 0.75 |
| 8–10 | Contract/registry/drift migrations — **5 tables, 7 guards, + the `reconciled` array (P3)** | 0.75 |
| 11 | Structure + helper + fixture tests + the two new D11 tests | 0.75 |
| 11 | Testcontainers execution scenarios (a)–(d) | 0.75 |
| **41-17A total** | | **4.25** *(story: 4–4.5)* |
| **14b** | **Open-PR platform surface: interface + 4 drivers + activity seam + 3 contract suites (D12)** | **1.25–1.5** |
| 13–14 | `PrTriageEvents` + `PrTriageBindingHelper` | 0.5 |
| 15 | `PrTriageSweepWorkflow` binding + per-PR loop | 1.0 |
| 16–17 | Registry row + edge-pin bump (18→19) + `reconciled` array + contract binding | 0.25 |
| 18 | Trigger wiring + durability tests | 0.75 |
| **41-17B total** (*after gate (b) clears*) | | **≈4.0** |
| ~~**Total**~~ | ~~**6.25**~~ | **8.25 across two stories** |

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
- **Blocked by:** ~~**41-1a** (the `(senior_developer, triage-pr)` cell — absent from `AgentAction.cs`
  and from `RolePhaseMap.cs:80-92`, verified), **and**~~ **the tenant-aware scheduled-trigger seam,
  which ~~NO story in Epic 41 owns~~ story 41-30 owns and has not yet built** (README Wave-0 table).
  The same seam blocks **41-5**, **41-7**, **41-11**, **41-16**, **41-20** and **41-23**; whoever
  writes it unblocks all seven at once.
  > **Amendment (2026-08-01) — 41-1a has LANDED (P4); it is no longer a blocker.** `AgentAction.cs:66`,
  > `RolePhaseMap.cs:98`, `Prompts/senior_developer/triage-pr.md`,
  > `TemplateExampleConformanceTests.cs:312`. **One blocker remains, not two.**
- **Owns (not blocked by):** the open-PR platform surface of **D12** — 1.25–1.5 d inside 41-17B.
- **Blocks:** nothing in Epic 41 directly.

**Shared-file register (coordinate before editing):**
`ContractBindingTests.cs` (also edited by 41-1a, 41-18, 41-19, 41-20, 41-21),
`DocumentTypeRegistry.BuildSeed` + ~~`WorkflowInterfaceGraphTests.cs:45`~~ **`WorkflowInterfaceGraphTests.cs:52`
(`Declared_edge_count_is_pinned`) *and* `:103-137` (`Seeded_declarations_are_provisional_except_reconciled_bindings`
— its `reconciled` array is bidirectional and must be edited alongside)** (edited by **every**
producing-workflow story in Epic 41 — the pin is a serialized, one-per-story bump),
`TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (same),
**`TemplateExampleConformanceTests.cs` `IntentionallyUnboundCells` / `ConformingUnboundCells`
(new to this register — 41-17A moves `(senior_developer, code-review)` between them).**
