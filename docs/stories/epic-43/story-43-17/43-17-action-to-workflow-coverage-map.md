# Story 43-17: Action-to-Workflow Coverage Map — Every Dial Row Names Its Performer, or Says Why Not

Status: drafted

Implements: Story 43-11's **Caller-kind re-audit** (the 156-row dial table) and **Missing actions** section, as a permanent, build-enforced map instead of a one-off hunt. Extends the 43-8 sweep family with the one direction it does not run.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **platform operator reading the dial**,
I want every dial-governed action to either name the code that performs it or carry an explicit RESERVED marker with a reason and an owner, kept true by a build-time harness,
So that a level is never assigned to a phantom, a workflow can never quietly perform an uncatalogued action, and "what does this dial position actually change" is answerable from the map instead of a grep.

## Priority

P1 — 43-11 assigned a level to all 156 dial rows, but a level on a row nothing performs is decoration, and the hand-audit that found this (43-11's Missing-actions hunt) rots the day it is merged. The measured state today: **86 of 156 dial rows have a live performer; 70 do not** — 47 of those are owned by a named drafted story, **23 have no performer and no owner**, and **6 live LLM-reachable code paths perform actions with no catalog key at all**. Nothing currently fails when any of those numbers drifts.

## Architectural Context (READ FIRST)

### 1. Performer ≠ enforcement site — the map is a third fact

`ActionEnforcementSitesTests.cs:159-176` pins **21 of 197 rows bound to a live seam** — that is where the gate *reads*. A **performer** is where the action *happens*, gated or not: `agent-action:deploy` has no seam, but it is performed at `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs:722` (`["action"] = (deploy ? AgentAction.Deploy : AgentAction.Rollback).ToWire()`). The 43-8 sweeps prove every *governed route* has a catalog key; nothing proves every *catalog row* has a performer, and nothing covers the dispatch plane at all.

### 2. The four performer planes, and the evidence source for each

| Plane | What performs | Evidence in the tree | Existing sweep |
|---|---|---|---|
| **Route** | governed HTTP routes | `.Governs()` / `[Governs]` metadata, discovered by `ActionEnforcementSites` (`apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionEnforcementSites.cs:69-98`) | `GovernedEndpointCoverageSweepTests` / `GovernedEndpointBindingSweepTests` (43-8 AC2/AC3; name mapping at `43-8-drift-harnesses.md:104-111`) |
| **Method** | engine mediation calls | 17 `[PerformsEffect(...)]` attributes on `TammaApiClient` (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs:267-944`) | `MediationClientEffectSweepTests` (43-8 AC4) |
| **Tool** | the tool loop | DI-registered executors behind `InlineToolLoopRunner`; all 8 `tool:*` members verified (43-11 "Checked and clean", Missing-actions close) | `DispatchPairCatalogSweepTests` (43-8 AC7) — partial |
| **Dispatch** | workflow → LLM cells | `["action"] = AgentAction.X.ToWire()` dispatch inputs (e.g. `CodeReviewWorkflow.cs:284`, `DeploymentPipelineWorkflow.cs:722`); `RolePhaseMap.LegacyPhaseAliases` (`apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:302-313`); document producers pinned in `ContractBindingTests` (`Bindings` at `tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:94`, `IntentionallyUnbound` at `:389`, `PendingProducerCells` per 41-1b AC6) | **none** — this story adds it |

Document-type acceptances have a fifth, shared performer: the lifecycle itself (`DocumentLifecycleWorkflow` + `PanelReviewWorkflow` / `SingleReviewerWorkflow` decide acceptance) — but a type nobody produces is never accepted, so a doc-type row's performer status follows its **producer**.

### 3. Why a fixture, not another audit

43-11's Missing-actions hunt found live, ungoverned routes (`POST /api/engine/create-issue`, `/issue-comment`, `/issue-labels`, `/trigger-ci`, `/execute-task`) *after* the catalog was declared complete — proof that the manual map was already stale when written. The map must be **derived from the tree at build time** and compared to a checked-in expectation, exactly the ratchet pattern 43-8 established (`KnownUngovernedEndpoints`, `KnownNonEffectClientMethods`) and 43-13 AC4 repeats for the machinery inventory.

### 4. The governed universe this story ranges over

**156 dial rows** (43-11 count check: 155 shipped descriptors + `effect:secret.read`, which is minted on paper by Amendment 4 but has **no descriptor in code until 42-10 lands**). The 42 machinery rows are out of scope here — 43-13 AC4's fixture owns them; the two fixtures must be disjoint and sum to the catalog. Keys minted later grow the universe: 43-12 (−2 retired, +10: three merge, five deploy, `git.checks.bypass`, `git.webhook.register`) and 31-13 (+10: seven PR-operation keys, three issue keys) bring it to **174**; each minting PR edits this story's fixture in the same diff (AC7).

## The Current Map (measured 2026-08-03)

Status legend — **P** = performed (live performer named), **R/o** = reserved with a named owning story, **R/–** = reserved with **no owner** (a finding: the fixture carries these with an explicit marker and they are flagged to the product owner), **V** = performer status could not be proven either way (verify during implementation; the fixture forces the answer).

Summary: **86 P · 47 R/o · 23 R/– = 156.** Separately, **6 live holes** (code performing uncatalogued actions) — see the holes table.

### Zone 5 — Read-only (42)

| Action | Status | Performer / reason |
|---|---|---|
| `agent-action:analyze-assessment-response` | P | dispatched by `AssessmentWorkflow.cs` |
| `agent-action:clarify-requirements` | P | `ClarifyingQuestionsWorkflow` (run A produce cell, `ContractBindingTests.cs` Bindings) |
| `agent-action:context-scan` | P | `ContextGatheringWorkflow` |
| `agent-action:create-tasks` | P | `TaskCreationWorkflow` (Bindings) |
| `agent-action:debug-rootcause` | P | `DebugDiagnosisWorkflow.cs:127` (producer cell) |
| `agent-action:decompose-issue` | P | `IssueDecompositionWorkflow` (Bindings) |
| `agent-action:define-acceptance-criteria` | P | `AcceptanceCriteriaAuthoringWorkflow` (41-2, done) |
| `agent-action:generate-assessment-questions` | P | `AssessmentWorkflow` |
| `agent-action:prioritize-backlog` | P | `BacklogPrioritizationWorkflow` (41-3 template rewrite still drafted; the dispatch exists) |
| `agent-action:research` | P | `ResearchWorkflow` (Bindings) |
| `agent-action:resolve-blocker` | P | `BlockerDiagnosisWorkflow.cs:228` |
| `agent-action:score-ambiguity` | P | `AmbiguityScoringWorkflow` (Bindings) |
| `agent-action:triage-context-scan` | P | `TriageContextGatheringWorkflow` |
| `agent-action:triage-intake` | P | `TriagePODecisionWorkflow` (Bindings; legacy AlwaysEscalate floor still wins — 43-11) |
| `tool:file_read`, `tool:get_acceptance_rules`, `tool:search_code`, `tool:git_operations.read` | P | DI-registered tool executors, Seam B |
| `agent-action:analyze-security-incident` | R/o | 41-21 |
| `agent-action:assess-capacity` | R/o | 41-23 |
| `agent-action:assess-technical-risk` | R/o | 41-11 |
| `agent-action:assess-vulnerability` | R/o | 41-20 |
| `agent-action:audit-accessibility` | R/o | 41-28 |
| `agent-action:audit-dependencies` | R/o | 41-20 |
| `agent-action:coordinate-release` | R/o | 41-24 |
| `agent-action:diagnose-incident` | R/o | 41-22 (triage-panel lens per sprint-status) |
| `agent-action:facilitate-retro` | R/o | 41-8 Phase A |
| `agent-action:incident-rootcause` | R/o | 41-22 (new cell via 41-1a amendment) |
| `agent-action:manage-regression` | R/o | 41-16 |
| `agent-action:monitor-health` | R/o | 41-23 |
| `agent-action:plan-incident-response` | R/o | 41-22 |
| `agent-action:plan-roadmap` | R/o | 41-4 |
| `agent-action:plan-test-strategy` | R/o | 41-13 |
| `agent-action:threat-model` | R/o | 41-19 |
| `agent-action:track-impediments` | R/o | 41-6 |
| `agent-action:triage-pr` | R/o | 41-17 (PR-triage half) |
| `agent-action:triage-tech-debt` | R/o | 41-11 |
| `agent-action:plan-debugging` | R/– | no dispatcher, no owning story |
| `agent-action:plan-deployment` | R/– | no dispatcher, no owning story |
| `agent-action:plan-scope` | R/– | no dispatcher, no owning story |
| `agent-action:triage-defect` | R/– | no dispatcher, no owning story |
| `agent-action:triage-technical` | R/– | no dispatcher, no owning story |

### Zone 10 — Sensitive metadata reads (1)

| Action | Status | Performer / reason |
|---|---|---|
| `agent-action:audit-secrets` | R/o | 41-20 (metadata-only pin owned by 42-10 AC7) |

### Zone 15 — Write documentation (13)

| Action | Status | Performer / reason |
|---|---|---|
| `agent-action:summarize-changes` | P | `PullRequestWorkflow.cs:110` |
| `agent-action:summarize-stakeholder` | P | live-bound to `ContextGatheringWorkflow` (sprint-status, 41-5 correction) |
| `agent-action:write-adr` | P | `AdrAuthoringWorkflow` (41-9, done) |
| `agent-action:report-status` | R/o | 41-5 |
| `agent-action:synthesize-standup` | R/o | 41-7 |
| `agent-action:update-changelog` | R/o | 41-24 |
| `agent-action:write-api-docs` | R/o | 41-25 |
| `agent-action:write-postmortem` | R/o | 41-22 |
| `agent-action:write-release-notes` | R/o | 41-24 |
| `agent-action:write-retro-narrative` | R/o | 41-8 Phase B |
| `agent-action:write-runbook` | R/o | 41-26 |
| `agent-action:write-user-docs` | R/o | 41-25 |
| `agent-action:summarize-technical` | R/– | no dispatcher, no owning story |

### Zone 20 — Write Tamma's own records (16) — all performed

`effect:llm.call` (route `POST /api/v1/llm/call`, Seam A + `TammaApiClient.cs:267`); `effect:schedule.create|update|delete` (human — `ScheduledTriggerEndpoints`); the 8 `effect:tracker.*` writes (human — `TrackerEndpoints` dashboard routes; the LLM path is planned, level binds it when it arrives); `effect:mentorship.session.start|pause|resume|cancel` (human — `MentorshipController` `[Governs]` attributes). The **7 dormant HUMAN rows** (3 schedule + 4 mentorship) are performed by people and never dial-gated (43-13 AC7 fixture); they appear here as performed-dormant, not reserved.

### Zone 25 — Write code on a branch (24)

| Action | Status | Performer / reason |
|---|---|---|
| `agent-action:address-review-comments` | P | `ReviewFixWorkflow.cs:241` |
| `agent-action:implement-fix` | P | `BlockerDiagnosisWorkflow.cs:785`, `TestingWorkflow.cs:274` |
| `agent-action:incorporate-answers` | P | `ClarifyingQuestionsWorkflow` run B (Bindings) |
| `agent-action:plan-system-design` | P | `PlanGenerationWorkflow` (Bindings) |
| `agent-action:propose-design` | P | `DesignProposalWorkflow` (Bindings) |
| `agent-action:write-tests` | P | `TestCaseCreationWorkflow` (Bindings) |
| `tool:file_write`, `tool:git_operations.write` | P | DI-registered tool executors, Seam B |
| `agent-action:implement-feature` | **V** | **no dispatch site under its own token** — only `RolePhaseMap.LegacyPhaseAliases` maps `CODE_GENERATION`/`PR_CREATION` to it (`RolePhaseMap.cs:308-309`) and no live caller passes those phases (grep over `Tamma.ElsaServer` + `Tamma.Activities` = 0); the coding path (`SingleIssueCycleWorkflow` → `TddWorkflow`) drives implementation through the tool loop / agent dispatch, not an `implement-feature` cell. The flagship coding action may be performed only *indirectly* (via `tool:*` and `effect:agent-dispatch.run`). The fixture must settle this one way or the other. |
| `agent-action:author-ui-spec` | R/o | 41-27 |
| `agent-action:design-system` | R/o | 41-10 |
| `agent-action:draft-user-flow` | R/o | 41-27 |
| `agent-action:plan-migration-strategy` | R/o | 41-12 |
| `agent-action:plan-refactor` | R/o | 41-18 |
| `agent-action:plan-sprint` | R/o | 41-6 |
| `agent-action:write-regression-test` | R/o | 41-16 |
| `agent-action:design-api-contract` | R/– | no dispatcher, no owning story |
| `agent-action:design-data-model` | R/– | no dispatcher, no owning story |
| `agent-action:design-integration` | R/– | no dispatcher, no owning story |
| `agent-action:implement-infrastructure` | R/– | no dispatcher; 41-29 routes `infra` tasks to the coding path, not to this cell |
| `agent-action:plan-fix` | R/– | no dispatcher, no owning story |
| `agent-action:plan-implementation` | R/– | no dispatcher, no owning story |
| `agent-action:refactor` | R/– | no dispatcher (TDD's refactor leg does not dispatch this cell) |
| `agent-action:write-test-cases` | R/– | no dispatcher (`TestCaseCreationWorkflow` dispatches `write-tests`) |

### Zone 30 — Run tests (4)

| Action | Status | Performer / reason |
|---|---|---|
| `agent-action:debug` | P | `DebuggingWorkflow.cs:643` |
| `effect:ci.tests.trigger` | P | `TammaApiClient.cs:420` + governed route |
| `tool:run_tests` | P | DI-registered executor |
| `agent-action:exploratory-test` | R/o | 41-14 |

### Zone 35 — Create branch / PR (5) — all performed

`effect:git.branch.create` (`TammaApiClient.cs:287`), `effect:git.pull-request.create` (`:296`), `effect:git.release.create` (`:330`), `effect:git.issue.patch` (`:314`), `effect:jira.ticket.patch` (`:455`) — each also a Seam C route (`Program.cs:3403-3470` band).

### Zone 40 — Approve PRs / routine docs (27)

| Action | Status | Performer / reason |
|---|---|---|
| `agent-action:code-review` | P | `CodeReviewWorkflow.cs:284`; dispatched from `SingleIssueCycleWorkflow.cs:601`, `MentorshipWorkflow.cs:402` |
| `agent-action:code-review-architecture` / `-coverage` / `-security` | P | `CodeReviewWorkflow` lens dispatches |
| `agent-action:mentor-feedback` | P | `CodeReviewWorkflow.cs:319`, `MentorshipWorkflow` |
| `agent-action:plan-review` | P | `PlanReviewWorkflow.cs:45` (shim) ← `PlanGenerationWorkflow.cs:258`, `SingleIssueCycleWorkflow.cs:229` |
| `document-type:ambiguity-assessment` | P | producer `AmbiguityScoringWorkflow`; acceptance via lifecycle |
| `document-type:clarification` | P | `ClarifyingQuestionsWorkflow` |
| `document-type:decomposition` | P | `IssueDecompositionWorkflow` |
| `document-type:diagnosis` | P | `BlockerDiagnosisWorkflow` / `DebugDiagnosisWorkflow` |
| `document-type:findings` | P | `ResearchWorkflow` |
| `document-type:prose` | P | `AdrAuthoringWorkflow` (41-9) |
| `document-type:test-spec` | P | `TestCaseCreationWorkflow` |
| `document-type:triage-decision` | P | `TriagePODecisionWorkflow` |
| `agent-action:review-design` | R/o | 41-28 |
| `agent-action:review-docs` | R/o | 41-24/41-25 (tech_writer reviewer arm, 41-1a selector) |
| `agent-action:review-operability` | R/o | 41-26 (default reviewer) |
| `agent-action:verify-acceptance` | R/o | 41-15 |
| `document-type:backlog-ordering` | R/o | 41-3 |
| `document-type:test-plan` | R/o | 41-13 |
| `agent-action:plan-review-security` | R/– | no dispatcher, no owning story |
| `agent-action:review-acceptance` | R/– | no dispatcher, no owning story |
| `agent-action:review-compliance` | R/– | no dispatcher, no owning story |
| `agent-action:review-feasibility` | R/– | no dispatcher, no owning story |
| `agent-action:review-scope` | R/– | no dispatcher, no owning story |
| `agent-action:review-testability` | R/– | no dispatcher, no owning story |
| `agent-action:self-review` | R/– | no dispatcher, no owning story |

### Zone 45 — Approve binding docs (7)

| Action | Status | Performer / reason |
|---|---|---|
| `document-type:plan` | P | `PlanGenerationWorkflow` / `TaskCreationWorkflow` |
| `document-type:acceptance-criteria` | P | `AcceptanceCriteriaAuthoringWorkflow` (41-2, done) |
| `document-type:review` | P | review workflows persist review documents |
| `document-type:design` | P | `DesignProposalWorkflow` |
| `document-type:sprint-plan` (level **95**, owner 2026-08-03) | R/o | 41-6 |
| `document-type:threat-model` | R/o | 41-19 |
| `document-type:ux-spec` | R/o | 41-27 |

### Zones 50–100 (the consequential half)

| Action | Level | Status | Performer / reason |
|---|---|---|---|
| `agent-action:configure-cicd` | 50 | R/– | no dispatcher, no owning story |
| *(zone 50 slot)* `effect:git.checks.bypass` | 50 | R/o | **confirmed hole**: no key, nothing performs it; 43-12 mints it as a reservation |
| *(zones 55/60)* `effect:git.merge.dev|qa` | 55/60 | R/o | keys do not exist until 43-12; the coarse merge key carries the worst target |
| `effect:git.pull-request.merge` | 65 | P | route `Program.cs:3413` / `GitEndpoints.cs:48`, `TammaApiClient.cs:305`, `MergeWorkflow` + `MergeApprovalWorkflow`; retired by 43-12 on split |
| *(zone 70)* `effect:deploy.dev` | 70 | R/o | **confirmed hole**: no dev pipeline stage — the shipped pipeline is QA → UAT → Prod (`DeploymentPipelineWorkflow.cs:113`, corrected in 43-12); minted reserved by 43-12 |
| `effect:notify.slack.queue` | 75 | P | `TammaApiClient.cs:560` + `Program.cs:3509` |
| `effect:notify.email.send` | 75 | P | `TammaApiClient.cs:578` + `Program.cs:3520` |
| `effect:engine.channel-outbox.enqueue` | 75 | P | `TammaApiClient.cs:891` |
| `tool:shell_execute` | 80 | P | `ShellExecuteTool`, Seam B |
| `effect:process.spawn` | 80 | P | same executor's process spawn |
| `effect:agent-dispatch.run` | 80 | P | `TammaApiClient.cs:473` + `Program.cs:3484` |
| `effect:mcp.tool.invoke` | 80 | P | human `SettingsManage` route today; LLM path is design intent (43-11 re-audit FLAG stands) |
| *(zone 85)* `effect:deploy.staging`, `effect:git.webhook.register` | 85 | R/o | **confirmed holes**: no staging stage (43-12), `RegisterWebhookAsync` has drivers but no caller (`IGitPlatformClient.cs:101`; minted reserved by 43-12) |
| `effect:deploy.promote-prod` | 90 | P | Seam E, `DeploymentPipelineWorkflow.cs:274`; becomes `deploy.prod` in 43-12 |
| `agent-action:deploy` | 90 | P | `DeploymentPipelineWorkflow.cs:722` |
| `effect:secret.read` | 90 | R/o | **confirmed**: no descriptor in code; 42-10 mints it and enforces at the reveal route + tool-loop grading — *performed via tools once 42-10 lands*, never a route of its own |
| `effect:git.branch.delete` | 95 | P | `TammaApiClient.cs:381` + `Program.cs:3438` |
| `effect:deploy.rollback` | 95 | P | pipeline rollback arm (sole inbound edge after prod retries exhaust — 41-31's finding stands) |
| `agent-action:rollback` | 95 | P | `DeploymentPipelineWorkflow.cs:722` |
| `effect:tracker.project.delete`, `effect:tracker.work-item.delete` | 95 | P | human `TrackerEndpoints` routes; LLM path dormant |
| *(zone 100 slot)* tenant deprovision/move **requests** | 100 | R/o | no catalog keys — the admin request is the action, minting is the vocabulary decision recorded in 43-11's dial table (Level 100 note) |

### The holes — live code performing uncatalogued actions

From 43-11's Missing-actions live-code table, still true today; the fixture's second failure direction (AC3) exists to catch the next one of these:

| Live path | Missing key | Owner |
|---|---|---|
| `POST /api/engine/create-issue` (`EngineEndpoints.CreateIssue`; `SingleIssueCycleWorkflow` also dispatches a nonexistent `create-issues` workflow) | `effect:git.issue.create` | 31-13 (route/key), 40-8 (the workflow) |
| `POST /api/engine/issue-comment` | `effect:git.issue.comment` | 31-13 |
| `POST` + `DELETE /api/engine/issue-labels` | `effect:git.issue.labels.set` / `.remove` | 31-13 |
| `POST /api/engine/trigger-ci` (second, uncatalogued CI route) | bind to `effect:ci.tests.trigger` or mint `engine.ci.trigger` | **no owner — flagged** |
| `POST /api/engine/execute-task` (LLM execution without tools) | `effect:engine.task.execute` | **no owner — flagged** |
| `tool:git_operations.write` args passing `push --force` | `tool:git_operations.force-push` (arg-level split) | recorded in 43-11; carried known |

(`POST /api/engine/command` is a do-nothing stub — 43-12 deletes it rather than cataloguing a lie. `engine.context.store` and `engine.cycle-result` are machinery — 43-13's inventory, not holes. The KB, MCP-server and secret-admin human routes are proposed HUMAN keys in 43-11's Missing-actions list; they are dial-dormant by 43-13 and are not blocking holes.)

### The drafted epic-41 consumers and the actions they will perform

Per `docs/sprint-status.yaml` (epic-41 block, lines 620-665): **28 of the 34 workable epic-41 stories are still `drafted`** (done: 41-1a/1b/1c, 41-2, 41-9, 41-30). When built they become the performers for the R/o rows above: 41-3 `prioritize-backlog`→`backlog-ordering`; 41-4 `plan-roadmap`; 41-5 `report-status`; 41-6 `plan-sprint`+`track-impediments`→`sprint-plan`; 41-7 `synthesize-standup`; 41-8 `facilitate-retro`+`write-retro-narrative`; 41-10 `design-system`; 41-11 `triage-tech-debt`+`assess-technical-risk`; 41-12 `plan-migration-strategy`; 41-13 `plan-test-strategy`→`test-plan`; 41-14 `exploratory-test`; 41-15 `verify-acceptance`; 41-16 `manage-regression`+`write-regression-test`; 41-17 `triage-pr`+`code-review` (standalone); 41-18 `plan-refactor`; 41-19 `threat-model`→`threat-model`; 41-20 `audit-dependencies`+`assess-vulnerability`+`audit-secrets`; 41-21 `analyze-security-incident`; 41-22 `incident-rootcause`+`diagnose-incident`+`plan-incident-response`+`write-postmortem`; 41-23 `assess-capacity`+`monitor-health`; 41-24 `write-release-notes`+`update-changelog`+`coordinate-release`; 41-25 `write-user-docs`+`write-api-docs`; 41-26 `write-runbook`; 41-27 `draft-user-flow`+`author-ui-spec`→`ux-spec`; 41-28 `review-design`+`audit-accessibility`; 41-29 (router — dispatches existing cells); 41-31 rollback dispatch; 41-32 (alert→workflow seam). Each landing PR flips its fixture rows R/o → P (AC4's stale-marker ratchet).

## The workflow-update list (owned by the cited stories, NOT by this one)

Existing workflows and routes that must be edited to route through NEW keys. This story owns the map and the fixture only; the edits belong to the owners named:

| Existing code | Edit needed | Owning story |
|---|---|---|
| `DeploymentPipelineWorkflow` | Seam E rebinds `deploy.promote-prod` → `deploy.prod`; QA/UAT stage entries gain `deploy.qa` / `deploy.uat` gate calls | 43-12 AC4 |
| Merge path (`GitEndpoints.MergePullRequest`, `MergeWorkflow`, `MergeApprovalWorkflow`) | resolve `git.merge.dev|qa|main` from the PR base (fail-closed to main); the approval step mints the correlation grant covering the composite | 43-12 AC3, 43-14 |
| `SingleIssueCycleWorkflow` | the dead `create-issues` dispatch becomes a real workflow; its issue-creation route then carries `git.issue.create` | 40-8 (workflow), 31-13 (key) |
| Engine issue routes + `TammaApiClient` | new PR-operation methods carry `[PerformsEffect]` + `.Governs`/`.EnforcesGovernance()`; existing issue routes gain their keys | 31-13 AC2/AC3 |
| Reveal route + `InlineToolLoopRunner` | LLM-caller reveal gates on `secret.read`; tool-loop secret-read grading | 42-10 AC5/AC6 |
| `TammaApiClient` correlation header | cycle instance id threaded as `X-Tamma-Correlation-Id` on mediation calls | 43-14 |

## Acceptance Criteria

1. **The fixture exists and derives, not asserts.** `ActionPerformerCoverageSweepTests` (43-8 sweep naming) builds the map `actionKey → performers[]` from the four planes: route (`ActionEnforcementSites` output), method (`[PerformsEffect]` reflection over `TammaApiClient`), tool (the DI-registered executor set), and dispatch (a compile-time inventory of `["action"] = AgentAction.X.ToWire()` dispatch inputs plus `ContractBindingTests`' `Bindings` keys and `RolePhaseMap.LegacyPhaseAliases` targets, exposed from one shared source rather than re-grepped). A checked-in `ReservedActions` fixture carries `(actionKey, reason, owner)` for every row with no performer. Deleting a dispatch site without touching the fixture fails the build.
2. **Failure direction 1 — orphaned level.** A dial-governed action with no derived performer and no `ReservedActions` entry fails, naming the key and the four planes checked. Seeding the fixture with today's 70 reserved rows (47 owned + 23 unowned, as tabled above) makes the sweep green on day one; deleting any one entry makes it red — pinned by a meta-test.
3. **Failure direction 2 — uncatalogued performer.** A derived performer whose action key is not in `ActionCatalog.All` fails. This extends 43-8's route-plane guarantee to the dispatch plane: a workflow dispatching a token that is not a catalogued `agent-action` (after `RolePhaseMap.NormalizePhase`) is the named failure. The six live holes above are grandfathered in a `KnownUncataloguedPerformers` ratchet (43-8's `KnownUngovernedEndpoints` pattern) that 31-13 / 42-10 shrink and may never grow.
4. **Stale reservations fail.** A `ReservedActions` entry whose action HAS a derived performer fails ("remove the marker — the action is performed now"), so a landing epic-41 story must flip its rows in the same PR.
5. **The two fixtures partition the catalog.** `ReservedActions` ∪ performed ∪ 43-13's machinery inventory = every catalog member, each exactly once; overlap or gap fails. (Runs against whatever the current counts are — 156/42 today, 174/42 after 43-12 + 31-13 — by deriving from `ActionCatalog.All`, not a literal.)
6. **The `implement-feature` question is answered in the fixture**, not left open: either a dispatch/performer is proven (and the row is P with a citation) or the row enters `ReservedActions` with the recorded reason that implementation is performed via the tool loop and `agent-dispatch.run`, and the cell is dormant. Whichever answer, a test names it.
7. **Minting stories edit the fixture in the same diff.** The fixture file carries a header comment naming 43-12, 42-10 and 31-13 as pending editors; AC5's partition check is what actually forces the edit when their keys land.
8. **The 23 unowned reserved rows are surfaced**, not buried: the fixture marks them `owner: none`, and this story's table is referenced from 43-11's changelog so the product owner has one list to either assign or retire from the catalog (retiring is a 43-11 amendment + count-pin move, out of scope here).
9. **`dotnet test` green; no production code changes** beyond whatever small hook exposes the dispatch inventory to the test project (no behavior change; no schema change; no count pins move).

## Dependencies

- **Story 43-11** — the 156-row dial table and Missing-actions list are this story's input. Blocking.
- **Story 43-13** — the machinery inventory fixture this story's AC5 partitions against. Blocking (or land the shared fixture format together).
- **Story 43-8** — the sweep family and ratchet idiom this story extends. Landed.
- **Stories 43-12, 42-10, 31-13, 40-8** — future editors of the fixture (AC7); none blocking.
- **Verified in tree**: `ActionEnforcementSites.cs:69-98`; `ActionEnforcementSitesTests.cs:159-176`; `TammaApiClient.cs:267-944` (17 `[PerformsEffect]` sites); `RolePhaseMap.cs:302-313`; `ContractBindingTests.cs:94,389`; `CodeReviewWorkflow.cs:284,319`; `DeploymentPipelineWorkflow.cs:722,274`; `SingleIssueCycleWorkflow.cs:229,601`; `PlanGenerationWorkflow.cs:258`; `MentorshipWorkflow.cs:402`; `docs/sprint-status.yaml` epic-41 block (`:620-665`); `43-8-drift-harnesses.md:104-111`.

## Out of Scope

- **Building any performer.** The map records; 41-x / 43-12 / 42-10 / 31-13 / 40-8 build.
- **The workflow edits in the update list** — owned by the cited stories.
- **Retiring the 23 unowned rows from the catalog** — a 43-11 amendment with count-pin moves, taken only on a product-owner decision (AC8 surfaces the list).
- **Machinery performers** — 43-13's inventory already names them (43-11 machinery tables).
- **Enforcement changes** — no seam is added or moved; performer and enforcement site remain distinct facts.

## Estimated Effort

4 days — 2 for the per-action verification pass (settling the V row and the P attributions the fixture will encode), 2 for the sweep, the reserved/uncatalogued ratchets, and the partition check against 43-13's fixture.

## Change Log

| Date       | Version | Changes                                                                 | Author |
| ---------- | ------- | ----------------------------------------------------------------------- | ------ |
| 2026-08-03 | 1.0.0   | Initial story — coverage map (86 P / 47 R-owned / 23 R-unowned / 6 live holes), performer drift harness, workflow-update list cross-linked to owners | Claude |
