# Implementation Plan — Story 41-31: Standalone Emergency Rollback

> **This story exists because another story's plan proved it had to.** 41-22's implementation plan
> (finding C5) established that *"there is no standalone rollback entry point… AC3's 'a rollback is
> performed by dispatching `deployment-pipeline`' [is] not implementable"*, and recorded it as *"filed,
> not fixed here"*. It was never filed anywhere — `.dev/findings/` contains no such file (verified).
> This plan closes both halves: the capability, and the paper trail.

## Scope & Deliverable

A new `DefinitionId = "emergency-rollback"` execution workflow (not a document producer), one shared
`ResolveLastKnownGoodReleaseActivity`, one start endpoint, and a **one-variable amendment** to
`DeploymentPipelineWorkflow`'s existing rollback dispatch so both rollback paths pass a resolved target.

**Not in scope:** a governed `deploy_control` tool (42-8b), post-deploy health probing, any change to
the pipeline's stage graph beyond the single variable.

## Pre-Reading

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs` — read **all** of:
  `Init` variable seeding (`:162-180`, note `mergeSha` at `:169`), `MaxStageRetries = 3` (`:102`), the
  rollback nodes (`:298-329`), `StageDeployDispatch` (`:588-621` — the dispatch variable set at
  `:604-613` and `enableTools = true` at `:614`), `ExtractRollbackResult` (`:728-745`),
  `ParseStageStatus` (`:669-702`), `RetryCheck` (`:763-768`), the wiring block (`:506-562`, especially
  the single edge at `:546` and the four paths that do **not** reach it), `CreateReleaseActivity`
  (`:355-384`) and the release-tag computation above it
- `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/rollback.md` — the cell being reused. Its declared
  `variables` list is the contract; adding `targetRef` is a front-matter + body edit and a `version`
  bump.
- `apps/tamma-elsa/src/Tamma.Activities/ADL/DeployEvents.cs:61,64,70,77,84` — the existing rollback and
  release event families and `StatusForEvent`/`ParseTenantId` shape
- `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DeploymentApprovalResumeEndpoint.cs` +
  `apps/tamma-elsa/src/Tamma.Api/Program.cs:2929` — the landed approval-suspend/resume pattern to copy
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:246-249` (the
  `(devops, rollback)` binding) and `:616-623` (`NonDocumentTypeResidual`) — **read before touching
  anything**, because AC7 is that they do not move
- `docs/stories/epic-41/story-41-22/implementation-plan.md:79-88` (C5) and `:165-174` (D6, *"rollback
  is an escalation, not a dispatch"*) — the two paragraphs this story is written to retire
- `docs/stories/epic-41/story-41-5/implementation-plan.md` step 2 —
  `QueryDcbEvidenceActivity`, the engine-side `IEventRepository.QueryEventsAsync` read that 41-5 and
  41-7 also need. **Coordinate: one component, now three consumers.**
- **NOT FOUND (verified):** any release-listing method on `IGitPlatformClient` (twelve methods, none);
  any standalone deploy start endpoint; any `deploy_control` `IToolExecutor` (six registered, all
  coding-oriented); any `.dev/findings/` file recording C5

## Corrections to the story

1. **CONFIRMED — the rollback branch has exactly one inbound edge, and it is three-failures-deep.**
   `:546` is the only `Connect(..., emitRollbackStarted)`; its source `emitProdFailed` is reached only
   from `:543`'s `RetryCheck` False arm. Four failure paths bypass it entirely (`:506-507`, `:519-520`,
   `:531`, `:562`). The story's framing is accurate as written.

2. **CONFIRMED — the rollback dispatch has no target.** `:604-613` passes `stage`, `operation`,
   `repository`, `mergeSha`, `issueNumber`, `branchName`, `completedStages`. `mergeSha` is the sha of
   the deploy that just failed, seeded at `:169` from workflow input. There is no previous-release
   input anywhere in the file, and `Prompts/devops/rollback.md` receives no such variable.

