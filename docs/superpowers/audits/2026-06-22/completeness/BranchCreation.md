# Completeness Audit — BranchCreationWorkflow

**Audited:** 2026-06-22
**Workflow:** `branch-creation` (`BranchCreationWorkflow`)
**File:** `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BranchCreationWorkflow.cs`
**Composed activity:** `CreateBranchActivity` (`/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/ADL/CreateBranchActivity.cs`)
**Integration impl:** `GitHubIntegrationService.CreateGitHubBranchAsync` (`/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/GitHubIntegrationService.cs`)

---

## Purpose & Owner

Create the feature branch that isolates an issue's autonomous-development work from the default branch. It is step ~7 of the ADL single-issue cycle (`SingleIssueCycleWorkflow` dispatches `branch-creation` between plan/task review and PR creation). Owning epic/story: **Epic 2 — Autonomous Development Loop**, **Story 2.4: Git Branch Creation** (`docs/epics.md` §"Story 2.4"; `docs/stories/epic-2/story-2-4/2-4-git-branch-creation.md`). PRD anchor: **FR-6** ("create Git branches, commits, pushes, and merge operations automatically").

---

## Maturity: **thin**

A happy-path skeleton — exactly the "user's complaint" pattern. The workflow is one real step (`CreateBranchActivity`) followed by 3 plumbing nodes (`SetVariable` + 2× `SetOutput`). It computes `success` purely from "did the activity write a non-empty branch name", ignores the activity's own `Created`/`Error` outcomes, has no failure edge, no conflict handling, no base-branch selection, no validation, no idempotency, and emits zero DCB audit events. The story spec for 2.4 describes a full `BranchManager` (conflict resolution, configurable naming, base-branch validation, retry, `BRANCH.CREATED.SUCCESS/FAILED` events) — almost none of which is present.

---

## Current Capabilities

- Reads inputs `repository`, `issueNumber`, `issueTitle` from workflow input.
- Calls `CreateBranchActivity`, which:
  - sanitizes the title (lowercase, spaces/slashes → `-`, alphanumerics+hyphens only, truncated to 40 chars);
  - builds branch name `adl/{issueNumber}-{sanitized-title}` (NB: hardcoded `adl/` prefix);
  - calls `IGitHubIntegrationService.CreateGitHubBranchAsync(repository, branchName)`;
  - returns outcome `Created` (sets `BranchName`) or `Error` (on `result.Success == false` or thrown exception) — but **the workflow does not wire the `Error` outcome edge**, so on the activity's `Error` branch the flowchart simply has nowhere to go.
- Sets workflow variable `Success = !string.IsNullOrEmpty(branchName)` and emits outputs `success` and `branchName`.
- Activity logs (`ILogger`) info on success and error on failure.

### What the underlying integration actually does (and its own gaps)
`GitHubIntegrationService.CreateGitHubBranchAsync` looks up the SHA of `heads/main` (falling back to `heads/master`), then `POST /git/refs` to create the ref. It **hardcodes main/master** (ignores any configured base branch), and on a pre-existing branch GitHub returns HTTP 422 → `EnsureSuccessStatusCode()` throws → caught → `Fail(...)` (so re-running the same issue **fails hard instead of being idempotent**). It is **GitHub-only** despite CLAUDE.md advertising 7 Git platforms.

---

## Intended Full Scope (with citations)

From **Story 2.4 acceptance criteria** (`docs/epics.md` §"Story 2.4" + `docs/stories/epic-2/story-2-4/2-4-git-branch-creation.md` §"Acceptance Criteria"):

