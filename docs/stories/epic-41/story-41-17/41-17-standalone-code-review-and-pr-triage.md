# Story 41-17: Standalone Code Review & PR Triage Workflow

Status: drafted

## User Story

As an **engineer / senior developer**, I want first-class code review of an arbitrary diff and a routed
PR-triage queue — independent of the mentorship engine — so that any PR (human- or agent-authored,
inside or outside the autonomous loop) gets a typed `Review` and open PRs are prioritised and assigned,
instead of code review only existing bolted into `code-review`/`mentorship`.

## Priority

P0 / Wave 1 for the **code-review half** — it needs no new cell and no new enabler, and code review is the
most universal team activity. A `code-review` workflow does exist, but it is the mentorship engine's PR
*lifecycle* sub-workflow (Story 7-1D, `CodeReviewWorkflow.cs:18-20`), not a `document-lifecycle` binding —
it stores no typed `Review` (824 lines, zero `document-lifecycle` / `DocumentType` references).

The **PR-triage half is not Wave 1**: it needs the `(senior_developer, triage-pr)` cell from 41-1a and the
tenant-aware scheduled-trigger seam, neither of which exists. Split the story on that line.

## Scope

Two thin bindings sharing this story:

- **Code review:** new `DefinitionId = "diff-review"`, `consumes: [diff/PR, Plan?, AcceptanceCriteria?]` /
  `produces: Review`. Produce cell `(senior_developer, code-review)` (developer/security/tester lenses
  available via panel policy). Subject is a diff — *code is not a document type* (Epic 39), so the review
  subject is a `ReviewSubject { kind = "diff", repository, prNumber|commitSha }` (`Review.cs:153-161`),
  not a stored code doc.
- **PR triage:** scheduled sweep of open PRs → new `DefinitionId = "pr-triage-sweep"`,
  `produces: TriageDecision` per PR (priority, staleness, needs-review/needs-author, suggested reviewer
  role). Produce cell `(senior_developer, triage-pr)` (**41-1a**).

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
> renames nothing and rewires nothing**; retiring `CodeReviewWorkflow` is out of scope.
>
> **Pre-existing defect, not introduced here:** the `SingleIssueCycleWorkflow` site already runs with an
> empty `StoryId`/`JuniorId`/`RepositoryUrl` because of the input-shape mismatch above. Record it; do not
> fix it under this story.
>
> **The provisional registry edge is reconciled.** `DocumentTypeRegistry.cs:158` declares
> `("code-review", produces Review, Provisional = true)` — an unreconciled 39-1 seed guess, since
> `CodeReviewWorkflow` produces no document. It is replaced by a non-provisional `("diff-review",
> produces Review)` row, plus a new `("pr-triage-sweep", produces TriageDecision)` row.

### The `(senior_developer, code-review)` cell is currently allowlisted, not bound

