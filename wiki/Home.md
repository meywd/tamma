# Welcome to the Tamma Wiki

**Tamma** is an autonomous development platform that maintains itself. This wiki provides comprehensive documentation for understanding, contributing to, and using Tamma.

## Quick Links

- [Project Roadmap](Roadmap) - All 37 epics with timeline, status, and layer placement
- [Architecture](Architecture) - System architecture overview (three deployment modes, `IAgentExecutor`, pluggable backends)
- [Event Schema & Catalog](Event-Schema-and-Catalog) - Epic 4 DCB event schema: `DomainEvent` shape, tags taxonomy, and the full `AGGREGATE.ACTION.STATUS` catalog
- [Deployment](Deployment) - Docker stack, least-privilege app-role runbook, env vars, Redis/Cranl activation
- [Installation & Setup](Installation) - Docker Compose stack, `.env`, health checks, VPS/qa-tag deploy (Story 5-9a)
- [Usage & Configuration](Usage-and-Configuration) - CLI commands, operating modes, `.tamma/config.json`, prompts, providers, BYOK (Story 5-9b)
- [API Reference](API-Reference) - REST surface, RBAC policies, SSE streams, webhooks, DCB event catalog (Story 5-9c)
- [Agent Dispatch](Agent-Dispatch) - Epic 19 completion: `LocalExecutor`, `GitHubActionsExecutor`, webhook mode, TS `execute-agent` CLI
- [Security](Security) - Schema-per-tenant isolation, rate limiting, API key hashing, content sanitization, libsodium, webhook tenant-scoping
- [Action Catalog & Autonomy](Action-Catalog-and-Autonomy) - How Tamma decides what it may do alone: the action catalog, the 1-100 autonomy dial, per-route enforcement, and approval scopes
- [GitHub Integration](GitHub-Integration) - Octokit App client, OAuth flows, Actions dispatch
- [Testing](Testing) - Test strategy, testcontainers patterns, per-scope coverage
- [Port Audit](Port-Audit) - TS → C# port-gap audit (196 findings) + 2026-04-20 code-review round (18 findings)
- [Secret Management](Secret-Management) - Epic 29: secret cabinet, envelope encryption, rotation workflows
- [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) - Epic 30: `ITenantInfrastructureProvider` v2, Cranl/Hetzner/Cloudflare/BYO backends
- [Multi Git Platform](Multi-Git-Platform) - Epic 31: `IGitPlatformClient`, Gitea/Forgejo/GitLab drivers
- [Identity Providers](Identity-Providers) - Epic 33 (deferred): per-tenant SAML/OIDC/LDAP
- [Document Lifecycle](Document-Lifecycle) - Epic 39: typed work documents + the universal produce→validate→review→revise→accept lifecycle
- [Resumable Workflows](Resumable-Workflows) - The resumable-by-design standard (Epic 39-10) generalized by Epics 40 and 41
- [Epics](Epics) - All 37 epics organized by phase
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
- [Epic 39: Document Lifecycle](Epics/Epic-39-Document-Lifecycle) - Typed work documents + universal lifecycle (**implemented** — spine complete)
- [Epic 40: Resumable Coding](Epics/Epic-40-Resumable-Coding) - Durable agent-run waits + the tamma-agent runner contract (planned)
- [Epic 41: Full-Team Workflows](Epics/Epic-41-Full-Team-Workflows) - Every remaining SDLC activity as a lifecycle workflow + the 41-29 flow router (planned)
- [Epic 42: Tool Layer](Epics/Epic-42-Tool-Layer) - Governed, extensible, secret-bound agent tool catalog (planned)
- [Workflows](Workflows) - All 30+ ELSA workflows with flow diagrams and dependency map
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

**Phase:** Active development on `main` — the Epic 39 document-lifecycle spine has landed (typed work documents + the universal produce→validate→review→revise→accept lifecycle; every document-producing workflow migrated onto it), alongside the Epic 23 monitoring-dashboard buildout, Epic 3 assessment workflows, and Epic 4 event capture + black-box replay
**Stack:** C#/.NET 8 backend (30+ Elsa workflows + `Tamma.Api` Minimal API) on PostgreSQL 17; TypeScript `intelligence-server` sidecar (KB/RAG) + React dashboards
**KB/RAG:** sidecar wired to a real vector store with self-hosted embeddings via local Ollama (`nomic-embed-text`) — no external embedding API required
**Deployment:** Hetzner VPS via Docker Compose (qa-tag–gated deploys); wiki site live at wiki.tamma.dev
**Epic Count:** epics 1–31 plus 32/34–38 (agents, pricing, billing, analytics, audit) tracked in `docs/sprint-status.yaml`, plus the workflow-platform wave (39 implemented; 40/41/42 scoped); Epic 33 deferred

### Recent Progress (main-branch development, 2026-07-04 → 2026-07-24)

