---
title: "Epic 22: CLI Mode Preservation"
sidebar:
  order: 22
---

**Status:** Drafted
**Stories:** 5 (22-1 through 22-5)
**Estimated Effort:** 44 hours

## Overview

Epic 22 ensures the standalone CLI mode (`tamma start`) continues to work without cloud dependencies, account creation, or SaaS enrollment, while sharing the ELSA workflow engine with the SaaS mode and allowing optional cloud connectivity for monitoring.

## Goals

1. Define `IAgentExecutor` interface with `LocalExecutor` and `RemoteExecutor` implementations
2. Wire CLI standalone mode to use `LocalExecutor` with local ELSA
3. Add optional cloud sync for monitoring without surrendering local execution
4. Maintain feature parity matrix between CLI and SaaS modes

## Value Delivered

- `tamma start` works fully standalone with zero cloud dependencies
- Local agent execution preserved for CLI users
- ELSA workflow engine shared between both modes via `IAgentExecutor`
- Optional cloud connectivity adds monitoring without gating core features
- Feature parity matrix prevents SaaS-only lock-in

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 22-1 | IAgentExecutor Abstraction | P0 (Critical) | 12 hours | Planned |
| 22-2 | CLI Standalone Workflow Engine | P0 (Critical) | 16 hours | Planned |
| 22-3 | Optional Cloud Sync | P2 (Medium) | 10 hours | Planned |
| 22-4 | CLI + SaaS Feature Parity Matrix | P1 (High) | 6 hours | Planned |

## Key Technical Details

### IAgentExecutor Abstraction

```
IAgentExecutor
  |
  +-- LocalExecutor      (spawns CLI agent on user's machine)
  |     uses: IAgentProvider / ICLIAgentProvider
  |
  +-- RemoteExecutor     (dispatches GitHub Actions workflow)
        uses: IGitPlatform.dispatchWorkflow()
```

The engine calls `IAgentExecutor.execute()` regardless of mode. Config determines which implementation is injected.

### Mode Selection

```
config.mode === 'standalone' --> LocalExecutor
config.mode === 'saas'       --> RemoteExecutor (via SaaSCoordinator)
config.mode === 'hybrid'     --> LocalExecutor + optional CloudSyncTransport
```

### Event Flow (Hybrid Mode)

```
Engine --> IEventStore.record()
              |
              +-- InMemoryEventStore (always, for TUI)
              +-- CloudSyncTransport (optional, when tamma.cloud.apiKey is set)
                      |
                      POST /api/v1/events/ingest --> Tamma Cloud Dashboard
```

### Key Principles

1. **No cloud required** for core functionality -- `tamma start` works with zero internet dependency beyond the target Git platform
2. **No account required** -- CLI users not forced to create Tamma Cloud account
3. **Agents run where user chooses** -- local means local; cloud sync is observability only
4. **Shared engine, different execution** -- same ELSA workflows run in both modes
5. **Additive cloud features** -- cloud connectivity adds monitoring, never gates core features

### Dependency Graph

```
Story 22.1 (IAgentExecutor abstraction)
  |
  +---> Story 22.2 (CLI standalone workflow engine)
  |       |
  |       +---> Story 22.3 (optional cloud sync)
  |       |
  |       +---> Story 22.4 (feature parity matrix)
```

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| AI Providers | Epic 1 | IAgentProvider for local execution |
| Engine Core | Epic 10 | TammaEngine and event store |
| ELSA Workflows | Epic 7 | Shared workflow engine |
| Agent Dispatch | Epic 19 | RemoteExecutor uses dispatch infrastructure |
| Agent Management | Epic 9 | Config-driven provider selection |

## Story Files

[Story documents on GitHub](/stories/epic-22/)
