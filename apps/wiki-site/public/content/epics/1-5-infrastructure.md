---
title: "Epic 1.5: Infrastructure & Deployment"
sidebar:
  order: 1.5
---

**Status:** Partially Complete — core infra done, secret-management track (1.5-16..1.5-45) in progress
**Stories:** 45 (1.5-1 through 1.5-45)
**Packages:** `@tamma/api`, `@tamma/cli`, `@tamma/orchestrator`, Docker stack, Elsa secret broker, GitHub Actions secret-fetcher

## Overview

Epic 1.5 establishes production-ready infrastructure, deployment automation, and operational capabilities for the Tamma platform. It grew from the original 10-story Docker/CLI scope into a 45-story epic that also owns the **secret-management track** — the LLM-safe cryptographic pipeline that Epic 29 builds its operator-facing cabinet on top of.

## Story groupings

| Group | Stories | Theme |
|-------|---------|-------|
| Core Deployment | 1.5-1 .. 1.5-10 | CLI modes, Docker, K8s, CI/CD, Installers |
| SaaS Coordination | 1.5-11 .. 1.5-15 | GitHub App auth, Worker mode, Multi-tenant task queue |
| Secret-Management Track | 1.5-16 .. 1.5-22 | Secret broker + commitment-hash protocol + OIDC trust |
| Platform Secret Mirrors | 1.5-23 .. 1.5-26 | GitHub / GitLab / Gitea / Forgejo / Bitbucket / Azure DevOps secret stores |
| Rotation & Leak Detection | 1.5-27 .. 1.5-31 | Probe workflows, leak detection, auto-rotation |
| Advanced Crypto | 1.5-32 .. 1.5-36 | Secret import, drift detection, KMS-backed root key |
| Ops & Observability | 1.5-37 .. 1.5-45 | Notifications, dashboards, mTLS, health checks, MCP tools |

## Core deliverables (shipped)

### Docker Compose Stack

Production deployment runs on Hetzner CPX42 (16GB RAM, amd64) at 204.168.131.39 with 10 services:

| Service | Technology | Purpose |
|---------|-----------|---------|
| PostgreSQL 17 | Database | Data, events, Elsa workflow state |
| RabbitMQ | Message broker | Async messaging |
| ChromaDB | Vector store | Code embeddings |
| elsa-server | .NET 8 | Elsa workflow engine |
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

## Stories

### Core Deployment (1.5-1 .. 1.5-10)

| Story | Title | Status |
|-------|-------|--------|
| 1.5-1 | Core Engine Separation | Done |
| 1.5-2 | CLI Mode Enhancement & Configuration Management | Done |
| 1.5-3 | Service Mode Implementation & Environment Deployments | Done |
| 1.5-4 | Web Server API & Secret Management Integration | Done |
| 1.5-5 | Docker Packaging | Done |
| 1.5-6 | Health Checks, Monitoring & Webhook Integration | Done |
| 1.5-7 | Backup & Recovery & System Configuration Management | Done |
| 1.5-8 | Documentation, Templates & NPM Package Publishing | Done |
| 1.5-9 | Binary Releases & Installers | Done |
| 1.5-10 | Kubernetes Deployment | In Progress |

### SaaS Coordination (1.5-11 .. 1.5-15)

| Story | Title | Status |
|-------|-------|--------|
| 1.5-11 | GitHub App Authentication | Done |
| 1.5-12 | SaaS Coordinator | Done |
| 1.5-13 | GitHub Actions Worker Mode | Done |
| 1.5-14 | Multi-Tenant Task Queue & Webhook Routing | Done |
| 1.5-15 | SaaS API Key Provisioning | Done |

### Secret-Management Track (1.5-16 .. 1.5-45)

The secret-management track ships the **LLM-safe** secret pipeline: Elsa workflows hash-commit secret values before emitting them to LLMs; workers fetch plaintext from a secret broker via OIDC-issued short-lived tokens; leak detection + rotation close the loop.

Epic 29 reuses these primitives for the **operator-facing cabinet** (platform + tenant admin UIs, rotation workflows for non-CI consumers like Postgres roles and Cranl env vars).

