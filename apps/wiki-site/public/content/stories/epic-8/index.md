---
title: "Epic 8: Distribution & Installation"
---

## Overview

This epic covers the distribution and installation infrastructure for Tamma, enabling users to install and run the platform without cloning the monorepo. The epic is organized into three tiers of increasing capability:

- **Tier 1 (npm)**: Publish `@tamma/cli` to npm so users can run `npx @tamma/cli init`
- **Tier 2 (Binary)**: Standalone binary via `curl install.sh | bash` with zero prerequisites
- **Tier 3 (Docker)**: Full-stack deployment with ELSA, Postgres, RabbitMQ, and Dashboard

## Stories

| Story | Title | Tier | Priority | Status |
|-------|-------|------|----------|--------|
| 8-1 | esbuild Bundle & Package Structure | Tier 1 | P0 | Planned |
| 8-2 | npm Publish CI/CD Pipeline | Tier 1 | P0 | Planned |
| 8-3 | Standalone Binary Compilation | Tier 2 | P1 | Planned |
| 8-4 | Install Scripts & GitHub Releases | Tier 2 | P1 | Planned |
| 8-5 | Auto-Update & Package Manager Distribution | Tier 2 | P2 | Planned |
| 8-6 | TypeScript & Dashboard Dockerfiles | Tier 3 | P1 | Planned |
| 8-7 | Docker Compose Full Stack | Tier 3 | P1 | Planned |
| 8-8 | Docker CI/CD & CLI Integration | Tier 3 | P2 | Planned |

## Architecture

```
+-----------------------------------------------------------------------------+
|                    EPIC 8: DISTRIBUTION & INSTALLATION                       |
+-----------------------------------------------------------------------------+
|                                                                             |
|  +-- TIER 1: npm Distribution -------------------------------------------+ |
|  |                                                                        | |
|  |  +-----------------+     +------------------+                          | |
|  |  |  esbuild Bundle |     |  npm Publish     |                          | |
|  |  |  & Package      | --> |  CI/CD Pipeline  |                          | |
|  |  |  (8-1)          |     |  (8-2)           |                          | |
|  |  +-----------------+     +------------------+                          | |
|  |                                                                        | |
|  |  User: npx @tamma/cli init                                            | |
|  +------------------------------------------------------------------------+ |
|                                                                             |
|  +-- TIER 2: Standalone Binary -------------------------------------------+ |
|  |                                                                        | |
|  |  +-----------------+  +------------------+  +--------------------+     | |
|  |  |  Bun Binary     |  |  Install Scripts |  |  Auto-Update &     |     | |
|  |  |  Compilation    |->|  & GH Releases   |->|  Homebrew          |     | |
|  |  |  (8-3)          |  |  (8-4)           |  |  (8-5)             |     | |
|  |  +-----------------+  +------------------+  +--------------------+     | |
|  |                                                                        | |
|  |  User: curl -fsSL https://.../install.sh | bash                        | |
|  +------------------------------------------------------------------------+ |
|                                                                             |
|  +-- TIER 3: Docker Full-Stack -------------------------------------------+ |
|  |                                                                        | |
|  |  +-----------------+  +------------------+  +--------------------+     | |
|  |  |  TS & Dashboard |  |  Docker Compose  |  |  Docker CI/CD &    |     | |
|  |  |  Dockerfiles    |->|  Full Stack      |->|  CLI Integration   |     | |
|  |  |  (8-6)          |  |  (8-7)           |  |  (8-8)             |     | |
|  |  +-----------------+  +------------------+  +--------------------+     | |
|  |                                                                        | |
|  |  User: tamma init --full-stack && docker compose up -d                 | |
|  +------------------------------------------------------------------------+ |
|                                                                             |
+-----------------------------------------------------------------------------+
```

## Dependencies

### On Other Epics

- **Epic 1**: CLI package (`@tamma/cli`) and engine infrastructure
- **Epic 2**: Engine orchestration (`TammaEngine`, `processOneIssue`)
- **Epic 5**: Observability (`createLogger`) and dashboard UI
- **Epic 6**: API routes (knowledge base, MCP) bundled into CLI
- **Epic 7**: ELSA workflows (Docker Tier 3 only)

### External Dependencies

- **esbuild**: Already in root devDependencies (`^0.24.2`)
- **Bun**: Required for Tier 2 binary compilation only
- **Docker / Docker Compose**: Required for Tier 3 only
- **npm registry**: Publishing target for Tier 1
- **GitHub Container Registry (GHCR)**: Image registry for Tier 3
- **GitHub Releases**: Binary hosting for Tier 2

## Implementation Phases

### Phase 1: npm Distribution (Stories 8-1, 8-2)
- Bundle the monorepo into a single distributable npm package
- Set up CI/CD for automated publishing on release tags
- Estimated: 4-5 days

### Phase 2: Standalone Binary (Stories 8-3, 8-4, 8-5)
- Compile to platform-specific binaries with `bun build --compile`
- Create install scripts and GitHub Releases pipeline
- Add auto-update mechanism and Homebrew tap
- Estimated: 9.5-10.5 days

### Phase 3: Docker Full-Stack (Stories 8-6, 8-7, 8-8)
- Create Dockerfiles for TS services and dashboard
- Build 7-service Docker Compose stack
- Set up GHCR publishing and `tamma init --full-stack`
- Estimated: 9-14 days

## Reference Documents

- `docs/architecture/installer-tier1-npx.md` - Detailed Tier 1 plan
- `docs/architecture/installer-tier2-curl.md` - Detailed Tier 2 plan
- `docs/architecture/installer-tier3-docker.md` - Detailed Tier 3 plan

## Success Metrics

- Tier 1: `npx @tamma/cli --version` works within 30s of first invocation
- Tier 1: Bundle size < 500KB uncompressed JS (excluding node_modules)
- Tier 2: Binary < 60MB uncompressed, < 25MB compressed
- Tier 2: Install script completes in < 30s on broadband
- Tier 3: `docker compose up -d` starts all 7 services healthy within 3 minutes
- Tier 3: Docker images < 300MB each (TS), < 30MB (dashboard)
- All tiers: `tamma init` wizard works identically across distribution methods
