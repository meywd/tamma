# Workflow Build-Out Roadmap — 2026-06-22

Phase-2 execution plan from the **completeness pass** (`docs/superpowers/audits/2026-06-22/completeness/<wf>.md`,
37 per-workflow build-out specs) layered on the **correctness audit**
(`docs/superpowers/audits/2026-06-22/README.md`). The driving finding: **no workflow is "complete"** —
many are happy-path skeletons (`PullRequestWorkflow` = `CreatePR → 3× SetOutput`, the user's exemplar).

## Maturity (37 workflows)
- **complete: 0**
- **partial: 26** — core flow present, real gaps (error paths, missing steps, events, tenant scoping)
- **thin: 9** — skeletal; the primary build-out targets:
  `PullRequest`, `BranchCreation`, `Merge`, `MergeApproval`*, `UpdateIssueStatus`, `ReviewFix`,
  `TriageContextGathering`, `TriagePanelReview`, `TddWithDebugRetry`
- **stub: 0**

\* `MergeApproval` is thin **and dead** (orphaned — nothing dispatches it). `CiWithDebugRetry` (partial)
and `ProvisionTenantV2` (partial) are likewise wired-but-unreachable. **Do not build out a dead
workflow** — these need a wire-or-delete decision first (flagged to the user).

## Cross-cutting build-out themes (recur across nearly every workflow)
1. **Error paths + guaranteed terminal state** — most flows have no failure edge; a throw faults the
   instance silently (e.g. `AdlOrchestrator` never self-restarts on error → one transient error kills
   the autonomous loop permanently). Every workflow needs explicit error edges → a terminal
   `exitReason=error` + the matching `*.FAILED` DCB event.
2. **No false success / fail-closed** — triage/deploy/CI/status flows swallow sub-failures into success
   (`{}` context, fabricated defaults, COMPLETED-on-failure). Honor tenant→system→error.
3. **DCB run-level summary events** — add a per-run summary event for time-travel reconstruction;
   thin wrappers (git steps) emit none today.
4. **`tenantId` threading** — SaaS scoping of config/budget/prompts/creds (rides 32-5's mediated path).
5. **Step mediation** — LLM steps via `call-LLM` (32-5); git/Slack/agent-dispatch via Epic 38.

## Build-out waves (each workflow: target design → TDD new activities + workflow steps → verify gate → spec+quality review → PR)

### Wave 1 — the thin workflows (skeleton → complete), highest value
Order within the wave by value + independence:
1. **PullRequest** (exemplar) — add: PR-description generation from plan+tests (dispatch `llm-call`),
   reviewer assignment, labels, issue link/auto-close keyword, draft→ready, CI-check wiring, update
   issue status to "in review", create-failure path, idempotency (PR already exists for branch), DCB
   events. (git ops via existing `IGitHubIntegrationService`/Epic-38 seam; description via call-LLM.)
2. **BranchCreation** — idempotency (branch exists), base-branch validation, failure path, events.
3. **Merge** — pre-merge checks (mergeable/approved/CI-green), merge-method, post-merge issue close +
   status, conflict/failure path, events. (highest blast-radius git write.)
4. **UpdateIssueStatus** — stop swallowing failure into COMPLETED; real status transition + FAILED event.
5. **ReviewFix** — complete the fix-apply loop bounds + error path; (its `ApplyReviewFixes` 2nd direct-LLM
   path is repointed by 32-5 T6).
6. **TriageContextGathering**, **TriagePanelReview** — fail-closed (no `{}` fallback), per-role failure
   signal, events, Story 26-1 event-name alignment.
7. **TddWithDebugRetry** — graph-enforced loop bound + error/exhaustion terminal.

### Wave 2 — partial workflows with P0 correctness gaps
The `partial` set, ordered by P0 density from the completeness reports — e.g. `AdlOrchestrator`
(guaranteed-restart error path + budget/emergency-stop/quota enforcement — its `CheckLimits` advertises
budget+emergency-stop it never implements), `SingleIssueCycle` (merge-failure hang + missing error
edges), `DeploymentPipeline` (fail-open stage gating + no prod human-gate + no real deploy step),
`Testing` (unenforced CI-wait timeout), `IssueTriage`/`TriageItemCycle`/`TriagePODecision`
(silent-failure → false success), `BlockerDiagnosis` (always-Escalated bug), the planning/mentorship
cluster (`tenantId` threading, `Assessment` fake-heuristics → real call-LLM scoring), `RotateSecret`
(RETIRE-drain), tenant lifecycle (`DeleteTenant` FAILED terminal, `CreateTenant`/`CleanUp` polish).

### Wave 3 — coordinated with other tracks
- Bucket A LLM-activity repoints land with **32-5 T6**.
- Git/Slack/agent-dispatch mediation = **Epic 38**.
- Cost/budget enforcement depends on Epic 34/35/36 (price-book/usage/analytics).
- Dead-workflow decisions (`MergeApproval`, `CiWithDebugRetry`, `ProvisionTenantV2`) resolved first.

## Sequencing vs 32-5
The LLM-driven build-outs (PullRequest description-gen, ReviewFix, Tdd*, Assessment) dispatch `call-LLM`,
which 32-5 mediates — building them out now is fine (they dispatch the sub-workflow; the activity-level
mediation is 32-5's concern). Prefer landing **32-5 T6** before deep work on the Tdd/Debug/ADL LLM
activities so the build-out sits on the mediated base.

## Per-workflow build-out detail
See `docs/superpowers/audits/2026-06-22/completeness/<WorkflowName>.md` — each has the maturity rating,
intended full scope (with PRD/epic citations), the missing-capability table (priority + dependency), and
the ordered build-out spec that drives the implementation.
