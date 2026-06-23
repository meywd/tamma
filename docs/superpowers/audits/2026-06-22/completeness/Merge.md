# Completeness Audit — `MergeWorkflow` (`merge`)

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeWorkflow.cs`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/ADL/MergePullRequestActivity.cs` (+ companion
`WaitForMergeApprovalActivity.cs`, `WaitForPRMergedActivity.cs`)
**Merge service:** `apps/tamma-elsa/src/Tamma.Api/Services/GitHubIntegrationService.cs` (`MergeGitHubPullRequestAsync`)

---

## Purpose & owner

A reusable Elsa code-first sub-workflow (`DefinitionId = "merge"`, name "Merge Complete") that is the
**MERGE step of the 14-step autonomous loop** (`docs/architecture.md:841` — `{ id: 'MERGE', name:
'Merge PR' }`). Its stated job (builder description): "Squash-merge PR, close issue, and delete
branch." It is dispatched **fire-and-forget** from the autonomous loop at
`SingleIssueCycleWorkflow.cs:551-567` ("Dispatch Merge … handles merge, CI on main, conflicts"),
while the loop separately blocks on `WaitForPRMergedActivity` (a GitHub-webhook bookmark) and closes
the issue itself.
Owner: **Epic 2 (Autonomous Development Loop — Core), Story 2-10 — PR Merge with Completion
Checkpoint** (`docs/stories/epic-2/story-2-10/2-10-pr-merge-with-completion-checkpoint.md`).

---

## Maturity: `thin`

This is the user's complaint pattern almost exactly. The workflow graph is **four nodes on a single
linear chain**:

```
MergePR → SetMergeSuccess → OutputSuccess → OutputMergeSha
```

`MergePullRequestActivity` does perform three real GitHub calls (merge, close-issue, best-effort
branch-delete), so it is not a pure placeholder. But the **workflow** around it is a happy-path
skeleton with several correctness defects:

- **The `Error` outcome is never wired.** `MergePullRequestActivity` declares
  `[FlowNode("Merged", "Error")]` and calls `CompleteActivityWithOutcomesAsync("Error")` on merge
  failure (`MergePullRequestActivity.cs:72,101`). The workflow connects **only**
  `Connect(mergePr, setSuccess)` (`MergeWorkflow.cs:52`); the `Error` outcome has **no edge**. A
  merge failure therefore dead-ends the flowchart with no failure terminal, no failure output, and no
  failure event — a silent stall, the exact "no false-success / no silent-failure" anti-pattern the
  project rules forbid.
- **No merge-readiness validation.** It merges blind — no check of CI/required status checks,
  approvals, branch protection, or `mergeable`/conflict state. Story 2-10 AC1 requires "merges PR when
  all requirements are met (approvals, checks, no conflicts)." `GetBuildStatusAsync(repo, branch)`
  exists on `IIntegrationService` and is unused here.
