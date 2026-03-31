---
title: "Epic 22: CLI Mode Preservation"
---

## Overview

**Goal**: Ensure the standalone CLI mode (`tamma start`) continues to work without cloud dependencies, account creation, or SaaS enrollment, while sharing the ELSA workflow engine with the SaaS mode and allowing optional cloud connectivity for monitoring.

**Value Delivered**:
- `tamma start` works fully standalone with zero cloud dependencies
- Local agent execution (Claude Code, OpenCode on user's machine) preserved for CLI users
- SaaS mode uses GitHub Actions dispatch (agents run on user's runners)
- ELSA workflow engine shared between both modes via `IAgentExecutor` abstraction
- CLI users can optionally connect to Tamma Cloud for dashboard/monitoring without surrendering local execution
- Feature parity matrix prevents SaaS-only lock-in for core functionality

## Current State (Context)

| Component | CLI Standalone | SaaS Mode | Gap |
|-----------|---------------|-----------|-----|
| `TammaEngine` | Runs locally via `tamma start` | Runs via `SaaSCoordinator` | Both work, but agent execution is tightly coupled |
| Agent execution | `IAgentProvider` runs CLI agents locally | `process-issue` runs in GitHub Actions | No unified abstraction; wiring differs by mode |
| ELSA workflows | `ElsaClient` talks to local/remote ELSA | Same `ElsaClient` | Shared, but local ELSA requires Docker |
| Event store | In-memory `IEventStore` | Same in-memory | No persistence or cloud sync for CLI mode |
| Config | `~/.tamma/providers.json` + `.tamma/config.json` | GitHub App + env vars | Config paths differ; no mode negotiation |
| Monitoring | CLI TUI (SessionLayout) | API + Dashboard | No bridge for CLI users to see dashboard |

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 22.1 | IAgentExecutor Abstraction | P0 (Critical) | None | Planned |
| 22.2 | CLI Standalone Workflow Engine | P0 (Critical) | Story 22.1 | Planned |
| 22.3 | Optional Cloud Sync | P2 (Medium) | Story 22.2 | Planned |
| 22.4 | CLI + SaaS Feature Parity Matrix | P1 (High) | Stories 22.1, 22.2 | Planned |

## Dependency Graph

```
Story 22.1 (IAgentExecutor abstraction)
  |
  +---> Story 22.2 (CLI standalone workflow engine)
  |       |
  |       +---> Story 22.3 (optional cloud sync)
  |       |
  |       +---> Story 22.4 (feature parity matrix)
  |
  (22.4 also depends on 22.1 for interface definition)
```

## Architecture

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

## Estimated Total Effort

| Story | Estimate |
|-------|----------|
| 22.1 IAgentExecutor Abstraction | 12 hours |
| 22.2 CLI Standalone Workflow Engine | 16 hours |
| 22.3 Optional Cloud Sync | 10 hours |
| 22.4 CLI + SaaS Feature Parity Matrix | 6 hours |
| **Total** | **44 hours** |

## Key Principles

1. **No cloud required for core functionality** -- `tamma start` must work with zero internet dependency beyond the target Git platform
2. **No account required** -- CLI standalone users are not forced to create a Tamma Cloud account
3. **Agents run where the user chooses** -- Local means local; cloud sync is observability, not delegation
4. **Shared engine, different execution** -- The same ELSA workflows and `TammaEngine` pipeline run in both modes; only the executor backend changes
5. **Additive cloud features** -- Cloud connectivity adds monitoring/dashboards, never gates core features

---

**Last Updated**: 2026-03-28
**Epic Owner**: Platform Engineering