- **Epic 39 document-lifecycle spine — implemented** -- typed work documents (Decomposition, Plan, Review, Findings, …) as static `Tamma.Core` types, one generic `DocumentLifecycleWorkflow` (produce→validate→review→revise→accept), acceptance rules + the 70–100 autonomy dial, the escalation/approval surface, real-time channels + orchestrator chat + Task View, teams/roles/repo-access routing, and the resumable-by-design standard. **The producer-migration spine is complete** — 39-12…39-15 all merged, so every document-producing workflow now rides the lifecycle. Remaining: 39-1 (I/O audit), 39-16 (generated prompt contracts), 39-17 (resident orchestrator agent), 39-21 (C# RAG). Epics 40 (resumable coding), 41 (full-team workflows), and 42 (tool layer) are scoped/planned on top of it.
- **Epic 23 monitoring dashboard — 8 of 12 stories landed** -- 23-12 nav/layout/shared primitives (foundation), 23-1 System Health overview, 23-2 Agent Monitor (realtime tool-loop tail), 23-3 Event Store Explorer (over the 4-7 query API), 23-4 Configuration Audit, 23-5 Workflow Monitor, 23-6 Provider Diagnostics (latency/error/cost aggregations + fail-closed tenant fix), 23-8 Infrastructure Monitor (runtime/disk/dependency health).
- **Epic 3 assessment workflows** -- 3-4 ResearchWorkflow + research action/prompt template (`RESEARCH.*` events, structured findings), 3-6 ambiguity scoring (mediated LLM, fail-closed parse, `AMBIGUITY.*` events), 3-7 DesignProposalWorkflow (approval gate + secure resume, `DESIGN.*` events).
- **Epic 4 audit trail** -- 4-5 DCB event coverage for code changes & git operations; 4-8 black-box replay (point-in-time state reconstruction via a pure fold over the DCB event slice); read-endpoint hardening fix (UTC date bounds, bounded fetches, empty-tenant guard).
- **Epic 2-14 issue decomposition** -- IssueDecompositionWorkflow: mediated LLM, ordered sub-tasks with dependencies, `DECOMPOSITION.*` events.
- **Epic 18-4 onboarding slices** -- repo activate/deactivate endpoint + first-run `ONBOARDING.COMPLETED` event (non-migration slices; the install-settings migration slice remains open).
- **Epic 21-4 user dashboard** -- Repos & Workflow Runs pages over installations + DCB run events.
- **Epic 6 KB/RAG** -- sidecar composition root wired to a real vector store (`createVectorStoreFromEnv`/`RagPipeline`), self-hosted embeddings via local Ollama (`nomic-embed-text`), RAG collection bootstrap so the sidecar boots configured, plus prod-image fixes.
- **Docs** -- CLAUDE.md stack-reality corrections: the active backend is C#/.NET 8 + Elsa; the TS toolchain notes now apply only to the legacy `packages/*`, the sidecar, and the dashboards.

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
| Epic 2 | Autonomous Development Loop | 14/20 | Provider selection, dependency mapping + sequencing, prompt overhauls |
| Epic 3 | Quality Gates & Intelligence | 12/13 | Intelligent test-execution pipeline (3-13) |
| Epic 4 | Event Sourcing & Audit Trail | 7/8 | Event store backend selection in progress |
| Epic 6 | Context & Knowledge Management | 10/11 | Vector DB provider stubs (sidecar now live w/ Ollama embeddings) |
| Epic 7 | Mentorship Workflow | 8/9 | TDD sub-workflow in progress |

### Newly Scoped (this sprint)

| Epic | Name | Stories | Layer | Status |
|------|------|---------|-------|--------|
| Epic 28 | Database-per-Tenant Foundation | 13 (1 blocker) | 4 | Briefs + 12 impl plans + 28-13 blocker |
| Epic 29 | Platform Secret Management | 10 | 4 | Briefs + 10 impl plans |
| Epic 30 | Pluggable Tenant Infrastructure | 10 | 5 | Briefs + 10 impl plans |
| Epic 31 | Multi Git Platform Support | 10 core + 2 optional | 4 + 5 | Briefs only |
| Epic 33 | Per-Tenant IdP (deferred) | — | post-launch | Forward-looking stub |

### Workflow-Platform Wave (Epics 39–42)

The document-lifecycle wave. **Epic 39 is implemented** (spine complete); **40, 41, 42 are scoped/planned — backlog, not built.**

| Epic | Name | Stories | Status |
|------|------|---------|--------|
| Epic 39 | Typed Work Documents & the Universal Lifecycle | 21 | **Implemented** — spine + producer migrations 39-12…39-15 merged; 39-1/16/17/21 remain |
| Epic 40 | Resumable Coding Execution | 7 | Planned / docs — backlog |
| Epic 41 | Full-Team Workflow Coverage | 28 + 41-29 router | Planned / docs — backlog |
| Epic 42 | Agent Capability & Tool Layer | 9 | Planned / docs — backlog |

### Partially Implemented (5)

| Epic | Name | Done | In Progress | Drafted |
|------|------|------|-------------|---------|
| Epic 5 | Observability Dashboard & Docs | 4 | 3 | 7 |
| Epic 18 | End-User Auth & Registration | 4 | 3 | 1 (18-8 UI) |
| Epic 21 | Marketing Site & User Dashboard | 2 | 0 | 2 (21-1/21-2 superseded by repo extraction) |
| Epic 23 | System Monitoring Dashboard | 8 | 0 | 3 (23-11 superseded by C# port) |
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
- [Stories](https://github.com/meywd/tamma/tree/main/docs/stories) - User story documentation (37 epics, 375+ stories, 80+ impl plans)
- [Code review 2026-04-20](https://github.com/meywd/tamma/blob/main/docs/review/session-2026-04-20.md) - Senior code review report
- [Layer placement plans](https://github.com/meywd/tamma/tree/main/docs/stories/plans) - Layer 4/5 layer placement for Epics 29/30/31/33

---

_Last updated: 2026-07-24 (workflow-platform wave: Epic 39 document-lifecycle spine implemented; Epics 40/41/42 scoped) | Maintained by: meywd_
