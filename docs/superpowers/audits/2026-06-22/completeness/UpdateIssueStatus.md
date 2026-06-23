# Completeness Audit — UpdateIssueStatusWorkflow

**Audited:** 2026-06-22
**Workflow:** `update-issue-status` (`UpdateIssueStatusWorkflow`)
**File:** `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/UpdateIssueStatusWorkflow.cs`
**Composed activity:** `UpdateIssueStatusActivity` (`/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/ADL/UpdateIssueStatusActivity.cs`)
**Integration path (today):** raw `IHttpClientFactory` → engine callback `POST /api/engine/issue-comment`, `POST /api/engine/issue-labels`, `DELETE /api/engine/issue-labels/{repo}/{n}/{label}` (`/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs`; impl `OctokitGitHubEngineCallbackService`)

---

## Purpose & Owner

Keep the GitHub issue a "living log" of what Tamma is doing during the autonomous cycle: post a status comment and (optionally) flip labels at each step boundary. It is dispatched **fire-and-forget** (`WaitForCompletion=false`) from `SingleIssueCycleWorkflow` via the `NotifyIssue(...)` helper at many points in the cycle (e.g. the misleadingly-named **`CloseIssue`** step at `SingleIssueCycleWorkflow.cs:586`, which actually only posts a comment + swaps `tamma-processing → tamma-completed` labels).

Owning epic/story: **Epic 2 — Autonomous Development Loop**. The product behavior it is meant to support is named in two acceptance criteria: **Story 2.10 AC5** — "System updates issue status to **closed** with comment linking to merged PR" (`docs/epics.md` §"Story 2.10"); and the broader "living log / status update" behavior of the 14-step loop (`docs/architecture.md` — "14-step autonomous development workflow", "<2hr autonomous loop completion"). DCB/audit anchor: **Epic 4** (every state transition emits a DCB event). Forward-looking owner: **Epic 38, Story 38-1 — Git-platform step mediation** (`docs/stories/epic-38/story-38-1/38-1-git-platform-step-mediation.md`), which lists `UpdateIssueStatusActivity` as a Class-A git-platform side-effect to re-point at `PATCH /api/v1/git/{repo}/issues/{n}`.

---

## Maturity: **thin**

A single-activity happy path — the user's complaint pattern. The workflow body is one node: `builder.Root = updateIssue;` (no flowchart, no branches, no outputs). The composed activity does real work (comment + add/remove labels, with a 3-attempt backoff) but **swallows all failures** — after 3 failed attempts it logs a WARN and returns normally, so the workflow always reports success even when nothing was posted (a false-success / silent-failure violation of project rules). It cannot actually change issue **state** (open→closed) at all — the engine layer has no close/transition endpoint — so the product's headline behavior ("updates issue status to **closed**", Story 2.10 AC5) is unimplemented; the cycle's `CloseIssue` step is a label swap with a celebratory comment. There is no failure outcome, no idempotency (re-runs post duplicate comments), no assignee/milestone/state-reason support, no multi-platform support, and the activity does NOT route through the mandated `TammaApiClient` git-mediation endpoint (it calls the engine callback directly via `IHttpClientFactory`).

---

## Current Capabilities

- Workflow reads inputs `repository`, `issueNumber`, `message`, `addLabels` (`string[]?`), `removeLabels` (`string[]?`) from workflow input and feeds them to one `UpdateIssueStatusActivity`. `builder.Root = updateIssue` — no flowchart, no connections, no `SetOutput`.
- `UpdateIssueStatusActivity` (`TammaAsyncActivity`, `EventType = "CYCLE.ISSUE.UPDATE"`):
  - If `Engine:CallbackUrl` or the `IHttpClientFactory` is absent, it **logs the message and returns** (degraded no-op — acceptable as a local/dev seam, but means "no callback configured" reports success).
  - Otherwise, in a **3-attempt loop with 1s/2s/4s backoff**: `POST /api/engine/issue-comment {repository, issueNumber, body}`; then if `addLabels` non-empty → `POST /api/engine/issue-labels`; then for each `removeLabels` entry → `DELETE /api/engine/issue-labels/{repo}/{n}/{label}`. On any exception within the loop it retries; **after the final failed attempt it logs a WARN and falls through to normal completion** (no throw, no failed outcome).
  - Emits DCB lifecycle events through the base class: `CYCLE.ISSUE.UPDATE.STARTED` / `.COMPLETED` / `.FAILED` (`.FAILED` only fires if `RunAsync` throws — which, by design, it never does after a real callback failure, so failures are recorded as `.COMPLETED`). `BuildEndData` carries `{issueNumber, message}` only.