- **No DCB / Tamma audit events.** `MergePullRequestActivity` extends plain
  `Elsa.Workflows.Activities.Activity`, **not** `TammaOutcomeActivity`/`ITammaActivity`
  (`Tamma.Activities/Core/TammaActivity.cs`), so it emits **zero** lifecycle events — unlike its
  sibling `WaitForPRMergedActivity` (`EventType = "CYCLE.PR.MERGE.WAIT"`). Story 2-10 AC5 ("logs
  merge, cleanup, and completion to event trail") and the story's Logging Requirements ("Every state
  transition must emit a corresponding DCB event") are not met.
- **`Success` is inferred, not verified.** `SetMergeSuccess` sets success = "SHA is non-empty"
  (`MergeWorkflow.cs:37`). The activity already swallows close-issue/branch-delete failures, so
  `success=true` can be emitted while the issue was never closed or the branch never deleted (Story
  2-10 AC2 "validates merge was successful and branch is cleaned up" / AC4 completion checkpoint).
- **No idempotency.** Re-dispatch (or webhook double-fire) re-attempts a merge of an already-merged
  PR; GitHub returns 405 → `EnsureSuccessStatusCode()` throws → `Error` outcome → silent stall.
- **Merge strategy hardcoded.** `merge_method = "squash"` is fixed in the service
  (`GitHubIntegrationService.cs:158`); Story 2-10 (`MergeStrategy = 'merge'|'squash'|'rebase'`,
  default-strategy config) and `MergeAndCompleteReviewActivity` (configurable `Strategy` input) both
  expect this to be selectable.

It is more functional than a bare stub (the merge/close/delete calls work on the happy path), but the
unhandled failure outcome, absent validation, and absent audit events keep it well short of
`complete`.

---

## Current capabilities (what it actually does today)

- **Binds inputs** from dispatch payload: `repository`, `prNumber`, `issueNumber`, `branchName`
  (`MergeWorkflow.cs:29-32`).
- **`MergePR`** (`MergePullRequestActivity`):
  1. Squash-merges the PR via `IGitHubIntegrationService.MergeGitHubPullRequestAsync` (strategy fixed
     to squash in the service). On `!Success` → logs error, completes with outcome `Error` (which the
     workflow ignores).
  2. On success, sets `MergeSha`, then **closes the issue** with a comment
     `"Resolved by PR #{n} (merge SHA: {sha})"` via `CloseGitHubIssueAsync` — return value
     **not checked**.
  3. **Best-effort deletes the branch** via `DeleteGitHubBranchAsync`, swallowing any exception as a
     non-fatal warning.
  4. Completes with outcome `Merged`. Any thrown exception → outcome `Error`.
- **`SetMergeSuccess`**: `Success = !string.IsNullOrEmpty(MergeSha)`.
- **`OutputSuccess` / `OutputMergeSha`**: two `SetOutput`s emitting `success` (bool) and `mergeSha`
  (string).
- Injects `ILogger`; logs INFO on success, ERROR on merge failure, WARN on branch-delete failure.

**Companion activities (not part of this workflow's graph, but the merge "family"):**
- `WaitForMergeApprovalActivity` (used by `MergeApprovalWorkflow`, `DefinitionId="merge-approval"`):
  human merge/test/reject bookmark — separate from this workflow.
- `WaitForPRMergedActivity` (used inline by `SingleIssueCycleWorkflow`): GitHub-webhook bookmark that
  blocks the loop until merged; **this** is the activity that emits the `CYCLE.PR.MERGE.*` event, not
  `MergeWorkflow`.

---

## Intended full scope (with citations)

**Story 2-10 — PR Merge with Completion Checkpoint** is the contract. Acceptance criteria:

- **AC1** Merge PR **when all requirements are met (approvals, checks, no conflicts)** — i.e. a
  `validateMergeRequirements` gate covering approvals, CI checks, merge conflicts, branch protection,
  policy compliance (story §Core Components `IPRMergeManager.validateMergeRequirements`,
  `MergeValidation`/`MergeRequirement` types).
- **AC2** Validate the merge succeeded **and the branch is cleaned up** (not best-effort/ignored).
- **AC3** Close the associated issue with a **completion comment** (merge SHA, strategy, change
  stats) — story §`generateIssueCloseComment`.
- **AC4** Perform a **completion checkpoint** validating issue-closure / branch-cleanup / (optional)
  deployment / notifications, with optional rollback (`CompletionResult`,
  `CompletionCheckpointConfig`).
- **AC5** **Log merge, cleanup, and completion to the event trail** — DCB events
  `PR.MERGE.SUCCESS` / `PR.MERGE.FAILED`, `ISSUE.CLOSED.SUCCESS` / `ISSUE.CLOSED.FAILED`,
  `WORKFLOW.NEXT_ISSUE.TRIGGERED` (story §Integration Points / §Implementation Strategy event
  appends). Story Logging Requirements: "Every state transition must emit a corresponding DCB event
  (see Epic 4)."
- **AC6** Trigger next-issue selection for continuous operation (in the C# loop this is the
  fire-and-forget dispatch + the loop's own continuation, so this AC is largely satisfied at the
  orchestrator level — not inside `MergeWorkflow`).
- **AC7** Integration test of the merge + completion workflow.
- **AC8** **Error handling for merge failures, permission issues, and cleanup failures** — story
  §`parseMergeError` classifies `permission_denied | merge_conflict | branch_protected | ci_pending |
  api_error` with `retryable` + `suggestedAction`; a failed terminal must be reachable and reported.
- **Config block** (story §Configuration Examples): `default_strategy`, `require_all_checks_pass`,
  `require_min_approvals`, `auto_delete_branch`, `close_associated_issues`, completion-checkpoint
  flags, post-merge action ordering/criticality, merge-commit message templating
  (`include_issue_number` → `Closes #N`).

**Architecture** (`docs/architecture.md:825-844`): MERGE is step 13 of the static 14-step loop, between
APPROVAL_GATE and DEPLOYMENT; DCB event sourcing (`CLAUDE.md` §Event Sourcing) demands a 100% audit
trail with `AGGREGATE.ACTION.STATUS` events for every action.

**Agent-architecture pivot** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`):
- This workflow performs **no LLM/agent work**, so the `call-LLM` mediation (Story **32-5**) does
  **not** apply to it.
- Its git operations (merge, close-issue, delete-branch, and the missing CI/approval/mergeable reads)
  are **Class A git platform calls through `IIntegrationService`**. The pivot's end-state routes those
  through the future **Epic 38** git-mediation API (`POST /api/v1/git/...`) for tenant-scoped creds,
  cross-tenant guard, and audit. That is the correct long-term target, not a "now" blocker — today the
  engine-local `IGitHubIntegrationService` is acceptable.

**Sibling-audit consistency:** `MergeAndCompleteReviewActivity` (Epic 7 mentorship) already models a
configurable `Strategy` input and analytics recording; the `CodeReview` completeness audit flags the
same "strategy passed but not applied / no CI gate before merge / no branch delete" gaps — they should
be fixed coherently with this workflow.

---

## Missing capabilities

| # | Capability (gap to complete) | Priority | Depends on |
|---|------------------------------|----------|------------|
| 1 | **Wire the `Error` outcome to a failure terminal.** Add an edge from `mergePr`'s `Error` outcome to a `failedEnd` that sets `success=false` and a specific `errorMessage`/`failureReason`, and reports back. Today a merge failure dead-ends the flowchart (silent stall) — P0 no-silent-failure violation. | P0 | none |
| 2 | **Merge-readiness validation (AC1).** Before merging, verify required CI/status checks are green (`GetBuildStatusAsync` exists), approvals satisfied, and no merge conflicts (`mergeable`). Route not-ready → a "not mergeable" failure terminal with the blocking reason. | P0 (safety) | Epic 38 (git mediation, end-state); usable now via `IIntegrationService` (+ a new `GetPullRequest`/mergeable read) |
| 3 | **DCB audit events (AC5).** Make `MergePullRequestActivity` an `ITammaActivity` (or emit via a shared event activity) and emit `PR.MERGE.SUCCESS`/`PR.MERGE.FAILED`, `ISSUE.CLOSED.SUCCESS`/`ISSUE.CLOSED.FAILED`, `BRANCH.DELETED.SUCCESS`/`BRANCH.DELETED.FAILED` with tags `{prNumber, issueNumber, repository, mergeSha, tenantId, mergeStrategy}`. Today the workflow emits no events at all. | P0 (contract) | Epic 4 event store (present) |
| 4 | **Verify success instead of inferring it (AC2/AC4).** Check `CloseGitHubIssueAsync` return value and the branch-delete result; surface partial-completion (merged-but-issue-not-closed / branch-not-deleted) as a distinct outcome rather than reporting blanket `success` from a non-empty SHA. | P0 | none |
| 5 | **Idempotency / already-merged handling.** Before merging, check PR state; if already merged, treat as success (set SHA, skip re-merge) rather than throwing on GitHub 405. Guard against webhook double-fire. | P1 (correctness) | none |
| 6 | **Configurable merge strategy (AC8/config).** Plumb a `mergeStrategy` input (default `squash`) from dispatch → activity → `MergeGitHubPullRequestAsync(repo, pr, strategy)` (service currently hardcodes `merge_method="squash"`). | P1 | none |
| 7 | **Merge-failure classification + retry (AC8).** Classify failure (`permission_denied | merge_conflict | branch_protected | ci_pending | api_error`), set `retryable`; on transient/api error retry once with backoff, then fail with `suggestedAction`. | P1 | none |
| 8 | **Structured / completion result (AC4).** Replace the two loose `SetOutput`s with a structured completion result (`success`, `mergeSha`, `mergeStrategy`, `issueClosed`, `branchDeleted`, `failureReason`) so the dispatcher and audit get a real completion record, not just `success`+`mergeSha`. | P1 | none |
| 9 | **Merge-commit message templating (config).** Build the squash commit title/body with issue reference (`Closes #N`) and an auto-generated-by-Tamma footer per story §`generateMergeCommitMessage` (currently the merge uses GitHub defaults; only the issue-close *comment* references the PR). | P2 | none |
| 10 | **Config binding (`pr_merge:*`).** Bind `default_strategy`, `require_all_checks_pass`, `require_min_approvals`, `auto_delete_branch`, `close_associated_issues` instead of hardcoded behaviour. | P2 | none |
| 11 | **Completion checkpoint / deployment + notification hooks (AC4).** Optional post-merge validation (issue-closure, branch-cleanup) and notification; in the C# loop deployment is a separate `deployment-pipeline` dispatch, so keep this minimal (validation + event), not a re-implementation. | P3 | none |
| 12 | **Git-call mediation (pivot end-state).** Re-point merge / close-issue / delete-branch / CI-status reads from engine-local `IGitHubIntegrationService` onto the Epic 38 `POST /api/v1/git/...` mediation (tenant-scoped creds, cross-tenant guard, audit). | P3 | Epic 38 |

---

## Ordered build-out spec (to reach `complete`)

1. **Add a `failedEnd` terminal and wire the `Error` outcome (fixes defect #1).** New nodes:
   `SetVariable("SetFailure")` setting `Success=false` + `FailureReason` (from a new `Error` output on
   the activity), then `SetOutput("success", false)` + `SetOutput("failureReason", …)`. Add
   `Connect(mergePr, setFailure)` on the **`Error`** endpoint (use the flow-endpoint overload that
   names the outcome), and keep `Connect(mergePr, setSuccess)` on the **`Merged`** endpoint. No more
   dead-end on failure.
2. **Add a `CheckMergeReadiness` gate before `MergePR` (AC1).** New `TammaOutcomeActivity`
   `CheckMergeReadinessActivity` (`[FlowNode("Ready","NotReady","Error")]`): read CI via
   `GetBuildStatusAsync(repo, branch)`; read PR `mergeable`/conflict + approvals via a new
   `GetGitHubPullRequestAsync(repo, prNumber)` on `IIntegrationService` (add the method — only
   `GetBuildStatusAsync` exists today). Config-gated by `require_all_checks_pass` /
   `require_min_approvals`. `Ready` → `MergePR`; `NotReady` → `failedEnd` with a blocking-reason
   message (honour tenant→system→error; never proceed on an unknown/`null` mergeable state — fail
   explicit). Emit `PR.MERGE.READINESS.CHECKED` event.
3. **Add idempotency to `MergePullRequestActivity` (#5).** At the top of `ExecuteAsync`, fetch PR
   state; if already merged → set `MergeSha` from the existing merge commit, skip re-merge, continue to
   close/delete, complete `Merged`. Otherwise proceed. Avoids GitHub 405 → spurious `Error`.
4. **Make `MergePullRequestActivity` an `ITammaActivity` and emit DCB events (#3, AC5).** Re-base it on
   `TammaOutcomeActivity` (it already has `[FlowNode]` outcomes) with
   `EventType => "PR.MERGE"` and a populated `BuildEndData` (`prNumber, issueNumber, repository,
   mergeSha, mergeStrategy`). Additionally emit explicit `ISSUE.CLOSED.SUCCESS|FAILED` and
   `BRANCH.DELETED.SUCCESS|FAILED` (via `TammaEventEmitter.Emit`) so each sub-action is on the audit
   stream — not just the umbrella merge event.
5. **Verify, don't infer, success (#4, AC2).** Capture the `CloseGitHubIssueAsync` result and the
   branch-delete result into activity outputs `IssueClosed`/`BranchDeleted`. If the merge succeeded but
   issue-close failed, complete a new `MergedWithWarnings` outcome (route to `successEnd` but set
   `success=true, partial=true, warnings=[…]`) instead of pretending everything is clean.
   `SetMergeSuccess` should then be `Success = mergeSucceeded` (from the activity), not "SHA non-empty".
6. **Plumb configurable merge strategy (#6).** Add `mergeStrategy` input to the workflow (default
   `"squash"`), pass to the activity, and add a `strategy` parameter to
   `MergeGitHubPullRequestAsync(repo, pr, strategy)` (replace the hardcoded `merge_method="squash"` in
   `GitHubIntegrationService.cs:158`). Bind from `pr_merge:default_strategy` config.
7. **Classify merge failures + single retry (#7, AC8).** In the activity, on `!mergeResult.Success`
   classify (`permission_denied|merge_conflict|branch_protected|ci_pending|api_error`); set
   `retryable`; retry once with backoff for transient/`api_error`; on final failure set
   `Error`/`FailureReason`/`SuggestedAction` outputs and complete `Error` (now wired to `failedEnd`).
8. **Emit a structured completion result (#8, AC4).** Add a `BuildResult` step before the outputs that
   composes `{ success, mergeSha, mergeStrategy, issueClosed, branchDeleted, partial, failureReason }`
   and a single `SetOutput("result", …)` (keep `success`/`mergeSha` for the existing caller). Map every
   terminal (Merged, MergedWithWarnings, NotReady, Error) to the correct shape.
9. **Merge-commit message templating (#9, config).** When merging, supply a squash commit title/body
   including `Closes #{issueNumber}` and an "Auto-merged by Tamma / strategy / timestamp" footer
   (story §`generateMergeCommitMessage`); gate via `merge_commit:*` config.
10. **Bind `pr_merge:*` config (#10).** Read `default_strategy`, `require_all_checks_pass`,
    `require_min_approvals`, `auto_delete_branch`, `close_associated_issues` in a binding head node;
    `auto_delete_branch=false` skips delete; `close_associated_issues=false` skips close.
11. **(Optional) completion checkpoint (#11, AC4).** A lightweight post-merge validation step (confirm
    issue state = closed, branch absent) emitting `PR.MERGE.COMPLETION.VALIDATED`; keep deployment out
    of scope (handled by the loop's separate `deployment-pipeline` dispatch).
12. **(Epic 38) git mediation (#12).** Once available, re-point merge / close-issue / delete-branch /
    CI-status / PR-detail reads to `TammaApiClient` → `POST /api/v1/git/...`; remove direct
    `IGitHubIntegrationService` calls from the engine.

**Verification (AC7):** integration-test (a) happy path (ready → merge → issue closed → branch deleted
→ structured `success` result + `PR.MERGE.SUCCESS` / `ISSUE.CLOSED.SUCCESS` / `BRANCH.DELETED.SUCCESS`
events); (b) **failure path** (merge returns `!Success` → `Error` outcome → `failedEnd` →
`success=false` + `PR.MERGE.FAILED` event — proving the dead-end is gone); (c) not-ready path (red CI /
conflicts → `failedEnd` with blocking reason, no merge attempted); (d) idempotency (already-merged PR →
success, no 405); (e) partial path (merge ok, close fails → `MergedWithWarnings`).
