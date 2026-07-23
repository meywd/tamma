# Epic 40: Resumable Coding Execution — the tamma-agent runner contract, durable agent-run waits, and per-task re-entry

## Overview

The coding/TDD implement step is the one place in `SingleIssueCycleWorkflow` where
Tamma hands a task to an autonomous coding agent, waits ~30 minutes for it, and folds
the result back into the cycle. It is also the **least durable** step in the platform and
the one whose **runner side was specified but never built**. Two truths, established by
the research below, motivate this epic:

1. **The CI-side runner (`tamma-agent.yml`) does not exist.** Story 19-1 authored a
   complete *contract* — inputs, steps, result-artifact schema, security posture — and
   left it `ready-for-dev`. No workflow file, no runner scripts, and no scaffolding that
   installs it into a user repo was ever created. The entire C# dispatch→monitor→collect
   stack (`Tamma.Activities/AgentDispatch/*` + `Tamma.Api` mediation) is built and
   **expects that file to already be present in the user's repo**; when it is absent the
   mediation fails loud with *"Add the Tamma agent workflow template to
   .github/workflows/"* — a dead end for every SaaS tenant. The single-user `LocalExecutor`
   path is symmetrically incomplete: it shells out to a `packages/cli execute-agent`
   command that is not implemented.

2. **The wait is not durable.** `ExecuteAgentActivity` runs the whole
   dispatch→monitor→collect cycle inline with `await`. The workflow instance stays
   **Running** (not suspended on a bookmark) for the full ~35-minute monitor loop; a
   deploy, pod eviction, or crash during that window loses the in-flight monitor and — with
   no task-level re-entry — restarts the entire `SingleIssueCycleWorkflow` from scratch,
   re-running context/plan/task/branch/PR generation and re-implementing tasks that already
   landed on the branch. The webhook signal plane (`WebhookSignalRegistry`) is an
   **in-memory, single-process** `ConcurrentDictionary<TaskCompletionSource>`: a
   `workflow_run.completed` webhook that lands on a different pod than the waiting monitor
   never matches, silently degrading to poll (Auto) or failing (Webhook).

Epic 40 closes both gaps. It **ships the runner contract** (the missing CI-side flow, both
SaaS and single-user), and it **makes the coding step resumable by design** to the Epic 39
standard — replacing the inline monitor with a durable bookmark suspend, replacing the
in-memory signal plane with a persisted signal that survives restart and crosses pods, and
giving the per-task loop a git-and-events-based re-entry so a crash resumes at the right
task instead of re-implementing.

**Epic 39 relationship — Code is NOT a document type.** Epic 39's README is explicit:
*"Code's store is git, its validator is the build/test/gate stack, and its review is a
`Review` whose subject is a diff. No schema needed."* So the coding step does **not** move
onto `DocumentLifecycleWorkflow`; it needs its **own** resumable pattern. That pattern is
built on 39-10's mechanism — `LifecycleBookmarks` (tenant-folded deterministic bookmark
names), the `[ResumeBehavior]` declaration + `ResumableStandardStructuralTests` build gate,
`CanonicalSuspendActivities`, and the `LegacyResumeAllowlist` burn-down — but its re-entry
read model is **git + DCB events**, not the 39-11 document store.

## The problem, traced through the code

The coding step's call chain (all under `apps/tamma-elsa/src/`):

```
SingleIssueCycleWorkflow.cs  (per-task TDD loop)
  initTaskLoop → hasMoreTasks (CurrentTaskIndex < TotalTasks)
    → extractCurrentTask (tasksJson[CurrentTaskIndex])
    → tddForTask : ExecuteAgentActivity   ◄── INLINE await, ~35 min, instance stays Running
        AgentExecutorFactory.Create(mode)
          ├─ GitHubActionsExecutor  (SaaS)
          │    IAgentDispatchService.DispatchAsync   → Tamma.Api mediation → workflow_dispatch on tamma-agent.yml  ◄── FILE DOES NOT EXIST
          │    IAgentMonitorService.MonitorAsync      → Poll | Webhook | Auto
          │         WebhookSignalRegistry (in-memory TCS)  ◄── single-process, no cross-pod, no bookmark
          │    IAgentResultCollectorService.CollectAsync → ActionsResultAggregator (reads `tamma-result`/result.json)
          └─ LocalExecutor  (single-user)
               shells node packages/cli execute-agent  ◄── CLI command NOT implemented
    → Completed → incrementTask → loop
    → Failed    → dispatchTddRetry (tdd-with-debug-retry) → advance | fail-cycle
  hasMoreTasks False → ciGate (ci-with-debug-retry) → merge gate → …
```

Contrast the durable primitive already used elsewhere in the same workflow family:
`WaitForCIResultsActivity` (`Tamma.Activities/Testing/`) **suspends on an Elsa bookmark +
`context.DelayFor(...)` durable timeout** with `Received`/`Timeout` edges, holding no thread
for the wait. The coding step must adopt exactly this shape.

