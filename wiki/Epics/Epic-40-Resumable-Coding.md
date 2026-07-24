# Epic 40: Resumable Coding Execution

**Status:** Planned / docs — briefs authored, no code yet. Ships the missing `tamma-agent.yml` runner contract and makes the coding step resumable-by-design on the Epic 39-10 mechanism.
**Stories:** 7 (40-1 through 40-7), all drafted
**Layer:** Layer 4 (integration/orchestration)
**Depends on:** Epic 39-10 (Resumable-by-Design Standard — HARD, must land first), Epic 39-8 (escalation/resume-endpoint precedent — SOFT), Story 4-7 (event query API) + 4-8 (`ReplayReconstructor`), the existing `Tamma.Activities/AgentDispatch/*` dispatch/monitor/collect stack

> This epic is **backlog** — scoped and specified, not built. The coding step described below is durable *by design intent*; today it is the least durable step in the platform.

## 1. Overview

The coding/TDD implement step is the one place in `SingleIssueCycleWorkflow` where Tamma hands a task to an autonomous coding agent, waits ~30 minutes for it, and folds the result back into the cycle. It is also the **least durable** step in the platform and the one whose **runner side was specified but never built**. Two truths motivate this epic:

1. **The CI-side runner (`tamma-agent.yml`) does not exist.** Story 19-1 authored a complete *contract* — inputs, steps, result-artifact schema, security posture — and left it `ready-for-dev`. No workflow file, no runner scripts, no scaffolding that installs it into a user repo was ever created. The entire C# dispatch→monitor→collect stack is built and **expects that file to already be present in the user's repo**; when it is absent the mediation fails loud with *"Add the Tamma agent workflow template to .github/workflows/"* — a dead end for every SaaS tenant. The single-user `LocalExecutor` path is symmetrically incomplete: it shells out to a `packages/cli execute-agent` command that is not implemented.
2. **The wait is not durable.** `ExecuteAgentActivity` runs the whole dispatch→monitor→collect cycle inline with `await`. The workflow instance stays **Running** (not suspended on a bookmark) for the full ~35-minute monitor loop; a deploy, pod eviction, or crash during that window loses the in-flight monitor and — with no task-level re-entry — restarts the entire `SingleIssueCycleWorkflow` from scratch. The webhook signal plane (`WebhookSignalRegistry`) is an **in-memory, single-process** `ConcurrentDictionary<TaskCompletionSource>`: a `workflow_run.completed` webhook that lands on a different pod than the waiting monitor never matches.

Epic 40 closes both gaps. It **ships the runner contract** (both SaaS and single-user), and it **makes the coding step resumable by design** to the Epic 39 standard — replacing the inline monitor with a durable bookmark suspend, replacing the in-memory signal plane with a persisted signal that survives restart and crosses pods, and giving the per-task loop a git-and-events-based re-entry so a crash resumes at the right task instead of re-implementing.

### Relationship to Epic 39 — code is NOT a document type

Epic 39 is explicit: *"Code's store is git, its validator is the build/test/gate stack, and its review is a `Review` whose subject is a diff. No schema needed."* So the coding step does **not** move onto `DocumentLifecycleWorkflow`; it needs its **own** resumable pattern. That pattern is built on 39-10's mechanism — `LifecycleBookmarks` (tenant-folded deterministic bookmark names), the `[ResumeBehavior]` declaration + `ResumableStandardStructuralTests` build gate, `CanonicalSuspendActivities`, and the `LegacyResumeAllowlist` burn-down — but its re-entry read model is **git + DCB events**, not the 39-11 document store.

## 2. Design principles (settled)

- **The runner is a versioned contract, shipped and installed, not assumed.** `tamma-agent.yml` carries a `tamma-runner-version`; Tamma scaffolds it into a user repo through the GitHub App and detects drift. The result-artifact schema is pinned to `AgentResultArtifactParser` by a drift test, exactly as prompt contracts are pinned to their parsers.
- **The wait suspends, it never spins.** The coding step dispatches, then **suspends on a durable Elsa bookmark** with a `DelayFor` timeout — the `WaitForCIResultsActivity` shape — so the workflow holds no thread and survives any restart. `Received`/`Timeout` are deterministic edges; a lost webhook times out to a loud escalation, never a hang.
- **The signal is durable and the bookmark store is the backplane.** Once the step suspends on a real DB-persisted Elsa bookmark, **any pod** can resume it through Elsa's bookmark store — the cross-pod delivery the in-memory TCS never had.
- **Code's re-entry read is git + events, not a document store.** "Which task already landed" is reconstructed from commits on the branch and `AGENT_RUN.*`/`CODE.*` DCB events keyed by the deterministic per-task session id (`adl-{issue}-task-{index}`) — never from Elsa instance internals.
- **One resumable standard, enforced by the same build gate.** `SingleIssueCycleWorkflow` declares `[ResumeBehavior]`, registers `WaitForAgentRunActivity` as a canonical suspend activity, and **burns down its `LegacyResumeAllowlist` entry** — the first Epic-40 consumer of the 39-10 gate.
- **Per-mode scoping, drawn twice.** SaaS: the runner executes in the *tenant's* GitHub Actions with the *tenant's* repo secrets; Tamma never sees the agent API keys; bookmarks and signals are tenant-folded. Single-user: the runner is the local CLI on the sole user's host with the user's own keys.

