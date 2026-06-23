# Completeness Audit — `CodeReviewWorkflow` (`code-review`)

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/Review/*` (+ `Review/Models/ReviewModels.cs`)

---

## Purpose & owner

A reusable Elsa code-first sub-workflow (`DefinitionId = "code-review"`) that manages the **full
pull-request lifecycle for the mentorship engine**: create PR → request review → wait for a human
reviewer's decision (bookmark) → on approval merge, on changes-requested deliver teaching guidance →
wait for the junior to push fixes (bookmark) → re-request review (loop, max 5) → escalate to a senior
(bookmark) on timeout / max-iterations. Owner: **Epic 7 (Mentorship), Story 7-1D — Code Review
Sub-Workflow** (`docs/stories/epic-7/story-7-1D/7-1D-code-review-sub-workflow.md`).

---

## Maturity: `partial`

The workflow is **structurally rich** — a 16-node Flowchart with three bookmark-based wait states,
an iteration loop with a max guard, a timeout branch, an escalation branch with resolve/reject
outcomes, and eight purpose-built activities that log mentorship events and notify via Slack. It is
far more built-out than the user's `PullRequest` example. **But it is not `complete`**, and one
defect makes it effectively non-functional end-to-end:

- **It never binds its inputs.** `Build()` declares 11 workflow variables (`SessionId`, `StoryId`,
  `JuniorId`, …) and immediately consumes them, but there is **no step that reads
  `context.GetInput<T>(...)`** to populate them (contrast `AssessmentWorkflow.cs:85-89`,
  `MergeWorkflow.cs:29-32`, which bind inputs first). Every variable therefore stays at its hardcoded
  default (`Guid.Empty`, `""`, `0`). When dispatched, `CreatePRActivity` looks up a story by an empty
  id, gets `null`, returns `Success=false`, `PRCreatedCheck` is false, and the run terminates at
  `failedEnd`. So despite the elaborate graph, **a real invocation always fails at step 1.** This is a
  P0 correctness defect (silent false-path), not polish.

---

## Current capabilities (what it actually does today)

- **CreatePR** (`CreatePRActivity`): gathers GitHub file-changes + commits via `IIntegrationService`,
  builds a Markdown PR body, creates the PR with fixed labels `["mentorship","code-review"]`, logs a
  `CodeReviewPrepared` mentorship event. Returns `{Success, PRNumber, PRUrl}`.
- **StorePRResult / PRCreatedCheck**: stores PR number into a variable and branches on `> 0`; false →
  `failedEnd`.
- **RequestReview** (`RequestReviewActivity`): Slack-DMs the junior, logs `CodeReviewSubmitted`.
  Has a `Reviewers` input it parses — **but the workflow never sets it**, and it does **not** call any
  GitHub "request reviewers" API.
- **MonitorReview** (bookmark `review-{sessionId}-{prNumber}`, 24 h): suspends; on resume reads the
  webhook payload from `WorkflowInput`, maps status → outcome `Approved` / `ChangesRequested` /
  `TimedOut`. `Commented` self-loops back to `MonitorReview`. (No `Dismissed` handling — falls into the
  `_ => ChangesRequested` default.)
- **StoreReviewComments → IncrementIteration → DeliverGuidance**: on changes-requested, serializes
  comments, bumps the counter, and generates guidance.
- **DeliverGuidance** (`DeliverGuidanceActivity`): **keyword-matching heuristics** (string `.Contains`
  on "null check", "test", "naming", …) produce canned guidance per comment, formatted and Slack-DM'd.
  Logs `GuidanceProvided`. Its doc-comment claims "Uses Claude" but **no LLM call occurs.**
- **WaitForFixes** (bookmark `fixes-{sessionId}-{prNumber}-{iteration}`, 24 h): suspends; resume reads
  commit sha / files-changed; `FixesReceived` / `TimedOut`.
- **ReRequestReview** (`ReRequestReviewActivity`): Slack-DMs the junior, logs `CodeReviewUpdate`. Does
  **not** call any GitHub re-request-review API; max-iterations check is duplicated here AND in the
  workflow's `MaxIterationsCheck` decision.
- **MaxIterationsCheck**: `iteration >= max` → `EscalateReview`; else loop back to `MonitorReview`.
- **MergeAndComplete** (`MergeAndCompleteReviewActivity`): merges via `IIntegrationService` (strategy
  passed but the service "handles it internally" — squash/merge/rebase **not actually applied**),
  records analytics `pr_merged` / `review_iterations`, Slack-notifies, logs `CodeReviewApproved`.
- **EscalateReview** (bookmark `escalate-{sessionId}-{prNumber}`): logs `EscalationTriggered`,
  notifies junior + `senior-review` Slack channel, suspends; resume maps senior action →
  `Resolved` / `Rejected`.
- **Terminal outputs**: `successEnd` emits loose `success=true, prUrl, iterations`; `failedEnd` emits
  `success=false, errorMessage="Code review failed"`.

All activities inject `ILogger<T>` and log INFO/WARN/ERROR. Mentorship events are written to
`IMentorshipSessionRepository.LogEventAsync`.

---

## Intended full scope (with citations)

**Story 7-1D** (`docs/stories/epic-7/story-7-1D/7-1D-code-review-sub-workflow.md`) is the contract:

- **AC2 Input/Output**: inputs `sessionId, storyId, repositoryUrl, branchName, baseBranch, juniorId,
  reviewerIds[]`; output a structured `ReviewResult { status(Approved|Rejected|Escalated|Timeout),
  prNumber, prUrl, mergeCommit, reviewIterations, reviewComments[], guidanceFeedback[] }`.
  (`CodeReviewWorkflowResult` already exists in `ReviewModels.cs` but is **unused** — the workflow emits
  three loose `SetOutput`s instead.)
- **AC3 PR Creation**: title/body/labels including **skill-level + story labels**, optional **draft**
  PR, fault-with-context on failure.
- **AC4 Reviewer Assignment**: use `reviewerIds` if given, else select from a **configured reviewer
  pool**; team-based assignment; **min 1 reviewer**; notify reviewers. (Today: not wired at all.)
- **AC5 Monitoring**: handle `approved / changes_requested / commented / dismissed`; 24 h timeout →
  escalate. (Today: `dismissed` unhandled.)
- **AC7 Fix-guidance loop**: `AnalyzeChanges` = **LlmCall (role=reviewer)** "what needs fixing …",
  then `GenerateGuidance` = **LlmCall (role=analyst)** "explain to a Level {skillLevel} developer …",
  then deliver, then `WaitForFixes` (spec says **60 min**), re-request, loop ≤ 5. (Today: heuristic,
  no LLM, 24 h.)
- **AC8 Merge & Completion**: configurable strategy **actually applied**, **delete source branch**
  (default true), **verify CI passes before merge** (default true), **merge failure → retry once →
  escalate**. (Today: none of these — single merge attempt, no CI gate, no branch delete, strategy not
  honoured.)
- **AC9 Escalation**: include PR link + review history + guidance provided + reason. (Today: reason +
  iteration count only.)
- **AC10 Observability**: per-iteration log (reviewer/decision/duration), metrics
  `review.iterations.total`, `review.time_to_approve`, `review.escalations.total`. (Today: only
  `pr_merged` / `review_iterations` recorded, at merge.)
- **Config block** (`CodeReview:*` — MaxReviewIterations, ReviewTimeoutHours, FixTimeoutMinutes,
  MergeStrategy, DeleteBranchAfterMerge, VerifyCIBeforeMerge, NotificationChannel, ReviewerPool,
  AutoAssignReviewers). (Today: hardcoded; `IConfiguration` injected into `DeliverGuidance` but unread.)

**Agent-architecture pivot** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`):
- §5.2 step 3 lists `ApplyReviewFixes` and the **Mentorship direct-LLM fallbacks** among the callers
  to redirect; AC7's `AnalyzeChanges`/`GenerateGuidance` LLM calls MUST route through the tamma-api
  **`POST /api/v1/llm/call`** mediation (Story **32-5**), never an in-engine provider call.
- §5.1 Class A places the git operations this workflow performs through `IIntegrationService`
  (CreatePR / RequestReviewers / Merge / read PR comments) behind a future **Epic 38** git-mediation
  API. This is a "follow-up epic" — not a "now" blocker — but it is the correct end-state for the
  tenant-scoped, credential-holding git calls.

**Caller divergence (important):**
- `MentorshipWorkflow.cs:394` dispatches `code-review` with `{SessionId, StoryId, JuniorId}` — matches
  the 7-1D contract.
- `SingleIssueCycleWorkflow.cs:519` (the autonomous loop) dispatches `code-review` with a **completely
  different payload** `{repository, prNumber, branchName, conventions, tenantId}` and the comment
  *"LLM reviews the PR"*. The mentorship workflow reads **none** of those keys, so the autonomous-loop
  dispatch is silently a no-op/mis-fire. Either the autonomous loop needs its own LLM-review workflow,
  or `code-review` must accept both shapes. This contract ambiguity must be resolved (see P0 below).

---

## Missing capabilities

| # | Capability (gap to complete) | Priority | Depends on |
|---|------------------------------|----------|------------|
| 1 | **Bind workflow inputs.** Add an initial step reading `sessionId, storyId, juniorId, repositoryUrl, branchName, baseBranch, reviewerIds, maxIterations, mergeStrategy` via `context.GetInput<T>` into the variables. Without it the run always fails at CreatePR. | P0 | none |
| 2 | **Resolve the dual caller contract.** Decide: (a) a separate LLM-review workflow for the autonomous loop, or (b) make `code-review` accept the `{repository,prNumber,branchName,tenantId}` shape and branch on a `mode`/presence of `sessionId`. Stop silently dropping the `SingleIssueCycle` payload. | P0 | 32-5 (for the LLM-review variant) |
| 3 | **Validate inputs** (story id, repo url, junior id, ≥1 reviewer) → explicit fault/`failedEnd` with a specific reason, not the generic `"Code review failed"`. No silent false-success/false-path. | P0 | none |
| 4 | **AC7 LLM guidance.** Replace `DeliverGuidance`'s keyword heuristics with `AnalyzeChanges` (LlmCall role=reviewer) + `GenerateGuidance` (LlmCall role=analyst, skill-level aware), routed via `call-LLM` mediation. Keep delivery, drop the "Uses Claude" doc lie. | P0 (contract) | 32-5 mediation |
| 5 | **AC8 merge robustness.** Honour the merge strategy; **verify CI green before merge**; **retry merge once then escalate** on conflict/CI failure; **delete source branch** after merge (configurable). | P0 (safety) | Epic 38 (git mediation) for the real merge/CI calls; usable now via `IIntegrationService` |
| 6 | **Structured output.** Emit the existing `CodeReviewWorkflowResult` (status enum, prNumber, prUrl, mergeCommit, reviewIterations, reviewComments, guidanceFeedback) instead of three loose `SetOutput`s. | P1 | none |
| 7 | **AC4 reviewer assignment.** Auto-select from `ReviewerPool` when `reviewerIds` empty; enforce ≥1; actually call the git "request reviewers" API; notify reviewers (not only the junior). | P1 | Epic 38 (git mediation); pool config |
| 8 | **DCB audit events.** Emit DCB `AGGREGATE.ACTION.STATUS` events (`CODE_REVIEW.PR_CREATED.SUCCESS`, `CODE_REVIEW.GUIDANCE_DELIVERED.SUCCESS`, `CODE_REVIEW.ITERATION.STARTED`, `CODE_REVIEW.MERGED.SUCCESS`, `CODE_REVIEW.ESCALATED`, `CODE_REVIEW.FAILED`) — today only `MentorshipEvent` rows are written, not the DCB stream the platform's audit/time-travel relies on. | P1 | Epic 4 event store (present) |
| 9 | **AC8/config: configuration binding.** Bind `CodeReview:*` config (timeouts, strategy, pool, flags) instead of hardcoded `24h`/`Squash`/`5`. Fix spec drift: `WaitForFixes` should default to `FixTimeoutMinutes` (60 min), not 24 h. | P1 | none |
| 10 | **AC5 `Dismissed` outcome.** Add explicit `Dismissed` handling (re-request or escalate) instead of the `_ => ChangesRequested` default in `MonitorReviewActivity`. | P2 | none |
| 11 | **De-duplicate the max-iterations guard.** It lives both in `ReRequestReviewActivity` and the workflow's `MaxIterationsCheck`; the activity's `MaxIterationsReached` output is computed but unused by the graph. Make one authoritative. | P2 | none |
| 12 | **AC10 metrics.** Add `review.iterations.total`, `review.time_to_approve`, `review.escalations.total`; per-iteration log of reviewer/decision/duration. | P2 | none |
| 13 | **AC3 draft PR + skill/story labels.** Support a `draft` option and skill-level + story labels (today: fixed two labels, no draft). | P3 | none |
| 14 | **Git-call mediation.** Move `CreatePR`/`RequestReviewers`/`Merge`/`read-comments` off in-engine `IIntegrationService` onto the Epic 38 `POST /api/v1/git/...` mediation (tenant-scoped creds, cross-tenant guard, audit). | P3 | Epic 38 |

---

## Ordered build-out spec (to reach `complete`)

1. **Add input-binding head node** (`Inline`/`SetVariable` "Bind Inputs", first in the Flowchart, before
   `createPR`): set `sessionId`/`sessionIdGuid` from `GetInput<string|Guid>("sessionId")`, `storyId`,
   `juniorId`, plus new variables `repositoryUrl`, `branchName`, `baseBranch` (default "main"),
   `reviewerIdsJson`, and bind `maxIterations`/`mergeStrategy` from input with the existing defaults as
   fallback. **Fixes defect #1.**
2. **Add `ValidateInputs` decision** after binding: if `storyId`/`repositoryUrl`/`juniorId` empty or no
   resolvable reviewer → route to a new `validationFailedEnd` that emits a *specific* errorMessage.
   Honour tenant→system→error (no empty fallback).
3. **Resolve the caller contract (decision #2).** Recommended: keep `code-review` as the mentorship
   PR-lifecycle workflow keyed on `sessionId`; create/point the autonomous loop at a distinct
   **LLM-review** workflow (or add a `mode` branch). Update `SingleIssueCycleWorkflow.cs:519` payload or
   target accordingly. (Audit-only note — no code change here.)
4. **Wire reviewer assignment (AC4).** Before `requestReview`, resolve reviewers: parse `reviewerIdsJson`
   → else load `CodeReview:ReviewerPool` from config → assert ≥1. Pass into `RequestReviewActivity.Reviewers`
   and have it call the git "request reviewers" endpoint (`IIntegrationService` now / Epic 38 later) and
   notify them. Branch to `validationFailedEnd` if no reviewer resolvable.
5. **Replace heuristic guidance with mediated LLM (AC7).** Insert two new steps on the ChangesRequested
   edge between `storeReviewComments`/`incrementIteration` and `deliverGuidance`:
   - `AnalyzeChanges` → `DispatchWorkflow("llm-call")` role=`reviewer`, prompt "what needs fixing based
     on these review comments: {reviewCommentsJson}", `WaitForCompletion=true`.
   - `GenerateGuidance` → `DispatchWorkflow("llm-call")` role=`analyst`, prompt "explain to a Level
     {skillLevel} developer how to address: {analysis}".
   Both resolve provider/credential inside `call-LLM` (32-5) — the step passes no key, calls no provider.
   `DeliverGuidance` then only formats + delivers the LLM output (drop the keyword switch and the
   "Uses Claude" doc comment). On LLM failure → `escalateReview` (reason `Other`/new
   `GuidanceGenerationFailed`), never silently ship empty guidance.
6. **Harden merge (AC8).** Before `mergeAndComplete`, add `VerifyCIBeforeMerge` decision (config-gated,
   default true): poll/await CI status; not-green → `escalateTimeout`-style escalation (reason
   `CriticalIssue`). Make `MergeAndCompleteReviewActivity` actually apply the strategy and, on success,
   delete the source branch (config `DeleteBranchAfterMerge`). On merge failure: add a single retry edge,
   then route to `escalateReview` (reason `MergeConflict`).
7. **Emit structured output.** Replace the `successEnd`/`failedEnd` loose `SetOutput`s with a
   `BuildResult` step producing `CodeReviewWorkflowResult` (status enum, prNumber, prUrl, mergeSha,
   totalIterations, reviewRounds, wasEscalated, escalationResolution, message) and one
   `SetOutput("result", …)`. Map every terminal path (merged, rejected, escalation-resolved/rejected,
   timeout, validation-failed) to the correct `FinalStatus`.
8. **Add DCB events at each milestone.** In each activity (or via a shared `EmitDcbEventActivity`):
   `CODE_REVIEW.PR_CREATED.SUCCESS|FAILED`, `CODE_REVIEW.REVIEW_REQUESTED.SUCCESS`,
   `CODE_REVIEW.REVIEW_RECEIVED` (tag status), `CODE_REVIEW.GUIDANCE_DELIVERED.SUCCESS|FAILED`,
   `CODE_REVIEW.ITERATION.STARTED` (tag iteration), `CODE_REVIEW.MERGED.SUCCESS|FAILED`,
   `CODE_REVIEW.ESCALATED`, `CODE_REVIEW.RESOLVED|REJECTED` — with tags `{sessionId, storyId, prId,
   tenantId, iteration}`. Keep the existing mentorship-event log in addition.
9. **Bind config (`CodeReview:*`).** Read MaxReviewIterations, ReviewTimeoutHours,
   FixTimeoutMinutes (→ `WaitForFixes.TimeoutHours` = minutes/60), MergeStrategy, DeleteBranchAfterMerge,
   VerifyCIBeforeMerge, NotificationChannel, ReviewerPool, AutoAssignReviewers in the binding head node.
10. **Handle `Dismissed`** in `MonitorReviewActivity` (`PRReviewStatus.Dismissed` → new outcome
    `Dismissed` → re-request review or escalate); add the corresponding Flowchart edge. Remove the
    silent `_ => ChangesRequested` default (map unknown → escalate).
11. **De-dup the max-iterations guard.** Keep `MaxIterationsCheck` in the graph as authoritative;
    simplify `ReRequestReviewActivity` to just re-request (drop its internal max check, or feed its
    `MaxIterationsReached` output into the decision instead of recomputing).
12. **Add metrics + per-iteration logging (AC10):** `review.iterations.total`,
    `review.time_to_approve` (from first request to merge), `review.escalations.total`; log
    `{iteration, reviewer, decision, durationMs}` on each `MonitorReview` resume.
13. **AC3 polish:** support `draft` PR option and skill-level + story labels in `CreatePRActivity`.
14. **(Epic 38) Git mediation:** once available, re-point `CreatePR`/`RequestReviewers`/`Merge`/
    `read-comments`/CI-status to `TammaApiClient` → `POST /api/v1/git/...`; remove direct
    `IIntegrationService` git calls from the engine.

**Verification:** integration test the full happy path (bind → create → approve → CI-green → merge →
structured `Approved` result + DCB events) and the loop path (changes → LLM guidance → fixes →
re-review → approve), bookmark resume on webhook, and the max-iterations + timeout escalation paths.
