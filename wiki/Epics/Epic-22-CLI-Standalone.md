# Epic 22: CLI Mode Preservation

**Status:** Largely superseded by Epic 19 (Agent Dispatch). Stories 22-1 and 22-2 delivered via `IAgentExecutor` + `LocalExecutor`; 22-3 (cloud sync) and 22-4 (parity matrix) remain drafted.
**Stories:** 4 active + 1 optional (22-5 CLI Docker install) drafted under stories folder
**Estimated Effort:** ~44h original scope; ~16h absorbed by Epic 19, ~16h remaining across 22-3/22-4

## Overview

Epic 22 is Tamma's "no cloud required" commitment. The CLI mode (`tamma start`) must keep working — with local agents running on the operator's machine, a local Elsa workflow engine, and local configuration files — whether or not the user has ever heard of tamma.dev. The epic exists to formally own the guarantee that the same platform runs in three shapes: standalone CLI (zero cloud), SaaS (GitHub App + Actions runners), and hybrid (local execution with optional cloud-sync observability).

Most of Epic 22's work is already done — but not in this epic. When Epic 19 shipped the GitHub-Actions dispatch path, it had to abstract execution behind the `IAgentExecutor` interface (originally scoped as Story 22-1) and it had to keep the local execution path working (originally scoped as Story 22-2). Both shipped in Story 19-5 in the auth-foundation sprint (2026-04-18..2026-04-21). Epic 22's residual scope is the optional cloud-sync transport (22-3) and the feature-parity documentation matrix (22-4).

## Architecture

```
User's machine
├── ~/.tamma/providers.json           (per-user provider + model config)
├── <repo>/.tamma/config.json         (per-repo agents, security, prompts)
├── tamma CLI (@tamma/cli)            (Ink 5 React-for-CLIs TUI)
│     └── spawns local child process for agent execution
├── @tamma/orchestrator               (14-step loop, in-process Elsa)
├── @tamma/events (InMemoryEventStore + optional CloudSyncTransport)
└── agent subprocess (claude, opencode, etc.)

Tamma Cloud (SaaS mode only)
├── Elsa Workflow Engine (global + per-tenant, C#)
├── AgentExecutorFactory →  GitHubActionsExecutor
│     └── POST workflow_dispatch  →  user's GitHub repo
│                                     └── .github/workflows/tamma-agent.yml
│                                         (agent runs on user's Actions runner)
└── Observability dashboard (optional sink for hybrid-mode clients)
```

**Mode resolution** — deterministic, four-step, **fail-fast** (Epic 19 Story 19-5):

1. Explicit override passed by the caller (`ExecuteAgentActivity` workflow input).
2. Environment variable `TAMMA_AGENT_MODE=Local | GitHubActions`.
3. Configuration `Agent:ExecutorMode = Local | GitHubActions | Auto`.
4. Auto-detection: `GitHubActions` if a GitHub App is configured; otherwise `Local`.

A misconfiguration — for example `TAMMA_AGENT_MODE=GitHubActions` with no GitHub App credentials — is caught at dispatch time by `NullGitHubActionsClient` reporting `NotConfigured`, not at startup and not silently falling back.

**Event flow (hybrid mode, 22-3 scope)**:

```
@tamma/orchestrator → IEventStore.append(event)
                            ├── InMemoryEventStore   (always, for CLI TUI)
                            └── CloudSyncTransport   (optional; when tamma.cloud.apiKey set)
                                      └── POST /api/v1/events/ingest  →  Tamma Cloud Dashboard
```

Cloud-sync is strictly observability: the agent still runs locally, the event store of record is still local, and offline operation is still fully supported. Packets are batched and signed; failures degrade silently (log-and-drop) without blocking the engine.

## Components

| Surface | Component | Location | Status |
|--------|-----------|----------|--------|
| Executor abstraction | `IAgentExecutor` | `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentExecutor.cs` | Done (via Epic 19) |
| Executor selection | `AgentExecutorFactory` | same folder / `AgentExecutorFactory.cs` | Done (Epic 19) |
| Local mode | `LocalExecutor` | `LocalExecutor.cs` | Done (Epic 19) |
| SaaS mode | `GitHubActionsExecutor` | `GitHubActionsExecutor.cs` | Done (Epic 19) |
| Subprocess protocol | `IProcessRunner` + `DefaultProcessRunner` | `IProcessRunner.cs`, `DefaultProcessRunner.cs` | Done |
| TS CLI bridge | `tamma execute-agent` | `packages/cli/src/commands/execute-agent.ts` | Done |
| Request/response shape | `AgentExecutionRequest`, `AgentExecutionResult`, `AgentResultArtifact` | `Models/` | Done (bounded strings, 4 MB artifact cap) |
| Standalone entry | `tamma start` | `packages/cli/src/commands/start.ts` | Done |
| Cloud-sync transport (22-3) | `CloudSyncTransport` | planned in `packages/events/src/cloud-sync.ts` | Planned |
| Parity matrix (22-4) | `docs/cli-saas-parity.md` | not yet written | Planned |
| Docker install (22-5) | `docker/tamma-cli.Dockerfile` | drafted in `docs/stories/epic-22/22-5-cli-docker-installation.md` | Drafted |