1. Generate branch name from a **configurable naming pattern** (epics AC1: `Tamma/issue-{number}-{sanitized-title}`; story spec: `feature/{issue-number}-{issue-title}`, `maxNameLength`, `sanitizeNames`). The current hardcoded `adl/` prefix and 40-char title cap is a fixed, non-configurable subset.
2. Create the branch **from the latest main/master via Git platform API** AND from a **configurable `baseBranch`** (story `BranchCreationConfig.baseBranch`; `SingleIssueCycleWorkflow` already passes a `baseBranch` input that this workflow drops on the floor).
3. **Handle branch-name conflicts** when the branch already exists — append a suffix/timestamp, or abort, per `conflictResolution` strategy (epics AC3; story `handleBranchConflict`, `addConflictSuffix`, `addTimestampSuffix`).
4. **Validate** branch creation succeeded (story AC5 + `validateBranchCreation`).
5. **Branch creation failure triggers a graceful abort with error logging** (epics AC5) — i.e. a real failure edge with a distinct failed outcome, not a swallowed `success=false`.
6. **Log branch creation with branch name and base SHA** (epics AC4) — base SHA is currently not surfaced at all.
7. **Error handling for insufficient permissions, conflicts, and network issues** (story AC8 + `BranchCreationError` codes `permission_denied`, `base_branch_not_found`, `base_branch_protected`) with **retry/backoff** for transient failures and no-retry for permanent ones (story `createBranchWithRetry`).
8. **Emit DCB audit events** for the audit trail: `BranchCreatedEvent` is explicitly called out in **Story 4.5 AC3** (`docs/epics.md` §"Story 4.5") as required when a branch is created; the 2.4 spec names `BRANCH.CREATED.SUCCESS` / `BRANCH.CREATED.FAILED` with tags `{issueId, issueNumber, planId, branchName}` and data `{baseBranch, baseSha, creationTime}`. Story 2.4 §"Logging Requirements" mandates: "Every state transition must emit a corresponding DCB event (see Epic 4)." **No event is emitted today.** The C# stack already has the mechanism (`IPlatformEventPublisher` + `BuildEvent` + the per-tenant `DomainEvents` table — used by e.g. `Analytics/ComputeTenantRollupActivity` and the `TenantLifecycle` activities), so this is wiring, not new infrastructure.
9. **Multi-platform** branch creation (`createBranch()` is part of the platform-agnostic `IGitPlatform` per `docs/epics.md` Epic 1 §Story 1.4, and CLAUDE.md lists 7 Git platforms). Today it is bound to `IGitHubIntegrationService` only.

**Mediation note:** Branch creation is **non-LLM git I/O**, so it is out of scope for the LLM call-mediation pivot (`docs/superpowers/specs/2026-06-20-*.md`) — steps-never-call-LLM does not apply here. It IS, however, exactly the class of external integration that **Epic 38 (non-LLM mediation)** is meant to centralize; if/when Epic 38 lands a git-integration seam, branch creation should route through it rather than calling `IGitHubIntegrationService` from the activity directly. Project rules still bind: no silent failure / no false success (the current `success=false`-on-error-with-no-edge violates "graceful abort"), and DCB events are mandatory.

---

## Missing Capabilities

