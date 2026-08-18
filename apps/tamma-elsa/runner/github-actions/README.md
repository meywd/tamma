# Tamma agent runner — setup

This directory is the **canonical source** of the CI-side coding-agent runner that
Tamma dispatches. It is a template Tamma ships to *your* repository; it is not a
workflow that runs on Tamma's own CI (Tamma's own copy under
`.github/workflows/tamma-agent.yml` is just the first install of it).

| File | Installs to | Purpose |
|---|---|---|
| `tamma-agent.yml` | `.github/workflows/tamma-agent.yml` | The `workflow_dispatch` runner Tamma triggers |
| `scripts/run-claude-code.sh` | `.github/tamma/scripts/run-claude-code.sh` | claude-code provider: install → run |
| `scripts/collect-results.sh` | `.github/tamma/scripts/collect-results.sh` | Assembles `.tamma/result.json` |
| `result.schema.json` | *(not installed)* | The result contract, pinned to Tamma's parser |
| `result.example.json` | *(not installed)* | Golden fixture used by `RunnerContractTests` |
| `install-runner.sh` | *(not installed)* | Installs the three files above into a repo |

All four installable/contract files carry a `# tamma-runner-version:` marker. The
workflow refuses to run if the scripts' marker differs from its own — a
half-upgraded install fails loud instead of running a mismatched contract.

## Install (SaaS / GitHub App)

```bash
# from a checkout of Tamma
apps/tamma-elsa/runner/github-actions/install-runner.sh --repo /path/to/your/repo
git -C /path/to/your/repo add .github && git -C /path/to/your/repo commit -m "Add the Tamma agent runner"
```

`--check` reports the state without writing (`absent` / `current` / `drifted` /
`customized`); `--upgrade` replaces an older version; `--force` overwrites a copy
you have edited. The installer never overwrites a customized file silently.

Without this file in the target repo, every dispatch fails the workflow-file
pre-check in `AgentDispatchMediationService` with `WorkflowNotFound` — that is the
dead end this template removes.

## Secrets and variables (in *your* repo)

| Name | Kind | Required | Meaning |
|---|---|---|---|
| `ANTHROPIC_API_KEY` | secret | yes, for `claude-code` | The agent's key. Lives only on your runner — Tamma never sends, receives, or logs it |
| `OPENAI_API_KEY` | secret | no | Passed through for providers that need it |
| `TAMMA_RUNNER_LABEL` | variable | no | `runs-on` label for self-hosted runners (default `ubuntu-latest`) |
| `TAMMA_RUNNER_TIMEOUT_MINUTES` | variable | no | Whole-job budget (default `30`) |
| `TAMMA_AGENT_TIMEOUT_MINUTES` | variable | no | Agent-step budget (default `25`; keep it below the job budget so collect still runs) |
| `TAMMA_CLAUDE_CODE_VERSION` | variable | no | Pin the agent CLI (default `latest`; pinning is recommended for reproducible runs) |
| `TAMMA_CLAUDE_EXTRA_ARGS` | variable | no | Replaces the default `claude` flags entirely |

A missing `ANTHROPIC_API_KEY` is a **loud** failure: the run still uploads a
`result.json` with `success:false` and that reason, so Tamma reports it instead of
timing out on a silent run.

### Self-hosted runners

Set `TAMMA_RUNNER_LABEL`, and make sure the runner image has `git`, `jq`, `node`
(22+), and ideally `gh`. Without `jq` the run still produces a schema-complete
`result.json`, but it reports failure with "jq is not installed on this runner".
Without `gh` the issue comment is skipped (never fatal).

## What a run does

1. Checks out `branch_name` (Tamma created it, and usually the PR, beforehand).
2. Verifies the install is a matched set.
3. Writes `.tamma/plan.json` and `.tamma/INSTRUCTIONS.md` (plan slice + issue/branch
   context + your `CLAUDE.md`, truncated). `.tamma/` is added to `.git/info/exclude`
   so scratch files are never committed.
4. Runs the agent for `agent_provider` — `claude-code` today, `mock` for smoke
   tests, and a documented `case` seam for adding others.
5. Commits and pushes to `branch_name`. **It does not open a pull request**: in a
   Tamma cycle the PR already exists, so a per-task run only adds commits.
6. Always writes `.tamma/result.json`, uploads it as the `tamma-result` artifact
   (1-day retention), comments on the issue, and fails the run if the agent failed.

## The contract (do not drift)

Tamma sends exactly seven `workflow_dispatch` inputs — `issue_number`, `task`,
`plan_json`, `branch_name`, `tamma_session_id`, `agent_provider`,
`agent_config_json` — and reads back exactly one artifact, `tamma-result`, whose
`result.json` matches `result.schema.json`.

Three copies of that key set exist: this schema, `TAMMA_RESULT_KEYS` in
`collect-results.sh`, and the `AgentResultArtifact` record in C#.
`RunnerContractTests` (`Tamma.Activities.Tests/AgentDispatch/`) asserts all three
agree, plus that the workflow's declared inputs equal what
`AgentDispatchService.BuildDispatchInputs` sends and that the installed copies are
byte-identical to the files here. Changing one side alone fails Tamma's build.

## Security posture

- Agent keys are repo secrets: they never leave the runner, and Tamma never sees them.
- The artifact is **metadata only** — paths, counts, a log tail. Never source.
- Tamma's inputs (`plan_json` above all) are attacker-shaped text and are passed to
  every `run:` block through `env:`, never interpolated into a shell body.
- Job permissions are `contents: write`, `pull-requests: write`, `issues: write` —
  nothing else.

## Single-user (self-hosted, no GitHub App)

This runner is not used. `AgentExecutorFactory` resolves the `local` executor when
no GitHub App is configured, and `LocalExecutor` runs the agent on the host with
the sole user's own keys, exchanging the same JSON shapes over files instead of
over an artifact. The entry point defaults to the repo-relative
`packages/cli/dist/index.js`, resolved to an absolute path against the app's base
directory and its ancestors; set `Agent:Local:CliEntryPoint` to an absolute path if
your CLI lives elsewhere, and build it first with `pnpm --filter @tamma/cli build`.

## History

Story 19-1 wrote this contract and left it `ready-for-dev`; the files were never
authored, so every hosted dispatch hit `WorkflowNotFound`. Story 40-1 ships them.
19-1 is delivered by 40-1.