3. **NEW — the git platform cannot answer "what was the last release", so do not try.**
   `IGitPlatformClient` exposes `GetRepo`, `ListRepoBranches`, `GetFileContent`, `CreateBranch`,
   `OpenPullRequest`, `GetPullRequest`, `ListPullRequestFiles`, `CreatePullRequestReviewComment`,
   `MergePullRequest`, `CreateIssueComment`, `RegisterWebhook` — **no release read**.
   (`CreateReleaseActivity` writes one through its own path; nothing reads them back.) Adding
   `ListReleasesAsync` would be a change to the abstraction plus three drivers plus the Bitbucket/Azure
   gap Epic 44 already documented — for a worse answer. **D1 uses the DCB stream instead.**

4. **NEW — "last known good" from the event stream is *more* correct than from git tags.** A git
   release tag records what was *cut*; `DEPLOY.STAGE.SUCCESS(stage=production)` records what Tamma
   actually *deployed and confirmed*. A tag cut by a run whose production stage later failed is
   precisely the release you must not roll back to. Prefer the deploy event; join
   `RELEASE.CREATED.SUCCESS` only to decorate the human-facing tag name.

5. **NEW — the approval gate is not optional, and the story's "configurable" must have a safe
   default.** The epic's *Deliberately out of scope* section already states that final production
   authorization for regulated/breaking changes stays a human decision by acceptance-rules policy.
   Reverting production is at least that consequential. Default the gate **on** (D3), make it
   rules-configurable, and pin the default in a test so a later "streamline the incident path" change
   has to argue with an assertion.

6. **NEW — this story must not become a document producer by accident.** The epic's rule 1 is written
   for producing workflows; applying it here would force a `WorkflowDocumentInterface` row and an edge
   pin bump for a workflow that produces no document. `deployment-pipeline` is the precedent: it is
   dispatch-bearing, has an inline parser, and is in `NonDocumentTypeResidual`. Follow it. AC7 pins
   that none of the five vocabulary/graph counts move.

7. **NEW — the amendment is one variable, and it must be provably one variable.** The temptation once
   `ResolveLastKnownGoodReleaseActivity` exists is to give `DeploymentPipelineWorkflow` a
   "rollback-only" input mode. Do not. AC5's structure test (still exactly one inbound edge to
   `emitRollbackStarted`) is the guard, and it should be written **first**, against the unmodified
   pipeline, so it is known to pass before the amendment lands.

## Design Decisions

- **D1 — `ResolveLastKnownGoodReleaseActivity` reads the DCB stream, fail-closed** (Corrections 3, 4).
  New activity in `Tamma.Activities/ADL/`. Inputs: `repository`, `tenantId`, `excludeRef?`
  (the release being reverted, so a rollback cannot resolve to itself), `maxLookback` (default 50
  events). Query: `DEPLOY.STAGE.SUCCESS` where `tags.repository` matches and `data.stage ==
  "production"`, newest first, skipping any whose `mergeSha == excludeRef`. Outputs: `TargetRef`,
  `TargetTag?` (decorated from the nearest following `RELEASE.CREATED.SUCCESS` for the same sha),
  `ResolvedFromEventId`, `Outcome` ∈ `{resolved, no_known_good_release, query_failed}`.
  **A query failure resolves to `query_failed`, not to "no target" and not to a guess** — an
  unreadable event store must not silently look like a fresh repository.
  Built on the same `IEventRepository.QueryEventsAsync` seam 41-5 step 2 introduces as
  `QueryDcbEvidenceActivity`; if that has landed, extend it rather than writing a second reader, and
  say so in the commit.