| Story | Title | Status |
|-------|-------|--------|
| 1.5-16 | Secret Store Interface + Commitment Hash Protocol | Planned |
| 1.5-17 | TammaVaultStore + Postgres schema + secret-broker HTTP service | Planned |
| 1.5-18 | Secret Activities — Elsa C# wrappers over the secret broker HTTP API | Planned |
| 1.5-19 | Secret workflows + `LlmWorkflowLaunchRegistry` + `alert_orchestrator` | Planned |
| 1.5-20 | OIDC Trust Registry + Validator | Planned |
| 1.5-21 | CI Fetch HTTP Endpoint | Planned |
| 1.5-22 | `actions/fetch-secrets/` GitHub Action | Planned |
| 1.5-23 | GitHub Actions Secrets Mirror (`GitHubSecretStore`) | Planned |
| 1.5-24 | GitLab CI/CD Variables Mirror + CI Template | Planned |
| 1.5-25 | Gitea + Forgejo Secret Stores | Planned |
| 1.5-26 | Bitbucket + Azure DevOps Secret Stores + Pipeline Tasks | Planned |
| 1.5-27 | ProbeSecretWorkflow & v1 Probe Handler Types | Planned |
| 1.5-28 | LeakDetectionWorkflow — LLM Output Scanner + GitHub Secret-Scanning Webhook | Planned |
| 1.5-29 | IRotationHandler + built-in handlers | Planned |
| 1.5-30 | RotationCascadeWorkflow | Planned |
| 1.5-31 | AutoRotateWorkflow — wires leak events to rotation cascade | Planned |
| 1.5-32 | Secret import path — TLS certs, SSH keys, externally-generated credentials | Planned |
| 1.5-33 | Drift detection via platform audit webhooks | Planned |
| 1.5-34 | Non-GitHub git leak scanning — trufflehog + bundled rule set | Planned |
| 1.5-35 | Cloud provider rotation handlers — AWS IAM, GCP, Azure | Planned |
| 1.5-36 | KMS-backed root key — envelope encryption via AWS KMS, GCP KMS, Azure Key Vault | Planned |
| 1.5-37 | Operator notification channels (Slack / Email / PagerDuty / Webhook) | Planned |
| 1.5-38 | Cascade scheduling — cron-based automatic rotation | Planned |
| 1.5-39 | Operator dashboard UI for secrets, rotations, and alerts | Planned |
| 1.5-40 | Self-hosted git platform variants (GHES / GitLab SM / Bitbucket Server / Azure DevOps Server) | Planned |
| 1.5-41 | mTLS transport between Elsa and the secret broker | Planned |
| 1.5-42 | Post-Rotation Health Checks | Planned |
| 1.5-43 | Custom Probe Types & Plugin Framework | Planned |
| 1.5-44 | Secret Metadata CRUD | Planned |
| 1.5-45 | MCP Tool Surface for Secret Management | Planned |

## Architecture / key decisions

1. **Core infra is complete**: 1.5-1..1.5-15 shipped as of the initial platform launch. Docker stack on Hetzner is the production deployment; SaaS coordinator orchestrates GitHub App installations.
2. **Secret-management track is LLM-safe by design**: Elsa never sees plaintext secrets. Commitment-hash protocol (1.5-16) ensures workflow variables carry only hashes; CI fetches plaintext via OIDC tokens from the secret broker.
3. **Epic 29 and Epic 1.5 share seams, not code**: Epic 1.5 owns the LLM-safe secret path (workflows, mirrors, leak detection). Epic 29 adds the operator cabinet on top. The `ISecretsService` seam is the same; rotation handlers (1.5-29, 29-6) are the same framework.
4. **Platform mirrors are per-platform stores**: GitHub (libsodium), GitLab (plaintext), Gitea/Forgejo (plaintext), Bitbucket / Azure DevOps (platform-specific). Each mirror registers with the broker as an `ISecretStore`.
5. **Rotation cascade on leak**: `LeakDetectionWorkflow` → `AutoRotateWorkflow` → `RotationCascadeWorkflow`. All handlers implement `IRotationHandler`; the cascade is saga-shaped with compensation.
6. **KMS-backed root key deferred**: env-var KEK is the v1 design (per Doc 01 §8.2); KMS (1.5-36) ships when a trigger condition fires.

## Dependencies

**Upstream**: Epic 1 (providers, platforms, CLI)

**Downstream**:
- [Epic 2](Epic-2-Autonomous-Loop.md), [Epic 19](Epic-19-Agent-Dispatch.md), [Epic 23](Epic-23-System-Monitoring.md), [Epic 25](Epic-25-Wiki-Site.md), all deployment-dependent epics
- [Epic 28](Epic-28-DB-Per-Tenant.md) — consumes 1.5-16 for KEK primitives
- [Epic 29](Epic-29-Secret-Management.md) — operator-facing cabinet on top of the secret-management track
- [Epic 31](Epic-31-Multi-Git-Platform.md) — consumes 1.5-23..1.5-26 mirrors

## Open questions

1. **Secret-management track scheduling**: the track is ~30 stories; does it ship as a Wave-3 block, or interleaved with Epic 29's cabinet work? Current plan: 1.5-16..1.5-22 first (broker + protocol), then Epic 29 Stories 29-1..29-3 (cabinet MVP), then interleave.
2. **KMS activation trigger**: same trigger set as Story 28-13 (paying tenants with breach clauses, compliance finding, threat-model change, provider LF-graduation). 1.5-36 only lands when triggered.
3. **Self-hosted git platform variants (1.5-40)**: GHES / GitLab Self-Managed / Bitbucket Server / Azure DevOps Server each have quirks. Defer until a tenant asks.

## Story files

[Epic 1.5 stories on GitHub](/stories/epic-1.5/)

---

_Last updated: 2026-04-21_