Execution flows through a **JSON shell-out protocol** between the C# Elsa activity and the TypeScript CLI: `LocalExecutor` writes `.tamma/exec-request-{sessionId}.json`, invokes `tamma execute-agent --request <path> --output <path>`, waits for the subprocess, and reads `.tamma/exec-result-{sessionId}.json` back. Both files are `AgentExecutionRequest`/`AgentResultArtifact`-shaped; a non-zero exit code is mapped to a diagnostic `AgentExecutionResult` with failure context.

## Class diagram

```
                   ┌─────────────────────────┐
                   │    IAgentExecutor       │
                   │  ExecuteAsync(req)      │
                   │  Mode {get;}            │
                   └───────────┬─────────────┘
                               │ implements
                 ┌─────────────┴─────────────┐
                 │                           │
      ┌──────────▼──────────┐     ┌──────────▼──────────┐
      │   LocalExecutor     │     │ GitHubActionsExecutor│
      │   Mode = "local"    │     │ Mode = "github_actions"│
      └──────────┬──────────┘     └──────────┬──────────┘
                 │ uses                      │ uses
      ┌──────────▼──────────┐     ┌──────────▼──────────┐
      │   IProcessRunner    │     │ IGitHubActionsClient│
      │  (DefaultProcessRunner)│  │ (OctokitClient /    │
      └──────────┬──────────┘     │  NullGitHubActionsClient)│
                 │                └─────────────────────┘
      ┌──────────▼──────────┐                     │
      │ tamma execute-agent │                     │ dispatches
      │ (Node subprocess)   │              ┌──────▼───────┐
      └──────────┬──────────┘              │ GitHub       │
                 │ reads/writes            │ workflow_dispatch│
      ┌──────────▼──────────┐              └──────────────┘
      │ .tamma/exec-*.json  │
      │ (request + result)  │
      └─────────────────────┘

 AgentExecutorFactory
  ├── Create(mode, deps)   → IAgentExecutor
  └── ResolveMode(env, config, auto) → ExecutionMode
```

## Sequence diagram (CLI standalone, happy path)

```
Operator        tamma CLI        C# Elsa          LocalExecutor   tamma execute-agent     ClaudeAgentProvider
    │                │               │                   │                │                       │
    │ tamma start    │               │                   │                │                       │
    │───────────────▶│               │                   │                │                       │
    │                │ boot engine   │                   │                │                       │
    │                │──────────────▶│                   │                │                       │
    │                │               │ ExecuteAgentActivity                                        │
    │                │               │──────────────────▶│                │                       │
    │                │               │                   │ write request  │                       │
    │                │               │                   │───────────────▶│                       │
    │                │               │                   │ spawn subprocess                       │
    │                │               │                   │───────────────▶│                       │
    │                │               │                   │                │ dispatch agent        │
    │                │               │                   │                │──────────────────────▶│
    │                │               │                   │                │                       │ run task
    │                │               │                   │                │◀──────────────────────│ artifact
    │                │               │                   │ exit 0         │                       │
    │                │               │                   │◀───────────────│                       │
    │                │               │                   │ read result.json                       │
    │                │               │ AgentExecutionResult                                        │
    │                │               │◀──────────────────│                │                       │
    │                │ TUI update    │                   │                │                       │
    │                │◀──────────────│                   │                │                       │
    │ live logs      │               │                   │                │                       │
    │◀───────────────│               │                   │                │                       │
```

## Use cases