- **D2 — new `DefinitionId = "emergency-rollback"`, no incumbent, nothing rewired.**
  Inputs: `repository`, `tenantId`, `reason` (required, free text — it goes in the audit trail),
  `targetRef?`, `requireApproval?` (default **true**, D3), `correlationId?`, `acceptanceRulesJson?`.
  Outputs: `status` ∈ `{rolled_back, rejected, no_known_good_release, failed}`, `targetRef`,
  `targetTag`. `builder.Version = WorkflowVersions.ComputedVersion`.
  Graph:
  `ReadInputs → hasExplicitTarget(FlowDecision)`
  → *(False)* `ResolveTarget → targetResolved(FlowDecision)` → *(False)* `EmitTargetUnresolved →
  ExposeOutput(no_known_good_release)`; *(True)* join
  → `EmitTargetResolved → needsApproval(FlowDecision)`
  → *(True)* `EmitApprovalRequested → WaitForRollbackApproval` (suspend) `→ approved(FlowDecision)`
  → *(False)* `ExposeOutput(rejected)`; *(True)* join
  → `EmitRollbackStarted → RollbackDispatch(llm-call, (devops, rollback), targetRef in variables,
  enableTools=true, WaitForCompletion=true) → ExtractRollbackResult(ParseStageStatus) →
  rollbackOk(FlowDecision)` → `EmitRollbackSuccess` / `EmitRollbackFailed` → `ExposeOutput`.
  **Zero `Finish`** — every exit is `ExposeOutput` with a typed status, so the workflow is
  composable from 41-22 and from the endpoint alike.

- **D3 — the approval gate defaults ON and is a real suspend** (Correction 5). Reuse the
  `DeploymentApprovalResumeEndpoint` pattern rather than inventing a second approval surface; add a
  `POST /api/adl/rollback-approval/resume` sibling with the same bookmark/payload shape. When
  `requireApproval` is false **and** the acceptance rules permit it, the gate is skipped — and that
  combination is what the 85–100 autonomy row means. A test pins that the default input value is
  `true`.

- **D4 — start surface and RBAC, answered per mode.** `POST /api/adl/rollback`
  `{ repository, reason, targetRef?, requireApproval? }`.
  - **single-user:** the sole user may invoke it.
  - **SaaS:** `tenant_owner` / `tenant_admin` for their own tenant's repositories; `member` ⇒ 403.
    Scoped by the repository→tenant join, not by a header.
  Register the effect in Epic 43's catalog as `effect:deploy.rollback` — **which already exists**
  (43-3 AC9 names it and ships it at `AutonomyDial.Min`). So this story adds **no** catalog member; it
  becomes a second enforcement site for an existing one. Say that explicitly in the PR so nobody adds
  a duplicate.

- **D5 — the pipeline amendment is one variable** (Correction 7). In `DeploymentPipelineWorkflow`:
  add a `rollbackTargetRef` workflow variable; insert one `ResolveLastKnownGoodReleaseActivity` node
  between `emitProdFailed` and `emitRollbackStarted` with `excludeRef = mergeSha`; thread
  `rollbackTargetRef` into `StageDeployDispatch`'s variable dictionary (`:604-613`). If resolution
  fails, the branch proceeds exactly as today (the agent gets no target, which is the current
  behaviour) and emits `DEPLOY.ROLLBACK.TARGET_UNRESOLVED` — **the pipeline's rollback must not become
  *less* available than it is today because a lookup failed.** That asymmetry with the new workflow
  (which stops) is deliberate: the pipeline is already in a failure path with nothing to lose; the
  standalone workflow is touching a *working* production and must not guess.

- **D6 — event families.** Reuse `DeployEvents.RollbackStarted/.RollbackSuccess/.RollbackFailed`
  verbatim so existing deploy dashboards and the `DEPLOY.*` alert rules see the new path with zero
  change. Add three members to the same file (not a new file — the family is one concept):
  `TargetResolved = "DEPLOY.ROLLBACK.TARGET_RESOLVED"`,
  `TargetUnresolved = "DEPLOY.ROLLBACK.TARGET_UNRESOLVED"` (LOUD),
  `RollbackApprovalRequested = "DEPLOY.ROLLBACK.APPROVAL_REQUESTED"`. Extend `StatusForEvent` so
  `TargetUnresolved` is error-status. Because `emergency-rollback` reuses the existing three, **tag
  every event with `source` ∈ `{pipeline, standalone}`** so the two paths are separable in the audit
  trail without new event names.