> **Found while verifying this story — a second CI-enforced collision, on the cell rather than the id.**
> `ContractBindingTests.cs:293-295` lists `(senior_developer, code-review)` in `IntentionallyUnbound`
> ("CodeReviewWorkflow.StoreAnalysis keeps the raw response text (analysisText) and feeds it into the
> mentor-feedback call — no structured slice"), and `:713-720` fails the build if a pair is in **both**
> `Bindings` and `IntentionallyUnbound`. So this story cannot simply add a `Bindings` entry.
>
> **Decision — bind the cell and retire the allowlist entry.** `(senior_developer, code-review)` is the
> correct produce cell (it *is* senior-developer code review); no second cell is minted. The consequence
> is owned here, not deferred: `Prompts/senior_developer/code-review.md` is rewritten to emit the
> canonical `Review` wire, and `CodeReviewWorkflow`'s `AnalyzeChanges` → `StoreAnalysis` path
> (`CodeReviewWorkflow.cs:274-305`) renders that validated `Review` to text before feeding
> `mentor-feedback` (`:327`), instead of posting the raw response verbatim. Mentorship guidance keeps
> reading prose; it just stops reading unvalidated prose.

## Produced documents

`Review` (per diff) and `TriageDecision` (per open PR, with closed-enum classification + reasoning).

## Events

`CODE_REVIEW.STARTED`/`.VERDICT`; `PR_TRIAGE.SWEEP.STARTED`/`.ITEM`/`.COMPLETED` alongside `DOCUMENT.*`,
tagged `prId`/`repository`.

## Orchestrator / user interaction

Review verdict + each PR-triage decision route through the accept gate; the orchestrator assigns a
reviewer/author-follow-up to the appropriate tenant role's Task View, or self-decides at high autonomy.

## Autonomy behavior

- **70–84:** agent drafts the review/triage; a human reviewer signs off.
- **85–94:** agent review accepted for non-blocking verdicts; blocking issues escalate.
- **95–100:** agent review self-accepted; PR-triage assignments made automatically within the eligible set.

## Acceptance Criteria

1. The `diff-review` binding produces a validated `Review` whose `subject.kind = "diff"` carries
   `repository` + `prNumber`|`commitSha`; a subject missing those ⇒ `SUBJECT_INCOMPLETE`, an unknown kind
   ⇒ `SUBJECT_UNKNOWN_KIND`, an issue with no suggested fix ⇒ `ISSUE_MISSING_FIX`
   (`ReviewDocumentType.cs:17-32`).
2. A `decision = approve` body carrying any critical-severity issue is rejected with
   `APPROVE_WITH_BLOCKING_ISSUES` (`ReviewDocumentType.cs:35`, `:88-97`) — the fixture pair is
   `valid-request-changes-with-blocking-issue` / `invalid-approve-with-blocking-issue` (`:185`, `:211`).
   No downgrade-to-concerns path exists (the `PlanReviewWorkflow.ExtractReview` anti-pattern stays dead).
3. `TriageDecision` classification is closed-enum: an out-of-vocabulary priority/type/complexity/automation
   value ⇒ `OUT_OF_VOCABULARY`, a classification with no reasoning ⇒ `REASONING_REQUIRED`
   (`TriageDecision.cs:146-149`).
4. The PR-triage sweep is tenant-scoped, fires at most once per window per tenant across a restart (the
   fired window is persisted, not held in memory), and is fail-closed per item: a failed PR emits
   `PR_TRIAGE.SWEEP.ITEM` with the failure and the sweep continues — an integration test kills the process
   mid-sweep and asserts no PR is double-triaged and none is dropped.
5. Reviewer-role selection comes from the acceptance/review rules: an integration test that changes the
   configured reviewer role changes the dispatched `(role, action)` pair, with no role literal in the
   workflow graph.
6. `(senior_developer, code-review)` moves from `IntentionallyUnbound` (`ContractBindingTests.cs:293-295`)
   into `Bindings` with authority `ReviewDocumentType.Validate`, and the both-classified contradiction
   guard (`:713-720`) stays green. `CodeReviewWorkflow`'s mentor-feedback input is the rendered `Review`
   text — a test asserts the mentorship path never receives raw JSON.
7. Both declare resume behavior (review `Both`; sweep `LatestStateReEntry`) and pass 39-10 without an
   allowlist entry. `DocumentTypeRegistry.cs:158` is reconciled as above and
   `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is bumped in the same change.

> Review *insightfulness* and triage *judgement* are not acceptance criteria — no deterministic check
> exists. Schema, closed enums, the approve/blocking invariant and the sweep's durability are.

## Dependencies

- **Blocking (code-review half):** Epic 39 (`Review`, lifecycle, review producers, task routing).
- **Blocking (PR-triage half):**
  - **41-1a** — the `(senior_developer, triage-pr)` cell. *Corrected: this was named in Scope but absent
    from Dependencies. It does not exist today — no `triage-pr` wire in `AgentAction.cs`, and it is not in
    `SeniorDeveloper`'s eligible set (`RolePhaseMap.cs:80-92`).*
  - **The tenant-aware scheduled-trigger seam — now owned by 41-30** (cadence AC only; the producing
    half is buildable before it). *Corrected: Scope cited
    "`HourlyAnalyticsRollupScheduler` pattern" as if reusable, and Dependencies omitted it entirely. That
    scheduler is hardcoded to one workflow (`HourlyAnalyticsRollupScheduler.cs:198-199`), has one
    `FireAtMinute` int rather than a window/cron shape (`:34`), threads no `tenantId` into the dispatch
    (`:202-203`), keeps its last-fired window in a per-process field, and its advisory-lock key has no
    tenant component (`ComputeAdvisoryLockKey(year, dayOfYear, hour)`, `:241`) — one tenant's leader would
    suppress every other tenant's fire for that hour. AC4 is unreachable without the seam.*
  - Epic 39 (`TriageDecision`, lifecycle, store).
- **Related:** reuses 39-7 panel; complements `review-fix`. Leaves `CodeReviewWorkflow` (`code-review`) and
  its two dispatch sites untouched.

## Estimated Effort

5–6 days (≈3 for the code-review half, ≈2–3 for the PR-triage half once its two blockers clear)
