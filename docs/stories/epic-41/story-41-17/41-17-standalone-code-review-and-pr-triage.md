# Story 41-17: Standalone Code Review & PR Triage Workflow

Status: **SPLIT — schedule as two stories.** `41-17A` (diff-review) is drafted and startable;
`41-17B` (pr-triage-sweep) is drafted and blocked. See the Amendment block below.

---

## AMENDMENT LOG

> **Amendment (2026-08-01) — verification pass against the tree. Six corrections, one of which
> changes what gets built.** The story was drafted against a snapshot that has since moved, and two
> of its acceptance criteria could not have passed as written. Nothing below is a silent rewrite:
> each correction quotes what the story used to say. The corrections are:
>
> | # | Section | What the story said | What is true |
> |---|---|---|---|
> | **A1** | Estimated Effort / Scope | one story, "5–6 days (≈3 code-review, ≈2–3 triage)" | two stories: **41-17A ≈ 4–4.5 d**, **41-17B ≈ 4 d**. 41-17B carries ~1.5 d of git-platform work the plan assumed existed. |
> | **A2** | AC7 clause 1 | review declares resume `Both` | **unpassable.** A thin binding has no suspend node of its own; `Both` fails the 39-10 gate. Both halves declare `LatestStateReEntry`. |
> | **A3** | AC7 clause 2 | "`WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is bumped in the same change" | wrong for 41-17A, which **replaces** the provisional `code-review` row — the count does **not** move. Only 41-17B bumps it. Also the pin is now `HaveCount(18)`, not 16. |
> | **A4** | "The cell is currently allowlisted, not bound" / AC6 | rewrite `senior_developer/code-review.md`, render the `Review` to prose, feed it to `mentor-feedback` | **the render seam writes into a variable nothing reads, and the rewrite makes an existing silent failure louder and more dangerous.** Decision taken below (§ Prompt-rewrite decision). |
> | **A5** | Dependencies (PR-triage half) | "41-1a … does not exist today — no `triage-pr` wire in `AgentAction.cs`, and it is not in `SeniorDeveloper`'s eligible set" | **stale — 41-1a has landed.** `AgentAction.cs:66` `[Wire("triage-pr")] TriagePr`; `RolePhaseMap.cs:98` puts it in `SeniorDeveloper`'s set; `Prompts/senior_developer/triage-pr.md` ships; the count pins are at 96 (`AgentActionTests.cs:42`, `RolePhaseMapTests.cs:74`). 41-17B has **one** blocker left, not two. |
> | **A6** | Scope / Dependencies (PR-triage half) | the sweep "enumerates open PRs" as if that were free | **there is no way to list a repo's open PRs in this tree.** `IGitPlatformClient` has no such method and **no Elsa activity consumes `IGitPlatformClient` at all**. New dependency recorded below. |
>
> Line-number drift found while verifying (the story's citations, corrected):
> `DocumentTypeRegistry.cs:158` → **`:172`**; `ContractBindingTests.cs:293-295` → **`:375-377`**;
> the both-classified guard `:713-720` → **`:1043-1051`**; `ReviewDocumentType.cs:17-32` → **`:16-38`**;
> the fixture pair `:185`/`:211` → **`:188`/`:214`** (the `APPROVE_WITH_BLOCKING_ISSUES` assertion is
> at `:226`); `RolePhaseMap.cs:80-92` → `SeniorDeveloper`'s set is **`:84-98`**;
> `WorkflowInterfaceGraphTests.cs:45 HaveCount(16)` → **`:52 HaveCount(18)`**.
> `Review.cs:153-161` (`ReviewSubject`) and `TriageDecision.cs:146`/`:149` still resolve exactly.

---

## User Story

As an **engineer / senior developer**, I want first-class code review of an arbitrary diff and a routed
PR-triage queue — independent of the mentorship engine — so that any PR (human- or agent-authored,
inside or outside the autonomous loop) gets a typed `Review` and open PRs are prioritised and assigned,
instead of code review only existing bolted into `code-review`/`mentorship`.

## The split (Amendment A1)

> **Amendment (2026-08-01).** The story previously said "**The PR-triage half is not Wave 1** … Split
> the story on that line" but then carried a single set of ACs, one Dependencies list and one estimate.
> That is not a split — a scheduler cannot act on it. It is now split for real.

**41-17A — `diff-review`.** Everything in this document tagged **[A]**. No new taxonomy cell, no new
enabler, no platform work. Startable today. **≈4–4.5 days.**

**41-17B — `pr-triage-sweep`.** Everything tagged **[B]**. Carries its own budget, its own blocker and
its own git-platform deliverable. **≈4 days**, and it may not start before its gate clears.

The two halves share only the `.md` file they were drafted in. They share **no** code file except
`DocumentTypeRegistry.BuildSeed` + its two test pins, which the epic already treats as a serialized,
one-story-at-a-time edit.

## Priority

P0 / Wave 1 for **41-17A** — it needs no new cell and no new enabler, and code review is the most
universal team activity. A `code-review` workflow does exist, but it is the mentorship engine's PR
*lifecycle* sub-workflow (Story 7-1D, `CodeReviewWorkflow.cs:18-20`), not a `document-lifecycle`
binding — it stores no typed `Review` (824 lines, zero `document-lifecycle` / `DocumentType`
references; verified 2026-08-01: its only two `DispatchWorkflow`s target `llm-call`).

**41-17B is not Wave 1.** Its remaining blocker is the tenant-aware scheduled-trigger seam
(now owned by **41-30**), plus the git-platform surface in Amendment A6 — which 41-17B owns itself.

## Scope

Two thin bindings, now two stories:

- **[A] Code review:** new `DefinitionId = "diff-review"`, `consumes: [diff/PR, Plan?, AcceptanceCriteria?]` /
  `produces: Review`. Produce cell `(senior_developer, code-review)` (developer/security/tester lenses
  available via panel policy). Subject is a diff — *code is not a document type* (Epic 39), so the review
  subject is a `ReviewSubject { kind = "diff", repository, prNumber|commitSha }` (`Review.cs:153-161`),
  not a stored code doc.
- **[B] PR triage:** scheduled sweep of open PRs → new `DefinitionId = "pr-triage-sweep"`,
  `produces: TriageDecision` per PR (priority, staleness, needs-review/needs-author, suggested reviewer
  role). Produce cell `(senior_developer, triage-pr)` — **landed** (Amendment A5).

> **Amendment (2026-08-01) — [A] the diff is an INPUT, and on GitHub nothing in this tree can produce
> it.** The story never said where `diff-review`'s diff comes from. The binding takes it as an input
> (`diffText`/`diffRef`), which is fine for a caller that already has one (the autonomous loop, a
> webhook payload, a CLI invocation). But a caller holding only `(repository, prNumber)` cannot fetch
> one on GitHub: `GitHubPlatformClient.ListPullRequestFilesAsync` is a stub returning
> `ServiceUnavailable` (`:162-171`), and there is no Elsa activity for it in any case. GitLab
> (`GitLabPlatformClient.cs:261`) and Gitea/Forgejo (`GiteaPlatformClient.cs:180`) do implement it.
> This is **recorded, not fixed here** — 41-17A ships a binding whose caller supplies the diff, and
> AC1 tests the subject/validation contract, not diff acquisition.

### Disposition of the incumbent `code-review` workflow

> **Decision — the incumbent keeps its id; the new bindings take new ids.** `CodeReviewWorkflow.cs:56`
> owns `DefinitionId = "code-review"` and is dispatched from **two live sites** with mutually
> incompatible input shapes:
>
> | Site | Wait | Input passed | Read by `CodeReviewWorkflow` |
> |---|---|---|---|
> | `SingleIssueCycleWorkflow.cs:601` | `WaitForCompletion = false` (fire & forget) | `repository`, `prNumber`, `branchName`, `conventions`, `tenantId` | only `branchName` + `tenantId` — it reads `RepositoryUrl`/`repositoryUrl`, never `repository`, `prNumber` or `conventions` (`:114-128`) |
> | `MentorshipWorkflow.cs:402` | `WaitForCompletion = true` | `SessionId`, `StoryId`, `JuniorId` | all three |
>
> Rebinding `code-review` in place would silently rewire both callers, so it is rejected. **This story
> renames nothing and rewires no caller**; retiring `CodeReviewWorkflow` is out of scope.
>
> **Pre-existing defect, not introduced here:** the `SingleIssueCycleWorkflow` site already runs with an
> empty `StoryId`/`JuniorId`/`RepositoryUrl` because of the input-shape mismatch above. Record it; do not
> fix it under this story. *(Re-verified 2026-08-01: still true.)*
>
> **The provisional registry edge is reconciled.** ~~`DocumentTypeRegistry.cs:158`~~ **`:172`** declares
> `("code-review", produces Review, Provisional = true)` — an unreconciled 39-1 seed guess, since
> `CodeReviewWorkflow` produces no document. **[A]** replaces it with a non-provisional
> `("diff-review", produces Review)` row; **[B]** adds a new `("pr-triage-sweep", produces
> TriageDecision)` row.

### The `(senior_developer, code-review)` cell is currently allowlisted, not bound

> **Found while verifying this story — a second CI-enforced collision, on the cell rather than the id.**
> ~~`ContractBindingTests.cs:293-295`~~ **`:375-377`** lists `(senior_developer, code-review)` in
> `IntentionallyUnbound` ("CodeReviewWorkflow.StoreAnalysis keeps the raw response text (analysisText)
> and feeds it into the mentor-feedback call — no structured slice"), and
> ~~`:713-720`~~ **`:1043-1051`** fails the build if a pair is in **both** `Bindings` and
> `IntentionallyUnbound`. So this story cannot simply add a `Bindings` entry.
>
> **Decision — bind the cell.** `(senior_developer, code-review)` is the correct produce cell for
> `diff-review`; no second cell is minted for the review half.
>
> > **Amendment (2026-08-01) — the second half of this decision was wrong and is replaced.** The
> > story went on to say: *"the consequence is owned here, not deferred: `Prompts/senior_developer/code-review.md`
> > is rewritten to emit the canonical `Review` wire, and `CodeReviewWorkflow`'s `AnalyzeChanges` →
> > `StoreAnalysis` path (`CodeReviewWorkflow.cs:274-305`) renders that validated `Review` to text
> > before feeding `mentor-feedback` (`:327`) … Mentorship guidance keeps reading prose; it just stops
> > reading unvalidated prose."* **Both clauses are false against the tree** — see § Prompt-rewrite
> > decision. The replacement decision is: rewrite the template **and take the incumbent off the cell**,
> > so the rewritten template is only ever rendered for a caller that can fill it.

### Prompt-rewrite decision (Amendment A4) — **decided, not deferred**

> **Amendment (2026-08-01).** This is the one correction that changes what gets built. It is stated
> here in full because the implementation plan's D5 must not be followed as written.

**What the tree actually does.** Verified 2026-08-01:

1. `Prompts/senior_developer/code-review.md:2` declares `variables: role, prDescription, diff, conventions`
   and its body renders `## Diff\n{{diff}}`.
2. `CodeReviewWorkflow.cs:286-296` — the only production dispatch of the cell — supplies exactly one
   variable, `reviewCommentsJson`, which the template **does not declare**. The inline comment at
   `:289` claims *"The template renders `{{reviewCommentsJson}}`"*. It does not; the token appears
   nowhere in the file.
3. `LlmCallWorkflow.cs:146-158` back-fills only `role` and `conventions`. `diff` and `prDescription`
   are never supplied by anyone.
4. `PromptStoreService.Render` (`:568-583`) leaves an unsupplied placeholder as the **literal token**
   and records it as unresolved. Unresolved is **non-fatal** — it is only counted into an emitted
   event (`:653`) and surfaced on a DTO (`PromptEndpoints.cs:448`). Nothing fails, nothing retries.
   The codebase states the rule itself, in `ReviewProducerHelper.cs:196-201`: *"a supplied-but-undeclared
   variable is silently dropped at render, so we only ever write into a declared one."*
5. `CodeReviewWorkflow` **has no diff at all** (variables, `:70-96`). `reviewCommentsJson` holds the
   **human reviewer's comments** lifted from `MonitorReviewActivity` (`:242-254`).

**So the model at that site is asked to review a pull request whose diff is the literal string
`{{diff}}`.** Today it answers with unvalidated prose that `DeliverGuidanceActivity` shows a junior
alongside the real human comments (`:354-355`). After the rewrite the *same blind call* would return a
schema-valid `Review` — hallucinated issues with file paths, line numbers, severities and suggested
fixes — which the plan's `ReviewProse.Render` would format into confident structured mentoring. That is
a strict regression: it upgrades noise into authoritative-looking fabrication, and nothing in the
proposed AC set can detect it.

**Second finding, same class, that kills the proposed mitigation.** `Prompts/senior_developer/mentor-feedback.md:2`
declares `variables: role, prDescription, diff, conventions` — it does **not** declare `analysis`.
`CodeReviewWorkflow.cs:326-331` supplies `analysis` and `skillLevel`; both are dropped at render (rule 4
above). **The mentorship path does not read the analysis text at all today.** The plan's D5 render seam
(`ReviewProse.Render(...) → analysisText → {{analysis}}`) therefore writes into a variable nothing reads,
and AC6's clause *"a test asserts the mentorship path never receives raw JSON"* pins a data flow that
does not exist. (`mentor-feedback.md` is also, verbatim, a code-review JSON template — not a mentoring
template. Same defect, same file family.)

**DECISION — take the incumbent off the cell; do not degrade knowingly, and do not build the render seam.**

41-17A does three things instead of the plan's D5:

- **(i)** Rewrite `Prompts/senior_developer/code-review.md` to the canonical `Review` wire, as
  originally planned. It becomes the `diff-review` produce template and nothing else.
- **(ii)** Repoint `CodeReviewWorkflow.AnalyzeChanges` (`:276-297`) from `(senior_developer, code-review)`
  to **`(senior_developer, summarize-technical)`**, supplying that template's **declared** variables —
  `workItemJson` (the PR identity the workflow already holds), `findings` (`reviewCommentsJson`, the
  human comments it actually has) and `audience` (the junior's skill level as prose).
  `summarize-technical.md:2` declares `role, workItemJson, findings, audience`; the action is in
  `SeniorDeveloper`'s eligible set (`RolePhaseMap.cs:93`); it is already classified free-text in
  `TemplateExampleConformanceTests.cs:438`. **No taxonomy cell is minted** — the 96-member pins
  (`AgentActionTests.cs:42`, `RolePhaseMapTests.cs:74`) do not move.
- **(iii)** Delete `ReviewProse` and the `analysis`-render seam from scope entirely.

**Why this and not the alternatives.**

- *"Fix the incumbent's variables" in the strong sense* — supply a real diff — was costed and rejected:
  there is no diff source reachable from that workflow. `GitHubPlatformClient.ListPullRequestFilesAsync`
  returns `ServiceUnavailable` (`:162-171`), **no Elsa activity consumes `IGitPlatformClient`** (verified:
  zero references outside `Tamma.Platforms*` and its tests), and `Tamma.Activities.csproj:40-48`
  references neither `Tamma.Platforms` nor `Tamma.Platforms.Abstractions` directly. That is 41-17B-sized
  work inside a story that is supposed to touch three lines of the incumbent.
- *"Record a knowingly-degraded path"* — rejected. The degradation is not neutral: it converts an
  ungrounded prose blob into an ungrounded **validated document** rendered as mentoring. A story whose
  whole point is "typed, validated review" must not ship a producer whose only live caller feeds it
  nothing.
- *Repointing is not the rewiring D1 forbids.* D1 protects `code-review`'s **DefinitionId, graph shape,
  inputs and its two external callers** — all untouched. Changing one internal `llm-call` dispatch's
  `role`/`action`/`variables` dictionary is the same edit class the plan already accepted for
  `StoreAnalysis`, and it is the house precedent: `TemplateExampleConformanceTests.cs:343-345` records
  that 39-15 D5 split `(developer, triage-context-scan)` off *"precisely so a document producer never
  shares a cell with a free-text scan."* This is that situation, exactly.
- *The junior loses nothing.* `DeliverGuidanceActivity` already receives `ReviewCommentsJson` verbatim
  (`CodeReviewWorkflow.cs:354`), so the real human review comments reach the junior regardless of what
  the LLM leg produces.

**Left open, deliberately, with an owner:** the `mentor-feedback` undeclared-variable defect (finding 2)
is a pre-existing bug in a workflow this story does not own, and fixing it means deciding what
`mentor-feedback.md` should even say (it is currently a code-review JSON template). **File it in
`.dev/bugs/` and do not fix it under 41-17.** It is named here so no future reader believes 41-17A left
the mentorship path healthy — 41-17A leaves it exactly as broken as it found it, minus one blind
LLM call.

## Produced documents

**[A]** `Review` (per diff). **[B]** `TriageDecision` (per open PR, with closed-enum classification +
reasoning).

## Events

> **Amendment (2026-08-01) — the `CODE_REVIEW.*` family is taken.** The story proposed
> `CODE_REVIEW.STARTED`/`.VERDICT`. `Tamma.Activities/Review/CodeReviewEvents.cs` already owns
> `CODE_REVIEW.*` for the incumbent PR-lifecycle workflow, with a pinned `StatusForEvent` switch;
> adding members would make one family mean two aggregates on one stream. **[A]** mints
> `DIFF_REVIEW.STARTED`/`.VERDICT`/`.FAILED` in a new `DiffReviewEvents.cs` instead.

**[A]** `DIFF_REVIEW.STARTED`/`.VERDICT`/`.FAILED`; **[B]** `PR_TRIAGE.SWEEP.STARTED`/`.ITEM`/`.COMPLETED`
— alongside the generic `DOCUMENT.*` family the lifecycle emits, tagged `prId`/`repository`/`tenantId`.

## Orchestrator / user interaction

Review verdict + each PR-triage decision route through the accept gate; the orchestrator assigns a
reviewer/author-follow-up to the appropriate tenant role's Task View, or self-decides at high autonomy.

## Autonomy behavior

- **70–84:** agent drafts the review/triage; a human reviewer signs off.
- **85–94:** agent review accepted for non-blocking verdicts; blocking issues escalate.
- **95–100:** agent review self-accepted; PR-triage assignments made automatically within the eligible set.

## Acceptance Criteria

> **Amendment (2026-08-01) — numbering is deliberately preserved across the split.** AC1–AC7 keep the
> numbers they had, so the implementation plan's Definition-of-Done table and every existing
> cross-reference still resolve; they are sorted into the two stories below, and new criteria take
> suffixed numbers (4b, 6b, 7b) or the next free number (8). ACs 1, 2, 5, 6, 6b, 7, 8 belong to
> **41-17A**; ACs 3, 4, 4b, 7b belong to **41-17B**.

### Acceptance Criteria — 41-17A

1. **[A]** The `diff-review` binding produces a validated `Review` whose `subject.kind = "diff"` carries
   `repository` + `prNumber`|`commitSha`; a subject missing those ⇒ `SUBJECT_INCOMPLETE`, an unknown kind
   ⇒ `SUBJECT_UNKNOWN_KIND`, an issue with no suggested fix ⇒ `ISSUE_MISSING_FIX`
   (~~`ReviewDocumentType.cs:17-32`~~ **`:16-38`** — the constants are at `:20`, `:23`, `:32`).
2. **[A]** A `decision = approve` body carrying any critical-severity issue is rejected with
   `APPROVE_WITH_BLOCKING_ISSUES` (`ReviewDocumentType.cs:35`, rule at `:91-100`) — the fixture pair is
   `valid-request-changes-with-blocking-issue` / `invalid-approve-with-blocking-issue`
   (~~`:185`, `:211`~~ **`:188`, `:214`**; the code assertion is at **`:226`**).
   No downgrade-to-concerns path exists (the `PlanReviewWorkflow.ExtractReview` anti-pattern stays dead).
5. **[A]** Reviewer-role selection comes from the acceptance/review rules: an integration test that
   changes the configured reviewer role changes the dispatched `(role, action)` pair, with no role
   literal in the workflow graph.
6. **[A] — REPLACED (Amendment A4).**
   > **Was:** *"`(senior_developer, code-review)` moves from `IntentionallyUnbound`
   > (`ContractBindingTests.cs:293-295`) into `Bindings` with authority `ReviewDocumentType.Validate`,
   > and the both-classified contradiction guard (`:713-720`) stays green. `CodeReviewWorkflow`'s
   > mentor-feedback input is the rendered `Review` text — a test asserts the mentorship path never
   > receives raw JSON."* The first sentence is right (with corrected line numbers). The second pins a
   > data flow that does not exist — `mentor-feedback.md:2` does not declare `analysis`, so the value
   > is dropped at render and no test could distinguish "rendered prose" from "raw JSON" from "nothing".

   **Now:** `(senior_developer, code-review)` moves out of `IntentionallyUnbound`
   (`ContractBindingTests.cs:375-377`) into `Bindings` with authority `ReviewDocumentType.Validate`,
   discovered via the lifecycle-binding walk (`TaxonomyDriftBuildTests.EnumerateAllDispatchPairs()`,
   `:441-451`, concatenates `ScanLifecycleBindingDispatches()`), and the contradiction guard
   (`:1043-1051`), the staleness guard (`:1053-1067`) and
   `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` (`:957`) all stay
   green. **And:** `CodeReviewWorkflow` no longer dispatches `(senior_developer, code-review)` — a test
   asserts its `AnalyzeChanges` dispatch materialises `(senior_developer, summarize-technical)`, with a
   new `IntentionallyUnbound` entry justifying that free-text pair.
6b. **[A] — NEW (Amendment A4).** Every variable key `CodeReviewWorkflow`'s two `llm-call` dispatches
   supply is **declared** by the target template's front matter. The test reads the shipped template via
   `SystemPrompts.GetRoleAction(role, action)` and asserts the supplied key set is a subset of its
   declared `variables`. This AC **fails today on both dispatches** (`code-review` is handed the
   undeclared `reviewCommentsJson`; `mentor-feedback` the undeclared `analysis`/`skillLevel`) — which is
   the point of writing it. 41-17A fixes the first; the second is carved out by an explicit,
   bug-referenced exclusion in the test so the AC can still go green while the pre-existing
   `mentor-feedback` defect stays visible and filed. *(Pattern already in the tree:
   `AcceptanceCriteriaAuthoringWorkflowStructureTests.cs:92-95`.)*
7. **[A] — CORRECTED (Amendments A2, A3).**
   > **Was:** *"Both declare resume behavior (review `Both`; sweep `LatestStateReEntry`) and pass 39-10
   > without an allowlist entry. `DocumentTypeRegistry.cs:158` is reconciled as above and
   > `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is bumped in the same change."*
   >
   > **Clause 1 was unpassable.** `ResumeBehaviorAttribute` requires `SuspendActivities` to be non-empty
   > for `Both` (`ResumeBehavior.cs:33-35`), and `ResumableStandardStructuralTests.EveryBookmarkSuspendWorkflow_HasACanonicalSuspendNode`
   > (`:158-197`) fails a `Both` declaration that either names no suspend activity, names a non-canonical
   > one, or names one absent from the built graph. A thin binding has no suspend node — the accept gate
   > suspends inside the dispatched `document-lifecycle` child, which the parent awaits with
   > `WaitForCompletion = true`. Every landed thin binding declares `LatestStateReEntry`
   > (`TaskCreationWorkflow.cs:47`, `ResearchWorkflow.cs:35`, `IssueDecompositionWorkflow.cs:70`,
   > `PlanGenerationWorkflow.cs:54`, `DesignProposalWorkflow.cs:39`, and eleven more); the only two
   > `Both` declarations in the tree are `DocumentLifecycleWorkflow.cs:54` and
   > `ClarifyingQuestionsWorkflow.cs:40`, and each names a real canonical suspend activity. Epic 41
   > README rule 5 already states this rule.
   >
   > **Clause 2 was wrong for the review half.** 41-17A **replaces** the provisional `code-review` row
   > rather than adding a row, so the edge count does not move. The pin is also no longer 16.

   **Now (41-17A):** `diff-review` declares `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, carries
   exactly one `ComputeReEntryPositionActivity`, contains no `Wait*` activity and no canonical suspend
   node, and passes `ResumableStandardStructuralTests` with **no** `LegacyResumeAllowlist` entry.
   `DocumentTypeRegistry.cs:172`'s provisional `("code-review" → review)` row is **replaced** by a
   non-provisional `("diff-review" → review)` row, so
   `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:52`) **stays at `HaveCount(18)`** and
   is edited only to record the swap in its comment. `"diff-review"` **is added** to the `reconciled`
   array in `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:110-137`) — that array is
   bidirectional, so a non-provisional row omitted from it fails the build.
8. **[A] — NEW.** `senior_developer/code-review.md` moves out of
   `TemplateExampleConformanceTests.IntentionallyUnboundCells` (`:397` — "diff-review lens: legacy
   code-review issue wire, raw text kept by CodeReviewWorkflow.StoreAnalysis", a claim that stops being
   true) into `ConformingUnboundCells`/the bound set as a `"review"` producer, and its worked example
   validates against `ReviewDocumentType` with zero violations. The table's own doc states this
   requirement at `:334-337`.

### Acceptance Criteria — 41-17B

3. **[B]** `TriageDecision` classification is closed-enum: an out-of-vocabulary priority/type/complexity/automation
   value ⇒ `OUT_OF_VOCABULARY`, a classification with no reasoning ⇒ `REASONING_REQUIRED`
   (`TriageDecision.cs:146`, `:149`).
4. **[B]** The PR-triage sweep is tenant-scoped, fires at most once per window per tenant across a restart
   (the fired window is persisted, not held in memory), and is fail-closed per item: a failed PR emits
   `PR_TRIAGE.SWEEP.ITEM` with the failure and the sweep continues — an integration test kills the process
   mid-sweep and asserts no PR is double-triaged and none is dropped.
   *(Unreachable until 41-30 lands the trigger seam; the per-item half is provable without it.)*
4b. **[B] — NEW (Amendment A6).** Listing a repository's open PRs is a first-class platform capability:
   `IGitPlatformClient` gains a `ListOpenPullRequestsAsync` (paged, `PlatformResult`-returning, same
   error posture as the rest of the surface), implemented by **all four** clients —
   `GitHubPlatformClient`, `GitLabPlatformClient`, `GiteaPlatformClient` (which backs both Gitea and
   Forgejo) and `NullGitPlatformDriver`'s inner client — and reachable from a workflow through a new
   Elsa activity behind a `Tamma.Activities`-side seam interface (the `IGitHubActionsClient` /
   `OctokitGitHubActionsClient` pattern; `Tamma.Activities.csproj:40-48` references no platform project,
   so the activity may not depend on `Tamma.Platforms` directly). The three contract suites that
   enumerate the surface are updated in the same change: `GiteaPlatformClientTests`,
   `ForgejoContractTests` (its doc-comment count 12 → 13, `:16-18`) and `GiteaIntegrationTests`
   (17 → 18 methods, `:19-27`). A driver that cannot list PRs returns `capability_unsupported` rather
   than an empty list — an empty list must mean "no open PRs", or the sweep silently no-ops.
7b. **[B]** `pr-triage-sweep` declares `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` and passes
   39-10 without an allowlist entry; its `("pr-triage-sweep" → triage-decision)` row is **added** to
   `DocumentTypeRegistry.BuildSeed` and to the `reconciled` array, and
   `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is bumped **18 → 19** in the same change
   (Epic 41 rule 1 clause (f) — one conscious bump per new producing workflow).

> Review *insightfulness* and triage *judgement* are not acceptance criteria — no deterministic check
> exists. Schema, closed enums, the approve/blocking invariant, the sweep's durability, and (new) the
> supplied-vs-declared variable contract are.

## Dependencies

**41-17A**
- **Blocking:** Epic 39 (`Review`, `document-lifecycle`, `document-review`, acceptance rules, 39-10
  resume standard, 39-11 store) — all landed and re-verified 2026-08-01. Nothing else.
- **Related:** reuses 39-7's panel and its already-shipped diff roster —
  `ReviewerSelectionHelper.DiffSubjectKind = "diff"` (`:33`), `s_diffRoster` (`:83`),
  `DiffReviewAction` (`:44`), `AllDispatchablePairs` (`:189`). The story's "developer/security/tester
  lenses available via panel policy" is therefore **already true at the helper level**; 41-17A adds the
  producing workflow, not the lens map. Note the pin is now
  `ReviewerSelectionHelper_AllDispatchablePairs_HasEighteenEligiblePairs` (`ContractBindingTests.cs:685`,
  9 document + 5 diff + 4 triage-panel = 18) and 41-17A **does not move it**.
- Leaves `CodeReviewWorkflow` (`code-review`), its `DefinitionId`, its graph and its two dispatch sites
  untouched — the single exception being the one internal `llm-call` cell repoint of Amendment A4.

**41-17B**
- ~~**41-1a** — the `(senior_developer, triage-pr)` cell. *It does not exist today — no `triage-pr` wire
  in `AgentAction.cs`, and it is not in `SeniorDeveloper`'s eligible set (`RolePhaseMap.cs:80-92`).*~~
  > **Amendment (2026-08-01) — LANDED; this blocker is discharged.** `AgentAction.cs:66`
  > (`[Wire("triage-pr")] TriagePr`, comment "Story 41-1a — 41-17's PR-triage cell"),
  > `RolePhaseMap.cs:98` (in `SeniorDeveloper`'s set, same comment),
  > `Prompts/senior_developer/triage-pr.md` ships, and
  > `TemplateExampleConformanceTests.ConformingUnboundCells` already declares
  > `("senior_developer", "triage-pr") → "triage-decision"` (`:312`) with the note "41-11 / 41-17 /
  > 41-16 bind them" — i.e. the template already instructs the `TriageDecision` wire and its worked
  > example validates. The taxonomy count pins are at **96** (`AgentActionTests.cs:42`,
  > `RolePhaseMapTests.cs:74`), not the 80 the plan cites.
- **The tenant-aware scheduled-trigger seam — owned by 41-30, not yet built.** *(Story text retained:
  Scope originally cited the "`HourlyAnalyticsRollupScheduler` pattern" as if reusable, and Dependencies
  omitted the seam entirely. That scheduler is hardcoded to one workflow
  (`HourlyAnalyticsRollupScheduler.cs:199-200`), has one `FireAtMinute` int rather than a window/cron
  shape (`:35`), threads no `tenantId` into the dispatch, keeps its last-fired window in a per-process
  field (`_lastFired`, `:84`), and its advisory-lock key has no tenant component
  (`ComputeAdvisoryLockKey(year, dayOfYear, hour)`, `:242`) — one tenant's leader would suppress every
  other tenant's fire for that hour. All re-verified 2026-08-01.)*
  > **Amendment (2026-08-01) — a third scheduler exists and is also not reusable.** The plan says
  > "there are **two** schedulers in the tree". There are three: `HourlyAnalyticsRollupScheduler`,
  > `SecretAutoRotationScheduler`, and `Tamma.Api/Services/Audit/AuditChainCheckpointScheduler.cs`.
  > None is tenant-partitioned or multi-target. The conclusion is unchanged; the count was wrong.
- **The git-platform open-PR surface (AC4b)** — 41-17B's own deliverable, not someone else's. See
  Amendment A6.
- Epic 39 (`TriageDecision`, lifecycle, store) — landed.

## Estimated Effort

> **Amendment (2026-08-01) — was "5–6 days (≈3 for the code-review half, ≈2–3 for the PR-triage half
> once its two blockers clear)". Both figures were low and the unit was wrong.**

| Story | Work | Days |
|---|---|---|
| **41-17A** | binding + helper + events + prompt rewrite + cell repoint + 5 drift-guard migrations + structure/helper/fixture tests + Testcontainers scenarios | **4–4.5** |
| **41-17B** | `IGitPlatformClient.ListOpenPullRequestsAsync` × 4 drivers + activity seam + 3 contract suites (Amendment A6) | 1.25–1.5 |
| **41-17B** | events + helper + sweep binding + per-PR loop + registry/edge-pin/contract migrations + durability tests | 2.5 |
| **41-17B total** | *(after 41-30 lands)* | **≈4** |

41-17A moved up from ≈3 for two reasons: the prompt/cell work of Amendment A4 is a real half-day with
its own regression surface, and the drift-guard migration is five interlocking tables
(`ContractBindingTests.Bindings` + `IntentionallyUnbound`, `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`,
`DocumentTypeRegistry.BuildSeed`, `WorkflowInterfaceGraphTests` × 2 tests,
`TemplateExampleConformanceTests` × 2 tables), not one.
