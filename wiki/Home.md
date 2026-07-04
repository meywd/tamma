# Welcome to the Tamma Wiki

**Tamma** is an autonomous development platform that maintains itself. This wiki provides comprehensive documentation for understanding, contributing to, and using Tamma.

## Quick Links

- [Project Roadmap](Roadmap) - All 31 epics with timeline, status, and layer placement
- [Architecture](Architecture) - System architecture overview (three deployment modes, `IAgentExecutor`, pluggable backends)
- [Event Schema & Catalog](Event-Schema-and-Catalog) - Epic 4 DCB event schema: `DomainEvent` shape, tags taxonomy, and the full `AGGREGATE.ACTION.STATUS` catalog
- [Deployment](Deployment) - Docker stack, least-privilege app-role runbook, env vars, Redis/Cranl activation
- [Agent Dispatch](Agent-Dispatch) - Epic 19 completion: `LocalExecutor`, `GitHubActionsExecutor`, webhook mode, TS `execute-agent` CLI
- [Security](Security) - Schema-per-tenant isolation, rate limiting, API key hashing, content sanitization, libsodium, webhook tenant-scoping
- [GitHub Integration](GitHub-Integration) - Octokit App client, OAuth flows, Actions dispatch
- [Testing](Testing) - Test strategy, testcontainers patterns, per-scope coverage
- [Port Audit](Port-Audit) - TS → C# port-gap audit (196 findings) + 2026-04-20 code-review round (18 findings)
- [Secret Management](Secret-Management) - Epic 29: secret cabinet, envelope encryption, rotation workflows
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) - Epic 30: `ITenantInfrastructureProvider` v2, Cranl/Hetzner/Cloudflare/BYO backends
- [Multi Git Platform](Multi-Git-Platform) - Epic 31: `IGitPlatformClient`, Gitea/Forgejo/GitLab drivers
- [Identity Providers](Identity-Providers) - Epic 33 (deferred): per-tenant SAML/OIDC/LDAP
- [Epics](Epics) - All 31 epics organized by phase
- [Epic 1: Foundation](Epics/Epic-1-Foundation) - Core infrastructure (AI providers, Git platforms, CLI)
- [Epic 1.5: Infrastructure & Deployment](Epics/Epic-1.5-Infrastructure) - Docker, CI/CD, SaaS coordinator
- [Epic 2: Autonomous Loop](Epics/Epic-2-Autonomous-Loop) - 14-step autonomous development loop
- [Epic 6: Context & Knowledge Management](Epics/Epic-6-Context-Knowledge) - Vector DB, RAG, MCP, cost monitoring
- [Epic 7: Mentorship Workflow](Epics/Epic-7-Mentorship) - ELSA workflow activities for autonomous mentorship
- [Epic 9: Agent Management](Epics/Epic-9-Agent-Management) - Config-driven multi-agent system
- [Epic 10: Engine Core](Epics/Epic-10-Engine-Core) - Workflow-driven architecture
- [Epic 11-14: ELSA Hardening](Epics/Epic-11-14-ELSA) - Security, agentic tool loop, workflow decomposition, custom studio
- [Epic 19: GitHub App Agent Dispatch](Epics/Epic-19-Agent-Dispatch) - Executor abstraction, Local/GitHubActions, dispatch/monitor/collect — **complete**
- [Epic 23: System Monitoring](Epics/Epic-23-System-Monitoring) - Production-grade monitoring & observability
- [Epic 24: Voice Conversation](Epics/Epic-24-Voice-Conversation) - Realtime voice conversation
- [Epic 25: Wiki Site](Epics/Epic-25-Wiki-Site) - Custom documentation site on Cloudflare Workers
- [Epic 26: Project Management & Triage](Epics/Epic-26-Project-Management) - Issue triage, scrum, release management
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
- **Three-Mode Architecture** -- CLI / SaaS single-tenant / SaaS multi-tenant via `IAgentExecutor` abstraction
- **Dual-Stack** -- TypeScript (Node.js) for providers, CLI, and API; C# (.NET 8) ELSA workflows for orchestration
- **Production-Ready Security** -- Content sanitization, URL validation, action gating, SSRF protection, LLM injection defense
- **ELSA Workflow Engine** -- Visual, composable, pausable/resumable workflows with ELSA Studio
- **Diagnostics Pipeline** -- Per-provider cost, token, latency, and error tracking
- **Self-Maintenance** -- Tamma maintains its own codebase (MVP validation goal)