| # | Capability (gap to "complete") | Priority | Depends on |
|---|---|---|---|
| 1 | **Failure edge / graceful abort.** Wire `CreateBranchActivity`'s `Error` outcome to a distinct failed path that sets `success=false`, surfaces an `error`/`errorCode` output, and emits a `BRANCH.CREATED.FAILED` event — instead of leaving the `Error` outcome dangling. (epics AC5) | P0 | none |
| 2 | **DCB audit events.** Emit `BRANCH.CREATED.SUCCESS` (tags issueId/issueNumber/branchName, data baseBranch/baseSha/durationMs) and `BRANCH.CREATED.FAILED` (data error/errorCode) via `IPlatformEventPublisher`. (Story 4.5 AC3; 2.4 Logging Requirements) | P0 | none (mechanism exists) |
| 3 | **Idempotent re-run / conflict handling.** Detect branch-already-exists and resolve (suffix/timestamp) or treat existing-pointing-at-base as success, per a `conflictResolution` strategy — rather than 422 → hard fail. (epics AC3; story `handleBranchConflict`) | P0 | none (needs `branchExists` on `IGitHubIntegrationService`) |
| 4 | **Configurable base branch.** Honor the `baseBranch` input (already passed by `SingleIssueCycleWorkflow`) instead of hardcoding `main`/`master` in the integration impl. (epics AC2; story `BranchCreationConfig.baseBranch`) | P0 | none (integration signature change) |
| 5 | **Permission / protected-base / not-found error classification + retry policy.** Map integration errors to codes (`permission_denied`, `base_branch_not_found`, `base_branch_protected`, transient) and retry transient with backoff, fail-fast on permanent. (story AC8 + `createBranchWithRetry`) | P1 | none |
| 6 | **Base SHA surfaced + logged.** Return the base SHA the branch was cut from and include it in the success event/log. (epics AC4) | P1 | #2 |
| 7 | **Configurable naming pattern.** Replace the hardcoded `adl/{n}-{title}@40` with a config-driven pattern + `maxNameLength` + sanitize toggle. (epics AC1; story `generateBranchName`) | P1 | none |
| 8 | **Post-create validation.** Confirm the ref exists after creation before reporting success (story AC5 / `validateBranchCreation`). | P2 | #3 (needs `branchExists`) |
| 9 | **Input-contract reconciliation.** `SingleIssueCycleWorkflow` sends `baseBranch` + `workItemJson` and does NOT send `issueTitle`, yet this workflow reads `issueTitle` and ignores the others — so today the branch title is effectively empty in the real cycle path. Align the dispatched inputs with what the workflow consumes. | P1 | none |
| 10 | **Multi-platform support.** Drive branch creation through the platform-agnostic seam (`IGitPlatform` / Epic 38 git mediation) so GitLab/Gitea/etc. work, not just GitHub. (Epic 1 Story 1.4; CLAUDE.md 7-platform claim) | P2 | Epic 38 / Epic 1 git-platform abstraction |
| 11 | **Branch-name injection hardening.** Validate the final ref name against git ref rules (no `..`, no leading `-`, no control chars) before the API call (story §Security Considerations). | P2 | none |

---

## Ordered Build-out Spec

Goal: take `branch-creation` from a 1-real-step happy path to a robust, auditable, idempotent sub-workflow. Honor project rules: tenant→system→error (never empty/plain fallback), no silent-failure / no false-success, no direct provider calls from steps for LLM work (N/A here — git I/O), and emit DCB events on every state transition.