- **D7 — `[ResumeBehavior(ResumeMode.Both)]`.** It suspends on the approval bookmark (the `Both`
  half the resume standard requires) and re-enters from `ReadInputs` on a cold restart with no
  suspended bookmark. One `ComputeReEntryPositionActivity` node; the 39-10 structural test must be
  green with **no** allowlist entry.

- **D8 — deliberately NOT chosen: making this a document-producing thin binding.** A rollback decision
  could in principle produce a `Diagnosis`-shaped record. It should not: the decision *record* is the
  DCB trail plus 41-22's postmortem prose, and forcing a document here would add a lifecycle,
  a review stage and an accept gate in front of an emergency action whose whole value is being fast.
  41-22 owns the writing-about-it; this owns the doing.

## Implementation Steps

1. **Precondition check (no code).** `dotnet build` green. Confirm: the single rollback inbound edge at
   `DeploymentPipelineWorkflow.cs:546`; `(devops, rollback)` in `ContractBindingTests:246-249` and
   `NonDocumentTypeResidual:616-623`; `Prompts/devops/rollback.md` present; `DeployEvents` constants;
   `DeploymentApprovalResumeEndpoint` present. Check whether 41-5's `QueryDcbEvidenceActivity` has
   landed (D1).

2. **WRITE THE PIPELINE STRUCTURE TEST FIRST** (Correction 7, AC5) — assert `DeploymentPipelineWorkflow`
   has exactly one inbound edge to `emitRollbackStarted`. Confirm it passes **before** step 6 touches
   the file.

3. **MODIFY** `apps/tamma-elsa/src/Tamma.Activities/ADL/DeployEvents.cs` — three new constants +
   `StatusForEvent` arm (D6).

4. **CREATE** `apps/tamma-elsa/src/Tamma.Activities/ADL/ResolveLastKnownGoodReleaseActivity.cs` (D1),
   with its own unit test. Total, fail-closed, never throws; three typed outcomes.

5. **HAND-EDIT** `apps/tamma-elsa/src/Tamma.Api/Prompts/devops/rollback.md` — add `targetRef` (and
   `reason`) to the front-matter `variables` list and reference them in the body ("roll back to
   `{{targetRef}}`"); bump `version`. **Front matter is an exact-key set** (`variables`, `enableTools`,
   `maxTokens`, `version`) — add to the `variables` value, never a new key, or `PromptFileLoader`
   refuses to start with `PROMPT.SEED.MALFORMED_FILE`.

6. **MODIFY** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs` (D5) —
   one variable, one activity node, one dispatch-variable addition. Re-run step 2's test.

7. **CREATE** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/EmergencyRollbackHelper.cs` —
   pure, Elsa-free, total: `BuildDispatchVariables(...)`, `BuildOutcome(exit)`,
   `IsApprovalRequired(requireApprovalInput, acceptanceRulesJson)`.

