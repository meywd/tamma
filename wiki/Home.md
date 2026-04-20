# Welcome to the Tamma Wiki

**Tamma** is an autonomous development platform that maintains itself. This wiki provides comprehensive documentation for understanding, contributing to, and using Tamma.

## Quick Links

- [Project Roadmap](Roadmap) - All 27 epics with timeline and status
- [Architecture](Architecture) - System architecture overview (three deployment modes, executor abstraction, Cranl provisioning)
- [Deployment](Deployment) - Docker stack, Phase-3 RLS runbook, env vars, Redis/Cranl activation
- [Agent Dispatch](Agent-Dispatch) - Epic 19 executor abstraction (Local + GitHub Actions)
- [Security](Security) - RLS, rate limiting, API key hashing, content sanitization, libsodium secrets
- [GitHub Integration](GitHub-Integration) - Octokit App client, OAuth flows, Actions dispatch
- [Testing](Testing) - Test strategy, testcontainers patterns, per-scope coverage
- [Port Audit](Port-Audit) - Summary of TS → C# port-gap audit (196 findings across 8 scopes)
- [Epics](Epics) - All 27 epics organized by phase
- [Epic 1: Foundation](Epics/Epic-1-Foundation) - Core infrastructure (AI providers, Git platforms, CLI)
- [Epic 1.5: Infrastructure & Deployment](Epics/Epic-1.5-Infrastructure) - Docker, CI/CD, SaaS coordinator
- [Epic 2: Autonomous Loop](Epics/Epic-2-Autonomous-Loop) - 14-step autonomous development loop (13/16 done)
- [Epic 6: Context & Knowledge Management](Epics/Epic-6-Context-Knowledge) - Vector DB, RAG, MCP, cost monitoring, permissions, knowledge base
- [Epic 7: Mentorship Workflow](Epics/Epic-7-Mentorship) - ELSA workflow activities for autonomous mentorship
- [Epic 9: Agent Management](Epics/Epic-9-Agent-Management) - Config-driven multi-agent system
- [Epic 10: Engine Core](Epics/Epic-10-Engine-Core) - Workflow-driven architecture
- [Epic 11-14: ELSA Hardening](Epics/Epic-11-14-ELSA) - Security, agentic tool loop, workflow decomposition, custom studio
- [Epic 19: GitHub App Agent Dispatch](Epics/Epic-19-GitHub-App-Agent-Dispatch) - Executor abstraction, Local/GitHubActions modes, dispatch/monitor/collect
- [Epic 23: System Monitoring](Epics/Epic-23-System-Monitoring) - Production-grade monitoring & observability dashboard
- [Epic 24: Voice Conversation](Epics/Epic-24-Voice-Conversation) - Realtime voice conversation with orchestrator
- [Epic 25: Wiki Site](Epics/Epic-25-Wiki-Site) - Custom documentation site on Cloudflare Workers
- [Epic 26: Project Management & Triage](Epics/Epic-26-Project-Management) - Issue triage, scrum management, release management
- [Workflows](Workflows) - All 21 ELSA workflows with flow diagrams and dependency map
- [Stories](Stories) - Detailed story documentation across all epics
- [Contributing](Contributing) - How to contribute to Tamma
- [GitHub Issues](https://github.com/meywd/tamma/issues) - Track progress

## What is Tamma?

Tamma is an **autonomous development platform** designed to achieve **70%+ autonomous completion** of software development tasks without human intervention. The platform's ultimate goal is **self-maintenance** -- Tamma will maintain its own codebase.

### Key Features

- **Autonomous Development Loop** -- 70%+ completion rate without human intervention
- **Multi-Provider Flexibility** -- 8+ AI providers, 7 Git platforms, no vendor lock-in
- **Config-Driven Multi-Agent System** -- Role-based agent selection with provider chains, fallback, and circuit breakers
- **Dual-Stack Architecture** -- TypeScript (Node.js) for providers, CLI, and API; C# (.NET 8) ELSA Workflows for orchestration
- **Production-Ready Security** -- Content sanitization, URL validation, action gating, SSRF protection, LLM injection defense
- **ELSA Workflow Engine** -- Visual, composable, pausable/resumable workflows with ELSA Studio
- **Diagnostics Pipeline** -- Per-provider cost, token, latency, and error tracking
- **System Monitoring** -- Comprehensive observability dashboard for all services, providers, workflows, and infrastructure
- **Voice Interface** -- Realtime voice conversation with the orchestrator via browser
- **Self-Maintenance** -- Tamma maintains its own codebase (MVP validation goal)

### Architecture Highlights

- **Hybrid TypeScript + C# Architecture** -- TypeScript for AI providers, CLI, API; .NET ELSA for workflow orchestration
- **Interface-Based Provider Abstraction** -- Swap AI providers (Claude, GPT-4, Gemini, OpenRouter, local LLMs)
- **Platform-Agnostic Git Integration** -- GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps
- **ELSA Workflow Engine** -- 20+ code-first C# workflows with visual designer (ELSA Studio)
- **Event Sourcing and Audit Trail** -- Complete transparency via DCB (Development Context Bus)
- **Role-Based Agent Resolution** -- Workflow phases map to agent roles; each role has an ordered provider chain
- **Defense-in-Depth Security** -- Content sanitization at prompt and output boundaries, secure fetch with SSRF protection
- **SaaS + CLI Dual Mode** -- Works as self-hosted CLI or multi-tenant SaaS with GitHub App integration

## Current Status

**Phase:** Active Implementation
**Deployment:** VPS at 204.168.131.39 (Hetzner CPX42, 16GB) with Docker Compose stack
**Domains:** app.tamma.dev, api.tamma.dev, elsa.tamma.dev (Cloudflare DNS, Full SSL)
**Last Audit:** 2026-04-02 (sprint-status.yaml audited against codebase for all 27 epics)
**Audit Results:** 117 done, 20 in-progress, 56 drafted

### Recent Progress (auth-foundation sprint)

- **TS → C# port audit** -- 196 per-finding audit notes across 8 scopes (`admin-db`, `auth`, `orgs`, `providers`, `prompts`, `engine`, `github`, `kb`); **118 findings landed** during this sprint. Notes live in `docs/audit/port-gaps/<scope>/NNN-*.md` with per-scope `index.md` summaries. See [Port Audit](Port-Audit) for the rollup.
- **Phase-3 dual-connection RLS (orgs/002, 004)** -- `TammaDb` (admin, migrations, background services) + `TammaAppDb` (per-request, role `tamma_app`, RLS-enforced). Tenant-context EF interceptor runs `SET LOCAL app.current_tenant_id` at the start of every DbCommand; every tenant-scoped entity has a query filter; filters **fail-closed** on null tenant. See [Deployment](Deployment#phase-3-rls-runbook) for the activation runbook.
- **Octokit GitHub App client (github/all 11 findings)** -- real `OctokitGitHubAppClient` in `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/` using installation-scoped JWTs. `NullGitHubAppClient` seam when `GitHub:AppId` is absent — endpoints surface a clean `github_client_not_configured` error instead of silently succeeding.
- **Libsodium secrets provisioner** -- `LibsodiumGitHubSecretsProvisioner` encrypts GitHub Actions repository secrets via `Sodium.Core` sealed boxes (public-key encryption). Null seam falls back when sodium isn't wired.
- **TammaEngine SSE lifecycle (engine/012)** -- in-process `InMemoryEngineLifecycleBus` + SSE endpoints `/api/engine/events/state` and `/api/engine/events/logs`. Tenant-scoped fanout (each subscriber filters by tenant claim); 15-second heartbeats keep proxies from timing out idle streams.
- **Redis-backed distributed rate limit (auth/014)** -- `IDistributedRateLimitBackend` with Lua `INCR + EXPIRE` script. In-process default; Redis activated by setting `ConnectionStrings:Redis`, making the rate limit multi-pod-safe.
- **Cranl per-tenant provisioner** -- `CranlTenantProvisioner` spins up a per-tenant Cranl project + Postgres DB + Elsa workflow app. Admin endpoint `POST /api/admin/tenants/{id}/provision`. `NullTenantProvisioner` is the default — tenants stay on the shared central Postgres via RLS until `Cranl:ApiKey` + `Cranl:OrganizationId` are both set.
- **Epic 19 agent dispatch (stories 19-2 / 3 / 4 / 5)** -- `IAgentExecutor` abstraction with `LocalExecutor` (CLI mode subprocess) + `GitHubActionsExecutor` (SaaS/multi-tenant). `DispatchAgentWorkflowActivity`, `MonitorAgentWorkflowActivity`, `CollectAgentResultsActivity`, and `ExecuteAgentActivity` in `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`. Three deployment modes: **CLI** (local), **SaaS single-tenant** (central Postgres + RLS), **SaaS multi-tenant** (Cranl-provisioned per-tenant infra). Executor is resolved via `TAMMA_AGENT_MODE` env > `Agent:ExecutorMode` config > auto-detect. See [Agent Dispatch](Agent-Dispatch).
- **Password hardening (auth/013)** -- `PasswordStrengthValidator` loads a top-1000 common-password list from SecLists, enforces length/upper/lower/digit + common-password rejection on register and password-reset.
- **Postgres budget-config persistence (providers/005)** -- `BudgetConfigRepository` + `BudgetService` now persist provider-level budget limits; enforcement is real (no-op removed).
- **Sole-owner delete guard + transfer-ownership (orgs/019)** -- deleting the sole owner of an org now requires a transfer-ownership flow; membership cascade cleans up stale rows.
- **Content sanitizer port (~360 LoC)** -- C# port of the TS `ContentSanitizer` with prompt-injection detection, zero-width removal, NFKD normalisation; enforced on LLM output + API responses.

### Completed Epics (13)

| Epic | Name | Key Deliverables |
|------|------|------------------|
| Epic 8 | Distribution & Installation | esbuild bundle, npm publish, standalone binary, Docker Compose, Homebrew, CI/CD |
| Epic 9 | Agent Management | Config-driven multi-agent, circuit breakers, diagnostics, security layer |
| Epic 11 | Security Hardening | C# security pipeline, LLM input/output sanitization, tool validation |
| Epic 12 | Agentic Tool Loop | Multi-turn tool execution, context compaction, streaming |
| Epic 13 | Workflow Decomposition | TDD/CI retry sub-workflows, consolidated finish sequences |
| Epic 14 | Custom ELSA Studio | Custom Blazor WASM studio, Tamma branding, UI hints |
| Epic 15 | Observability | OpenSearch log aggregation (3 bug fixes for ESM/Serilog/Fastify) |
| Epic 16 | Unified Auth & Admin | GitHub OAuth SSO (oauth2-proxy removed), user management, admin panel, RBAC, ELSA Studio auto-login |
| Epic 25 | Documentation & Wiki Site | Vite+React SPA wiki site, React Flow diagrams, deployed to wiki.tamma.dev |

### Near Complete (7)

| Epic | Name | Done | Remaining |
|------|------|------|-----------|
| Epic 1 | Foundation & Core Infrastructure | 10/15 | 2 in progress (AI providers, agent customization), 3 ready |
| Epic 1.5 | Infrastructure & Deployment | 9/10 | Kubernetes deployment in progress |
| Epic 2 | Autonomous Development Loop | 13/20 | Priority work item selection (2-20 drafted), issue decomposition (2-14), task dependencies (2-15), sequencing (2-16), + 2-17/2-18/2-19 |
| Epic 3 | Quality Gates & Intelligence | 8/12 | 4 drafted (research, clarifying questions, ambiguity, design proposals) |
| Epic 4 | Event Sourcing & Audit Trail | 6/8 | PostgreSQL backend in progress, replay drafted |
| Epic 6 | Context & Knowledge Management | 10/11 | Vector DB stubs (Pinecone, Qdrant, Weaviate) in progress |
| Epic 7 | Mentorship Workflow | 8/9 core | TDD sub-workflow in progress (test execution mocked) |

### Partially Implemented (5)

| Epic | Name | Done | In Progress | Drafted |
|------|------|------|-------------|---------|
| Epic 5 | Observability Dashboard & Docs | 4 | 3 | 7 |
| Epic 18 | End-User Auth & Registration | 1 | 2 | 2 |
| Epic 19 | GitHub App Agent Dispatch | 1 | 1 | 3 |
| Epic 21 | Marketing Site & User Dashboard | 1 | 1 | 3 |
| Epic 26 | Project Management & Triage | 0 | 1 | 3 |

### Planned / Drafted

| Epic | Name | Stories | Status |
|------|------|---------|--------|
| Epic 10 | Engine Core | 9 | Drafted (engine exists but stories define new architecture); Story 10-9 (TammaActivity base class) in progress |
| Epic 17 | Multi-Tenancy Foundation | 5 | Drafted |
| Epic 20 | Billing & Payments | 5 | Drafted |
| Epic 22 | CLI Mode Preservation | 5 | Drafted |
| Epic 23 | System Monitoring & Observability Dashboard | 12 | Drafted (26 task plans) |
| Epic 24 | Realtime Voice Conversation | 7 | Drafted (24 task plans) |

## Getting Started

1. Read the [Architecture](Architecture) overview
2. Review the [Roadmap](Roadmap) to understand the project timeline
3. Check out [Epic 1](Epics/Epic-1-Foundation) to see foundational work
4. See [Epic 9](Epics/Epic-9-Agent-Management) for the multi-agent system
5. Visit [Contributing](Contributing) to learn how to help

## Documentation

All technical documentation is maintained in the [/docs](https://github.com/meywd/tamma/tree/main/docs) directory:

- [PRD](https://github.com/meywd/tamma/blob/main/docs/PRD.md) - Product requirements
- [Architecture](https://github.com/meywd/tamma/blob/main/docs/architecture.md) - Technical architecture
- [Epics](https://github.com/meywd/tamma/blob/main/docs/epics.md) - Epic breakdown
- [Tech Specs](https://github.com/meywd/tamma/tree/main/docs) - Technical specifications per epic
- [Stories](https://github.com/meywd/tamma/tree/main/docs/stories) - User story documentation (27 epics, 220+ stories, 50+ task plans)

---

_Last updated: 2026-04-18 (auth-foundation sprint sync) | Maintained by: meywd_
