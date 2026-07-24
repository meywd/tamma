# Story 40-1: The `tamma-agent.yml` Runner Contract & Repo Scaffolding (+ single-user CLI parity)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **user who installed the Tamma GitHub App** (SaaS) — and as the **sole user of a
self-hosted Tamma** (single-user),
I want the CI-side coding-agent runner that Tamma dispatches to **actually exist, be
installed into my repo (or runnable locally), and produce exactly the result artifact
Tamma's collector expects**,
So that the coding/TDD implement step can run end-to-end instead of failing with *"Add the
Tamma agent workflow template to .github/workflows/"* — the dead end every tenant hits
today.

## Priority

P0 — Without a shipped runner the entire dispatch→monitor→collect stack has nothing to
dispatch to. Every other Epic-40 story (durable wait, signal plane, re-entry) makes the
*wait* correct; this story makes there be something to wait **for**.

## Architectural Context (READ FIRST)

**This is net-new, not a refactor.** Story 19-1 (`docs/stories/epic-19/story-19-1/`)
authored the runner *contract* — inputs, step outline, result-artifact schema, security
posture — and left it `ready-for-dev`. **The file was never created.** Confirmed by
research: no `tamma-agent.yml` anywhere on disk, no `templates/` runner dir (only
`.dev/templates`, unrelated), no `run-claude-code.sh`/`collect-results.sh`, and no
scaffolding that installs it into a user repo. Tamma's own `.github/workflows/` has
`tamma-worker.yml` but no `tamma-agent.yml`.

The C# side is fully built and pins the runner's output contract:

- **Dispatch inputs** — `AgentDispatchService.BuildDispatchInputs`
  (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentDispatchService.cs:91`) sends
  exactly seven `workflow_dispatch` string inputs: `issue_number, task, plan_json,
  branch_name, tamma_session_id, agent_provider, agent_config_json`.
- **Workflow file name** — *Corrected (this story previously said "three places"):* the
  literal `tamma-agent.yml` is a hardcoded default in **six** behavioural places:
  1. `Tamma.Activities/AgentDispatch/AgentDispatchService.cs:49`
  2. `Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs:187`
  3. `Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs:100`
  4. `Tamma.Activities/LlmCall/Models/TammaApiModels.cs:494`
     (`AgentDispatchRunApiRequest`) — the **engine→API wire default**, so an engine
     caller that omits the field silently gets `tamma-agent.yml` on the API side too
  5. `Tamma.Api/Services/AgentDispatch/AgentDispatchRequests.cs:25`
     (`DispatchAgentRunRequest`)
  6. `Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs:41`
     (`DefaultWorkflowFile`)

  Two further mentions are **prose only, not behaviour** and must not be counted:
  `Tamma.Platforms.Abstractions/Models/WorkflowDispatchRequest.cs:11` and
  `AgentDispatchRequests.cs:24`.
- **Presence check vs. fail-loud policy — two different owners.** *Corrected (this story
  previously wrote "the mediation `CheckWorkflowFileAsync`", implying the mediation
  service declares it):* the check is declared on `IGitHubActionsClient.cs:33` and
  implemented by `Tamma.Api/Services/GitHub/OctokitGitHubActionsClient.cs:63` (null seam:
  `Tamma.Activities/AgentDispatch/NullGitHubActionsClient.cs:16`).
  `AgentDispatchMediationService` only **calls** it (`AgentDispatchMediationService.cs:101`).
  What the mediation service *does* own is the fail-loud policy and the user-facing
  string: `WorkflowNotFound` + *"Add the Tamma agent workflow template to
  .github/workflows/"* at `AgentDispatchMediationService.cs:107-112`. AC7 edits that
  string, not a method on the client.
- **Result artifact** — `ActionsResultAggregator`
  (`apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/ActionsResultAggregator.cs:39`)
  downloads the artifact named **`tamma-result`**, opens the entry ending **`result.json`**,
  and `AgentResultArtifactParser.ParseResultJson`
  (`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentResultArtifactParser.cs:38`)
  decodes the `AgentResultArtifact` fields
  (`Models/AgentExecutionModels.cs:151`): `success, task, issue_number, branch_name,
  tamma_session_id, files_changed[], pr_number, commit_sha, error_message,
  agent_log_summary, tokens_used, duration_seconds, agent_provider, agent_version`.
  Caps: `MaxResultJsonBytes=4MB`, `MaxFilesChangedCount=2000`, `MaxAgentLogSummaryChars=32K`.

**Single-user parity is unshipped — but not for the reason the code claims.**
`LocalExecutor` (`Tamma.Activities/AgentDispatch/LocalExecutor.cs`) spawns
`node packages/cli/dist/index.js execute-agent --request … --output …` and reads back an
`AgentResultArtifact`-shaped file. *Corrected (this story previously said the
`execute-agent` command is "not implemented"):* it **is** implemented —
`packages/cli/src/commands/execute-agent.ts` (473 lines) implements the request/result
JSON protocol verbatim, is registered at `packages/cli/src/index.tsx:158`, and has unit
tests in `execute-agent.test.ts`. Three real defects break the path instead:

- **No build output.** There is no `packages/cli/dist/` in the tree — the shell-out
  target only exists after `pnpm --filter @tamma/cli build`.
- **The default entry point cannot resolve.** `LocalExecutorOptions.CliEntryPoint`
  defaults to the **relative** path `packages/cli/dist/index.js` (`LocalExecutor.cs:246`)
  while the child process is launched with `WorkingDirectory` = a per-session temp dir
  (`LocalExecutor.cs:94`; `ResolveWorkingDirectory` `:184-193`). `node` therefore resolves
  it against the temp dir and fails, unless `Agent:Local:CliEntryPoint` is configured to
  an absolute path.
- **Stale in-code documentation.** `LocalExecutor.cs:16-18`, `:40-43`, and the error
  string at `:139` still assert the command "may not be implemented yet". They are wrong
  and misled this story's first draft.

This is not a self-hosting-only concern: `AgentExecutorFactory` auto-resolves **`local`**
whenever no GitHub App is configured (`AgentExecutorFactory.cs:69-77`), so `local` is the
*default* executor on a self-hosted install — and today it fails on entry-point
resolution rather than on a missing feature.

**In the SingleIssueCycle context the runner does NOT open the PR.** The `pull-request`
sub-workflow creates the draft PR *before* the TDD loop
(`SingleIssueCycleWorkflow.cs` `createPR` precedes `initTaskLoop`), and `task="implement"`
with `plan_json` = a single-task slice. So the per-task runner **pushes commits to the
existing branch**; `pr_number` in the artifact may be null and the collector derives PR/commit
state from git (`ActionsResultAggregator` steps 2-4). The runner contract must therefore be
correct for the "implement one task on an existing branch" call, not only the "one-shot whole
issue" call.

## Acceptance Criteria

1. **Canonical, versioned runner workflow shipped in-repo.** A real
   `apps/tamma-elsa/runner/github-actions/tamma-agent.yml` (path may differ; a `runner/`
   home under the app, not `.github/workflows/` of Tamma itself) triggers **only** on
   `workflow_dispatch` with exactly the seven inputs
   `AgentDispatchService.BuildDispatchInputs` sends (names/types byte-matched). It carries a
   `tamma-runner-version` marker (comment + an env/echo the run logs) so drift/upgrade can be
   detected.

   **The filename default is pinned everywhere it exists.** The shipped file's basename is
   byte-matched against **all six** hardcoded defaults enumerated in Architectural Context —
   either by collapsing them to one shared constant that the test pins, or by a test that
   asserts all six literals equal the shipped basename. *Falsifiable:* changing any one of
   the six sites (or renaming the shipped file) without changing the rest fails the build.
   A test that pins only three of the six does not satisfy this AC.

2. **Runner steps implement the contract.** checkout `branch_name` → set up the agent
   environment → write the plan slice (`plan_json`) and a `.tamma/INSTRUCTIONS.md` (plan +
   branch/issue context + repo conventions read from `CLAUDE.md` if present) → run the coding
   agent (`agent_provider`, default `claude-code`) in headless mode using **repo-secret API
   keys** (`ANTHROPIC_API_KEY`, …) → run the repo's tests (TDD) → commit and **push to
   `branch_name`** → always emit `.tamma/result.json` → upload it as an artifact named
   **`tamma-result`** → post an issue status comment. Configurable `runs-on` and a
   `timeout-minutes` (default 30).

3. **Result artifact matches the parser exactly (drift-pinned).** The emitted `result.json`
   uses the exact snake_case keys `AgentResultArtifactParser` reads, with correct JSON types.
   A **drift test** (C#, `Tamma.Activities.Tests` or `Tamma.Api.Tests`) parses a golden
   `result.json` fixture that the runner's collect script also validates against, and asserts
   round-trip against `AgentResultArtifact` — so a schema change on either side fails the
   build (the prompt-contract-pin precedent).

4. **Agent keys never reach Tamma.** The workflow reads agent API keys from GitHub Actions
   secrets only; no key is echoed, logged, or written into the artifact. The artifact carries
   metadata only (no source content). Retention ≤ 1 day (`actions/upload-artifact@v4`).

5. **Idempotent and fail-safe.** Re-running with the same `tamma_session_id`/`branch_name`
   does not duplicate commits or PRs (guard by checking existing branch state); on agent
   failure or timeout the workflow **still uploads** a `result.json` with `success:false` and
   an `error_message`; the collect step runs even under GitHub's cancellation signal.

6. **Multi-agent dispatch seam.** The `agent_provider` input routes to a per-agent runner
   script (`claude-code` shipped; a documented `case` dispatch for adding `aider`/others),
   each with install → run → collect phases producing the same `result.json`.

7. **SaaS scaffolding path — install/update into a user repo via the GitHub App.** A Tamma
   service (e.g. `RunnerScaffoldService` in `Tamma.Api`) can, through the tenant's GitHub App
   installation, **commit `tamma-agent.yml` (and its scripts) into `.github/workflows/` of a
   target repo** and **detect version drift** against the shipped canonical copy. Endpoint(s)
   under the tenant-scoped admin surface: check status, install/upgrade. Tenant↔repo
   authorization reuses the existing `IGitRepoAuthorizer` guard; the install commit uses the
   installation token minted inside `Tamma.Api` (never in the engine). The fail-loud
   `WorkflowNotFound` message at **`AgentDispatchMediationService.cs:107-112`** — *not* a
   method on `IGitHubActionsClient`, see Architectural Context — points at this install
   action instead of the bare "add the template" string.

8. **The single-user local path runs on a default install.** *Corrected: the
   `execute-agent` command already exists (Architectural Context), so the deliverable is
   making the local path **resolvable**, not writing the command.* On a self-hosted install
   with no GitHub App configured — where `AgentExecutorFactory` resolves `local`
   (`AgentExecutorFactory.cs:69-77`) — the coding step completes and produces a parsed
   `AgentResultArtifact` **without hand-editing configuration**. The plan picks and
   justifies exactly one of: (a) package the existing Node CLI (build `dist/` as part of
   the app build/image and default `Agent:Local:CliEntryPoint` to an absolute, resolvable
   path), or (b) replace the shell-out with an in-process C# runner. Either way the sole
   user's own agent keys from local config are used, never a tenant secret, and the stale
   claims at `LocalExecutor.cs:16-18`, `:40-43`, `:139` are corrected to match reality.
   *Falsifiable:* a test exercising the local path with **only default configuration**
   yields a parsed `AgentResultArtifact`; run against today's tree the same test fails on
   entry-point resolution.

9. **Documentation.** A user-facing setup doc (secrets to add, how the App installs the
   workflow, self-hosted runner labels) and inline YAML comments per step. The
   `docs/stories/epic-19/story-19-1` contract doc is cross-linked as the historical origin and
   marked delivered-by-40-1.

10. **Per-mode ownership is explicit.** The story/plan states, for both modes, who owns the
    runner, whose keys it uses, and where the result lands — and the scaffolding path is a
    no-op in single-user mode (no GitHub App), where the local runner (AC8) is used instead.

## Technical Notes

- **The 19-1 YAML skeleton is a starting point, not a spec to copy blindly** — reconcile every
  input name and every `result.json` key against the *current* C# parser/aggregator (drift may
  have occurred since 19-1 was written), and let AC3's drift test be the arbiter.
- **PR creation belongs to the cycle, not the per-task runner** (see Architectural Context) —
  the runner must behave correctly when `pr_number` is null and only commits are pushed.
- Keep the workflow's `permissions` minimal (`contents: write`, `pull-requests: write`,
  `issues: write`) — least privilege, 19-1 AC.
- The scaffolding commit must be reviewable/diffable and must not clobber a user's customized
  copy without an explicit upgrade action (drift *detected*, upgrade *opted into*).

## Dependencies

- **Existing (verified):** `AgentDispatchService`/`AgentResultArtifactParser`/
  `ActionsResultAggregator`/`AgentDispatchMediationService`, `IGitHubActionsClient` +
  `OctokitGitHubActionsClient` (installation-token commit path), `IGitRepoAuthorizer`, the
  GitHub App installation flow, `LocalExecutor` + `IProcessRunner` +
  `AgentExecutorFactory`. *Scope note:* those classes and their DI wiring are in place; the
  local **path** is what does not work today (entry-point resolution, AC8) — do not read
  "verified" as "the local coding step runs".
- **Story 19-1** — the contract this story delivers (was never implemented).
- **Independent of** 40-2..40-7 **for the GitHub Actions path** — ships the runner
  regardless of the durable-wait work; those stories change *how Tamma waits*, not *what
  the runner does*.
- **Feeds 40-2 AC8 (single-user).** 40-2's local branch short-circuits to `Received` by
  running whatever `AgentExecutorFactory` resolves; that is unit-testable against a stubbed
  executor today, but the **single-user end-to-end** proof (a real local run producing a
  real `AgentResultArtifact`) is only reachable once **this story's AC8** lands. 40-2
  states the same edge from its side.

## Estimated Effort

6-8 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
| 2026-07-24 | 1.1.0   | Code-verified revision: `tamma-agent.yml` default count 3 → 6 (+2 prose-only) and AC1 byte-match widened to all six; `CheckWorkflowFileAsync` re-attributed to `IGitHubActionsClient`/`OctokitGitHubActionsClient` with the fail-loud message located at `AgentDispatchMediationService.cs:107-112`; AC8 rewritten — `execute-agent` exists, the defect is entry-point resolution/packaging; 40-1 AC8 recorded as feeding 40-2 AC8 | Claude |