8. **CREATE** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/EmergencyRollbackWorkflow.cs` (D2, D3, D7).

9. **CREATE** `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/RollbackApprovalResumeEndpoint.cs` and
   **MODIFY** `apps/tamma-elsa/src/Tamma.Api/Program.cs` — `POST /api/adl/rollback` +
   `POST /api/adl/rollback-approval/resume` with D4's per-mode RBAC.

10. **MODIFY** `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` —
    add `"EmergencyRollbackWorkflow"` to `ExpectedContributingWorkflows` (it dispatches a `(role,
    action)` pair). **Nothing else in the drift set moves** — verify `ContractBindingTests` passes
    **unchanged**, which is AC7's real content.

11. **CREATE the tests** (below); full `dotnet test`;
    `dotnet ef migrations has-pending-model-changes` stays clean (no schema change).

## Data & Migrations

**None.** No table, no document type, no entity. Everything persists through the existing
`domain_events` drain and Elsa's own instance store.

## Events

- **Reused:** `DEPLOY.ROLLBACK.STARTED` / `.SUCCESS` / `.FAILED` — now tagged `source ∈ {pipeline,
  standalone}` (D6).
- **New:** `DEPLOY.ROLLBACK.TARGET_RESOLVED`, `DEPLOY.ROLLBACK.TARGET_UNRESOLVED` (LOUD),
  `DEPLOY.ROLLBACK.APPROVAL_REQUESTED`.
- **Read (not emitted):** `DEPLOY.STAGE.SUCCESS`, `RELEASE.CREATED.SUCCESS` — D1's resolution source.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`DeploymentPipelineRollbackEntryEdgeTests`** (**step 2, AC5**) — exactly one inbound edge to
  `emitRollbackStarted`, before and after the amendment. *Written first, on purpose.*
- **`ResolveLastKnownGoodReleaseActivityTests`** (**AC2**) — a stream with three production
  successes ⇒ the newest resolves; `excludeRef` matching the newest ⇒ the second resolves; a stream
  with only QA/UAT successes ⇒ `no_known_good_release`; an empty stream ⇒ `no_known_good_release`; a
  throwing repository ⇒ `query_failed` (**not** `no_known_good_release` — pin the distinction, D1);
  tag decoration from `RELEASE.CREATED.SUCCESS` present and absent.
- **`EmergencyRollbackWorkflowStructureTests`** — `DefinitionId == "emergency-rollback"`;
  `OfType<Finish>()` **empty**; exactly one `DispatchWorkflow`, literal id `llm-call`; the dispatch
  materialises `(devops, rollback)` and includes `targetRef` in its variables; one
  `ComputeReEntryPositionActivity`; `[ResumeBehavior(Both)]`; the `requireApproval` input **defaults to
  true** (D3, AC3); `EmitRollbackStarted` is unreachable except through the approval join —
  i.e. **no path executes a rollback without passing the gate or an explicit rules-permitted skip**.
- **`EmergencyRollbackHelperTests`** — `IsApprovalRequired` truth table incl. the
  rules-say-escalate-anyway case; `BuildOutcome` names every reachable status.
- **`ContractBindingTests` run UNCHANGED** (**AC7**) — plus an explicit assertion in the new structure
  test that the `(devops, rollback)` entry's authority is still
  `DeploymentPipelineWorkflow.ParseStageStatus` and that the pair is still in `NonDocumentTypeResidual`.
  Also assert the five count pins are untouched (`AgentActionTests:38`, `RolePhaseMapTests:64`,
  `DocumentTypeKeyTests:20`, `DocumentTypeRegistryTests:37`, `WorkflowInterfaceGraphTests:45`) — this
  story's most likely failure mode is someone "helpfully" registering it as a producer.
- **`EmergencyRollbackExecutionTests`** (Testcontainers) —
  (a) **the headline scenario:** no deploy in flight, no `mergeSha` anywhere; dispatch with only
  `repository` + `reason`; target resolves; approval granted; the `(devops, rollback)` dispatch is
  observed with the resolved `targetRef`; status `rolled_back`; the `DEPLOY.ROLLBACK.*` trail carries
  `source=standalone`. **Covers AC1, AC2, AC4.**
  (b) rejection at the gate ⇒ `rejected`, **zero** `llm-call` dispatches observed.
  (c) no candidate ⇒ `no_known_good_release`, `TARGET_UNRESOLVED` emitted, **zero** dispatches.
  (d) restart mid-suspend ⇒ the bookmark re-arms and the resume still lands (**AC6**).
  (e) **the pipeline amendment:** drive `deployment-pipeline` to a three-times-failed production
  deploy; assert its rollback dispatch now carries a resolved `targetRef` **and** that a resolution
  failure still lets the rollback proceed (D5's deliberate asymmetry).
- **`ResumableStandardStructuralTests`** green with **no** `EmergencyRollbackWorkflow` allowlist entry
  (**AC6**).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — dispatchable with no `mergeSha`, no PR, no in-flight deploy | 8, 9 (D2) | execution (a) |
| 2 — DCB target resolution, fail-closed on no candidate | 4, 8 (D1) | activity tests; execution (a), (c) |
| 3 — approval gate suspends, default on, rejection executes nothing | 8, 9 (D3) | structure test default pin; execution (b) |
| 4 — mediated `(devops, rollback)` with `targetRef`, fail-closed parse | 5, 8 | structure test; execution (a) |
| 5 — pipeline amendment is one variable, still one entry edge | 2, 6 (D5) | `DeploymentPipelineRollbackEntryEdgeTests`; execution (e) |
| 6 — `[ResumeBehavior(Both)]`, 39-10 green without allowlist | 8 (D7) | `ResumableStandardStructuralTests`; execution (d) |
| 7 — no taxonomy / document-vocabulary change | 10 | `ContractBindingTests` unchanged + the five count-pin assertions |

## Risks & Mitigations

- **"While we're here, let's give the pipeline a rollback-only entry mode."** This is the change that
  would break the pipeline. Mitigation: AC5's edge test, written first (step 2) so it is known-green
  before the file is touched, and Correction 7 says why in the plan rather than only in review.
- **Resolving the wrong target is worse than resolving none.** Rolling production back to a release
  that was itself broken turns an incident into two. Mitigations: `excludeRef`; the *deploy-success*
  event rather than the release tag (Correction 4); `query_failed` kept distinct from
  `no_known_good_release`; and the human gate on by default.
- **The execution path is ungoverned** (`ShellExecute` through the mediated cell), and this story puts
  a *new, easier* trigger in front of it. Mitigation: it is the same path `deployment-pipeline` already
  uses — so the marginal risk is the trigger, which is exactly what D3/D4's gate and RBAC cover. The
  governed fix is 42-8b and this story states that rather than pretending otherwise.
- **41-22 must be revised, not just unblocked.** Its D6 ("rollback is an escalation, not a dispatch")
  and its AC3 both need rewriting to name `emergency-rollback`. Mitigation: listed under Blocks, and
  flagged in the epic README so the revision is not forgotten. **Do not edit 41-22 as a side effect of
  this story** — file it.
- **`Prompts/devops/rollback.md` is shared with the pipeline.** Adding `targetRef` changes a cell both
  paths render. Mitigation: the variable is additive and the pipeline now supplies it too (D5), so
  neither renders a missing variable; the `version` bump forces a re-seed; and the exact-key front
  matter rule means the edit is to the `variables` **value**, never a new key.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition check + the entry-edge test, written first | 0.4 |
| 3–4 | Event constants + `ResolveLastKnownGoodReleaseActivity` + its tests | 1.0 |
| 5 | Prompt cell edit | 0.2 |
| 6 | Pipeline amendment | 0.4 |
| 7–8 | Helper + `EmergencyRollbackWorkflow` | 1.0 |
| 9 | Start + approval-resume endpoints, per-mode RBAC | 0.8 |
| 10–11 | Drift entry + structure/helper tests + Testcontainers (a)–(e) | 1.2 |
| **Total** | | **5.0** (story estimate 4–5 days — **confirmed at the top of the range**) |

## Blocks / Blocked by

- **Blocked by:** nothing hard. Soft: if 41-5's `QueryDcbEvidenceActivity` lands first, D1 extends it
  instead of adding a second DCB reader — coordinate, do not duplicate.
- **Blocks / unblocks:** **41-22 AC3** and its D6. Also makes **41-32**'s reactive path worth having:
  an alert that can reach a rollback is materially different from an alert that can reach a document.
- **Degraded by Epic 42 (42-8b)** — ungoverned execution path, unchanged from today.
- **Shared-file register (coordinate before editing):** `DeploymentPipelineWorkflow.cs` (also touched
  by nothing else in Epic 41 today — but Epic 43's 43-9 Seam E reaches its approval gate over HTTP;
  that is a different region of the file); `Prompts/devops/rollback.md` (shared with the pipeline —
  this story is its only editor); `DeployEvents.cs`; `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`
  (41-20, 41-17, 41-18, 41-19 all append here — serialize).
- **Documentation debt this story clears:** 41-22's plan C5 claims a `.dev/findings/` entry that does
  not exist. Either write it as part of this story or delete the claim from 41-22's plan — do not leave
  a third document asserting a fourth one exists.
