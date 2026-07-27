# Story 41-31: Standalone Emergency Rollback — rolling back a deploy that already succeeded

Status: drafted

## User Story

As a **devops** engineer (or the incident-response path at high autonomy), I want a workflow that rolls
production back to the **last known-good release** on demand — without a failing deploy in flight and
without re-running a promotion pipeline — so that a bad release that only reveals itself *after* it
shipped can be reverted through Tamma, with the same audit trail as any other deploy action.

## Priority

**P1 / Wave 3.** It is the hard blocker under **41-22 AC3**, which today cannot be implemented as
written (see Scope).

## The gap, stated precisely

The epic README corrected an earlier draft to record that **rollback is not missing** —
`DeploymentPipelineWorkflow.cs:298-329` builds a real, executed rollback branch. That correction is
right about the *branch* and wrong about the *capability*, because of where the branch is wired:

- The **only** inbound edge is `Connect(emitProdFailed, emitRollbackStarted)` (`:546`), and
  `emitProdFailed` is reachable only from `ConnectOutcome(prodRetryCheck, "False", emitProdFailed)`
  (`:543`) — i.e. **after a production deploy in this same run has failed `MaxStageRetries = 3` times**
  (`:102`). No edge reaches it from a QA failure (`:506-507`), a UAT failure (`:519-520`), a rejected
  prod approval (`:531` → `setProdFailed`), or a release-cut failure (`:562`).
- `deployment-pipeline` has **no standalone entry point**. Its sole dispatch site is
  `SingleIssueCycleWorkflow.cs:721-742` (step 15, post-merge), and its `mergeSha` comes from
  `WaitForPRMergedActivity` (`:700-709`). No API endpoint starts it; the only deploy HTTP surface is
  the approval **resume** bookmark (`Program.cs:2929` → `DeploymentApprovalResumeEndpoint`).
- The rollback dispatch passes **`mergeSha` — the sha of the deploy that just failed** (`:604-613`) —
  and **no previous-release ref at all**. `Prompts/devops/rollback.md` asks the agent in prose to
  "roll the {{stage}} environment back to the previous known-good release" and hands it nothing with
  which to identify one.

So the shipped capability is *"undo the deploy I was in the middle of"*. The missing capability is
*"revert a release that is already live"* — a different trigger, a different input, and a different
lifecycle. **41-22's implementation plan already found this** (C5: *"There is no standalone rollback
entry point… AC3's 'a rollback is performed by dispatching deployment-pipeline' [is] not
implementable"*) and filed it as *"filed, not fixed here"* — **and the finding file it says it filed
does not exist in `.dev/findings/`.** This story is that fix.

## New workflow, not an amendment — and the one amendment it does make

**New workflow (`DefinitionId = "emergency-rollback"`).** Per the epic's own test — amend when the gap
is a missing branch or typed exit; build new when the trigger, the produced artifact or the lifecycle
differs — all three differ here: the trigger is an operator or an incident (not a failed stage), the
input is a **resolved target release** (not the in-flight `mergeSha`), and the lifecycle is a single
gated action (not a QA→UAT→Prod promotion chain). Adding a second entry into
`DeploymentPipelineWorkflow` would mean an input mode that skips stages 1–5 — a mode that must never be
reachable from the normal path, in a workflow whose stage wiring is already the most intricate in the
tree.

**The one amendment** (small, and it fixes a real defect rather than adding a mode):
`DeploymentPipelineWorkflow`'s existing `rollbackCall` (`:311-312`, body `:588-621`) gains a
`targetRef` variable resolved by this story's shared activity, threaded into the same dispatch
variables. Today it passes only the failing `mergeSha`; after this story both rollback paths tell the
agent *what to roll back to*. No new node, no new edge, no new stage — one resolved variable added to
an existing dispatch. Structure tests pin that the pipeline still has exactly one rollback entry edge.

## Scope

1. **`ResolveLastKnownGoodReleaseActivity`** — reads the DCB stream for the most recent
   `DEPLOY.STAGE.SUCCESS` with `stage = production` for the repository (optionally joined to
   `RELEASE.CREATED.SUCCESS` for the tag), **excluding the release being rolled back**. Fail-closed: no
   candidate ⇒ a typed `no_known_good_release` outcome, never a guess. *Source rationale:* the git
   platform abstraction cannot answer this — `IGitPlatformClient` has twelve methods and **no release
   listing** — and the DCB stream is the better source anyway, because it records what Tamma actually
   deployed **and** whether it succeeded, which is what "known good" means.
2. **`emergency-rollback` workflow** — resolve target → human/orchestrator approval gate → dispatch the
   mediated `(devops, rollback)` cell with the resolved `targetRef` → verify → emit.
3. **A start surface** — `POST /api/adl/rollback` (mode-appropriate RBAC, D4), and dispatchability from
   41-22's incident path so its `rollbackDisposition` can become `dispatched` rather than always
   `escalated`.
4. **The `DeploymentPipelineWorkflow` amendment** above.

## Explicitly out of scope

- **A `deploy_control` tool.** The rollback executes through the mediated `(devops, rollback)`
  `llm-call` with `enableTools = true`, exactly as the pipeline's does today — i.e. through
  `ShellExecuteTool`, ungoverned. **Epic 42's 42-8b is the fix and this story does not pre-empt it**;
  it inherits the same caveat the pipeline already carries.