## Research findings (honest gap analysis)

| Question | Finding |
|---|---|
| **Does `tamma-agent.yml` exist anywhere?** | **No.** Not in Tamma's own `.github/workflows/` (which has `tamma-worker.yml` but no `tamma-agent.yml`), not in any `templates/` dir (only `.dev/templates` exists, unrelated), no `run-claude-code.sh`/`collect-results.sh`. Story 19-1 defined the contract; the file was never authored. No scaffolding/generator installs it into a user repo — `AgentDispatchMediationService.CheckWorkflowFileAsync` only *checks presence* and fails with a "add the template" message. It is **assumed user-provided and undocumented** today. |
| **What is the runner contracted to do?** | Reconstructable from the dispatch inputs (`AgentDispatchService.BuildDispatchInputs`: `issue_number, task, plan_json, branch_name, tamma_session_id, agent_provider, agent_config_json`) and the result contract (`AgentResultArtifactParser` + `ActionsResultAggregator`): **checkout `branch_name` → install the agent CLI (claude-code default) → materialize the plan slice → run the coding agent + TDD with repo-secret API keys → push commits to the branch → emit `.tamma/result.json` (the `AgentResultArtifact` schema) → upload it as an artifact named `tamma-result` → post an issue comment.** Idempotent, 30-min timeout. In the SingleIssueCycle context the PR is created by a *separate* `pull-request` step **before** the TDD loop, so the per-task runner pushes commits to an existing branch and does not itself open the PR. All of this is *specified* (19-1) but *unimplemented*. |
| **Result-artifact schema (drift-pinned)** | Artifact **name** `tamma-result` (`ActionsResultAggregator.ResultArtifactName`); entry a file ending `result.json` (`ResultArtifactFileName`); JSON fields consumed by `AgentResultArtifactParser.ParseResultJson`: `success, task, issue_number, branch_name, tamma_session_id, files_changed[], pr_number, commit_sha, error_message, agent_log_summary, tokens_used, duration_seconds, agent_provider, agent_version`. Caps enforced server-side (4 MB result.json, 2000 files, 32 KB log summary). |
| **How is completion observed?** | `AgentMonitorService` in three modes — **Poll** (mediated `GET runs/{id}` loop, ~35 min deadline), **Webhook** (park on `WebhookSignalRegistry`), **Auto** (webhook then poll fallback). The webhook is published by `Tamma.Api` `InstallationRouterService.HandleWorkflowRunEvent` on `workflow_run.completed` → `IWebhookSignalRegistry.PublishSignal`. **Gaps:** inline `await` (instance never suspends), in-memory single-instance registry (no cross-pod), no Elsa bookmark, no DelayFor durable timeout at the activity, no task-level re-entry. |
| **LocalExecutor (single-user) path** | Writes `.tamma/exec-request-{session}.json`, spawns `node packages/cli/dist/index.js execute-agent --request … --output …`, reads back an `AgentResultArtifact`-shaped file. The `execute-agent` CLI command **is not implemented** (the code itself says so). Single-user coding execution is therefore as unshipped as the SaaS runner. |
| **Epic 39 hooks consumed** | 39-10: `LifecycleBookmarks` (tenant-folded builder), `ResumeBehaviorAttribute`/`ResumeMode`, `CanonicalSuspendActivities`, `LegacyResumeAllowlist`, `ResumableStandardStructuralTests`, the `ComputeReEntryPositionActivity` pattern. 39-8: `DocumentDecisionResumeEndpoint` as the tenant-folded resume-endpoint precedent; `NormalizeSegment`. **NOT consumed:** 39-11 document store (code is not a document — re-entry reads git + DCB events, mirroring 39-10's 4-7/4-8 read path). |

## Design principles (settled)

- **The runner is a versioned contract, shipped and installed, not assumed.** `tamma-agent.yml`
  carries a `tamma-runner-version`; Tamma scaffolds it into a user repo through the GitHub App
  and detects drift. The result-artifact schema is pinned to `AgentResultArtifactParser` by a
  drift test, exactly as prompt contracts are pinned to their parsers.
- **The wait suspends, it never spins.** The coding step dispatches, then **suspends on a
  durable Elsa bookmark** with a `DelayFor` timeout — the `WaitForCIResultsActivity` shape —
  so the workflow holds no thread and survives any restart. `Received`/`Timeout` are
  deterministic edges; a lost webhook times out to a loud escalation, never a hang.
- **The signal is durable and the bookmark store is the backplane.** Once the step suspends on
  a real DB-persisted Elsa bookmark, **any pod** can resume it through Elsa's bookmark store —
  that is the cross-pod delivery the in-memory TCS never had. The persisted signal row only
  carries the `sessionId`/bookmark-name that the `workflow_run.completed` webhook lacks.
- **Code's re-entry read is git + events, not a document store.** "Which task already landed"
  is reconstructed from commits on the branch and `AGENT_RUN.*`/`CODE.*` DCB events keyed by the
  deterministic per-task session id (`adl-{issue}-task-{index}`) — never from Elsa instance
  internals. This is 39-10's latest-state re-entry, with git as the store.
- **One resumable standard, enforced by the same build gate.** `SingleIssueCycleWorkflow`
  declares `[ResumeBehavior]`, registers `WaitForAgentRunActivity` as a canonical suspend
  activity, and **burns down its `LegacyResumeAllowlist` entry** — the first Epic-40 consumer
  of the 39-10 gate.
- **Per-mode scoping, drawn twice.** SaaS: the runner executes in the *tenant's* GitHub Actions
  with the *tenant's* repo secrets; Tamma never sees the agent API keys; bookmarks and signals
  are tenant-folded. Single-user: the runner is the local CLI on the sole user's host with the
  user's own keys. Every feature answers both ownership questions (CLAUDE.md two-scoping rule).

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 40-1 | The `tamma-agent.yml` Runner Contract & Repo Scaffolding (+ single-user CLI parity) | P0 | drafted | 6-8 days |
| 40-2 | `WaitForAgentRunActivity` — durable bookmark suspend + `DelayFor` timeout | P0 | drafted | 5-7 days |
| 40-3 | Durable agent-run signal plane + resume endpoint (cross-pod, restart-safe) | P0 | drafted | 5-7 days |
| 40-4 | Per-task re-entry — reconstruct landed tasks from git + DCB events | P0 | drafted | 5-7 days |
| 40-5 | `[ResumeBehavior]` on `SingleIssueCycleWorkflow` + allowlist burn-down | P0 | drafted | 2-3 days |
| 40-6 | Agent-run lifecycle event family + re-entry feed | P1 | drafted | 3-4 days |
| 40-7 | End-to-end crash/restart + mode-matrix integration proof | P0 | drafted | 4-5 days |

## Supersedes / absorbs

- **Story 19-1 (tamma-agent workflow template)** — formally *completed* by 40-1: 19-1
  authored the contract but never shipped the file/scripts/scaffolding; 40-1 delivers them
  and pins the artifact schema to `AgentResultArtifactParser`.
- **The inline monitor inside `ExecuteAgentActivity`** — its dispatch/collect halves are
  retained (reused by `WaitForAgentRunActivity`), but the inline ~35-min `await` on
  `IAgentMonitorService.MonitorAsync` is replaced by the durable bookmark suspend (40-2).
  `ExecuteAgentActivity` remains for non-resumable callers/tests; the SingleIssueCycle TDD
  loop switches to `WaitForAgentRunActivity`.
- **`WebhookSignalRegistry` (in-memory)** — superseded by the durable signal + Elsa bookmark
  store (40-3). The in-memory registry may remain as a same-process fast-path optimization,
  but correctness no longer depends on it.
- **NOT superseded:** `AgentDispatchService`/`AgentMonitorService` (poll) /
  `AgentResultCollectorService` / `ActionsResultAggregator` / `AgentResultArtifactParser`
  stay the dispatch/collect substrate; the `AGENT_DISPATCH.RUN_*` mediation event family
  (Tamma.Api) stays; 40-6 adds the engine-side *wait/re-entry* events beside it.

## Dependencies

- **Epic 39-10 (Resumable-by-Design Standard) — HARD, must land first.** Epic 40 consumes
  `LifecycleBookmarks`, `ResumeBehaviorAttribute`/`ResumeMode`, `CanonicalSuspendActivities`,
  `LegacyResumeAllowlist`, and `ResumableStandardStructuralTests`. 40-2's bookmark uses the
  builder; 40-5's declaration + allowlist burn-down is meaningless without the gate.
- **Epic 39-8 (Escalation & Approval Surface) — SOFT.** `DocumentDecisionResumeEndpoint` is
  the tenant-folded resume-endpoint precedent 40-3 mirrors (404/409 posture, `NormalizeSegment`
  reuse). 40-3 can develop against the pattern without waiting on 39-8's document specifics.
- **Story 4-7 (event query API) + 4-8 (`ReplayReconstructor`)** — the DCB read path 40-4's
  re-entry uses (identical to how 39-10 reads latest state), plus the git compare/PR reads
  already in `ActionsResultAggregator`.
- **Existing substrate (in place, verified):** the full `Tamma.Activities/AgentDispatch/*`
  dispatch/monitor/collect stack, `AgentResultArtifactParser`, `ActionsResultAggregator`,
  `WaitForCIResultsActivity` (the durable-wait precedent), the GitHub App installation +
  `InstallationRouterService` webhook receiver, Elsa 3 bookmarks + EF persistence.
- **NOT a dependency:** 39-11 document store / `DocumentLifecycleWorkflow` — code is not a
  document type; re-entry reads git + events instead. Called out so the coupling is not
  mistakenly introduced.
- **Operating-mode detection (single-user vs SaaS)** — 40-1's runner install path and every
  bookmark/signal scoping decision is per-mode (CLAUDE.md universal rule).
