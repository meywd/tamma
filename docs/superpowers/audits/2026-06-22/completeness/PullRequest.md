# Completeness Audit — `PullRequestWorkflow` (`pull-request`)

**Date:** 2026-06-22
**Workflow:** `PullRequestWorkflow` (`DefinitionId = "pull-request"`)
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PullRequestWorkflow.cs`
**Composed activity:** `apps/tamma-elsa/src/Tamma.Activities/ADL/CreatePullRequestActivity.cs`
**Reference (richer sibling):** `apps/tamma-elsa/src/Tamma.Activities/Review/CreatePRActivity.cs`

---

## 1. Purpose & owner

- **Purpose:** The PR-creation step of the 14-step Autonomous Development Loop — open a pull request from the feature branch to the base branch with a description, link the issue, and surface PR number/URL/success to the parent cycle.
- **Owning epic/story:** Epic 2 (Autonomous Development Loop – Core), **Story 2.8 "Pull Request Creation"** (`docs/stories/epic-2/story-2-8/2-8-pull-request-creation.md`). PRD **FR-1** lists "PR creation" as step 3 of the loop; **FR-6** (auto Git branch/commit/push/merge), **FR-19e** (PR creation must survive observability failures), **FR-20** (all actions emit immutable events). Caller: `SingleIssueCycleWorkflow` step 8 ("Create Draft PR"), `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:381`.

## 2. Maturity: **thin** (happy-path skeleton — the user's complaint is accurate)

The entire workflow is `CreatePR → OutputSuccess → OutputPrNumber → OutputPrUrl`. It calls one activity and copies three values into workflow outputs. It is a textbook "thin happy-path" skeleton:

- It links the `Created`/`Error` outcomes of `CreatePullRequestActivity` to **nothing** — both fall through the single `Connect(createPr, outputSuccess)` edge, so a failed PR creation produces `success=false` silently and the workflow still "completes" normally with `prNumber=0`. There is no failure edge, no escalation, no event.
- The caller passes `["draft"] = true` and names the step "Create Draft PR", but the workflow **does not read `draft`** and the activity/`CreatePullRequestRequest`/`IGitHubIntegrationService` have **no draft parameter** — the draft contract from `SingleIssueCycleWorkflow` is silently dropped (the PR is always opened ready, never draft).

## 3. Current capabilities

- Reads inputs `repository`, `branchName`, `baseBranch` (default `main`), `issueNumber`, `issueTitle`, `planJson`.
- Invokes `CreatePullRequestActivity`, which:
  - Builds a fixed title `"[ADL] #{n}: {title}"`.
  - Builds a minimal body: a hard-coded "Autonomous Development / Closes #n" preamble plus the raw `planJson` fenced in a ```json``` block (issue auto-close via the literal `Closes #n` string is the only "link" — no platform issue-link API call).
  - Adds two static labels `tamma-auto`, `adl`.
  - Calls `IGitHubIntegrationService.CreateGitHubPullRequestAsync`.
  - On `!result.Success` or exception: logs and completes with outcome `Error` (which the workflow then ignores).
  - On success: sets `PrNumber`, `PrUrl`, completes with `Created`.
- Workflow outputs: `success` (= `prNumber > 0`), `prNumber`, `prUrl`.

## 4. Intended full scope (with citations)

**Story 2.8 Acceptance Criteria** (`docs/stories/epic-2/story-2-8/2-8-pull-request-creation.md` lines 39–47):

1. Create PR from feature branch to target branch.
2. **Comprehensive description** with issue context + implementation details.
3. **Automated labels, reviewers, and project metadata.**
4. Description includes **test results, coverage metrics, change summary** (files added/modified/deleted, lines, commits).
5. **Validate PR creation was successful** (re-fetch / verify, not just non-zero number).
6. **PR creation and metadata logged to the event trail** (`PR.CREATED.SUCCESS` / `PR.CREATED.FAILED`, lines 243–289).
7. Integration test of the full workflow.
8. **Error handling for permission issues, conflicts (PR already exists → update existing), and API failures** (lines 8 / 573–581).

