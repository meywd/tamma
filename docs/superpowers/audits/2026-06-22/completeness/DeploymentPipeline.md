# Completeness Audit — DeploymentPipelineWorkflow

**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs`
**Definition id:** `deployment-pipeline`
**Audited:** 2026-06-22
**Verdict:** **PARTIAL** — real staged structure with per-stage failure branches and mediated llm-call dispatch, but missing the gates / releases / tags it advertises, no DCB audit events, an optimistic (silent false-success) result parse, no rollback, and no Business-Mode production approval. The doc-comment also describes behaviour (bookmark/signal waiting) the code does not implement.

---

## Purpose & owner

Post-merge deployment orchestration. Step 15 of the autonomous loop — invoked as a `WaitForCompletion` sub-workflow by `SingleIssueCycleWorkflow` after merge + issue-close (`SingleIssueCycleWorkflow.cs:591-608`). Takes a merged commit through **QA → UAT → Production** staged promotion, returning `deploymentStatus` + `completedStages`.

Owning loop step is PRD **FR-1 / FR-16 / FR-32** (deploy stage of the 14-step loop, quality gates at CI/CD, mode-adaptive deployment speed). No dedicated epic story for the C# DeploymentPipeline workflow was found in `docs/stories/` — deployment infra stories (Epic 1.5 1.5-1..1.5-9) cover packaging/CD-to-VPS, not this per-issue promotion pipeline. The workflow exists ahead of a written story spec.

---

## Maturity: PARTIAL

Calibration: noticeably more built-out than the `PullRequestWorkflow` "thin" baseline (which is `CreatePR → 3× SetOutput`, no branches). DeploymentPipeline has 3 real staged gates, a decision per stage, three distinct per-stage failure terminals, sequential promotion ordering, completed-stage accumulation, and correct routing through the `llm-call` mediation endpoint (no direct provider call — honours the 32-5 pivot rule). That is genuine core logic, not a placeholder. But the gap to "complete" is large and includes P0 correctness/safety items, so it is **partial**, not complete.

---

## Current capabilities (what it actually does today)

- **Init** (`SetVariable`): reads `repository`, `mergeSha`, `issueNumber`, `branchName` from workflow input; resets `completedStages` to `[]`.
- **Three sequential stages** QA → UAT → Production, each:
  - `StageDeployDispatch` → `DispatchWorkflow(llm-call)` with `role=devops`, `action=deploy`, `enableTools=true`, `WaitForCompletion=true`, passing stage + repo/sha/issue/branch/completedStages as variables. Routes LLM/agent work through the mediation endpoint — **does not** call a provider directly (compliant with the "steps never call external APIs" rule).
  - `ExtractStageResult` (`SetVariable`): parses the llm response, pulls a `status` field out of an embedded JSON blob, appends the stage to `completedStages` on non-failure.
  - `FlowDecision` `stageResult != "failed"` → True advances to next stage; False routes to that stage's failure terminal.
- **Per-stage failure terminals**: `SetQAFailed` / `SetUATFailed` / `SetProdFailed` set `deploymentStatus = "failed:<stage>"` and converge on Set Outputs.
- **Success terminal**: after Prod OK, sets `deploymentStatus = "success"`.
- **Set Outputs**: emits `deploymentStatus` + `completedStages`; `Finish`.

Available-but-unused building blocks in the tree: `GitHubActivity` (CreateBranch/Merge/RunTests — but **no** release/tag actions), `GitHubActionsExecutor` (dispatch+monitor+collect of a CI run), `WaitForMergeApprovalActivity` (bookmark/human-gate pattern to copy for a prod approval gate). There is **no** GitHub-release or git-tag activity anywhere in `apps/tamma-elsa/src` (verified by grep).

---

## Intended full scope (with citations)

What a complete post-merge deployment pipeline for this product must do:

- **Be the deploy stage of the 14-step loop** — issue → … → merge → **deploy**, targeting 70%+ autonomous completion (PRD lines 14, 30, 40 — FR-1).
- **Enforce quality gates with a 3-retry limit and mandatory escalation, no bypass** (PRD line 78 — FR-16). Each stage promotion is a gate; a failed gate must retry up to the limit then escalate, not silently pass.
- **Strategic human checkpoint before production deployment** — "Smart Friction… strategic checkpoints at critical decision points (design approval, breaking changes, production deployments)" (PRD line 374) and FR-32: "adapt… approval gates and deployment speeds based on selected mode and environment (dev vs production)" (PRD line 128). Business Mode requires approval before prod deploy ("zero deployments without approval in Business Mode", PRD line 279; approval chain in the audit journey, lines 265-275).
- **Rollback capability / rollback plan** — "Provide 'undo' and rollback capabilities" (PRD line 378); audit journey: "Deployed to production, rollback plan activated" / "rollback plan in place" (PRD lines 269, 275). A failed prod deploy must trigger or at least record a rollback path, not just stop.
- **Releases + tags** — the workflow's own `builder.Description` promises "Deploy through QA -> UAT -> Prod with **gates, releases, and tags**" (`DeploymentPipelineWorkflow.cs:44`). A complete prod promotion cuts a version tag and a release.
- **Full DCB audit trail** — every action emits an immutable event with tags for time-travel debugging + SOC2/audit compliance; the auditor journey explicitly filters "event type (deployments)" and reconstructs deploy-time state (PRD lines 250-268; architecture §DCB, lines 293-360). Deploy stages currently emit **zero** events.
- **Mediated LLM/agent execution** — any LLM/agent work routes through the tamma-api `POST /api/v1/llm/call` endpoint; steps never call providers directly (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1, §2; companion deep-dive). The current `DispatchWorkflow(llm-call)` already satisfies this.
- **No silent failure / no false success / tenant→system→error resolution** (CLAUDE.md project rules + `feedback_resolution_no_empty_fallback`).

---

## Missing capabilities

| # | Capability | Priority | dependsOn |
|---|---|---|---|
| 1 | **Optimistic status default is a silent false-success.** `ExtractStageResult` defaults `status="success"` and swallows parse failures in a bare `catch {}` — a missing/empty/garbled llm response, a deploy that errored, or a non-JSON response all read as success and promote to the next stage / report `success`. Must default to `failed` on missing/unparseable/empty result and on dispatch error. | P0 | none |
| 2 | **No DCB audit events.** No `DEPLOY.STAGE.STARTED/SUCCESS/FAILED`, no `DEPLOY.PIPELINE.COMPLETED/FAILED`, no `DEPLOY.PRODUCTION.APPROVED`. Breaks the compliance + time-travel requirement and the auditor journey (filter by deployment events, reconstruct deploy-time state). | P0 | none (event-emit activity/seam) |
| 3 | **No production approval gate.** PRD FR-32 + Business Mode require a human checkpoint before prod deploy; today prod runs automatically if UAT passes. Needs a bookmark-based gate (pattern: `WaitForMergeApprovalActivity`), conditional on mode/environment. | P0 | none (mode/env input) |
| 4 | **No rollback path.** A failed prod (or UAT) deploy just stops and reports `failed:<stage>`. PRD requires rollback capability / rollback-plan activation. Needs a rollback branch (revert/redeploy-previous) on prod failure + a `DEPLOY.ROLLBACK.*` event. | P0 | none (rollback activity) |
| 5 | **No release / tag step** despite the description claiming "releases, and tags". No git-tag or GitHub-release activity exists in the tree at all. A complete prod promotion should cut a version tag + release. | P1 | new CreateRelease/CreateTag activity |
| 6 | **No gate retry / escalation.** FR-16 mandates a 3-retry limit with mandatory escalation, no bypass. A failed stage gets no retry and no escalation event — it just terminates. | P1 | none (retry loop + escalation event) |
| 7 | **Doc-comment lies about behaviour.** Header says each stage "waits for an external signal (bookmark) confirming stage completion" — the code creates **no bookmark**; it is a synchronous `WaitForCompletion` dispatch. Either implement async deploy-confirmation bookmarks (real-world deploys are async) or correct the comment. Real CD deploys are long-running/async, so the bookmark model is likely the intended design. | P1 | none |
| 8 | **No idempotency / re-entrancy guard.** Re-running the pipeline for the same `mergeSha` would re-deploy and re-tag. Should check `completedStages` / prior events and skip already-completed stages. | P1 | none |
| 9 | **No environment/health verification after each deploy.** A deploy "status:success" from the agent is taken at face value; no post-deploy smoke/health check before promoting to the next stage. | P2 | post-deploy check activity |
| 10 | **Stage result depends on agent free-text JSON.** Pulling `status` out of `IndexOf('{')..LastIndexOf('}')` of the llm response is brittle. Should use a typed contract from the mediation result, not substring-scrape. | P2 | 32-5 typed result |
| 11 | **No per-stage timeout / stuck-deploy handling.** A hung deploy hangs the whole loop (`SingleIssueCycle` waits on it). Needs a timeout edge → fail/escalate. | P2 | none |
| 12 | **No notification on stage transitions / failures.** `Integration/SlackActivity` + `EmailActivity` exist; a complete pipeline notifies on prod-approval-needed and on failure. | P3 | none |
| 13 | **No tests.** No `*deploy*` test file found under `apps/tamma-elsa`. | P1 | none |

---

## Ordered build-out spec (to reach complete)

Honour project rules throughout: route all LLM/agent work via `DispatchWorkflow(llm-call)` (never call a provider in a step); resolution is tenant→system→error (never empty/plain fallback); never report success on missing/ambiguous results; emit a DCB event at every meaningful edge.

1. **Fix the silent false-success (P0, item 1).** In `ExtractStageResult`: default `status = "failed"`. Only set `success` when the mediation result is present AND a `status:"success"` (or equivalent typed field) is explicitly parsed. On dispatch error / null result / empty `llmResponse` / parse exception → `failed`, and capture the reason into a new `stageError` variable. Do **not** append a stage to `completedStages` unless it explicitly succeeded (already the case — keep it).

2. **Emit DCB audit events at every edge (P0, item 2).** Add an event-emit step (reuse the existing emit seam used by tenant-lifecycle / analytics workflows, or a small `EmitDeploymentEventActivity`) firing:
   - `DEPLOY.STAGE.STARTED` (tags: issueId, repository, mergeSha, stage, mode) before each `StageDeployDispatch`.
   - `DEPLOY.STAGE.SUCCESS` / `DEPLOY.STAGE.FAILED` (data: status, reason, durationMs) after each `ExtractStageResult`.
   - `DEPLOY.PRODUCTION.APPROVAL_REQUESTED` / `DEPLOY.PRODUCTION.APPROVED` / `DEPLOY.PRODUCTION.REJECTED` around the prod gate.
   - `DEPLOY.PIPELINE.SUCCESS` / `DEPLOY.PIPELINE.FAILED` (data: completedStages, failedStage) at the terminals.
   - `DEPLOY.ROLLBACK.STARTED` / `DEPLOY.ROLLBACK.SUCCESS` / `DEPLOY.ROLLBACK.FAILED` in the rollback branch.

3. **Add the production approval gate (P0, item 3).** Add a `mode` (+ `environment`) input. Before `ProdDeploy`, insert a `FlowDecision(mode == business || requireProdApproval)`:
   - True → `WaitForDeploymentApprovalActivity` (new, modelled on `WaitForMergeApprovalActivity`: bookmark `deploy-prod-approval-{issueNumber}-{mergeSha}`, resume via `POST /api/adl/{instanceId}/deploy-approval` with `{ decision: approve|reject, feedback }`; outcomes `Approve`/`Reject`). Emit `DEPLOY.PRODUCTION.APPROVAL_REQUESTED` on suspend; on `Approve` emit `…APPROVED` → `ProdDeploy`; on `Reject` emit `…REJECTED` → `SetProdFailed` (reason=rejected).
   - False (dev mode) → straight to `ProdDeploy`.

4. **Add rollback on production failure (P0, item 4).** Re-route `ConnectOutcome(prodOk, "False", …)` to a new `RollbackProduction` step before `SetProdFailed`. Rollback dispatches `llm-call` with `action=deploy`/a `rollback` variable (or a dedicated `PlanDeployment`+rollback flow) to revert prod to the previous release, emitting `DEPLOY.ROLLBACK.STARTED` then `…SUCCESS`/`…FAILED`. Then `SetProdFailed` (data includes rollbackStatus). Apply the same option to UAT if UAT deploys to a shared env.

5. **Add release + tag on successful prod (P1, item 5).** Add a `CreateReleaseActivity` (new — owner/repo, tag = computed semver or `mergeSha`-derived, release notes from issue/PR). Insert between Prod OK and `SetSuccess`. On tag/release failure: emit `DEPLOY.RELEASE.FAILED`, do not flip the pipeline to failed (deploy already succeeded) but record it in outputs/events. Emit `DEPLOY.RELEASE.CREATED` (data: tag, releaseUrl) on success and surface `releaseTag`/`releaseUrl` as outputs.

6. **Add gate retry + escalation (P1, item 6).** Wrap each stage in a bounded retry (max 3, per FR-16): a `RetryCount` variable per stage; on stage `failed` and `RetryCount < 3` → re-dispatch (emit `DEPLOY.STAGE.RETRY`); on exhaustion → `DEPLOY.STAGE.ESCALATED` event + route to the stage failure terminal (no silent bypass).

7. **Decide bookmark vs sync + fix the comment (P1, item 7).** If deploys are async (the realistic case), replace each synchronous `WaitForCompletion` dispatch with: dispatch deploy (fire-and-bookmark) → `WaitForDeploymentSignalActivity` (bookmark `deploy-{stage}-{issueNumber}-{mergeSha}`, resumed by the CD system / GitHub Actions webhook with the real deploy result). Otherwise correct the doc-comment to describe the synchronous model. Either way the header must match the code.

8. **Add idempotency guard (P1, item 8).** At Init, query prior `DEPLOY.STAGE.SUCCESS` events (or read inbound `completedStages`) for this `mergeSha`; skip stages already completed and emit `DEPLOY.STAGE.SKIPPED`. Make `CreateReleaseActivity` no-op-if-tag-exists.

9. **Add post-deploy health verification (P2, item 9).** After each `ExtractStageResult` success, add a `VerifyDeploymentHealthActivity` (smoke/health probe of the stage env) gating promotion; failure → treat as stage failure (with rollback for prod). Emit `DEPLOY.HEALTHCHECK.PASSED/FAILED`.

10. **Typed mediation result (P2, item 10).** Once 32-5 lands a typed `LlmCallResponse`/deploy contract, replace the substring JSON scrape with the typed `status`/`details` fields.

11. **Per-stage timeout (P2, item 11).** Add a timeout edge on each deploy/wait → emit `DEPLOY.STAGE.TIMEOUT`, route to failure/escalation so the parent loop never hangs.

12. **Notifications (P3, item 12).** On `APPROVAL_REQUESTED` and on `PIPELINE.FAILED`, dispatch `SlackActivity`/`EmailActivity`.

13. **Tests (P1, item 13).** Add workflow tests: QA-fail-stops, UAT-fail-stops, prod-approval-reject path, prod-failure→rollback path, happy-path success→tag/release, retry-then-escalate, idempotent re-run skips completed stages, and the silent-false-success regression (empty/garbled llm result must NOT promote).

---

### Notes
- The mediation/no-direct-provider rule is **already honoured** — `StageDeployDispatch` uses `DispatchWorkflow(llm-call)`, not a provider call. No work needed there beyond consuming the typed result (step 10).
- The `Devops` role (`AgentRole.cs:14`) and `Deploy` / `PlanDeployment` actions (`AgentAction.cs:87,90`) already exist, so the agent-side prompts/actions are in place.
