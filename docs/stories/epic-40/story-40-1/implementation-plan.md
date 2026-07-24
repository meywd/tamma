# Implementation Plan — Story 40-1: The `tamma-agent.yml` Runner Contract & Repo Scaffolding

## Scope & Deliverable

When this story is done, the coding-agent runner that Tamma dispatches to **exists, is
version-marked, and produces exactly the artifact the collector parses** — closing the gap
where 19-1 specified but never shipped it. Concretely: a canonical `tamma-agent.yml` +
per-agent runner scripts live in-repo under `apps/tamma-elsa/runner/github-actions/`; a
drift test pins the emitted `result.json` to `AgentResultArtifactParser`; a
`RunnerScaffoldService` in `Tamma.Api` installs/upgrades the workflow into a tenant's repo
through the GitHub App and reports drift; and the single-user `LocalExecutor` path becomes
runnable on default configuration (today it fails on entry-point resolution — the
`execute-agent` command itself already exists). A tenant that runs the SingleIssueCycle now
gets a real agent run instead of a `WorkflowNotFound` dead end, and a self-hosted single
user gets a local run instead of a mis-resolved `node` invocation.

## Pre-Reading

- `docs/stories/epic-40/story-40-1/40-1-tamma-agent-runner-contract-and-scaffolding.md` — this story (ACs are source of truth)
- `docs/stories/epic-19/story-19-1/19-1-tamma-agent-workflow-template.md` — the historical contract (skeleton YAML, schema, security) — **reconcile, do not copy blindly**
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentDispatchService.cs:91` — `BuildDispatchInputs` (the seven inputs the runner MUST accept, exact names)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Models/AgentExecutionModels.cs:151` — `AgentResultArtifact` (the result schema)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentResultArtifactParser.cs` — the parser + caps the runner output must satisfy
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/ActionsResultAggregator.cs:39` — artifact name `tamma-result`, entry `result.json`, PR/compare/checks derivation
- `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs:101` — the **call** to `CheckWorkflowFileAsync`; **`:107-112`** — the `WorkflowNotFound` branch + the "add the template" string (the dead end this story removes; step 7 edits `:110`). *Corrected: the earlier cite `:100` is the preceding `// AC-8` comment, and the method itself is not declared here — see next line.*
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IGitHubActionsClient.cs:33` — where `CheckWorkflowFileAsync` is **declared**; implemented at `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/OctokitGitHubActionsClient.cs:63` (null seam `Tamma.Activities/AgentDispatch/NullGitHubActionsClient.cs:16`, test double `tests/Tamma.Api.Tests/AgentDispatch/FakeGitHubActionsClient.cs:34`). Same file is the installation-token client the scaffolding commit path uses.
- `apps/tamma-elsa/src/Tamma.Api/Services/Git/IGitRepoAuthorizer.cs` (via `AgentDispatchMediationService` usage) — tenant↔repo guard reused by the scaffold endpoint
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/LocalExecutor.cs` — the single-user request/result JSON protocol. **Read `:94` + `:184-193` (temp-dir `WorkingDirectory`) against `:246` (relative `CliEntryPoint`) — that mismatch is the actual defect.** Its XML doc (`:16-18`, `:40-43`) and error string (`:139`) claiming the CLI command is unimplemented are **stale and wrong**; fix them in this story.
- `packages/cli/src/commands/execute-agent.ts` (473 lines, registered at `packages/cli/src/index.tsx:158`, tested in `execute-agent.test.ts`) — the shell-out target. It **exists and implements the protocol**; there is simply no built `packages/cli/dist/`.
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentExecutorFactory.cs:69-77` — `local` is the auto-resolved default whenever no GitHub App is configured, so the broken local path is the *default* self-hosted path
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/AgentDispatchServiceTests.cs` — input-composition test shape to extend with the pin
- `.github/workflows/tamma-worker.yml` — an existing Tamma-authored workflow for house YAML conventions

## Design Decisions