### Architecture Highlights

- **Three Deployment Modes** — CLI (`LocalExecutor`), SaaS on the central pool (schema-per-tenant), SaaS with Cranl-minted per-tenant hosting infra
- **`IAgentExecutor` Abstraction** — Local subprocess or GitHub Actions dispatch; mode resolved via `TAMMA_AGENT_MODE` env / config / auto-detect
- **Interface-Based Provider Abstraction** -- Swap AI providers (Claude, GPT-4, Gemini, OpenRouter, local LLMs)
- **Platform-Agnostic Git Integration** -- GitHub today; Gitea/Forgejo/GitLab via Epic 31 drivers
- **ELSA Workflow Engine** -- 20+ code-first C# workflows with visual designer (ELSA Studio)
- **Event Sourcing & Audit Trail** -- Complete transparency via DCB (Development Context Bus)
- **Defense-in-Depth Security** -- Content sanitization at prompt and output boundaries, secure fetch with SSRF protection

## Current Status

**Phase:** Active Implementation — `feat/auth-foundation` branch (PR #328)
**Branch status:** 85+ commits ahead of `main`, CI green, **1817 tests passing**, PR mergeable
**Deployment:** VPS at 204.168.131.39 (Hetzner CPX42, 16GB) with Docker Compose stack
**Domains:** app.tamma.dev, api.tamma.dev, elsa.tamma.dev, wiki.tamma.dev, tamma.dev (Cloudflare DNS, Full SSL)
**Last Audit:** 2026-04-20 senior code review (18 findings; 4 merge-blockers closed)
**Epic Count:** **31 epics** (1–26 original plus 28–31, 33 new)

### Recent Progress (auth-foundation sprint, 2026-04-18 → 2026-04-21)

- **Epic 19 complete** -- all 4 stories (19-2 / 19-3 / 19-4 / 19-5) landed. `IAgentExecutor` with `LocalExecutor` (CLI) + `GitHubActionsExecutor` (SaaS). `WebhookSignalRegistry` for resumable webhook-mode monitoring (tenant-scoped via `install:{id}:` prefix after review finding 5). TS `execute-agent` CLI command in `packages/cli/` for `LocalExecutor` shell-out. 4 MB artifact cap + string clamps after review finding 6. See [Agent Dispatch](Agent-Dispatch).
- **Code review (2026-04-20)** -- 18 findings identified; 4 merge-blockers closed (`c404b51` audit downgrade, `b76ea79` 19-6 follow-up story, `9160db1` webhook tenant-scoping, `aab36e3` NULL-tenant RLS drop, `ced59bc` artifact size cap). Phase-3 RLS audit markers downgraded to "scaffold only — not live" since no endpoints actually inject `TammaAppDbContext` yet. See [Port Audit](Port-Audit).
- **Dependabot bumps** -- `System.Text.Json` 8.0.0 → 8.0.6, `MailKit` 4.15.1 → 4.16.0. Both NuGet vulnerabilities closed.
- **Connection-string resolver fix** -- `appsettings.json` `TammaDb` default cleared; new `ConnectionStringResolver` uses `IsNullOrWhiteSpace` fallback. Fixes deploy-to-VPS regression where empty default values were preferred over env-provided ones.
- **Marketing site live** -- Midnight Ocean redesign deployed to `tamma.dev` via wrangler (Cloudflare Worker).
- **Wave-2 impl-plan campaign** -- 39 new impl plans written across Epic 9 (9-12), Epic 12 (12-5a/b/d), Epic 18 (18-4, 18-5), Epic 19-6 follow-up, Epic 28 (28-1..28-12 + 28-13 blocker), Epic 29 (10 stories), Epic 30 (10 stories). ~800h of planned work catalogued.
- **New epics scoped** -- **Epic 29** Platform Secret Management (10 stories, 166h, Layer 4), **Epic 30** Pluggable Tenant Infrastructure Provisioning (10 stories, 216h, Layer 5), **Epic 31** Multi Git Platform Support (10 core + 2 deferred, ~228h), **Epic 33** Per-Tenant Identity Providers (deferred stub). Briefs + impl plans live under `docs/stories/epic-{29..33}/`.
- **Epic 18 extensions** -- stories 18-7 (tenant-admin user-mgmt API gaps) and 18-8 (tenant-admin UI). Backend mostly exists; these close the thin gaps + add the UI.
- **Research docs** -- `docs/stories/research/secret-management-and-multi-backend-provisioning-2026.md` and `docs/stories/research/multi-git-platform-2026.md` (2025–2026 citations).
- **TS → C# port audit** -- 196 per-finding notes across 8 scopes; **118 findings landed** this sprint. Notes live in `docs/audit/port-gaps/<scope>/NNN-*.md`. See [Port Audit](Port-Audit).
- **Phase-3 dual-connection RLS scaffolding** -- `TammaDb` + `TammaAppDb` split committed. Runtime wiring of `TammaAppDbContext` deferred to story 19-6 (the real RLS wiring follow-up). See [Deployment](Deployment#phase-3-rls-runbook).
- **Octokit GitHub App client** (github/all 11 findings) -- `OctokitGitHubAppClient` using installation-scoped JWTs. `NullGitHubAppClient` seam when `GitHub:AppId` absent.
- **Libsodium secrets provisioner** -- `LibsodiumGitHubSecretsProvisioner` encrypts GitHub Actions repository secrets via `Sodium.Core` sealed boxes.
- **TammaEngine SSE lifecycle** (engine/012) -- in-process `InMemoryEngineLifecycleBus` + SSE endpoints `/api/engine/events/state`, `/api/engine/events/logs`. Tenant-scoped fanout; 15-second heartbeats.
- **Redis-backed distributed rate limit** (auth/014) -- `IDistributedRateLimitBackend` with Lua `INCR + EXPIRE`; activated by `ConnectionStrings:Redis`.
- **Cranl per-tenant provisioner** -- `CranlTenantProvisioner`; admin endpoint `POST /api/admin/tenants/{id}/provision`. `NullTenantProvisioner` is the default — no external resources are minted until `Cranl:ApiKey` + `Cranl:OrganizationId` set (tenant placement stays on the central pool).
- **Content sanitizer port (~360 LoC)** -- C# port of the TS `ContentSanitizer` with prompt-injection detection, zero-width removal, NFKD normalisation.
- **Mobile-responsive wiki nav** -- 2026-04-19 fix: side nav hides on mobile with hamburger toggle (commit `365ef54`).

### Completed Epics (13)

| Epic | Name | Key Deliverables |
|------|------|------------------|
| Epic 8 | Distribution & Installation | esbuild bundle, npm publish, standalone binary, Docker Compose, Homebrew, CI/CD |
| Epic 9 | Agent Management | Config-driven multi-agent, circuit breakers, diagnostics, security layer |
| Epic 11 | Security Hardening | C# security pipeline, LLM input/output sanitization, tool validation |
| Epic 12 | Agentic Tool Loop | Multi-turn tool execution, context compaction, streaming |
| Epic 13 | Workflow Decomposition | TDD/CI retry sub-workflows, consolidated finish sequences |
| Epic 14 | Custom ELSA Studio | Custom Blazor WASM studio, Tamma branding, UI hints |
| Epic 15 | Observability | OpenSearch log aggregation |
| Epic 16 | Unified Auth & Admin | GitHub OAuth SSO, user management, admin panel, RBAC |
| Epic 19 | GitHub App Agent Dispatch | `IAgentExecutor`, Local + GitHub Actions, webhook-mode monitor, tenant-scoped signal keys |
| Epic 21 | Marketing Site | Midnight Ocean landing page deployed |
| Epic 25 | Documentation & Wiki Site | Vite+React SPA wiki site, React Flow diagrams, deployed to wiki.tamma.dev |

### Near Complete (6)

| Epic | Name | Done | Remaining |
|------|------|------|-----------|
| Epic 1 | Foundation & Core Infrastructure | 10/15 | 2 in progress, 3 ready |
| Epic 1.5 | Infrastructure & Deployment | 9/10 | Kubernetes deployment in progress |
| Epic 2 | Autonomous Development Loop | 13/20 | Priority work item selection + issue decomposition |
| Epic 3 | Quality Gates & Intelligence | 8/12 | 4 drafted |
| Epic 4 | Event Sourcing & Audit Trail | 6/8 | PostgreSQL backend in progress |
| Epic 6 | Context & Knowledge Management | 10/11 | Vector DB stubs |
| Epic 7 | Mentorship Workflow | 8/9 | TDD sub-workflow in progress |

### Newly Scoped (this sprint)

| Epic | Name | Stories | Layer | Status |
|------|------|---------|-------|--------|
| Epic 28 | Database-per-Tenant Foundation | 13 (1 blocker) | 4 | Briefs + 12 impl plans + 28-13 blocker |
| Epic 29 | Platform Secret Management | 10 | 4 | Briefs + 10 impl plans |
| Epic 30 | Pluggable Tenant Infrastructure | 10 | 5 | Briefs + 10 impl plans |
| Epic 31 | Multi Git Platform Support | 10 core + 2 optional | 4 + 5 | Briefs only |
| Epic 33 | Per-Tenant IdP (deferred) | — | post-launch | Forward-looking stub |

### Partially Implemented (4)

| Epic | Name | Done | In Progress | Drafted |
|------|------|------|-------------|---------|
| Epic 5 | Observability Dashboard & Docs | 4 | 3 | 7 |
| Epic 18 | End-User Auth & Registration | 1 | 2 | 2 (+ 18-7 / 18-8 new) |
| Epic 21 | Marketing Site & User Dashboard | 1 | 1 | 3 |
| Epic 26 | Project Management & Triage | 0 | 1 | 3 |

## Getting Started

1. Read the [Architecture](Architecture) overview
2. Review the [Roadmap](Roadmap) to understand the project timeline
3. Check out [Epic 1](Epics/Epic-1-Foundation) for foundational work
4. See [Epic 19](Epics/Epic-19-Agent-Dispatch) for the completed agent dispatch layer
5. Visit [Contributing](Contributing) to learn how to help

## Documentation

All technical documentation is maintained in the [/docs](https://github.com/meywd/tamma/tree/main/docs) directory:

- [PRD](https://github.com/meywd/tamma/blob/main/docs/PRD.md) - Product requirements
- [Architecture](https://github.com/meywd/tamma/blob/main/docs/architecture.md) - Technical architecture
- [Epics](https://github.com/meywd/tamma/blob/main/docs/epics.md) - Epic breakdown
- [Tech Specs](https://github.com/meywd/tamma/tree/main/docs) - Technical specifications per epic
- [Stories](https://github.com/meywd/tamma/tree/main/docs/stories) - User story documentation (31 epics, 260+ stories, 80+ impl plans)
- [Code review 2026-04-20](https://github.com/meywd/tamma/blob/main/docs/review/session-2026-04-20.md) - Senior code review report
- [Layer placement plans](https://github.com/meywd/tamma/tree/main/docs/stories/plans) - Layer 4/5 layer placement for Epics 29/30/31/33

---

_Last updated: 2026-04-21 (auth-foundation sprint + Wave-2 planning sync) | Maintained by: meywd_
