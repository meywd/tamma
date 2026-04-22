# Epic 22: CLI Mode Preservation

**Status:** Partially Superseded — `IAgentExecutor` (Story 22-1) delivered by Epic 19; CLI standalone working; cloud-sync (22-3) and parity matrix (22-4) still drafted
**Stories:** 4 (22-1 through 22-4)
**Estimated Effort:** ~44 hours (much of which Epic 19 absorbed)

## Overview

Epic 22 ensures the standalone CLI mode (`tamma start`) continues to work without cloud dependencies, account creation, or SaaS enrollment, while sharing the Elsa workflow engine with the SaaS mode and allowing optional cloud connectivity for monitoring.

The epic was originally written before Epic 19 (Agent Dispatch) and overlapped substantially with it. Epic 19 delivered the `IAgentExecutor` abstraction (Story 22-1's main deliverable) as part of its agent-dispatch work; the CLI standalone path (Story 22-2's mode) already works. Stories 22-3 (optional cloud sync) and 22-4 (feature parity matrix) remain as the residual work for this epic.

## Current state

- **`IAgentExecutor` lives in Epic 19** — `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentExecutor.cs`
- **`LocalExecutor`** (CLI mode) — shipped as part of Epic 19 Story 19-5; subprocess execution via `IProcessRunner`; deterministic test fakes
- **`GitHubActionsExecutor`** (SaaS mode) — shipped as part of Epic 19 Story 19-5; webhook-mode resume; tenant-scoped via `install:{id}:` prefix
- **TS `execute-agent` CLI** — `packages/cli/src/commands/execute-agent.ts` — the subprocess entry point used by `LocalExecutor`
- **Mode resolution via `AgentExecutorFactory`** — env var → config → auto-detect
- **`tamma start` works fully standalone** — no cloud dependencies; agents run locally
- **Cloud sync (22-3)**: optional `CloudSyncTransport` for observability — not yet shipped
- **Feature parity matrix (22-4)**: documentation — not yet shipped

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 22-1 | `IAgentExecutor` Abstraction | P0 | 12h | **Superseded by Epic 19 Story 19-5** |
| 22-2 | CLI Standalone Workflow Engine | P0 | 16h | **Done via Epic 19 + existing `tamma start`** |
| 22-3 | Optional Cloud Sync | P2 | 10h | Planned |
| 22-4 | CLI + SaaS Feature Parity Matrix | P1 | 6h | Planned |

## Architecture

### `IAgentExecutor` abstraction (delivered by Epic 19)

```csharp
public interface IAgentExecutor
{
    Task<DispatchResult> DispatchAsync(DispatchRequest req, CancellationToken ct);
    Task<MonitorResult> MonitorAsync(MonitorRequest req, CancellationToken ct);
    Task<CollectResult> CollectAsync(CollectRequest req, CancellationToken ct);
}
```

| Mode | Class | Surface |
|------|-------|---------|
| `Local` | `LocalExecutor` | subprocess on operator machine (CLI mode) |
| `GitHubActions` | `GitHubActionsExecutor` | GitHub Actions `workflow_dispatch` (SaaS mode) |

### Mode selection

```
config.mode === 'standalone'  → LocalExecutor
config.mode === 'saas'        → GitHubActionsExecutor (via SaaSCoordinator)
config.mode === 'hybrid'      → LocalExecutor + optional CloudSyncTransport (22-3)
```

### Event flow (hybrid mode, 22-3)

```
Engine → IEventStore.record()
              ├── InMemoryEventStore (always, for TUI)
              └── CloudSyncTransport (optional, when tamma.cloud.apiKey is set)
                      → POST /api/v1/events/ingest → Tamma Cloud Dashboard
```

## Key principles

1. **No cloud required** for core functionality — `tamma start` works with zero internet dependency beyond the target Git platform
2. **No account required** — CLI users not forced to create Tamma Cloud account
3. **Agents run where user chooses** — local means local; cloud sync is observability only
4. **Shared engine, different execution** — same Elsa workflows run in both modes
5. **Additive cloud features** — cloud connectivity adds monitoring, never gates core features

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| AI Providers | Epic 1 | `IAgentProvider` for local execution |
| Engine Core | Epic 10 | `TammaEngine` and event store |
| Elsa Workflows | Epic 7 | Shared workflow engine |
| **Agent Dispatch (delivered)** | **Epic 19** | **`IAgentExecutor` + `LocalExecutor` + `GitHubActionsExecutor`** |
| Agent Management | Epic 9 | Config-driven provider selection |

## Why Epic 22 still exists

Even though Epic 19 absorbed the `IAgentExecutor` work, Epic 22 remains as the home for:

1. **Optional cloud-sync (22-3)** — bridge for CLI users to see dashboard data in Tamma Cloud while keeping execution local
2. **Feature parity matrix (22-4)** — documentation matrix that prevents SaaS-only lock-in for core functionality
3. **CLI-mode preservation as a project value** — formal owner of the "no cloud required" guarantee

## See also

- [Agent Dispatch](Agent-Dispatch) — the root topic page where `IAgentExecutor` is documented in full
- [Epic 19 — GitHub App Agent Dispatch](Epic-19-Agent-Dispatch.md) — where 22-1 and 22-2 actually shipped

## Story files

[Epic 22 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-22)

---

_Last updated: 2026-04-21_
