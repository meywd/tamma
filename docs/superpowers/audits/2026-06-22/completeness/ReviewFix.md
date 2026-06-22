# Completeness Audit — ReviewFixWorkflow

**Date:** 2026-06-22
**Workflow:** `review-fix` (`ReviewFixWorkflow`)
**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ReviewFixWorkflow.cs`
**Verdict:** **THIN** (happy-path skeleton — analyzes comments and asks the LLM for fixes, but the fixes are never written, committed, pushed, verified, or re-submitted for review; no DCB events; no failure edges)

---

## 1. Purpose & Owner

**Purpose:** Phase of the Autonomous Development Loop (ADL) that closes the review loop — fetch a PR's review comments, decide which are actionable, generate code fixes via the mediated LLM path, apply them to the branch, and push them back so CI/review can re-run.

**Owner:** Epic 2 (Autonomous Development Loop) — specifically the work captured in **Story 2-18 "Git Workflow Prompt Overhaul"** (`docs/stories/epic-2/story-2-18/`), Phases 4 & 5 ("Review Fix"). Cross-cuts the **Epic 32 agent-architecture pivot** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`) because `ApplyReviewFixesActivity` is a listed LLM-mediation violator, and Story 2-9 ("PR Status Monitoring", AC: "System automatically addresses review comments when possible").

---

## 2. Maturity: THIN

The workflow has the right outer shape (analyze → decide → generate → "apply" → index → outputs) but the load-bearing middle is hollow:

- It **never applies the generated fix to the repository.** `ApplyReviewFixesActivity` parses the LLM JSON into `ReviewFixResult { FixedCode, FilesFixed, FixDescriptions }` and stops. There is **no file write, no `git add`, no commit, no push** (confirmed: no `FileWrite`/`WriteAllText`/`git_commit`/`push` in the activity). The fix text dead-ends — it is handed to `UpdateCodeIndexActivity` (which only POSTs paths to the KB indexer) and then discarded.
- It **never re-requests review, replies to / resolves the comment threads, or re-triggers CI.** After "Apply Fixes" it just sets three `SetOutput` flags and ends.
- It **ignores the `success` output of the dispatched `llm-call`** — `Connect(generateFixes, applyFixes)` has no failure edge, so an LLM error silently flows into "apply" as if it had fixed code (false-success risk; violates the project's no-silent-failure rule).
- It **emits zero DCB audit events** for any step (analysis, fix generation, apply, push) — breaking the 100%-audit-trail invariant.
- It is **not wired into the autonomous loop.** `SingleIssueCycleWorkflow` does not dispatch `review-fix`; the only review-comment handling there is a CI re-test branch. So today this workflow is a standalone leaf with no production caller.

This matches Story 2-18's own assessment ("`ApplyReviewFixesActivity` is currently a stub … it does not actually apply anything") — partially improved since (it now dispatches `llm-call` and parses a real response), but the apply/commit/push/re-review core is still missing, so it remains thin rather than partial.

---

## 3. Current Capabilities (what it does today)

Flowchart (`ReviewFixWorkflow.cs`), start = `AnalyzeReview`:

1. **AnalyzeReview** (`AnalyzeReviewActivity`) — fetches PR review comments via `IGitHubIntegrationService.GetPullRequestReviewCommentsAsync(repo, prNumber)`, **heuristically** categorizes each (`bug/security/performance/design/style/question/praise/unknown` by keyword matching — no LLM), assigns a priority, computes `HasActionableComments`, serializes a `ReviewAnalysisResult` to `AnalysisJson`. Outcomes `Done`/`Error`.
2. **HasActionable?** (`FlowDecision`) — `True` → generate; `False` → straight to outputs.
3. **Generate Fixes** (`DispatchWorkflow` → `llm-call`) — mediated LLM call with `agentRole=Developer`, `action=AddressReviewComments`, prompt = `"Apply fixes for the following review comments:\n" + SecurityHelpers.SanitizeForPrompt(analysisJson)`, `WaitForCompletion=true`, result captured in `llmResultVar`. (This part already honors the mediation rule — it routes through `llm-call`, not a direct provider call.)
4. **Apply Fixes** (`ApplyReviewFixesActivity`) — takes the `llmResponse` string from the dispatch result, parses JSON (`fixedCode`, `filesFixed`, `fixDescriptions`) into a `ReviewFixResult`, sets `FixesApplied` from `result.Success` (which is just "did the JSON contain files or code"). **Does not touch the filesystem or git.** Retains a non-mediated internal fallback path (`CallLlm` → Anthropic `/v1/messages`, `CallEngineCallback`) used only when no external response is supplied.
5. **Update Code Index** (`UpdateCodeIndexActivity`) — best-effort POST of `FilesFixed` to the KB indexer; swallows all failures.
6. **OutputSuccess / OutputHasComments / OutputFixesApplied** — three `SetOutput` flags.

**Inputs:** `repository` (string), `prNumber` (int), `branchName` (string).
**Outputs:** `success` (always `true`), `hasComments` (bool), `fixesApplied` (bool).
**Variables:** `HasActionable`, `AnalysisJson`, `FixesApplied`, `FixResult`, `llmResult`.

---

## 4. Intended Full Scope (with citations)

A production-complete "address review comments" phase, per the cited sources, must:

- **Classify comments with AI context, not just keywords; filter resolved/outdated threads; distinguish inline vs PR-level comments; populate `SuggestedFix`.** — Story 2-18 (`2-18-git-workflow-prompt-overhaul.md` §3–4): "No AI analysis … `SuggestedFix` … never populated … No filtering of resolved/outdated threads … No distinction between inline code comments … and general PR-level comments." Impl-plan Phase 4 adds a `ClassifyReviewCommentsActivity` (LLM classification + context enrichment).
- **Build a contextualized fix prompt** (surrounding code, the diff, what the reviewer meant), ordered by priority (critical first) — Story 2-18 §4 + impl-plan Testing Checklist ("Fix prompt: includes surrounding code context", "ordered by priority").
- **Actually apply the fix to the working tree, then verify it** (type-check/lint via the testing pipeline), then **commit and push** — Story 2-18 §4 ("`ApplyReviewFixesActivity` is a stub … It does not actually apply anything … No verification step after applying fixes"); impl-plan Phase 5 flow: `GenerateFixes → ApplyFixes (actually apply changes, not stub) → VerifyFixes (run type-check/lint) → UpdateCodeIndex`. Story 2-18 Risk table: "Phase 5 must fully implement this; block merge on incomplete implementation."
- **Re-submit for review / re-trigger CI and reset the retry budget on re-entry** — Story 2-18 §4 ("When fixes are applied and CI is re-run … `ciRetryCount` is NOT reset … known bug"); impl-plan "SingleIssueCycle (Phase 6 fix)": `HasComments? YES → ResetCiRetryCount → DispatchCiRetry`. CodeReviewWorkflow establishes the canonical loop (`deliver guidance → wait for fixes → re-request review → loop to monitor, max 5 iterations → escalate`).
- **Sit inside the autonomous loop**, not as an orphan — `docs/architecture.md` §"Base 14-Step Workflow" (CODE_REVIEW → PR_CREATION → CI_CHECK steps with 3-retry quality gates); `docs/PRD.md` (the toil Tamma removes explicitly includes "addressing review comments"); Story 2-9 AC "System automatically addresses review comments when possible."
- **Honor the LLM mediation rule with NO direct fallback** — `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1 "A workflow STEP MUST NEVER call an external API/provider directly", §1.2 violations table flags `ADL/ApplyReviewFixesActivity` as a **P0 LLM violator** ("direct keyed LLM fallback … must be removed and routed through `call-LLM`"). The activity's git reads (`AnalyzeReview`) and any git writes are **VIOLATION-by-co-hosting** — git ops belong behind the internal API too (Epic 38 / §6 follow-up).
- **Emit DCB audit events** at every meaningful step — `CLAUDE.md` "Event Sourcing (DCB Pattern)": "All operations must emit events for audit trail"; event-type pattern `AGGREGATE.ACTION.STATUS`.
- **Robust failure handling**: branch on `llm-call` `success`; surface analysis `Error` outcome; never report `success=true` when nothing was fixed; idempotency (don't re-apply the same fix on re-entry); resolution/no-empty-fallback discipline.

---

## 5. Missing Capabilities (gap to "complete")

| # | Capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Apply the generated fix to the working tree** (write `FilesFixed` content from `FixedCode`) — today nothing is written | P0 | none (Epic-38 once git/FS is mediated) |
| 2 | **Commit & push the fix** to `branchName` (reuse the `git_commit`/engine-callback pattern from `CommitChangesActivity`) | P0 | Epic 38 (git write mediation) |
| 3 | **Branch on `llm-call` `success`** — add a failure edge so an LLM failure does not flow into "apply" as false-success | P0 | none |
| 4 | **Wire `AnalyzeReview`'s `Error` outcome** to a failure path (today it's an unconnected dead end → workflow faults) | P0 | none |
| 5 | **Do not report `success=true` / `fixesApplied=true` unless files were actually written & committed** (no silent false-success) | P0 | depends on #1/#2 |
| 6 | **Remove the direct-LLM fallback in `ApplyReviewFixesActivity`** (`CallLlm` → Anthropic `/v1/messages`) and force the mediated path | P0 | 32-5 (`POST /api/v1/llm/call`) |
| 7 | **Emit DCB audit events** (`REVIEW.ANALYZED`, `REVIEW.FIX_GENERATED`, `REVIEW.FIX_APPLIED.SUCCESS/FAILED`, `REVIEW.FIX_PUSHED`) | P0 | none |
| 8 | **Re-request review / reply-resolve comment threads** after pushing fixes | P1 | Epic 38 (git write mediation) |
| 9 | **Re-trigger CI and reset `ciRetryCount`** on review-fix re-entry (documented bug) | P1 | Story 2-18 Phase 6/7 |
| 10 | **Verify fixes** (type-check / lint via the testing pipeline) before pushing | P1 | none |
| 11 | **Wire `review-fix` into `SingleIssueCycleWorkflow`** so it actually runs in the ADL (it has no caller today) | P1 | Story 2-18 |
| 12 | **AI-based comment classification + context enrichment** (`ClassifyReviewCommentsActivity`), populate `SuggestedFix`, filter resolved/outdated threads, split inline vs PR-level | P1 | 32-5 |
| 13 | **Contextualized fix prompt** (surrounding code + diff + reviewer intent), ordered critical-first | P1 | 32-5 |
| 14 | **Iteration cap + escalation** when fixes don't converge (mirror CodeReviewWorkflow's max-5 + escalate) | P2 | none |
| 15 | **Idempotency** — don't re-apply the same comment's fix across re-entries (track resolved comment ids) | P2 | none |
| 16 | **Per-comment failure reporting** (post a reply when a comment couldn't be auto-fixed; flag for human) | P2 | Epic 38 |
| 17 | **Mediate the git reads** (`AnalyzeReview` GitHub call) through the internal API to fix VIOLATION-by-co-hosting | P2 | Epic 38 |
| 18 | **Tenant/session scoping & cost attribution** carried through to the `llm-call` (so usage is metered to the right tenant) | P3 | 32-9 |

---

## 6. Ordered Build-Out Spec

Reach a complete, robust `review-fix`. Steps honor: tenant→system→error resolution, no empty/plain fallback, no silent false-success, steps never call providers directly (route via `call-LLM` / internal API), DCB events everywhere.

1. **Make `AnalyzeReview` robust + audited.**
   - Connect its `Error` outcome to a new terminal `OutputFailure` (`SetOutput success=false`, `errorReason="analysis_failed"`) — do not leave it dead-ended.
   - Have it emit DCB `REVIEW.ANALYZED.SUCCESS` (tags: `prId`, `repository`, counts) / `REVIEW.ANALYZED.FAILED`.
   - (P1) Replace pure-keyword categorization by dispatching classification through `llm-call` (`action=ClassifyReviewComments`) — populate `SuggestedFix`, filter resolved/outdated threads, split inline vs PR-level. Keep the heuristic as a deterministic pre-filter only.

2. **Add the `llm-call` success gate after Generate Fixes.**
   - Read `success` (and `errorCode`) from `llmResultVar`. Add `FlowDecision GenerateSucceeded?`:
     `True` → `ApplyFixes`; `False` → emit DCB `REVIEW.FIX_GENERATED.FAILED` → `OutputFailure`.
   - Emit `REVIEW.FIX_GENERATED.SUCCESS` on the true edge (tags include `provider`, `costUsd`, `tokensUsed` already returned by `llm-call`).

3. **Turn "Apply Fixes" into a real apply step.**
   - In `ApplyReviewFixesActivity` (or a new `WriteReviewFixesActivity`), write each `FilesFixed`/`FixedCode` entry to the working tree via the mediated FS/engine-callback path (the `IFileSystemTool`/`Engine:CallbackUrl` pattern used by the TDD activities) — never a direct provider/SDK call.
   - **Delete the direct-LLM fallback** (`CallLlm` → Anthropic `/v1/messages`) from the activity; require the response to come from the dispatched `llm-call` (per Epic-32 §1.2 / 32-5). If no mediated response is available → `Error`, never simulate success in production.
   - Set `FixesApplied=true` **only after files are written**, not merely because JSON parsed.
   - Emit DCB `REVIEW.FIX_APPLIED.SUCCESS` (tags: `filesFixed`) / `REVIEW.FIX_APPLIED.FAILED`.

4. **Add VerifyFixes (P1).**
   - New step dispatching the testing/quality pipeline (lint + type-check) against the changed files.
   - `FlowDecision VerifyPassed?`: `False` → loop back into Generate Fixes with the verify errors appended to the prompt, bounded by an iteration counter (see step 7); or → `OutputFailure` when the budget is exhausted.

5. **Add Commit & Push.**
   - New `CommitReviewFixActivity` (model on `CommitChangesActivity`: `git_commit` via engine-callback / mediated git API, message e.g. `fix(review): address PR #{prNumber} comments`), then push to `branchName`.
   - Emit DCB `REVIEW.FIX_PUSHED.SUCCESS` (tags: `commitSha`, `branch`, `prId`) / `REVIEW.FIX_PUSHED.FAILED`. On push failure → `OutputFailure` (no silent success).
   - All git writes route through the internal API (Epic 38) — not a co-hosted token.

6. **Re-request review + re-trigger CI.**
   - After a successful push: `ReRequestReviewActivity` (reuse from `Tamma.Activities/Review`) to re-notify reviewers and (P2) reply/resolve the addressed comment threads.
   - Dispatch CI re-run; **reset `ciRetryCount`** on this re-entry (the documented Story 2-18 bug) — do this where the parent loop owns the counter.
   - Emit `REVIEW.REREVIEW_REQUESTED` / `CI.RETRIGGERED`.

7. **Iteration cap + escalation (P2).**
   - Add `Iteration`/`MaxIterations` variables (mirror `CodeReviewWorkflow`'s max-5). When fixes don't converge, route to an `EscalateReviewActivity` (human bookmark) instead of looping forever. Emit `REVIEW.ESCALATED`.

8. **Idempotency (P2).**
   - Track which comment ids have been resolved (in `AnalysisJson` / a workflow variable) so re-entries don't re-apply the same fix. Skip already-resolved threads in step 1's filter.

9. **Wire into the loop (P1).**
   - Dispatch `review-fix` from `SingleIssueCycleWorkflow` on the "PR has actionable review comments" branch (replacing/feeding the current CI-only re-test), passing `repository`, `prNumber`, `branchName`, tenant/session context. Carry the result (`fixesApplied`, `success`, `commitSha`) back into the cycle for the merge gate.

10. **Outputs & terminal hygiene.**
    - Add `errorReason` / `commitSha` / `filesFixedCount` outputs. Ensure every branch terminates at exactly one of `OutputSuccess` (only when files committed+pushed or genuinely no actionable comments) or `OutputFailure`. `success` must reflect reality, never a constant `true`.

**Net new/changed pieces:** failure edges on `AnalyzeReview.Error` + `llm-call success`; real file-write in apply (delete direct-LLM fallback); `VerifyFixesActivity`; `CommitReviewFixActivity` + push; `ReRequestReviewActivity` + CI re-trigger with `ciRetryCount` reset; iteration cap + `EscalateReviewActivity`; `ClassifyReviewCommentsActivity` + contextualized prompt; DCB events at analyze/generate/apply/push/re-review; wire into `SingleIssueCycleWorkflow`.

---

## 7. Effort & Priority

- **Overall priority: P0** — the workflow today does not perform its core function (it never applies a fix), silently reports success, and emits no audit trail. These are correctness/contract defects, not polish.
- **Effort: L** — multiple new activities (write/verify/commit-push/classify), real git+FS integration via the mediated path, the `call-LLM` cutover for `ApplyReviewFixesActivity`, DCB event wiring, the iteration/escalation loop, and integration into `SingleIssueCycleWorkflow`. Several items are gated on external work (32-5 `call-LLM`, Epic 38 non-LLM/git mediation).