1. **Offline developer hacks locally** — `tamma start` in a repo with `.tamma/config.json`. No network calls beyond the target Git remote. All events in `InMemoryEventStore`, TUI shows real-time progress.
2. **Self-hosted server** — `tamma server` runs Fastify API + engine on user's VPS; no Tamma Cloud dependency. Agents still dispatched via `LocalExecutor`.
3. **SaaS mode (Tamma Cloud)** — user installs GitHub App; `AgentExecutorFactory` resolves to `GitHubActionsExecutor`; Tamma Cloud never clones user code (Epic 19 security model).
4. **Hybrid mode (planned 22-3)** — CLI user opts in with `tamma.cloud.apiKey` set; local execution continues; events additionally POST to `/api/v1/events/ingest` so the user can watch runs in the Tamma Cloud dashboard. If cloud is unreachable the CLI keeps running.
5. **Docker distribution (drafted 22-5)** — `docker run ghcr.io/tamma/cli` image for teams that want `tamma` in a sealed container. Mounts `$HOME/.tamma` + the project repo as volumes.
6. **Parity matrix (22-4)** — maintainers block SaaS-only scope creep: every new feature must declare whether it's CLI, SaaS, or both. Doc lives at `docs/cli-saas-parity.md` and is referenced from PR templates.

## Principles

1. **No cloud required** — `tamma start` works with zero internet beyond the target Git platform.
2. **No account required** — CLI users are never forced to create a Tamma Cloud account.
3. **Agents run where the user chooses** — local is local; cloud-sync is observability only, never delegation.
4. **Shared engine, different execution** — the same Elsa workflows run in both modes; only the executor backend changes.
5. **Additive cloud features** — cloud connectivity can add monitoring but never gate core features.

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 22-1 | `IAgentExecutor` Abstraction | P0 | 12h | **Superseded — delivered by Epic 19 Story 19-5** |
| 22-2 | CLI Standalone Workflow Engine | P0 | 16h | **Done — Epic 19 Story 19-5 + existing `tamma start`** |
| 22-3 | Optional Cloud Sync (`CloudSyncTransport`) | P2 | 10h | Drafted |
| 22-4 | CLI + SaaS Feature Parity Matrix | P1 | 6h | Drafted |
| 22-5 | CLI Docker Installation | P2 | — | Drafted |

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| AI Providers | Epic 1 | `IAgentProvider` + `ICLIAgentProvider` for local execution |
| Engine Core | Epic 10 | `TammaEngine` + in-memory event store |
| Elsa Workflows | Epic 7 | Shared workflow engine (both modes) |
| **Agent Dispatch (delivered)** | **Epic 19** | **`IAgentExecutor`, `LocalExecutor`, `GitHubActionsExecutor`** |
| Agent Management | Epic 9 | Role-based provider selection |
| Events | Epic 4 | Event store that cloud-sync will drain |

## Current state

- **Delivered**: `IAgentExecutor` interface and both implementations ship in `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`. `AgentExecutorFactory` resolves mode via the env/config/auto precedence. The TS `packages/cli/src/commands/execute-agent.ts` implements the JSON shell-out protocol. `tamma start`, `tamma server`, and `tamma api` CLI modes all work.
- **Security hardening** (from the Epic 19 2026-04-20 code review) applies here: tenant-scoped `WebhookSignalRegistry` aliases, 4 MB artifact cap in `LimitedStream`, and 2 KB/32 KB string clamps in `AgentResultArtifact`. None of these affect the CLI standalone path — they only protect the SaaS path.
- **Planned**: `CloudSyncTransport` (22-3) for hybrid-mode observability; the feature parity matrix (22-4); optional Docker distribution (22-5).
- **Open risks**: none for the standalone path (already shipped). For cloud-sync, the transport must be purely additive with circuit-breaker behaviour so a Tamma Cloud outage never stops a local run.

## Why Epic 22 still exists (and is not deleted)

Even though Epic 19 absorbed 22-1 and 22-2, this epic stays as the permanent home of:

1. **Optional cloud-sync (22-3)** — the observability bridge that's explicitly not SaaS lock-in.
2. **Feature parity matrix (22-4)** — documentation that blocks "we'll do this SaaS-only" from quietly happening.
3. **CLI-mode preservation as a project value** — someone has to formally own the "no cloud required" guarantee, with this page as the durable reference.

## See also

- [Epic 19 — GitHub App Agent Dispatch](Epic-19-Agent-Dispatch.md) — where 22-1 and 22-2 actually shipped.
- [Agent Dispatch](Agent-Dispatch) — root-level topic page with full executor abstraction.
- [Epic 10 — Engine Core](Epic-10-Engine-Core.md) — shared engine consumed by both modes.
- [Epic 1 — Foundation](Epic-1-Foundation.md) — `IAgentProvider` / `ICLIAgentProvider` used by `LocalExecutor`.
- [Roadmap](Roadmap.md) — how this epic sits in the overall plan.

## Story files

[Epic 22 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-22)

---

_Last updated: 2026-04-22_