- **D1 — The runner lives in-repo under `apps/tamma-elsa/runner/github-actions/`, NOT in Tamma's `.github/workflows/`.** It is a *template Tamma ships to users*, not a workflow that runs on Tamma's own repo. Keeping it in `.github/workflows/` would make GitHub try to run it on the Tamma repo (wrong) and would hide it from the drift test. `runner/` is the canonical source; the scaffold service embeds it (build-time `EmbeddedResource`) so `Tamma.Api` can commit its exact bytes into a tenant repo. Tamma's own dogfooding install (Tamma developing Tamma) is just another scaffold target.
- **D2 — Result schema is the single source of truth; the runner conforms to it, pinned by a golden fixture.** Rather than hand-syncing YAML to C#, ship `apps/tamma-elsa/runner/github-actions/result.schema.json` (JSON Schema) + a golden `result.example.json`; the collect script validates the runner's output against the schema in-run (fail the run on mismatch), and a C# drift test (`RunnerResultContractTests`) deserializes the same golden fixture through `AgentResultArtifactParser` and asserts every field maps. One fixture, checked both sides — schema drift fails CI on whichever side moved (mirrors `ContractBindingTests`/prompt-contract pinning).
- **D3 — Per-agent runner scripts behind a provider `case`, `claude-code` shipped.** `run-claude-code.sh` (install `@anthropic-ai/claude-code`, run headless with `.tamma/INSTRUCTIONS.md` + plan, capture tokens/duration) + `collect-results.sh` (assemble `result.json` from git state + agent exit + logs, validate against the schema). The dispatch `case "${{ inputs.agent_provider }}"` is the extension seam (19-1 AC); adding `aider` later is a new script + case, no workflow surgery.
- **D4 — The runner NEVER opens the PR in the per-task call.** Since the cycle creates the PR before the TDD loop (`SingleIssueCycleWorkflow` `createPR` precedes `initTaskLoop`), the runner's job for `task=implement` is: implement the plan slice, run tests, **push commits to `branch_name`**, emit the artifact with `pr_number` possibly null. The collector already derives PR/commit/files from git when the artifact omits them (`ActionsResultAggregator` steps 2-4). The runner supports an optional "open PR if none exists" only for the standalone (non-cycle) call, gated by an input default that the cycle leaves off.
- **D5 — Scaffolding is install + drift-detect + opt-in upgrade, never silent clobber.** `RunnerScaffoldService.GetStatusAsync(tenant, repo)` reads the repo's `.github/workflows/tamma-agent.yml`, parses its `tamma-runner-version`, and returns `{ absent | current | drifted(userVersion, shippedVersion) | customized }`. `InstallAsync` commits the canonical bytes only when absent (or on explicit `upgrade=true`); a user-customized copy (version marker removed/edited) reports `customized` and refuses to overwrite without `force`. Commit via `IGitHubActionsClient` extension (`CreateOrUpdateFileAsync` on the installation token) — the token stays inside `Tamma.Api`. Endpoints on the existing tenant-admin surface: `GET /api/repos/{repo}/runner`, `POST /api/repos/{repo}/runner/install`.
- **D6 — Single-user parity ships an in-process C# runner and retires the Node shell-out (path b).** *Corrected: the earlier framing ("an unimplemented `packages/cli` command") was false — `packages/cli/src/commands/execute-agent.ts` implements the protocol and is unit-tested. The choice is therefore between **packaging** working TS and **replacing** it, not between writing it and replacing it.* Path (a) would require adding `pnpm --filter @tamma/cli build` to the .NET app's build/image, shipping `dist/` alongside the binary, and defaulting `Agent:Local:CliEntryPoint` to an absolute path — i.e. binding the C# runtime to the legacy TS toolchain CLAUDE.md marks as largely superseded, and adding a Node dependency to the container. We take path (b): ship a C# `InProcessLocalRunner` that `LocalExecutor` invokes directly, running the coding-agent CLI as a child process on the host with the sole user's keys and emitting the `AgentResultArtifact` JSON. `LocalExecutor`'s file-protocol shape is preserved for back-compat/tests, and the existing Node command stays usable for anyone who configures an absolute `CliEntryPoint` — it is deprecated, not deleted. Step 8 also corrects the stale XML doc/error string (`LocalExecutor.cs:16-18`, `:40-43`, `:139`), which is what caused this mis-scoping in the first place.
- **D8 — The `tamma-agent.yml` default is collapsed to one constant.** Six behavioural sites hardcode the literal (story Architectural Context). Rather than pin six literals in a test, introduce one shared constant (`Tamma.Activities/AgentDispatch/` — reachable from both `Tamma.Activities` and `Tamma.Api`, matching the existing reference direction `Tamma.Api → Tamma.Activities`) and have all six read it. The drift test then pins **one** value against the shipped file's basename. If a reviewer prefers to leave the literals in place, the test must assert all six — pinning three (as the first draft implied) leaves `DispatchAgentWorkflowActivity.cs:100`, `AgentDispatchRequests.cs:25` and, most consequentially, the engine→API wire default `TammaApiModels.cs:494` free to drift.
- **D7 — Fail-loud on missing secrets, fail-safe on agent error.** No agent key ⇒ the runner writes `result.json {success:false, error_message:"ANTHROPIC_API_KEY secret not set"}` and exits non-zero (loud, but still uploads the artifact so the collector reports it, not a phantom timeout). Agent crash/timeout ⇒ same fail-safe artifact via a `always()` collect step (AC5). The workflow never completes green with no artifact.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/runner/github-actions/tamma-agent.yml`** — the canonical workflow (D1): `workflow_dispatch` with the seven inputs pinned to `BuildDispatchInputs` (`AgentDispatchService.cs:91`, names/types), `permissions` least-privilege, `timeout-minutes` from a `timeout` derivation (default 30), configurable `runs-on`, a `tamma-runner-version` env + echo, and the step sequence of AC2 with an `always()` collect+upload. Inline per-step YAML comments (AC9). **Then apply D8:** replace the six hardcoded `"tamma-agent.yml"` literals (`AgentDispatchService.cs:49`, `ExecuteAgentActivity.cs:187`, `DispatchAgentWorkflowActivity.cs:100`, `TammaApiModels.cs:494`, `AgentDispatchRequests.cs:25`, `AgentDispatchMediationService.cs:41`) with the shared constant, leaving the two doc-comment mentions (`WorkflowDispatchRequest.cs:11`, `AgentDispatchRequests.cs:24`) as prose.

2. **CREATE `apps/tamma-elsa/runner/github-actions/scripts/run-claude-code.sh` + `collect-results.sh`** (D3) — install/run/collect for claude-code; `collect-results.sh` assembles `result.json` from git (`git diff --name-only`, HEAD sha), agent exit code, token/duration capture, and validates against `result.schema.json` before upload.

3. **CREATE `apps/tamma-elsa/runner/github-actions/result.schema.json` + `result.example.json`** (D2) — the drift-pinned schema + golden fixture, snake_case keys matching `AgentResultArtifactParser`.

4. **CREATE the multi-agent dispatch seam** — the `case "${{ inputs.agent_provider }}"` block in `tamma-agent.yml` (AC6), `claude-code` wired, an `aider`/`*` documented-but-unshipped branch that exits with a clear "unsupported provider" artifact.

5. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/RunnerScaffoldService.cs` + `IRunnerScaffoldService.cs`** (D5, AC7) — `GetStatusAsync`/`InstallAsync` over an `IGitHubActionsClient` file-commit extension; embed the runner files as `EmbeddedResource` (csproj `<EmbeddedResource Include="..\..\runner\**" />` or a copied build asset) so the shipped bytes are the committed bytes. Reuse `IGitRepoAuthorizer` for the tenant↔repo guard.

