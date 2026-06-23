# Completeness Audit — MergeApprovalWorkflow

**Date:** 2026-06-22
**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeApprovalWorkflow.cs`
**Definition ID:** `merge-approval`
**Composed activity:** `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForMergeApprovalActivity.cs`

---

## Purpose & owner

The human **APPROVAL_GATE** for a completed PR: suspend the loop on a bookmark until a human chooses
**merge / test / reject**, then surface that decision (and free-text feedback) as workflow outputs so the
parent can branch. This is the "test/merge completion checkpoint" of the 14-step loop.

- **Architectural owner:** the `APPROVAL_GATE` step in the Base 14-Step Workflow (`docs/architecture.md` line 840, between `CI_CHECK` and `MERGE`).
- **Requirement owner:** PRD **FR-19** (approval at design + test/merge checkpoint), **FR-34** (mandatory manual approval for breaking changes, NEVER auto-approve), **FR-32** (mode-based gate behavior). `docs/PRD.md` lines 84, 134, 128, 215-219.
- **DCB / quality-signal owner:** Epic-32 **Story 32-8** (Outcome Capture & Bug Taxonomy *at Review/Gate*) — the gate is an explicit defect/outcome capture point.

---

## Maturity: **thin** (happy-path skeleton)

It is the user's exact complaint pattern: one bookmark activity wired straight to two `SetOutput`
nodes, with no branching, no events, no detection, no failure path, and an undelivered resume contract.

---

## Current capabilities (what it actually does today)

The whole workflow is:

```
WaitForMergeApprovalActivity ──► SetOutput("decision") ──► SetOutput("feedback")
```

- `WaitForMergeApprovalActivity` creates an auto-burn bookmark `adl-merge-approval-{issue}-{pr}` and suspends.
- On resume it reads `decision` ("merge"/"test"/"reject", default → "reject") and `feedback` from workflow input, sets them on outputs, and completes with a typed outcome (`Merge` / `Test` / `Reject`).
- The workflow then unconditionally flows `waitMerge → outputDecision → outputFeedback` via plain (untyped) `Connect()`, copies `decisionVar`/`feedbackVar` into workflow outputs `decision`/`feedback`, and ends.

That's it. It is a pure pass-through of a human decision string.

---

## Intended full scope (with citations)

A production-complete approval gate for an AI-authored PR must:

1. **Branch on the decision it already produces.** The activity declares `[FlowNode("Merge","Test","Reject")]` and `CompleteActivityWithOutcomesAsync(outcome)`, but the workflow ignores the outcomes (uses default `Connect`). A complete gate routes each outcome to a distinct edge — the established pattern in `CodeReviewWorkflow.cs` (lines 304-320: `new FlowEndpoint(monitorReview,"Approved")→merge`, `"ChangesRequested"→…`, `"TimedOut"→escalate`).
2. **Enforce breaking-change protection (FR-34).** `docs/architecture.md` lines 1674-1721: detect breaking changes (API signature/export removals, DB migrations) and require **mandatory** manual approval with **no timeout** and **escalation** — and NEVER auto-approve. The gate must refuse to emit a `Merge` outcome for a breaking change unless an explicit human did so.
3. **Be mode-aware (FR-32 / architecture line 861-862).** `APPROVAL_GATE.mode: business` requires a business-stakeholder approver; dev mode is lighter. The gate must resolve required-approver policy tenant→system (never silent default to "anyone").
4. **Handle timeout & escalation.** The comparable bookmark gates (`MonitorReviewActivity`, `WaitForFixesActivity`) expose a `TimedOut` outcome routed to an escalation node (`CodeReviewWorkflow` line 307/320). This gate has no timeout, no reminder, no escalation — a human who never answers hangs the loop forever with no audit trail.
5. **Emit DCB audit events (FR-19c / Story 32-8 / `TammaActivity.cs`).** The architecture lists "approvals/escalations" as a first-class captured event family (`docs/stories/epic-4/4-6-event-capture-approvals-escalations.md`). `WaitForMergeApprovalActivity` extends plain `Elsa…Activity`, NOT `TammaOutcomeActivity` (`Tamma.Activities/Core/TammaActivity.cs` line 234), so it emits **zero** `tamma:events` — unlike its sibling `WaitForPRApprovalActivity` which sets `EventType = "CYCLE.PR.APPROVAL.WAIT"` and implements `BuildStartData/BuildEndData`. Story 32-8 (AC1/AC3/AC7) requires the gate to record `AGENT.OUTCOME.RECORDED` and `AGENT.DEFECT.RECORDED` tagged with `agentId` + config version.
6. **Have a delivered resume contract.** The activity docstring promises `POST /api/adl/{instanceId}/merge-approval`. **No such endpoint exists** anywhere in `apps/tamma-elsa/src` (grep-verified). The bookmark also reads `decision` from `context.WorkflowInput`, so a generic resume must inject those keys — there is no documented, validated API surface to do so.
7. **Validate the decision input.** Unknown/empty `decision` silently maps to "reject" (activity line 96) — a malformed payload or a key-name typo silently rejects a good PR with no error and no audit signal. That violates the project rule "no silent-failure / no false result" and "never empty/plain fallback."
8. **Actually be wired into the loop, or be retired.** Grep shows `MergeApprovalWorkflow` (`merge-approval`) is dispatched by **nothing**. The live autonomous loop (`SingleIssueCycleWorkflow.cs` lines 538-581) uses a *different, simpler* gate: `WaitForPRApprovalActivity` (binary approve) → `DispatchWorkflow("merge")` → `WaitForPRMergedActivity`. So the 3-way merge/test/reject gate that the PRD persona flow demands ("Test in staging or merge directly?" — `docs/PRD.md` line 215) is built but orphaned, while the loop runs a thinner approve-only gate with no test-branch and no breaking-change guard.

---

## Missing capabilities

| # | Capability | Priority | dependsOn |
|---|---|---|---|
| 1 | Branch the workflow on the `Merge` / `Test` / `Reject` outcomes the activity already produces (use typed `FlowEndpoint(source, outcome)`); reject/test must not fall through to the same single path | P0 | none |
| 2 | Breaking-change detection + mandatory-approval enforcement (FR-34): block `Merge` for breaking changes (API/migration) unless an explicit human approved; never auto-approve | P0 | none (detector); use call-LLM mediation **only if** an LLM classifier is used (then route via 32-5) |
| 3 | Delivered, validated resume API surface (`POST /api/adl/{instanceId}/merge-approval` body `{decision, feedback, approver}`) — the documented contract that currently does not exist | P0 | none |
| 4 | DCB audit events for the gate decision: emit `APPROVAL.GATE.WAIT.STARTED`, `APPROVAL.GATE.DECISION.{MERGED\|TEST\|REJECTED}`, `APPROVAL.ESCALATED` (re-base activity on `TammaOutcomeActivity`); plus Story-32-8 `AGENT.OUTCOME.RECORDED` / `AGENT.DEFECT.RECORDED` tagged `agentId`+version | P0 | Story 32-8 (outcome/defect events); Story 4-6 (approval-event schema) |
| 5 | Decision input validation: reject unknown/empty `decision` with a typed error + `APPROVAL.GATE.DECISION.INVALID` event, never the silent → "reject" default | P0 | none |
| 6 | Mode-aware required-approver policy (dev vs business, FR-32) resolved tenant→system→error, never empty/plain default | P1 | none |
| 7 | Timeout + reminder + escalation path (no-timeout for breaking changes per FR-34; finite timeout → escalate for normal PRs), with a `TimedOut`/escalation terminal | P1 | none |
| 8 | Decide ownership vs `SingleIssueCycleWorkflow`'s `WaitForPRApprovalActivity`: either (a) wire `merge-approval` into the loop as THE gate (delivering the PRD test/merge decision point), or (b) fold its 3-way decision + breaking-change guard into the loop's existing gate and retire this workflow. Do not leave two divergent gates | P1 | none |
| 9 | On `Merge` outcome, actually trigger the merge (dispatch `merge` workflow / `MergePullRequestActivity`) and on `Reject` close/label the PR + notify; today the workflow emits a decision string and stops, taking no action on it | P1 | none |
| 10 | On `Test` outcome, route back to the test/CI sub-workflow (`testing` / `ci-with-debug-retry`) then re-enter the gate — the loop the PRD "run more tests before merging" decision implies | P2 | none |
| 11 | Idempotency on replayed/duplicate resume (per `(issue,pr)`), matching Story 32-8 AC8 — a replayed approval webhook must not double-merge or double-emit | P2 | Story 32-8 |
| 12 | Notify approver(s) when the gate opens (reuse the `NotifyIssue`/alert pipeline) so a human knows action is required | P2 | none |

---

## Build-out spec (ordered)

Reach a complete, robust gate by adding the following. Honor: steps never call external providers
directly (route any LLM use via the tamma-api call-LLM mediation, Story 32-5); never empty/plain
fallback (tenant→system→error); no silent-failure / no false-success; emit DCB events for every edge.

1. **Re-base the activity on `TammaOutcomeActivity`** (`Tamma.Activities/Core/TammaActivity.cs`). Set `EventType = "APPROVAL.GATE"`, implement `BuildStartData` (`issueNumber`, `prNumber`, `prUrl`, `requiredApproverRole`, `breakingChange`) and `BuildEndData` (`decision`, `approver`, `feedback`). This makes the gate emit `APPROVAL.GATE.STARTED` / `.COMPLETED` / `.FAILED` automatically.

2. **Add a breaking-change detection step before the bookmark.** New activity `DetectBreakingChangesActivity` (Tamma.Activities/Review or Assessment): pull changed files via `IGitHubIntegrationService`, flag `/api/` signature/export removals and any `/migrations/` change (per architecture `detectBreakingChanges`). Output `bool BreakingChange` + reasons. If an LLM is used to classify a diff, it MUST go through the call-LLM endpoint (`POST /api/v1/llm/call`, Story 32-5) — never an inline provider call. Emit `APPROVAL.BREAKING_CHANGE.DETECTED` when true.

3. **Resolve required-approver policy** (mode-aware, FR-32). New step / input: resolve `requiredApproverRole` tenant→system (Prompt/Settings store); error (do not default to "anyone") if unresolved. In `business` mode require a business-stakeholder approver; in `dev` mode allow the assignee. Feed this into the bookmark's start data and into resume validation.

4. **Harden `WaitForMergeApprovalActivity.OnMergeDecisionAsync`:**
   - Validate `decision ∈ {merge,test,reject}` (case-insensitive). On unknown/empty → set outcome `Invalid`, emit `APPROVAL.GATE.DECISION.INVALID`, do NOT silently reject.
   - Capture `approver` from input; if breaking-change is true and the decision is `merge`, require a human-approver field present and authorized for `requiredApproverRole` — otherwise route to `Invalid`/escalation (FR-34: never auto-approve breaking changes).
   - Emit `APPROVAL.GATE.DECISION.{MERGED|TEST|REJECTED}` with `approver`, `feedback`, `breakingChange`, `agentId`, `agentVersion` tags.

5. **Add a timeout/escalation arm.** Give the bookmark a finite timeout for non-breaking PRs (reminder at T/2, escalate at T) via a `Delay`/`Timer` companion; for breaking-change PRs use **no timeout** (FR-34). Add a `TimedOut`→`escalateApproval` node emitting `APPROVAL.ESCALATED`, mirroring `CodeReviewWorkflow`'s `escalateTimeout`.

6. **Rebuild the flowchart with typed-outcome branching** (mirror `CodeReviewWorkflow` lines 294-320):
   - `detectBreaking → resolvePolicy → notifyApprover → waitMerge`
   - `FlowEndpoint(waitMerge,"Merge") → onMerge`
   - `FlowEndpoint(waitMerge,"Test") → onTest`
   - `FlowEndpoint(waitMerge,"Reject") → onReject`
   - `FlowEndpoint(waitMerge,"Invalid") → notifyApprover` (re-prompt) or `escalateApproval`
   - `FlowEndpoint(waitMerge,"TimedOut") → escalateApproval`

7. **Implement the action on each outcome (stop emitting a bare string):**
   - **onMerge:** `DispatchWorkflow("merge")` (the existing `MergeWorkflow`, which already squash-merges + closes issue + deletes branch) → success terminal. Emit `MERGE.REQUESTED`.
   - **onTest:** `DispatchWorkflow("testing")` (or `ci-with-debug-retry`) with `WaitForCompletion=true`, then loop back to `waitMerge` for a re-decision. Emit `APPROVAL.TEST_REQUESTED`.
   - **onReject:** close/label PR (`tamma-rejected`) + comment the `feedback`, notify; terminal. Emit `APPROVAL.REJECTED`.
   - **escalateApproval:** notify owners/raise an alert (reuse `NotifyIssue`/alert pipeline); terminal `escalated` outcome.

8. **Capture Story-32-8 quality signals at the gate:** on completion emit one `AGENT.OUTCOME.RECORDED` (`outcome`, `iterationsToDone`) and, for any human-flagged defect in `feedback`, `AGENT.DEFECT.RECORDED` (`bugType` taxonomy, `source:"human"`), tagged `agentId`+`agentVersion`. Non-blocking, idempotent per `(issue,pr)` (AC8).

9. **Deliver the resume API.** Implement `POST /api/adl/{instanceId}/merge-approval` (body `{decision, feedback, approver}`) in a new ADL endpoints file: validate body, resolve the running instance, resume the `adl-merge-approval-{issue}-{pr}` bookmark with those keys injected as workflow input (via `IElsaWorkflowService`/the engine resume seam used by `MentorshipController`). RBAC: in SaaS only `tenant_owner`/`tenant_admin` (or the resolved `requiredApproverRole`) may submit; reject others 403. Make resume idempotent (AutoBurn already prevents double-burn; add a dedupe guard for replays).

10. **Resolve the duplicate-gate problem.** Either wire `merge-approval` into `SingleIssueCycleWorkflow` in place of the bare `WaitForPRApprovalActivity` (so the loop gains the test-branch + breaking-change guard the PRD requires), OR fold steps 2-8 into the loop's existing gate and retire `MergeApprovalWorkflow`. Pick one; do not ship two divergent approval gates.

11. **Tests:** decision branching (each outcome → correct terminal/action), breaking-change forces manual-approval and blocks auto-merge, invalid/empty decision → `Invalid` (not silent reject), timeout→escalate (and no-timeout for breaking), DCB events emitted on every edge, resume-endpoint RBAC + idempotency, tenant isolation of outcome/defect rows.

---

## Summary

`MergeApprovalWorkflow` is **thin**: a real human-decision bookmark whose 3-way outcome is built but
thrown away (untyped fan-out to two `SetOutput`s), with no breaking-change guard (FR-34), no
mode-aware approver policy (FR-32), no timeout/escalation, no DCB audit events (the activity isn't even
a `TammaActivity`), an undelivered resume endpoint, a silent unknown→reject fallback, and no action
taken on the decision — and it is currently orphaned (dispatched by nothing) while the live loop runs a
thinner approve-only gate. Overall priority **P0** (FR-34 safety + no-silent-failure + missing audit
trail), effort **L**.