- Dispatched fire-and-forget from `SingleIssueCycleWorkflow` (`WaitForCompletion=false`), so the cycle never observes its result regardless — by design for notifications, but it also means a genuinely-failed status update (e.g. the `CloseIssue`/`tamma-completed` label swap) is invisible to the cycle.

### What the underlying engine layer actually supports (and its gaps)
`IGitHubEngineCallbackService` / `OctokitGitHubEngineCallbackService` expose `ListIssuesAsync`, `PostIssueCommentAsync`, `AddIssueLabelsAsync`, `RemoveIssueLabelAsync`, `CreateIssueAsync`. There is **no close/state/assignee/milestone operation at the engine layer** (`Program.cs:2037-2040` maps only comment + add-labels + delete-label + create-issue). A `CloseIssueAsync` exists on the separate `GitHubIntegrationService` (`GitHubIntegrationService.cs:313`) but is **not reachable from this activity** — it is wired for a different code path (`IGitHubIntegrationService`), which is exactly the seam Story 38-1 re-points. Net: the workflow physically cannot close an issue today. The per-label `DELETE` calls and the label `POST` are **not retried inside the backoff** in a way that's atomic with the comment — if the comment succeeds on attempt 1 but the label POST throws, the loop retries the whole block, re-posting the comment (duplicate-comment hazard, see #5).

---

## Intended Full Scope (with citations)

1. **Actually update issue state (open ↔ closed), not just comment.** Story 2.10 AC5 (`docs/epics.md`): "System updates issue status to **closed** with comment linking to merged PR." The complete flow must support a real state transition (and `state_reason` = `completed`/`not_planned`), driven by the cycle's `CloseIssue` step on merge. Today this is a label swap only.
2. **Comment + label management** (the part that exists) — post a status body and add/remove labels — but as a **reliable, observable** operation, not fire-and-forget-and-swallow.
3. **Link the merged PR** in the close comment (Story 2.10 AC5 "comment linking to merged PR") — the body should be composed with the PR URL/number, not a static "PR merged! Issue resolved." string.
4. **DCB audit events on every state transition** (Epic 4; CLAUDE.md "Every operation must emit events for audit trail"). The activity already emits `CYCLE.ISSUE.UPDATE.*`, but a **failed callback must emit a `.FAILED` event** (today it does not — failures are silently recorded as `.COMPLETED`). Story 38-1 AC7 names the API-side family `GIT.ISSUE_UPDATED.SUCCESS|FAILED` with tags `{tenantId, repo, operation, credentialSource, correlationId}`, key-free payload.
5. **Route through the git-mediation endpoint, not the raw engine callback.** Story 38-1 (the design of record §1: "steps never call external APIs directly") mandates this activity become a **thin `TammaApiClient` client** calling `PATCH /api/v1/git/{repo}/issues/{n}` (body `{ Status: open|closed, Labels[], CorrelationId }`), which performs the cross-tenant guard → BYOK→platform token resolution (`feedback_resolution_no_empty_fallback`: tenant→system→error, never empty/default) → the platform call inside `Tamma.Api` → the DCB audit. The current `IHttpClientFactory` + `Engine:CallbackUrl` path is a transitional shape, not the target contract.
6. **No silent failure / no false success** (CLAUDE.md; `feedback_resolution_no_empty_fallback`; project memory "No Empty/Plain Fallback"). A failed update must surface a typed failure (`200 success:false` with `failureCode ∈ {NOT_FOUND, PLATFORM_ERROR}` per Story 38-1 AC6 / `GIT_TOKEN_UNAVAILABLE` 503 fail-closed) and emit a `.FAILED` event — never report success.
7. **Idempotency / de-duplication.** Re-running the cycle (retries, replays) must not spam the issue with duplicate comments or repeat label ops; the comment block and the label block must be independently retryable (and ideally keyed by `correlationId` so a re-delivery is a no-op).
8. **Cross-tenant safety + per-tenant credential** (Story 38-1 AC2/AC3): the tenant↔repo guard runs first (deny → 403 `REPO_NOT_AUTHORIZED`, platform never called); the git token is the tenant's BYOK→platform token, request-scoped, never logged/returned/persisted; `credentialSource` is stamped on the audit.
9. **Assignee / milestone / `state_reason`** (GitHub issue-update domain best-practice; the full `PATCH issues/{n}` surface). The platform-agnostic intent (CLAUDE.md "7 Git platforms") means the operation should be expressed against a normalized issue-update DTO, not GitHub-only label/comment primitives.
10. **Fire-and-forget vs. blocking by caller.** For a pure status comment, fire-and-forget is fine — but the **`CloseIssue` step (state transition on merge) is part of the success contract** and should be observed/awaited (or at least its `.FAILED` event acted on), not dispatched and forgotten.