- **Post-deploy health probing / auto-detection of a bad release.** The pipeline's class doc names it
  as an unlanded follow-up (`:76-77`); detecting the badness is 41-23 (capacity/health) and 41-32
  (the reactive trigger seam). This story owns the *reversal*, given a decision.
- **Rolling back anything other than a deploy** — agent versions, plan versions, secrets and DB
  migrations all have their own, unrelated rollback paths and none of them is this.

## Produced document

**None.** This is an execution workflow, not a document producer — the same class as
`deployment-pipeline`. It therefore does **not** ride the thin-binding recipe, declares no
`WorkflowDocumentInterface` row, and does **not** move the
`WorkflowInterfaceGraphTests` edge pin. It reuses the existing `(devops, rollback)` execution cell,
which `ContractBindingTests` already binds to `DeploymentPipelineWorkflow.ParseStageStatus` and lists
in `NonDocumentTypeResidual` — **no taxonomy change, no new cell, no count-pin bump.**

## Events

Reuses `DeployEvents.RollbackStarted` / `.RollbackSuccess` / `.RollbackFailed`
(`Tamma.Activities/ADL/DeployEvents.cs:61,64,70`) so existing deploy dashboards see it with no change,
plus three new members for the parts the pipeline has no concept of:
`DEPLOY.ROLLBACK.TARGET_RESOLVED` (data: `targetRef`, `targetTag`, `resolvedFromEventId`),
`DEPLOY.ROLLBACK.TARGET_UNRESOLVED` (LOUD — the fail-closed exit), and
`DEPLOY.ROLLBACK.APPROVAL_REQUESTED`. Tagged `repository`, `tenantId`, `correlationId`.

## Autonomy behavior

- **70–84:** the target is resolved and presented; a human approves before anything executes.
- **85–100:** the orchestrator may approve, **except** that reverting production is a strong candidate
  for the always-escalate class by acceptance-rules policy — the epic's own "final production-deploy
  authorization stays a human decision" line applies here at least as much as it does to a forward
  deploy. The workflow makes it *configurable*, and ships with the approval gate **on** by default.
- At every level the approval gate is a real suspend, never an if-else.

## Acceptance Criteria

1. `emergency-rollback` is dispatchable **without** a `mergeSha`, without a PR, and without any
   in-flight deploy. Inputs: `repository`, `tenantId`, `reason`, `targetRef?` (explicit override),
   `correlationId?`.
2. When `targetRef` is omitted, `ResolveLastKnownGoodReleaseActivity` resolves it from the DCB stream
   and emits `DEPLOY.ROLLBACK.TARGET_RESOLVED`. **With no candidate the workflow stops at a typed
   `no_known_good_release` outcome and emits `DEPLOY.ROLLBACK.TARGET_UNRESOLVED` — it never dispatches
   a rollback with an unresolved target.**
3. The approval gate suspends on a resumable bookmark and is **on by default**; resuming with a
   rejection terminates with `rejected` and executes nothing.
4. The rollback executes through the mediated `(devops, rollback)` `llm-call` with the resolved
   `targetRef` among its variables, and its result is parsed fail-closed (success only on an explicit
   `status: "success"`, the `ParseStageStatus` contract).
5. **The `DeploymentPipelineWorkflow` amendment:** its `rollbackCall` receives the same resolved
   `targetRef`. A structure test pins that the pipeline still has **exactly one** inbound edge to
   `emitRollbackStarted` — this story adds a variable, not a second way in.
6. `[ResumeBehavior(Both)]`; the 39-10 structural test is green without an allowlist entry.
7. **No taxonomy or document-vocabulary change**: `AgentActionTests.cs:38` (`Be(80)`),
   `RolePhaseMapTests.cs:64` (`HaveCount(80)`), `DocumentTypeKeyTests.cs:20`,
   `DocumentTypeRegistryTests.cs:37` and `WorkflowInterfaceGraphTests.cs:45` are all **unchanged**, and
   a test asserts the `(devops, rollback)` `ContractBindingTests` entry is untouched.

## Dependencies

- **Blocking:** nothing hard. `(devops, rollback)` + its prompt cell, `DeployEvents`,
  `ParseStageStatus`, the deploy-approval resume endpoint and `IEventRepository.QueryEventsAsync` are
  all landed.
- **Blocks / unblocks:** **41-22 AC3** — with this story its `rollbackDisposition` can be `dispatched`
  at high autonomy instead of always `escalated` (41-22 plan D6). 41-22 must be revised to dispatch
  `emergency-rollback`, **not** `deployment-pipeline` (which its own plan already proved
  unimplementable).
- **Degraded (not blocked) by Epic 42** — the execution path is `ShellExecute`-shaped and ungoverned
  until **42-8b** lands a `deploy_control` tool. Identical to the pipeline's existing caveat; this
  story does not make it worse and does not fix it.
- **Related:** **41-32** (the reactive-trigger seam) is what would let an alert reach this workflow
  without a human noticing an email; **41-23** is what would detect the bad release.
- **Corrects:** the epic README's *"Rollback ✅ — `deployment-pipeline`, auto rollback-on-prod-failure
  branch"* row, which is true of the branch and misleading about the capability. The row becomes
  ◑ with this story named.

## Estimated Effort

**4–5 days.**