## 3. The runner contract (reconstructed from the dispatch/result seam)

The runner is contracted to: **checkout `branch_name` → install the agent CLI (Claude Code default) → materialize the plan slice → run the coding agent + TDD with repo-secret API keys → push commits to the branch → emit `.tamma/result.json` (the `AgentResultArtifact` schema) → upload it as an artifact named `tamma-result` → post an issue comment.** Idempotent, 30-min timeout. In the SingleIssueCycle context the PR is created by a separate `pull-request` step *before* the TDD loop, so the per-task runner pushes commits to an existing branch and does not itself open the PR.

Result-artifact schema (drift-pinned): artifact **name** `tamma-result`; entry a file ending `result.json`; JSON fields consumed by `AgentResultArtifactParser.ParseResultJson`: `success, task, issue_number, branch_name, tamma_session_id, files_changed[], pr_number, commit_sha, error_message, agent_log_summary, tokens_used, duration_seconds, agent_provider, agent_version`. Caps enforced server-side (4 MB result.json, 2000 files, 32 KB log summary).

## 4. Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 40-1 | The `tamma-agent.yml` Runner Contract & Repo Scaffolding (+ single-user CLI parity) | P0 | Drafted |
| 40-2 | `WaitForAgentRunActivity` — durable bookmark suspend + `DelayFor` timeout | P0 | Drafted |
| 40-3 | Durable agent-run signal plane + resume endpoint (cross-pod, restart-safe) | P0 | Drafted |
| 40-4 | Per-task re-entry — reconstruct landed tasks from git + DCB events | P0 | Drafted |
| 40-5 | `[ResumeBehavior]` on `SingleIssueCycleWorkflow` + allowlist burn-down | P0 | Drafted |
| 40-6 | Agent-run lifecycle event family + re-entry feed | P1 | Drafted |
| 40-7 | End-to-end crash/restart + mode-matrix integration proof | P0 | Drafted |

## 5. Supersedes / absorbs

- **Story 19-1 (tamma-agent workflow template)** — formally *completed* by 40-1: 19-1 authored the contract but never shipped the file/scripts/scaffolding; 40-1 delivers them and pins the artifact schema to `AgentResultArtifactParser`.
- **The inline monitor inside `ExecuteAgentActivity`** — its dispatch/collect halves are retained (reused by `WaitForAgentRunActivity`), but the inline ~35-min `await` on `IAgentMonitorService.MonitorAsync` is replaced by the durable bookmark suspend (40-2). `ExecuteAgentActivity` remains for non-resumable callers/tests.
- **`WebhookSignalRegistry` (in-memory)** — superseded by the durable signal + Elsa bookmark store (40-3). It may remain as a same-process fast-path optimization, but correctness no longer depends on it.
- **NOT superseded:** `AgentDispatchService` / `AgentMonitorService` (poll) / `AgentResultCollectorService` / `ActionsResultAggregator` / `AgentResultArtifactParser` stay the dispatch/collect substrate; the `AGENT_DISPATCH.RUN_*` mediation event family stays; 40-6 adds engine-side *wait/re-entry* events beside it.

## 6. Dependencies

- **Epic 39-10 (Resumable-by-Design Standard) — HARD, must land first.** Epic 40 consumes `LifecycleBookmarks`, `ResumeBehaviorAttribute`/`ResumeMode`, `CanonicalSuspendActivities`, `LegacyResumeAllowlist`, and `ResumableStandardStructuralTests`.
- **Epic 39-8 (Escalation & Approval Surface) — SOFT.** `DocumentDecisionResumeEndpoint` is the tenant-folded resume-endpoint precedent 40-3 mirrors.
- **Story 4-7 (event query API) + 4-8 (`ReplayReconstructor`)** — the DCB read path 40-4's re-entry uses, plus the git compare/PR reads already in `ActionsResultAggregator`.
- **Existing substrate (in place, verified):** the full `Tamma.Activities/AgentDispatch/*` stack, `AgentResultArtifactParser`, `ActionsResultAggregator`, `WaitForCIResultsActivity` (the durable-wait precedent), the GitHub App installation + `InstallationRouterService` webhook receiver, Elsa 3 bookmarks + EF persistence.
- **NOT a dependency:** the 39-11 document store / `DocumentLifecycleWorkflow` — code is not a document type; re-entry reads git + events instead.
- **Operating-mode detection (single-user vs SaaS)** — 40-1's runner install path and every bookmark/signal scoping decision is per-mode.

## 7. See also

- [Resumable Workflows](Resumable-Workflows) — the 39-10 standard this epic is the first non-document consumer of
- [Document Lifecycle](Document-Lifecycle) — Epic 39, the spine (and why code is deliberately not on it)
- [Epic 39: Document Lifecycle](Epics/Epic-39-Document-Lifecycle) — the resumable mechanism (39-10) reused here
- [Epic 19: GitHub App Agent Dispatch](Epics/Epic-19-Agent-Dispatch) — the dispatch/monitor/collect stack the runner completes
- [Agent Dispatch](Agent-Dispatch) — `LocalExecutor` / `GitHubActionsExecutor` topic page
- Story files: [Epic 40 on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-40)

---

_Last updated: 2026-07-24_