**Mediation note:** This is **non-LLM git I/O**, so the LLM call-mediation pivot (32-5 `/llm/call`) does not apply to it directly. It IS, however, the canonical **Class-A** target of **Epic 38 / Story 38-1** (git-platform step mediation), which is the design of record for how this activity must be wired. The current direct-engine-callback path partially mediates (the token does live in `Tamma.Api`, not the engine) but does not implement the cross-tenant guard, BYOK resolution, typed-failure contract, or the `GIT.ISSUE_UPDATED.*` audit that 38-1 requires, and the activity still composes the call itself rather than delegating a single `UpdateIssueStatusAsync` to `TammaApiClient`.

---

## Missing Capabilities

| # | Capability (gap to "complete") | Priority | Depends on |
|---|---|---|---|
| 1 | **Real issue state transition (open→closed + `state_reason`).** The product's headline behavior (Story 2.10 AC5 "updates issue status to **closed**") is unimplemented; the engine layer has no close endpoint and the activity can only swap labels. Add a state field and a backing platform call. | P0 | Story 38-1 (`PATCH /api/v1/git/{repo}/issues/{n}` with `Status`) — or, interim, an engine `issue-state` endpoint |
| 2 | **No silent failure / no false success.** After 3 failed attempts the activity logs WARN and **returns success**, and emits `.COMPLETED` (never `.FAILED`). Make a genuine failure surface a typed failure and emit `GIT.ISSUE_UPDATED.FAILED` / `CYCLE.ISSUE.UPDATE.FAILED`. | P0 | none (also tightened by 38-1 AC6) |
| 3 | **`.FAILED` DCB event on callback failure.** Today the only way `.FAILED` fires is an unhandled throw, which the retry loop prevents — so audit shows success for failed updates. Emit a real failure event with `{repository, issueNumber, error, failureCode, durationMs}`. (Epic 4; 38-1 AC7) | P0 | none |
| 4 | **Route through `TammaApiClient` git-mediation, not raw engine callback.** Gut the `IHttpClientFactory` + `Engine:CallbackUrl` composition into a thin `UpdateIssueStatusAsync(repo, n, req, tenantId)` client call against `PATCH /api/v1/git/{repo}/issues/{n}` (cross-tenant guard + BYOK→platform token + API-side audit). (38-1 §1, AC5) | P0 | Story 38-1 |
| 5 | **Idempotency / duplicate-comment prevention.** The whole comment+label block is retried as a unit, so a label-POST failure re-posts the comment; cycle replays also re-comment. Make comment and label ops independently retryable and de-dupe by `correlationId`. | P1 | none (38-1 supplies `CorrelationId`) |
| 6 | **Compose the PR link into the close comment.** Story 2.10 AC5 requires "comment linking to merged PR"; the `CloseIssue` body is a static string. Pass `prNumber`/`prUrl` and build the body. | P1 | none |
| 7 | **Cross-tenant guard + per-tenant credential + `credentialSource` audit.** No tenant↔repo authorization or BYOK→platform resolution today (works only because engine+API are co-hosted). | P1 | Story 38-1 (Epic 28 registry + Epic 29 cabinet) |
| 8 | **`CloseIssue` step is observed, not fire-and-forget.** The state transition on merge is part of the success contract; dispatching it `WaitForCompletion=false` means a failed close is invisible to the cycle. Make the merge-time close blocking (or act on its `.FAILED`). | P1 | #1, #2 |
| 9 | **Assignee / milestone / normalized issue-update DTO.** Extend beyond comment+label to the full issue-update surface, expressed against a platform-agnostic DTO. | P2 | Story 38-1 (DTO) |
| 10 | **Multi-platform support.** Bound to GitHub (engine callback / Octokit) despite the 7-platform claim; drive through the normalized git seam. | P2 | Story 38-1 / Epic 1 git-platform abstraction |
| 11 | **Failure outcome edge in the workflow.** With the activity becoming outcome-bearing (success/failed), the workflow needs a real flowchart with a failed path + `success`/`error`/`errorCode` outputs instead of a bare `builder.Root = updateIssue`. | P1 | #2 |
| 12 | **Tests.** No unit/integration tests exist for the workflow or activity (verified — `Tamma.Activities.Tests`/`Tamma.Api.Tests` have none referencing `UpdateIssueStatus`). Cover comment/label/state happy paths, each typed failure, idempotency, and event emission. | P1 | #1–#4 |