6. **ADD the scaffold endpoints** to the tenant-admin API surface (`Tamma.Api` endpoint module) — `GET /api/repos/{repo}/runner`, `POST /api/repos/{repo}/runner/install` (body `{ upgrade?, force? }`), `PlatformOwner`/`tenant_admin` policy per mode; single-user mode → the endpoints are present but report `mode: single-user, use local runner` (no GitHub App).

7. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs:107-112`** — the `WorkflowNotFound` failure message (the string at `:110`) points at the scaffold action (`POST /api/repos/{repo}/runner/install`) rather than a bare "add the template" string. No behavior change beyond the message. *(Nothing changes in `IGitHubActionsClient`/`OctokitGitHubActionsClient`, which only report presence.)*

8. **IMPLEMENT single-user parity (D6)** — CREATE `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/InProcessLocalRunner.cs`; **MODIFY `LocalExecutor.cs`** to invoke it when `Agent:Local:InProcess=true` (default for single-user), preserving the file-protocol path behind the flag; run the coding-agent CLI as a child process via `IProcessRunner` with the user's local keys, emit the `AgentResultArtifact` JSON. **Also correct the stale in-code documentation** in the same edit: `LocalExecutor.cs:16-18` and `:40-43` (XML doc asserting the CLI command is unimplemented) and the `:139` error string — replace with the true failure mode ("result file not produced — check `Agent:Local:CliEntryPoint` resolves; the Node CLI requires a built `packages/cli/dist/`").

9. **CREATE the drift + scaffold tests** (see Test Plan) — `RunnerResultContractTests`, `RunnerScaffoldServiceTests`, extend `AgentDispatchServiceTests` with the input-name pin, `InProcessLocalRunnerTests`.

10. **CREATE `docs/guides/github-actions-runner-setup.md`** (AC9) — secrets, App-driven install, self-hosted runner labels, single-user local mode; cross-link 19-1 as delivered-by-40-1. Finish with full `dotnet test` + a YAML lint of the runner workflow.

## Data & Migrations

None. Runner status is read live from the target repo (no persisted state); no EF entities.
`dotnet ef migrations has-pending-model-changes` stays clean.

## Events

- **Reuses (no new engine events):** dispatch/collect emit the existing
  `AGENT_DISPATCH.RUN_TRIGGERED.*` / `RESULTS_COLLECTED.*` family (`AgentDispatchEventTypes`)
  unchanged. The 40-6 wait/re-entry events are out of scope here.
- **New (scaffold audit):** `RUNNER.SCAFFOLD.INSTALLED` / `RUNNER.SCAFFOLD.UPGRADED` /
  `RUNNER.SCAFFOLD.SKIPPED` emitted by `RunnerScaffoldService` on the DCB stream (tags
  `tenantId`, `repo`, `runnerVersion`) so an install into a user repo is auditable. One
  constant block in a `RunnerScaffoldEventTypes.cs`.

## Test Plan

All NUnit + FluentAssertions (+ Moq). YAML/script validated by CI lint, not C# unit tests.

- **`RunnerResultContractTests`** (unit, drift gate) — deserialize `result.example.json`
  through `AgentResultArtifactParser.ParseResultJson`; assert every field populated and typed;
  assert the JSON-Schema `required` set equals the parser's read set (fail if either side adds/
  drops a field). **Covers AC3.**
- **`AgentDispatchServiceTests` (extend)** — two pins. (a) the runner YAML's declared input
  names equal `BuildDispatchInputs`' keys (read the YAML from the runner path, assert
  set-equality); (b) the **filename** pin — after D8 the shared constant equals the shipped
  file's basename; if D8 is declined, assert all **six** literals instead. *Falsifiable:*
  renaming the shipped file, or editing any one of the six sites in isolation, reddens this
  test. **Covers AC1.**
- **`RunnerScaffoldServiceTests`** (unit, Moq'd `IGitHubActionsClient` + `IGitRepoAuthorizer`) —
  status matrix (absent/current/drifted/customized); install commits only when absent or
  `upgrade`; customized refuses without `force`; guard-denied → no commit + typed 403; emits
  `RUNNER.SCAFFOLD.*`. **Covers AC7.**
- **`InProcessLocalRunnerTests`** (unit, fake `IProcessRunner`) — happy path produces a valid
  `AgentResultArtifact`; agent non-zero exit → `success:false` artifact with error; timeout →
  fail-safe artifact; uses local keys, never a tenant secret. Plus the AC8 falsifiability
  case: resolve the executor through `AgentExecutorFactory` with **only default
  configuration** (no GitHub App, no `Agent:Local:*` overrides) and assert a parsed
  `AgentResultArtifact` — a test that passes today's tree would mean the story changed
  nothing. **Covers AC8, AC5 (local half).**
- **Runner self-test (CI job, optional integration)** — dispatch the shipped workflow on a
  throwaway test repo with a mock agent (`agent_provider=mock`), assert a `tamma-result`
  artifact with a schema-valid `result.json` and an issue comment. **Covers AC2, AC4, AC5, AC6.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — versioned workflow, seven inputs pinned + filename pinned at all six sites | 1 | `AgentDispatchServiceTests` input-name pin + filename pin |
| 2 — steps implement the contract | 1, 2, 4 | Runner self-test CI job |
| 3 — artifact matches parser (drift-pinned) | 2, 3 | `RunnerResultContractTests` |
| 4 — keys never reach Tamma; metadata-only artifact | 1, 2 | Reviewer check + self-test (no secret in logs/artifact) |
| 5 — idempotent, fail-safe artifact | 1, 2 | Runner self-test failure variant; `InProcessLocalRunnerTests` |
| 6 — multi-agent dispatch seam | 4 | Self-test with `agent_provider=mock` |
| 7 — SaaS scaffold install/upgrade/drift | 5, 6, 7 | `RunnerScaffoldServiceTests` |
| 8 — single-user local path runs on default config | 8 | `InProcessLocalRunnerTests` (incl. the default-configuration case) |
| 9 — documentation | 10 | Reviewer check: guide exists, YAML commented, 19-1 cross-linked |
| 10 — per-mode ownership explicit | 5, 6, 8 | Reviewer check: scaffold no-op in single-user, local runner used |

## Dependencies & Sequencing

- **Hard prerequisites:** none — this story ships on the *existing* dispatch/collect stack.
- **In place, verified:** `AgentResultArtifactParser`, `ActionsResultAggregator`,
  `IGitHubActionsClient`/`OctokitGitHubActionsClient` (needs a `CreateOrUpdateFile` extension —
  add if absent), `IGitRepoAuthorizer`, `LocalExecutor`/`IProcessRunner`/`AgentExecutorFactory`
  (classes + DI wiring only — the local *path* is broken, which is what step 8 fixes),
  the GitHub App flow.
- **Feeds:** 40-7 (the mode-matrix integration proof dispatches this real runner); 40-2/40-3
  wait *for* this runner's `workflow_run` but do not depend on its internals. **Step 8
  specifically feeds 40-2 AC8** — 40-2's single-user branch runs whatever
  `AgentExecutorFactory` resolves, so its *end-to-end* single-user proof is unreachable
  until this step lands (40-2 remains unit-testable against a stubbed executor meanwhile).
- **Independent of** 39-x — no Epic-39 hook is consumed here.
- **Sequencing within the story:** 1-4 (runner) → 5-7 (scaffold) ∥ 8 (local) → 9 → 10.

## Risks & Mitigations

- **Schema drift between YAML and C# recurs silently.** Mitigation: D2's single golden fixture
  checked both sides + `RunnerResultContractTests` as a build gate — the whole point of AC3.
- **claude-code headless flags change upstream.** Mitigation: pin the CLI version in the runner;
  the `run-claude-code.sh` install step uses an exact version; a broken agent still yields a
  fail-safe artifact (D7), never a hang.
- **Scaffolding overwrites a user's customized workflow.** Mitigation: D5's `customized` status +
  `force`-required overwrite; version marker is the drift signal; upgrade is opt-in.
- **19-1's skeleton has drifted from the current parser.** Mitigation: reconcile against the
  *code* (Pre-Reading paths), not the 19-1 doc; AC3's test arbitrates.
- **Node toolchain creeps into the .NET build.** The working `execute-agent` command makes
  path (a) tempting, but it would put `pnpm build` + a Node runtime in the app image.
  Mitigation: D6 chooses the in-process C# runner; the Node file protocol stays as a tested,
  deprecated back-compat seam for anyone who configures an absolute `CliEntryPoint`.
- **The stale `LocalExecutor` doc mis-scopes the story again.** It already did once (this
  plan's first draft). Mitigation: step 8 corrects `:16-18`, `:40-43`, `:139` in the same
  commit as the runner change, so the next reader is not misled.
- **Six defaults drift apart after D8 is declined.** Mitigation: the AC1 filename pin covers
  all six explicitly, including the engine→API wire default at `TammaApiModels.cs:494`.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1, 2, 4 | `tamma-agent.yml` + claude-code/collect scripts + provider seam | 2.0 |
| 3, 9 (contract) | result schema + golden fixture + `RunnerResultContractTests` | 0.75 |
| 5, 6, 7 | `RunnerScaffoldService` + endpoints + mediation message | 1.75 |
| 8 | single-user `InProcessLocalRunner` + `LocalExecutor` wiring | 1.5 |
| 9 (rest) | scaffold/local unit tests + dispatch input pin | 1.0 |
| 10 | runner self-test CI job + docs | 1.0 |
| **Total** | | **8.0** (story estimate: 6-8 days) |
