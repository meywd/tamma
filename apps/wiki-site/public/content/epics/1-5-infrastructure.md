---
title: "Epic 1.5: Infrastructure & Deployment"
sidebar:
  order: 1.5
---

**Status:** Completed
**Stories:** 15 (1.5-1 through 1.5-15)
**Packages:** `@tamma/api`, `@tamma/cli`, `@tamma/orchestrator`, Docker stack

## Overview

Epic 1.5 establishes production-ready infrastructure, deployment automation, and operational capabilities for the Tamma platform. It covers everything from core engine separation and Docker packaging to GitHub App authentication and SaaS coordination.

## Key Deliverables

### Docker Compose Stack

Production deployment runs on Hetzner CPX42 (16GB RAM, amd64) at 204.168.131.39 with 10 services:

| Service | Technology | Purpose |
|---------|-----------|---------|
| PostgreSQL 17 | Database | Data, events, ELSA workflow state |
| RabbitMQ | Message broker | Async messaging |
| ChromaDB | Vector store | Code embeddings |
| elsa-server | .NET 8 | ELSA workflow engine |
| tamma-api-dotnet | .NET 8 | .NET REST API |
| tamma-api | Node.js 22 / Fastify | TypeScript REST API |
| tamma-engine | Node.js 22 | TypeScript engine |
| tamma-dashboard | nginx | React SPA |
| elsa-studio | nginx | Custom Blazor WASM |
| nginx-proxy | nginx | Reverse proxy |
| OpenSearch (opt-in) | OpenSearch 2.x | Log aggregation |

### CI/CD Pipelines

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `ci.yml` | PRs | Build, lint, test |
| `deploy.yml` | main push | Deploy to VPS via SSH |
| `docker-publish.yml` | Tags | Build + push images to GHCR |
| `docker-smoke-test.yml` | deploy | Smoke test stack |
| `release.yml` | Tags | GitHub releases |
| `tamma-worker.yml` | dispatch | GitHub Actions worker template |

### API Server (`@tamma/api`)

Fastify REST API with 70+ source files:

| Route Group | Endpoints | Purpose |
|-------------|-----------|---------|
| `/routes/auth/` | GitHub OAuth, me, role check | Authentication |
| `/routes/admin/` | Health, system status | Admin panel backend |
| `/routes/github/` | Webhook, callback | GitHub App integration |
| `/routes/settings/` | Agents, diagnostics, health, prompts, providers, security | Settings management |
| `/routes/knowledge-base/` | Analytics, context, index, MCP, RAG, vector-db | Knowledge base API |
| `/routes/saas/` | Key rotation, LLM proxy, workflow status/result | SaaS features |
| `/routes/users/` | API keys, invites, user CRUD | User management |
| `/routes/workflows/` | Workflow listing | Workflow management |
| `/routes/engine/` | Engine control | Engine lifecycle |
| `/routes/dashboard/` | Dashboard data | Dashboard backend |

### GitHub App Authentication

- Dual auth modes: PAT (self-hosted) and GitHub App (SaaS)
- JWT signed with RSA private key using `@octokit/auth-app`
- Installation token auto-refresh
- Installation callback endpoint and persistence
- API key provisioning into repository GitHub Actions secrets

### SaaS Coordinator

- Discovers active GitHub App installations
- Dispatches `workflow_dispatch` events to user repositories
- Installation lifecycle management (new, removed, suspended)
- Reconciliation loop on configurable interval

---

## Stories

### Story 1.5-1: Core Engine Separation
**Status:** Done

Extract core engine into separate package with launch wrappers for CLI, server, and worker modes.

---

### Story 1.5-2: CLI Mode Enhancement
**Status:** Done

Enhanced CLI with multiple modes:
- `tamma start` -- Self-hosted engine
- `tamma server` -- Self-hosted HTTP server
- `tamma api` -- SaaS/GitHub App mode
- `tamma init-fullstack` -- Full-stack Docker setup
- `tamma process-issue` -- Single issue processing
- `tamma upgrade` -- Version upgrade

---

### Story 1.5-3: Service Mode Implementation
**Status:** Done

Background service with Docker Compose for production deployment.

---

### Story 1.5-4: Web Server & API
**Status:** Done

Fastify HTTP server with REST API, webhook receivers, and authentication.

---

### Story 1.5-5: Docker Packaging
**Status:** Done

Multi-stage Dockerfiles:
- `docker/Dockerfile.ts` -- TypeScript services (API, engine)
- `docker/Dockerfile.dashboard` -- Dashboard (nginx-served SPA)
- ELSA Dockerfiles in `apps/tamma-elsa/`
- Docker Compose with layered deploy (postgres -> rabbitmq -> elsa -> APIs -> dashboard + nginx)

---

### Story 1.5-6: Webhook Integration
**Status:** Done

GitHub webhook verification with HMAC-SHA256 signature validation. Event filtering for issue events.

---

### Story 1.5-7: System Configuration Management
**Status:** Done

Configuration resolution with support for config files and environment variable overrides.

---

### Story 1.5-8: NPM Package Publishing
**Status:** Done

CI/CD pipeline for npm publishing implemented.

---

### Story 1.5-9: Binary Releases & Installers
**Status:** Done

Standalone binary compilation and release pipeline implemented.

---

### Story 1.5-10: Kubernetes Deployment
**Status:** In Progress

Kubernetes/Helm deployment implementation in progress.

---

### Story 1.5-11: GitHub App Authentication & Installation Management
**Status:** Done

GitHub App auth with dual mode (PAT + App), JWT generation, installation token refresh, callback endpoint.

Key files:
- `packages/platforms/src/github/github-platform-factory.ts`
- `packages/api/src/routes/github/github-callback.ts`
- `packages/api/src/persistence/installation-store.ts`

---

### Story 1.5-12: SaaS Coordinator
**Status:** Done

Multi-installation engine orchestration via `packages/orchestrator/src/saas-coordinator.ts`.

---

### Story 1.5-13: GitHub Actions Worker Mode
**Status:** Done

Worker mode with `tamma process-issue` command and result callback.

Key files:
- `packages/cli/src/commands/process-issue.ts`
- `packages/cli/src/worker/result-callback.ts`
- `.github/workflows/tamma-worker.yml`

---

### Story 1.5-14: Multi-Tenant Task Queue & Webhook Routing
**Status:** Done

Task queue with installation-scoped routing.

Key files:
- `packages/api/src/services/task-queue.ts`
- `packages/api/src/services/in-memory-task-queue.ts`
- `packages/api/src/services/installation-router.ts`

---

### Story 1.5-15: SaaS API Key Provisioning & GitHub Secrets Setup
**Status:** Done

Per-installation API key generation and GitHub Actions secret provisioning.

Key files:
- `packages/api/src/services/github-secrets-provisioner.ts`
- `packages/api/src/routes/saas/key-rotation.ts`
- `packages/api/src/routes/saas/llm-proxy.ts`

---

## Cloudflare DNS Configuration

| Domain | Service |
|--------|---------|
| app.tamma.dev | Dashboard + nginx proxy |
| api.tamma.dev | Fastify REST API |
| elsa.tamma.dev | ELSA Studio |

All domains use Cloudflare Full SSL with origin certificates.

---

_For more details, see [docs/stories/epic-1.5/](/stories/epic-1.5/) in the repository._