### Phase 0 — Integration-layer prerequisites (enable the rest)
0a. **Add `BranchExistsAsync(repository, branchName)`** to `IGitHubIntegrationService` (+ composite + impl): `GET /repos/{repo}/git/refs/heads/{branch}` → 200=exists, 404=absent, else error. Needed by conflict handling (#3) and validation (#8).
0b. **Make `CreateGitHubBranchAsync` base-branch aware:** add a `baseBranch` parameter; resolve its SHA (config base → `main` → `master`, in that order; **error, do not silently fall back to an unrelated branch** if an explicit base is given and missing), and **return the base SHA** in `GitHubBranchResult` (add `BaseSha`). Map 422-already-exists, 403/permission, 404-base-not-found, protected-base to typed errors rather than a bare `EnsureSuccessStatusCode()` throw.

### Phase 1 — Activity: real outcomes, conflict handling, events (P0)
1. **`CreateBranchActivity` inputs:** add `BaseBranch` (default `main`), `ConflictStrategy` (`suffix`|`timestamp`|`abort`, default `suffix`), and an `IssueId`/`PlanId` passthrough for event tags. Add outputs `BaseSha`, `Error`, `ErrorCode`.
2. **Conflict resolution before create:** if `BranchExistsAsync(candidate)` → apply strategy: `suffix` (loop `-2`, `-3`, … capped at 100 → else `Error`/`ErrorCode=conflict_exhausted`), `timestamp` (append epoch ms, re-check), `abort` (→ `Error`/`ErrorCode=branch_exists`). Log the resolution (WARN, `{baseName, finalName, strategy}`).
3. **Create + classify:** call the base-branch-aware create; on failure map to `ErrorCode ∈ {permission_denied, base_branch_not_found, base_branch_protected, transient, unknown}`; set `Error`. Set `BaseSha` from the result.
4. **Validate (post-create):** `BranchExistsAsync(finalName)` must be true; if not, outcome `Error`/`ErrorCode=validation_failed`.
5. **Emit DCB events** via `IPlatformEventPublisher` (mirror `Analytics/ComputeTenantRollupActivity`):
   - on success → `BRANCH.CREATED.SUCCESS`, tags `{issueId, issueNumber, branchName, repository}`, data `{baseBranch, baseSha, conflictResolved, finalName, durationMs}`;
   - on every failure path → `BRANCH.CREATED.FAILED`, tags `{issueId, issueNumber, repository}`, data `{attemptedName, error, errorCode, durationMs}`.
6. Keep the two existing outcomes `Created` / `Error` (the `[FlowNode("Created","Error")]` contract) but make them meaningful (every path completes with one).

### Phase 2 — Workflow: wire the failure edge & richer outputs (P0)
7. In `BranchCreationWorkflow.Build`, **wire `CreateBranch`'s `Error` endpoint** to a `SetFailure` path:
   - `Connect(createBranch /*"Created"*/, setSuccess)` → existing success chain;
   - `new(new Endpoint(createBranch, "Error"), new Endpoint(setFailure))` where `setFailure` sets `Success=false`.
   (Use the named-endpoint connection form already used by `DebuggingWorkflow`: `new FlowConnection(new FlowEndpoint(activity, "Error"), new FlowEndpoint(target))`.)
8. **Add outputs** `baseSha`, `error`, `errorCode` alongside `success` and `branchName`, on BOTH the success and failure paths (failure path emits `branchName=""`, `success=false`, populated `error`/`errorCode`). Never emit `success=true` with an empty branch name.
9. **Pass inputs through** to the activity: `baseBranch` (from input, default `main`), `issueId`/`planId`/`workItemJson` (derive `issueTitle` from `workItemJson` if `issueTitle` is not provided — closing gap #9), `conflictStrategy` (from input/config, default `suffix`).

### Phase 3 — Caller contract & config (P1)
10. **Reconcile `SingleIssueCycleWorkflow` dispatch:** ensure it passes `issueTitle` (or that the activity derives it from `workItemJson`), and confirm it consumes the new `error`/`errorCode`/`baseSha` outputs (e.g. for its abort/notify path) rather than only `branchName`/`success`.
11. **Naming/config:** lift the hardcoded `adl/` + 40-char cap into config (`namingPattern`, `maxNameLength`, `sanitizeNames`, `conflictResolution`, `baseBranch`) resolved tenant→system→error — no empty/plain fallback for a missing pattern.
12. **Retry policy:** wrap the create in the project's retry/backoff for `errorCode=transient` only; fail-fast (no retry) for `permission_denied` / `base_branch_not_found` / `base_branch_protected` (story `createBranchWithRetry`).

### Phase 4 — Hardening & platform reach (P2)
13. **Ref-name validation** before the API call (reject `..`, leading `-`, control chars, double-slashes) — outcome `Error`/`ErrorCode=invalid_ref`.
14. **Platform abstraction:** route branch creation through `IGitPlatform` / the Epic 38 git-mediation seam so non-GitHub platforms work; keep `IGitHubIntegrationService` as one implementation.
15. **Tests (TDD, mandatory):** unit tests for naming, sanitize, each conflict strategy, base-SHA capture, each error-code mapping, and event emission on success+failure; integration test against a mock Git platform (epics AC6 / story AC7).

### Acceptance for "complete"
All Story 2.4 ACs satisfied; `Error` edge wired with a graceful-abort path; idempotent re-run on an existing branch; configurable base branch + naming + conflict strategy; base SHA surfaced and logged; `BRANCH.CREATED.SUCCESS`/`FAILED` DCB events emitted; transient-retry/permanent-fail policy; caller-contract reconciled; tests green.
