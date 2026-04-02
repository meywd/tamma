---
title: "Epic 8: Distribution & Installation"
sidebar:
  order: 8
---

**Status:** Completed
**Stories:** 8 (8-1 through 8-8)
**Task Plans:** 0

## Overview

Epic 8 covers the distribution and installation infrastructure for Tamma, enabling users to install and run the platform without cloning the monorepo. The epic is organized into three tiers of increasing capability:

- **Tier 1 (npm)**: Publish `@tamma/cli` to npm so users can run `npx @tamma/cli init`
- **Tier 2 (Binary)**: Standalone binary via `curl install.sh | bash` with zero prerequisites
- **Tier 3 (Docker)**: Full-stack deployment with ELSA, Postgres, RabbitMQ, and Dashboard

## Goals

1. Bundle the monorepo into a distributable npm package via esbuild
2. Set up CI/CD for automated npm publishing
3. Compile standalone binaries (Bun) for Windows, macOS, Linux
4. Create install scripts and GitHub Releases pipeline
5. Add auto-update and Homebrew distribution
6. Build TypeScript and Dashboard Dockerfiles
7. Create Docker Compose full-stack deployment
8. Set up Docker CI/CD and CLI integration

## Stories

| Story | Title | Tier | Priority | Status |
|-------|-------|------|----------|--------|
| 8-1 | esbuild Bundle & Package Structure | Tier 1 (npm) | P0 | Done |
| 8-2 | npm Publish CI/CD Pipeline | Tier 1 (npm) | P0 | Done |
| 8-3 | Standalone Binary Compilation | Tier 2 (binary) | P1 | Done |
| 8-4 | Install Scripts & GitHub Releases | Tier 2 (binary) | P1 | Done |
| 8-5 | Auto-Update & Package Manager Distribution | Tier 2 (binary) | P2 | Done |
| 8-6 | TypeScript & Dashboard Dockerfiles | Tier 3 (Docker) | P1 | Done |
| 8-7 | Docker Compose Full Stack | Tier 3 (Docker) | P1 | Done |
| 8-8 | Docker CI/CD & CLI Integration | Tier 3 (Docker) | P2 | Done |

## Key Technical Details

### Architecture

```
Tier 1: npm Distribution
  npx @tamma/cli init
  |-- esbuild Bundle (8-1)
  |-- npm Publish CI/CD (8-2)

Tier 2: Standalone Binary
  curl -fsSL https://.../install.sh | bash
  |-- Bun Binary Compilation (8-3)
  |-- Install Scripts & GitHub Releases (8-4)
  |-- Auto-Update & Homebrew (8-5)

Tier 3: Docker Full-Stack
  tamma init --full-stack && docker compose up -d
  |-- TypeScript & Dashboard Dockerfiles (8-6)
  |-- Docker Compose Full Stack (8-7)
  |-- Docker CI/CD & CLI Integration (8-8)
```

### Implementation Phases

| Phase | Stories | Estimated Effort |
|-------|---------|-----------------|
| Phase 1: npm Distribution | 8-1, 8-2 | 4-5 days |
| Phase 2: Standalone Binary | 8-3, 8-4, 8-5 | 9.5-10.5 days |
| Phase 3: Docker Full-Stack | 8-6, 8-7, 8-8 | 9-14 days |

### Success Metrics

- Tier 1: `npx @tamma/cli --version` works within 30s; bundle < 500KB
- Tier 2: Binary < 60MB uncompressed; install completes in < 30s
- Tier 3: `docker compose up -d` starts all 7 services healthy within 3 minutes
- All tiers: `tamma init` wizard works identically

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| CLI Package | Epic 1 | `@tamma/cli` and engine infrastructure |
| Engine Orchestration | Epic 2 | `TammaEngine`, `processOneIssue` |
| Observability | Epic 5 | `createLogger` and dashboard UI |
| Context & Knowledge | Epic 6 | API routes bundled into CLI |
| ELSA Workflows | Epic 7 | Docker Tier 3 requires ELSA |

## External Dependencies

- **esbuild**: Already in root devDependencies
- **Bun**: Required for Tier 2 binary compilation only
- **Docker / Docker Compose**: Required for Tier 3 only
- **npm registry**: Publishing target for Tier 1
- **GitHub Container Registry (GHCR)**: Image registry for Tier 3
- **GitHub Releases**: Binary hosting for Tier 2

## Story Files

[Story documents on GitHub](/stories/epic-8/)