The story's `IPullRequestCreator` contract defines: `generatePRDescription` (AI-generated, with a `generateFallbackDescription` fallback), `assignReviewers` (selection strategies: random / round-robin / code-ownership / expertise), `addLabels` (pattern → label mapping), `linkIssues` (main + related `#refs`), `validatePRCreation`, and `draftMode`. Config (`PRCreationConfig`, lines 170–182) includes `draftMode`, `linkIssues`, `updateExisting`, `mergeOnApproval`, `mergeStrategy`.

**PRD:** FR-1 (PR creation = loop step 3); FR-6 (automated Git ops); FR-19e ("observability failures do not block PR creation" — graceful degradation); FR-20/21 (immutable event capture). `docs/epics.md:1370` — `PRCreatedEvent` must capture PR number, URL, base/head branches. `docs/epics.md:1486` — `prs_created_total` metric. Story 2.8 Logging Requirements (lines 1147–1156): "Every state transition must emit a corresponding DCB event."

**Agent-architecture pivot** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`, locked rule #1/#2): a workflow step that needs an LLM (here: **PR-description generation**, Story 2.8 lines 291–312) **must never call a provider directly** — it must route through the tamma-api `call-LLM` mediation (`POST /api/v1/llm/call`), in-engine via the `LlmCallWorkflow` / `CallLlmActivity` seam. So the description-generation sub-step is a `DispatchWorkflow → llm-call` (or an inline `CallLlmActivity`), never a raw SDK call inside `CreatePullRequestActivity`.

**Project rules (CLAUDE.md / memory):** resolution is tenant→system→error, **never empty/plain fallback**; **no silent-failure / false-success**; emit DCB audit events for every operation.

**Platform reality check** (what the substrate already supports vs. what's missing):
- `OpenPullRequestRequest` (`Tamma.Platforms.Abstractions/Models/OpenPullRequestRequest.cs`) has `IsDraft` and `PullRequest.IsDraft` exists — **draft is supported at the platform layer** but not surfaced through `IGitHubIntegrationService` / `CreatePullRequestRequest`.
- `CreatePullRequestRequest` (`IIntegrationService.cs:190`) already has a `Reviewers` list and a `Labels` list — reviewers are **never populated**.
- `IGitHubIntegrationService` exposes `GetGitHubFileChangesAsync`, `GetGitHubCommitsAsync`, `CloseGitHubIssueAsync`, `GetPullRequestReviewCommentsAsync` — the change-summary inputs (files/commits) the story body needs are available but unused here.
- **Gaps in the substrate:** no "assign reviewers (post-create)", no "add labels (post-create)", no "convert draft → ready", no "link issue (API)", no `IsDraft` on the `IGitHubIntegrationService` create path, and **no PR DCB event type / emitter** (grep for `PR.CREATED` across `apps/tamma-elsa/src` returns nothing; event-emit activities exist only for TenantLifecycle and Analytics).

## 5. Missing capabilities (gap to "complete")

| # | Missing capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Failure edge** — wire `CreatePullRequestActivity`'s `Error` outcome to an explicit failure path (set `success=false`, emit `PR.CREATED.FAILED`, escalate / set exit reason). Today `Error` is silently swallowed → false "completed". | P0 | none |
| 2 | **`PR.CREATED.SUCCESS` / `PR.CREATED.FAILED` DCB events** with tags (`issueId`, `issueNumber`, `prNumber`, `repository`, `tenantId`) + data (url, base/head, filesChanged, linesAdded/Deleted, testCoverage, reviewers, labels, durationMs). FR-20, epics.md:1370. No PR events exist anywhere. | P0 | new `PR.*` event types + an emit activity (pattern exists: `EmitDeletedSuccessActivity`) |
| 3 | **Honor the `draft` input** end-to-end: read `draft` in the workflow, thread `IsDraft` through `CreatePullRequestRequest` → `IGitHubIntegrationService.CreateGitHubPullRequestAsync` → platform `OpenPullRequestRequest.IsDraft`. Contract is passed by the caller and currently dropped. | P0 | substrate change: add `IsDraft` to create path |
| 4 | **Idempotency / "PR already exists"** — detect existing open PR for `head→base` (or 422 from API), and either reuse (return existing number/URL) or update it (`updateExisting`), instead of erroring. Re-runs of `SingleIssueCycle` must not double-open or hard-fail. AC8 / story lines 573–581. | P0 | `ListGitHubIssuesAsync`-equivalent for PRs / get-PR-by-branch on the service |
| 5 | **AI-generated comprehensive description** (summary, technical details, breaking-changes, migration, checklist) replacing the raw-JSON dump. Must be produced via the **call-LLM mediation**, with a deterministic non-LLM fallback body on LLM failure (FR-19e — never block PR creation). | P1 | 32-5 (`call-LLM` endpoint) / `LlmCallWorkflow` seam |
| 6 | **Change summary in body** — files added/modified/deleted, +lines/−lines, commit count, modified-file list (data already available via `GetGitHubFileChangesAsync` + `GetGitHubCommitsAsync`; `Review/CreatePRActivity` already does this). AC4. | P1 | none |
| 7 | **Test/coverage summary in body** — testsRun/passed/failed, coverage %, CI status. AC4. (Inputs flow from the test/CI steps of the cycle.) | P1 | upstream test-result inputs from `SingleIssueCycleWorkflow` |
| 8 | **Reviewer assignment** — populate `CreatePullRequestRequest.Reviewers` (or a post-create assign step) per a selection strategy; failure to assign must NOT fail PR creation (degrade gracefully, warn). AC3. | P1 | reviewer-source config; possibly platform `RequestReviewers` op (not yet in `IGitHubIntegrationService`) |
| 9 | **Smart labels** — derive labels from issue labels / change type / risk (new-feature, enhancement, breaking-change, security) instead of only the two static `tamma-auto`/`adl`. AC3. | P2 | none |
| 10 | **Issue auto-close / linking via API** — currently relies solely on the literal `Closes #n` in the body. Add explicit linkage + related-issue `#ref` extraction; verify the close-on-merge keyword is platform-correct. AC1/AC3. | P2 | possibly platform link op |
| 11 | **Post-create validation** — re-fetch the PR (`GetPullRequestAsync`) and verify state=open / title matches before reporting success (don't trust the create response alone). AC5. | P2 | get-PR on the service |
| 12 | **`prs_created_total` metric** emission (epics.md:1486) alongside the DCB event. | P2 | OTel meter (pattern exists in Analytics) |
| 13 | **Multi-platform parity** — the activity is GitHub-only (`IGitHubIntegrationService`). PRD FR-10 wants 7+ platforms; route through `IGitPlatformClient` (which already models `IsDraft`) for portability. | P3 | platform-abstraction adoption (separate epic) |
| 14 | **Output the draft state** (`isDraft`) and **base/head branches** as workflow outputs so the parent cycle can later flip draft→ready and audit the merge target. | P3 | depends on #3 |

## 6. Ordered build-out spec (to reach complete + robust)

Implement in this order; honor: no silent failure, no false success, tenant→system→error, LLM only via mediation, DCB event on every transition. (Numbers below are workflow steps / new edges; activity/event names are concrete.)

1. **Substrate prerequisites (enable the rest):**
   - Add `bool IsDraft` to `CreatePullRequestRequest` and thread it through `IGitHubIntegrationService.CreateGitHubPullRequestAsync` → driver → `OpenPullRequestRequest.IsDraft`.
   - Add a PR DCB event family `PR.CREATED.SUCCESS` / `PR.CREATED.FAILED` (+ optional `PR.MARKED_READY.SUCCESS`) and an `EmitPrEventActivity` modeled on `TenantLifecycle/EmitDeletedSuccessActivity` (tags: `issueId`,`issueNumber`,`repository`,`prNumber`,`tenantId`; data per table row #2).
   - Add an "existing PR for head→base" lookup (service method) and (P1) a reviewer/label post-create or get-PR-by-number method on the service.

2. **Step: ReadInputs/Resolve** — read `draft` (default false), `tenantId`, plus existing `repository/branchName/baseBranch/issueNumber/issueTitle/planJson`; carry test/coverage/CI inputs from the parent cycle (new optional inputs `testSummaryJson`, `changeSummaryJson`).

3. **Step: GenerateDescription (mediated LLM)** — `DispatchWorkflow → "llm-call"` (or inline `CallLlmActivity`) with role/prompt for PR-description generation, fed issue context + plan + change summary + test summary.
   - **Outcome `Generated`** → continue with AI body.
   - **Outcome `Failed`/timeout** → **deterministic fallback body** (structured: Summary / Changes / Testing / Breaking Changes / Migration / Checklist, built from the change+test summaries — never empty/plain). FR-19e: LLM failure must NOT abort PR creation.

4. **Step: BuildChangeSummary** — call `GetGitHubFileChangesAsync` + `GetGitHubCommitsAsync`; compute files added/modified/deleted, +/- lines, commit count, top-N modified files (reuse `Review/CreatePRActivity.BuildPRBody` logic). Merge into the body. On fetch error → warn + continue with a "change summary unavailable" note (degrade, don't fail).

5. **Step: DetermineLabels & Reviewers** — derive labels from issue labels + change type/risk; resolve reviewers from config/code-ownership. Populate `CreatePullRequestRequest.{Labels,Reviewers}`. Empty reviewer set is allowed (warn, continue).

6. **Step: CheckExistingPR (idempotency)** — look up an open PR for `head→base`.
   - **Found** → branch to **UpdateExistingPR** (update title/body/labels; do NOT re-create) → join at step 8.
   - **Not found** → continue to CreatePR.

7. **Step: CreatePR** — `CreatePullRequestActivity` (extended) with `IsDraft = draft`, composed body, labels, reviewers.
   - **`Created`** → step 8.
   - **`Error`** → **Failure path**: classify (permission / merge-conflict / 422-already-exists / rate-limit / generic). For 422-already-exists, route to step 6's UpdateExistingPR (defensive). For others → `SetVariable success=false`, **emit `PR.CREATED.FAILED`** (with error code + durationMs), set exit reason (`pr-creation-failed`), and surface a typed failure outcome to the parent (`Escalate`/`Failed`) — **no fall-through to OutputSuccess**.

8. **Step: ValidatePR** — `GetPullRequestAsync(prNumber)`; verify state=open and title matches. On mismatch/null → treat as failure (step 7 failure path semantics). (P2; can be feature-flagged for first cut.)

9. **Step: EmitSuccessEvent** — `EmitPrEventActivity` → `PR.CREATED.SUCCESS` (prNumber, url, base/head, filesChanged, lines, coverage, reviewers, labels, isDraft, durationMs); increment `prs_created_total`.

10. **Step: Outputs** — `success=true`, `prNumber`, `prUrl`, plus **new outputs** `isDraft`, `baseBranch`, `headBranch`, `linkedIssue`. (These let `SingleIssueCycleWorkflow` later run a `MarkPrReady` step after CI/review — the draft→ready flip the cycle currently has no way to perform.)

11. **(Follow-on, parent-driven) MarkReady** — add a `ConvertDraftToReadyAsync` op + a small step (or reuse this workflow with a `mode=markReady` input) so the cycle flips the draft PR to ready after tests/review pass, emitting `PR.MARKED_READY.SUCCESS`. Tracks the "draft→ready" gap implied by `SingleIssueCycleWorkflow` opening a draft PR up front.

12. **Tests** — workflow integration test covering: happy path (draft + ready), LLM-fallback path, create-failure → escalation (assert NO false success, event emitted), existing-PR idempotency, reviewer/label-assign-failure degrades gracefully. (AC7.)

---

### Bottom line
`pull-request` today is a one-activity happy-path shell that drops the caller's `draft` flag, swallows its own `Error` outcome (false success), generates no real description, assigns no reviewers/smart-labels, performs no validation/idempotency, and emits zero audit events. To reach Story 2.8 / FR-1 "complete," it needs the failure edge + DCB events (P0), draft + idempotency (P0), mediated AI description with deterministic fallback (P1), and change/test summaries + reviewers (P1), built on a handful of substrate additions (draft on the create path, a `PR.*` event family, an existing-PR lookup).