---

## Ordered Build-out Spec

Goal: take `update-issue-status` from a swallow-failures, comment-only, fire-and-forget notifier to a robust, auditable, idempotent, **state-capable** git-mediated step. Honor project rules: tenant→system→error (never empty/plain fallback), no silent-failure / no false-success, steps never call external providers directly (route git I/O via the `Tamma.Api` git-mediation endpoint), and emit DCB events on every state transition.

### Phase 0 — Mediation endpoint prerequisite (Story 38-1; unblocks the rest)
0a. **Land `PATCH /api/v1/git/{repo}/issues/{n}` in `Tamma.Api`** per Story 38-1: request `UpdateIssueRequest { Status: "open"|"closed", StateReason?, Labels?, AddLabels?, RemoveLabels?, Assignees?, CommentBody?, CorrelationId }`; pipeline = cross-tenant guard (`IGitRepoAuthorizer`, deny → 403 `REPO_NOT_AUTHORIZED`, platform never called) → BYOK→platform token (`IGitTokenResolver`, null → 503 `GIT_TOKEN_UNAVAILABLE`, fail-closed) → `IGitHubIntegrationService` (comment + labels + `CloseIssueAsync`/state) inside the API → emit `GIT.ISSUE_UPDATED.SUCCESS|FAILED`. Response `GitMediationResult { Success, IssueStatus, CredentialSource, FailureCode?, FailureReason?, PlatformStatusCode? }`. (This is the existing 38-1 AC scope; the only addition is wiring `CloseIssueAsync`/state into the issue endpoint so #1 is satisfiable.)
0b. **Add `UpdateIssueStatusAsync(repo, issueNumber, UpdateIssueRequest, tenantId, ct)`** to `TammaApiClient` (mirror the `PostAsync<T>`/`PatchAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` convention).

### Phase 1 — Activity: real outcomes, state, typed failure, idempotency (P0)
1. **Gut `UpdateIssueStatusActivity` into a thin client.** Drop `IHttpClientFactory` + `Engine:CallbackUrl` composition; inject `TammaApiClient` (no Octokit, no token). Make it a `TammaOutcomeActivity` with outcomes `Updated` / `Failed`.
2. **Inputs:** keep `Repository`, `IssueNumber`, `Message`/`CommentBody`, `AddLabels`, `RemoveLabels`; **add** `Status` (`open`|`closed`|null=no-change), `StateReason`, `Assignees`, `PrUrl`/`PrNumber` (for the link), and a `CorrelationId` passthrough (default `context.WorkflowExecutionContext.Id`).
3. **Call the mediation endpoint once** with a fully-composed `UpdateIssueRequest`; map the result: `Success` → outcome `Updated`, set outputs `issueStatus`, `credentialSource`; `!Success` → outcome `Failed`, set `error`/`errorCode` (`failureCode`), preserve `platformStatusCode`. **Never return success on failure** (closes #2).
4. **Emit DCB events** matching the result: success → `CYCLE.ISSUE.UPDATE.COMPLETED` (and the API emits `GIT.ISSUE_UPDATED.SUCCESS`); failure → `CYCLE.ISSUE.UPDATE.FAILED` with `{repository, issueNumber, error, failureCode, durationMs}` (and API `GIT.ISSUE_UPDATED.FAILED`). The `.FAILED` path must actually fire (closes #3). Keep payloads key-free (no token).
5. **Idempotency:** the mediation endpoint de-dupes by `correlationId` (and/or the comment block and label/state blocks are independently applied server-side so a partial retry doesn't re-comment). The activity passes a stable `correlationId` so a cycle replay is a no-op (closes #5).
6. **Compose the PR link** into the close comment body when `PrUrl`/`PrNumber` is supplied (closes #6) — e.g. "Resolved by #{prNumber} ({prUrl})".

### Phase 2 — Workflow: real flowchart with a failure edge & outputs (P0/P1)
7. Replace `builder.Root = updateIssue` with a `Flowchart`: `updateIssue` → (`Updated`) `SetOutput success=true`, `issueStatus`, `credentialSource`; → (`Failed`) `SetOutput success=false`, `error`, `errorCode`, `platformStatusCode`. Use the named-endpoint connection form (`new FlowConnection(new FlowEndpoint(updateIssue, "Failed"), …)`) as `DebuggingWorkflow` does. (closes #11)
8. **Outputs:** emit `success`, `issueStatus`, `error`, `errorCode` on both paths; never `success=true` without a real applied change.

### Phase 3 — Caller contract: make CloseIssue real and observed (P0/P1)
9. **`SingleIssueCycleWorkflow` `CloseIssue` step (line 586):** pass `Status="closed"`, `StateReason="completed"`, `PrUrl`/`PrNumber` (already available from the PR step), and `addLabels/removeLabels` as today — so the step truly closes the issue with a PR-linked comment (closes #1, satisfies Story 2.10 AC5).
10. **Make the merge-time close blocking** (`WaitForCompletion=true`) or branch on its `.FAILED` outcome to a notify/alert path — the close is part of the success contract, not a fire-and-forget notification (closes #8). Keep mid-cycle progress comments fire-and-forget (those remain notifications).
11. **Naming:** consider renaming the helper for the close case so the graph/wiki viewer doesn't show a `CloseIssue` node that only commented (cosmetic, but the wiki workflow viewer surfaces these).

### Phase 4 — Breadth & hardening (P2)
12. **Assignee / milestone / `state_reason`** via the normalized `UpdateIssueRequest` DTO (closes #9).
13. **Multi-platform:** the mediation endpoint targets the normalized git seam, so GitLab/Gitea/etc. work without changing the activity (closes #10).
14. **Tests (TDD, mandatory):** activity maps each `GitMediationResult` to `Updated`/`Failed` + correct outputs; `.FAILED` event fires on failure; idempotent re-run (same `correlationId`) does not duplicate; PR-link composition; the close path sets `Status=closed`; endpoint tests for guard (403), `GIT_TOKEN_UNAVAILABLE` (503), typed failures (404/PLATFORM_ERROR → 200 `success:false`), and credential-safety (token never in response/log/event). (closes #12)

### Acceptance for "complete"
Issue state transition (open→closed + `state_reason`) actually works on merge with a PR-linked comment (Story 2.10 AC5); failures surface typed and emit `CYCLE.ISSUE.UPDATE.FAILED` + `GIT.ISSUE_UPDATED.FAILED` (no false success, no silent swallow); the activity is a thin `TammaApiClient` client against `PATCH /api/v1/git/{repo}/issues/{n}` with cross-tenant guard + BYOK→platform credential (Story 38-1); idempotent under retry/replay; the workflow has a real failed edge + typed outputs; the merge-time close is observed; tests green.
