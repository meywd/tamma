---
title: "Tamma Project Roadmap"
sidebar:
  order: 1
---

Comprehensive roadmap covering all 27 epics from foundation through SaaS platform.

_Last audited: 2026-04-01_

## Epic Overview

| Epic | Name | Stories | Done | Status |
|------|------|---------|------|--------|
| **Epic 1** | Foundation & Core Infrastructure | 15 | 10 | Near Complete (2 in progress, 3 ready) |
| **Epic 1.5** | Infrastructure & Deployment | 10 | 9 | Near Complete (1 in progress) |
| **Epic 2** | Autonomous Development Loop | 16 | 13 | Near Complete (3 ready for dev) |
| **Epic 3** | Quality Gates & Intelligence | 12 | 8 | Near Complete (4 drafted) |
| **Epic 4** | Event Sourcing & Audit Trail | 8 | 6 | Near Complete (1 in progress, 1 drafted) |
| **Epic 5** | Observability Dashboard & Docs | 14 | 4 | Partially Implemented (3 in progress, 7 drafted/backlog) |
| **Epic 6** | Context & Knowledge Management | 10 | 9 | Near Complete (1 in progress) |
| **Epic 7** | Autonomous Mentorship Workflow | 9 | 8 | Near Complete (1 in progress) |
| **Epic 8** | Distribution & Installation | 8 | 8 | Completed |
| **Epic 9** | Config-Driven Multi-Agent Management | 11 | -- | Completed |
| **Epic 10** | Engine Core -- Workflow-Driven Architecture | 8 | 0 | Drafted (engine exists but stories define new architecture) |
| **Epic 11** | Security Hardening (ELSA) | 5 | -- | Completed |
| **Epic 12** | Agentic Tool Loop | 4 | -- | Completed |
| **Epic 13** | Workflow Decomposition | 3 | -- | Completed |
| **Epic 14** | Custom ELSA Studio | 3 | -- | Completed |
| **Epic 15** | Observability & Log Aggregation | 1 | -- | Completed (3 bug fixes shipped) |
| **Epic 16** | Unified Auth, User Management & Admin | 6 | -- | Completed |
| **Epic 17** | Multi-Tenancy Foundation | 5 | 0 | Drafted |
| **Epic 18** | End-User Auth & Registration | 5 | 1 | Partially Implemented (2 in progress, 2 drafted) |
| **Epic 19** | GitHub App Agent Dispatch | 5 | 1 | Partially Implemented (1 in progress, 3 drafted) |
| **Epic 20** | Billing & Payments | 5 | 0 | Drafted |
| **Epic 21** | Marketing Site & User Dashboard | 5 | 1 | Partially Implemented (1 in progress, 3 drafted) |
| **Epic 22** | CLI Mode Preservation | 5 | 0 | Drafted |
| **Epic 23** | System Monitoring & Observability Dashboard | 12 | 0 | Drafted (26 task plans, some in progress) |
| **Epic 24** | Realtime Voice Conversation | 7 | 1 | Drafted (24 task plans) |
| **Epic 25** | Documentation & Wiki Site | 1 | 1 | Completed |
| **Epic 26** | Project Management & Triage | 4 | 0 | Drafted |

---

## Completed / Near Complete Epics

### Epic 1: Foundation & Core Infrastructure

**Goal:** Establish multi-provider AI abstraction, multi-platform Git integration, and hybrid orchestrator/worker architecture.
**Status:** Near Complete -- 10/15 stories done, 2 in progress (additional AI providers, agent customization), 3 ready for dev.

**Key Deliverables:**
- `IAIProvider` and `IAgentProvider` interfaces in `@tamma/providers`
- Claude Code, OpenCode, OpenRouter, Zen MCP provider implementations
- `IGitPlatform` interface with GitHub platform implementation in `@tamma/platforms`
- Basic CLI scaffolding with mode selection (`@tamma/cli`)
- Provider configuration management
- Marketing website (Cloudflare Workers)

**Remaining:** GitLab platform (1-6), additional Git platforms (1-11), performance impact analysis (1-14); additional AI providers (1-10) and agent customization (1-13) in progress.

**Stories:** 1-0 through 1-14 (15 stories)

[Detailed Breakdown](/epics/1-foundation/)

---

### Epic 1.5: Infrastructure & Deployment

**Goal:** Production-ready deployment, Docker packaging, CI/CD, GitHub App auth, SaaS coordinator.
**Status:** Near Complete -- 9/10 stories done, Kubernetes deployment (1.5-10) in progress.

**Key Deliverables:**
- Docker Compose stack (PostgreSQL, RabbitMQ, ChromaDB, ELSA server, Tamma API, dashboard, nginx)
- CI/CD pipelines (GitHub Actions): ci.yml, deploy.yml, docker-publish.yml, docker-smoke-test.yml
- GitHub App authentication with installation management
- SaaS coordinator for multi-installation engine orchestration
- GitHub Actions worker mode
- Multi-tenant task queue and webhook routing
- API key provisioning and GitHub Secrets setup
- NPM package publishing pipeline
- Binary releases and installers

**Stories:** 1.5-1 through 1.5-10 (10 stories)

[Detailed Breakdown](/epics/1.5-infrastructure/)

---

### Epic 6: Context & Knowledge Management

**Goal:** Advanced context gathering through vector databases, RAG systems, MCP servers, cost monitoring, permissions, and knowledge base.
**Status:** Near Complete -- 9/10 stories done. Vector DB integration (6-2) in progress (ChromaDB + pgvector real; Pinecone, Qdrant, Weaviate are stubs).

**Key Deliverables:**
- Codebase indexer with TypeScript-aware chunking (`@tamma/intelligence`)
- Vector database integration (ChromaDB, pgvector real; Pinecone, Qdrant, Weaviate stubs) (`@tamma/intelligence`)
- RAG pipeline with hybrid search (`@tamma/intelligence`)
- MCP client with transport support (stdio, SSE, WebSocket) (`@tamma/mcp-client`)
- Context aggregator service with token budget management (`@tamma/intelligence`)
- Knowledge base management dashboard (`@tamma/dashboard`)
- LLM cost monitoring and reporting (`@tamma/cost-monitor`)
- Agent permissions system (`@tamma/gates`)
- Agent knowledge base with recommendations, prohibitions, and learnings (`@tamma/intelligence`)
- Scrum master task loop (`@tamma/scrum-master`)

**Stories:** 6-1 through 6-10 (10 stories)

[Detailed Breakdown](/epics/6-context-knowledge/)

---

### Epic 7: Autonomous Mentorship Workflow

**Goal:** AI-powered mentorship workflow guiding developers through story implementation using a 28-state state machine.
**Status:** Near Complete -- 8/9 core stories done. TDD sub-workflow (7-8) in progress (structure real but test execution is mocked).

**Key Deliverables:**
- 28-state mentorship state machine in ELSA (.NET/C#)
- 12+ ELSA activities (assessment, context gathering, Claude analysis, monitoring, blocker diagnosis, quality gates, code review, merge)
- TypeScript bridge layer (`@tamma/orchestrator` ElsaClient)
- 9 code-first ELSA sub-workflows (LLM Call, Testing, Assessment, Context, Code Review, Blocker, TDD, Debugging, Main Mentorship)

**Stories:** 7-1 through 7-10, 7-1A through 7-1I (19 stories)

[Detailed Breakdown](/epics/7-mentorship/)

---

### Epic 9: Config-Driven Multi-Agent Management

**Goal:** Config-driven multi-agent system with provider chains, circuit breakers, diagnostics, content sanitization, and role-based resolution.

**Key Deliverables:**
- `IAgentsConfig` schema with validation (`@tamma/shared`)
- Provider diagnostics with typed events (`@tamma/shared`)
- Circuit breaker health tracker (`@tamma/providers`)
- Agent provider factory with 4 built-in providers (`@tamma/providers`)
- Provider chain with ordered fallback (`@tamma/providers`)
- Prompt template registry with 6-level resolution (`@tamma/providers`)
- Content sanitization, URL validation, action gating, secure fetch (`@tamma/shared`)
- Role-based agent resolver (`@tamma/providers`)
- Diagnostics queue and MCP tool interceptors (`@tamma/shared`, `@tamma/mcp-client`)

**Stories:** 9-1 through 9-11 (11 stories)

[Detailed Breakdown](/epics/9-agent-management/)

---

### Epic 10: Engine Core -- Workflow-Driven Architecture

**Goal:** Refactor the engine from a hardcoded state machine into a workflow-driven orchestration service with ELSA as the workflow provider.
**Status:** Drafted -- the engine exists as an imperative state machine but these stories define the target agentic architecture (smart queue, event store PostgreSQL/Emmett, state reconstruction, input normalization). None of the 8 stories match current implementation.

**Stories:** 10-1 through 10-8 (8 stories)

[Detailed Breakdown](/epics/10-engine-core/)

---

### Epic 11: Security Hardening (ELSA)

**Goal:** Port TypeScript security pipeline to C# for the ELSA workflow layer.

**Key Deliverables:**
- C# ContentSanitizer port with NFKD normalization
- LLM input sanitization in prompt resolution activities
- Tool call validation (name allowlist, argument schema, size cap)
- LLM output sanitization and prompt hardening
- Fail-closed guards on circuit breaker and budget checks
- Provider name allowlist enforcement

**Stories:** 11-1 through 11-5 (5 stories)

---

### Epic 12: Agentic Tool Loop

**Goal:** Multi-turn tool execution in ELSA `CallLlmInlineActivity`.

**Key Deliverables:**
- `IToolExecutor` interface and `ToolExecutorRegistry` in C#
- Tool executors: FileRead, FileWrite, SearchCode, ShellExecute, RunTests, GitOperations
- Agentic tool loop in `CallLlmInlineActivity`
- Context compaction with `TokenEstimator` and `ContextCompactor`
- SSE streaming and parallel tool execution

**Stories:** 12-1 through 12-4 (4 stories)

---

### Epic 13: Workflow Decomposition

**Goal:** Split the 783-line `SingleIssueCycleWorkflow` into composable sub-workflows.

**Key Deliverables:**
- `TddWithDebugRetryWorkflow` sub-workflow
- `CiWithDebugRetryWorkflow` sub-workflow
- Consolidated finish sequences (7 duplicates reduced to 1)

**Stories:** 13-1 through 13-3 (3 stories)

---

### Epic 14: Custom ELSA Studio

**Goal:** Custom Blazor WASM project replacing the upstream ELSA Studio Docker image.

**Key Deliverables:**
- Custom Blazor WASM project (`Tamma.Studio`)
- Docker build and CI/CD integration
- Custom UI hint handlers (JSON editor, provider selector)
- Tamma branding and purple MudBlazor theme

**Stories:** 14-1 through 14-3 (3 stories)

---

### Epic 15: Observability & Log Aggregation

**Goal:** Centralized log aggregation with OpenSearch.

**Key Deliverables:**
- OpenSearch integration in Docker Compose (optional profile)
- Pre-built dashboards for errors, workflow timelines, LLM call latency
- ISM policies for 30-day retention

**Post-completion fixes:**
- Fixed ESM `require` error in Node.js OpenSearch client initialization
- Bumped `Serilog.Sinks.File` to 6.0.0 for OpenSearch sink compatibility in .NET services
- Fixed Fastify logger instance mismatch (`loggerInstance` instead of `logger` option) for Pino compatibility

**Stories:** 15-1 (1 story)

---

### Epic 16: Unified Auth, User Management & Admin

**Goal:** Single GitHub OAuth flow across all services with user management and RBAC.

**Key Deliverables:**
- GitHub OAuth SSO (Dashboard, ELSA Studio, OpenSearch Dashboards)
- User management API with invite flow and role assignment (`@tamma/api`)
- Admin dashboard with system health, user management, API keys, quick links (`@tamma/dashboard`)
- Cross-service navigation header
- RBAC enforcement (member, admin, owner) at API and nginx levels
- ELSA Studio auto-login (bypass internal ELSA Identity login page)

**Post-completion changes:**
- OAuth2-proxy fully removed; auth consolidated on app-level GitHub OAuth
- ELSA Studio Blazor WASM static assets (`.dll`, `.wasm`) now skip auth to prevent rate limiting
- RabbitMQ health check updated to use basic `Authorization` header instead of URL-embedded credentials
- Role-check endpoint exempted from rate limiting
- New logo/favicon deployed across all sites

**Stories:** 16-1 through 16-6 (6 stories)

---

## Near Complete Epics

### Epic 2: Autonomous Development Loop

**Goal:** Implement the 14-step autonomous development loop.
**Status:** Near Complete -- 13 of 16 stories done, retrospective completed.

**Stories:** 2-1 through 2-16 (16 stories)
- Core loop (2-1 through 2-11): All done -- issue selection, context analysis, plan generation, branch creation, TDD, code generation, refactoring, PR creation, status monitoring, merge, auto-next
- Intelligent provider selection (2-12): Done
- Prompt engineering optimization (2-13): Done
- Remaining: Issue decomposition engine (2-14), task dependency mapping (2-15), incremental task sequencing (2-16) -- ready for dev

---

## Planned / In-Progress Epics

### Epic 3: Quality Gates & Intelligence

**Goal:** Build automation, test execution, CI/CD integration with retry limits and mandatory escalation.
**Status:** Near Complete -- 8/12 stories done.

**Stories:** 3-1 through 3-12 (12 stories)
- Done: Build automation (3-1), test execution (3-2), escalation workflow (3-3), static analysis (3-8), security scanning (3-9), agent performance monitoring (3-10), cost-aware AI usage (3-11), task complexity assessment (3-12)
- Drafted: Research workflow (3-4), clarifying questions (3-5), ambiguity scoring (3-6), design proposals (3-7)

---

### Epic 4: Event Sourcing & Audit Trail

**Goal:** CQRS event sourcing for complete audit trail.
**Status:** Near Complete -- 6/8 stories done, PostgreSQL backend in progress.

**Stories:** 4-1 through 4-8 (8 stories)
- Done: Event schema design (4-1), event capture for issues (4-3), AI interactions (4-4), code changes (4-5), approvals (4-6), time-travel query API (4-7)
- In Progress: Event store backend -- PostgreSQL (4-2)
- Drafted: Black-box replay (4-8)

---

### Epic 5: Observability Dashboard & Docs

**Goal:** Real-time observability and comprehensive documentation.

**Status:** Partially implemented -- 4 done, 3 in progress, 7 drafted/backlog.

**Stories:** 5-1 through 5-10 (14 stories)
- Done: Structured logging (5-1), system health dashboard (5-3), integration testing suite (5-8), alpha release preparation (5-10)
- In Progress: Event trail exploration (5-5), alert system (5-6), feedback collection (5-7)
- Drafted: Metrics collection (5-2), development velocity (5-4), documentation (5-9a through 5-9e)

---

### Epic 8: Distribution & Installation

**Goal:** Three-tier distribution: npm, standalone binary, Docker full-stack.
**Status:** Completed -- all 8 stories done.

**Key Deliverables:**
- Tier 1: esbuild bundle (build.mjs) + npm publish CI/CD (release.yml, package.json.publish, prepare-package.mjs)
- Tier 2: Standalone binary (build-binary.mjs via Bun) + install scripts + Homebrew template
- Tier 3: Docker Compose full stack (Dockerfile.ts, Dockerfile.dashboard) + Docker CI/CD (docker-publish.yml, docker-smoke-test.yml)

**Stories:** 8-1 through 8-8 (8 stories)

---

### Epic 17: Multi-Tenancy Foundation

**Goal:** PostgreSQL Row-Level Security for multi-tenant SaaS.

**Stories:** 17-1 through 17-5 (5 stories)
- Tenant model and database schema
- RLS-based tenant isolation
- Tenant-scoped event store and workflow instances
- API tenant context middleware

---

### Epic 18: End-User Auth & Registration

**Goal:** Self-service registration, email verification, organization management, GitHub App onboarding.
**Status:** Partially Implemented -- GitHub App onboarding (18-4) done; login/session (18-2) and dashboard shell (18-5) in progress; registration (18-1) and org creation (18-3) drafted.

**Stories:** 18-1 through 18-5 (5 stories)
- Done: GitHub App installation onboarding (18-4)
- In Progress: User login & session management (18-2), user-facing dashboard shell (18-5)
- Drafted: User registration & email verification (18-1), organization/tenant creation (18-3)

---

### Epic 19: GitHub App Agent Dispatch

**Goal:** Orchestrate agents on user's GitHub Actions runners.
**Status:** Partially Implemented -- workflow template (19-1) done; result collection (19-4) in progress; dispatch (19-2), monitoring (19-3), abstraction (19-5) drafted.

**Stories:** 19-1 through 19-5 (5 stories)
- Done: Tamma agent workflow template (19-1)
- In Progress: Result collection (19-4)
- Drafted: Workflow dispatch from ELSA (19-2), agent monitoring (19-3), agent executor abstraction (19-5)

---

### Epic 20: Billing & Payments

**Goal:** Stripe-based billing with tiered subscription plans.

**Plans:** Free ($0, 50 runs/mo), Pro ($29, 2000 runs/mo), Enterprise (custom)

**Stories:** 20-1 through 20-5 (5 stories)
- Stripe integration, subscription management
- Usage metering (workflow runs, LLM tokens, repos)
- Limits enforcement at orchestrator level
- Billing dashboard for self-service

---

### Epic 21: Marketing Site & User Dashboard

**Goal:** Production marketing site and user-facing dashboard.

**Status:** Partially Implemented -- Marketing landing page (21-1) done with Midnight Ocean redesign; documentation site (21-3) in progress (wiki-site scaffold exists); pricing (21-2), user repos/runs (21-4), settings/billing (21-5) drafted.

**Stories:** 21-1 through 21-5 (5 stories)
- Done: Marketing landing page (21-1) -- Midnight Ocean redesign deployed
- In Progress: Documentation site (21-3) -- wiki-site scaffold at apps/wiki-site/
- Drafted: Pricing page (21-2), user repos/runs (21-4), settings/billing (21-5)

---

### Epic 22: CLI Mode Preservation

**Goal:** Standalone CLI works without cloud dependencies while sharing ELSA workflow engine.

**Stories:** 22-1 through 22-5 (5 stories)
- Agent executor abstraction
- CLI standalone workflow engine
- Optional cloud sync
- Feature parity matrix
- CLI Docker installation

---

### Epic 23: System Monitoring & Observability Dashboard

**Goal:** Production-grade monitoring, diagnostics, and observability for every service, provider, workflow, and infrastructure component.

**Stories:** 23-1 through 23-12 (12 stories, **26 detailed task plans**)

Each of the 12 stories now has implementation-ready task plan breakdowns:
- 23-1 System Health Dashboard (2 task plans)
- 23-2 Agent Monitor (2 task plans)
- 23-3 Event Store Explorer (2 task plans)
- 23-4 Configuration Audit (2 task plans)
- 23-5 Workflow Monitor (2 task plans)
- 23-6 Provider Diagnostics (2 task plans)
- 23-7 Log Explorer (2 task plans)
- 23-8 Infrastructure Monitor (2 task plans)
- 23-9 Knowledge Base Monitor (2 task plans)
- 23-10 Security & Access Audit (2 task plans)
- 23-11 Monitoring API Foundation (3 task plans)
- 23-12 Dashboard Navigation & Layout (3 task plans)

[Detailed Breakdown](/epics/23-system-monitoring/)

---

### Epic 24: Realtime Voice Conversation

**Goal:** Voice as a first-class input/output mode for the orchestrator -- users talk to Tamma through their browser.

**Status:** Research complete (Story 24-0 done). Implementation planned with **24 detailed task plans**.

**Stories:** 24-0 through 24-6 (7 stories, **24 detailed task plans**)

Each implementation story now has task plan breakdowns:
- 24-1 WebSocket Foundation (5 task plans)
- 24-2 Speech-to-Text Integration (4 task plans)
- 24-3 Text-to-Speech Integration (4 task plans)
- 24-4 Intent Classification + Engine Integration (3 task plans)
- 24-5 Dashboard Voice UI (4 task plans)
- 24-6 Hardening + Production Readiness (4 task plans)

[Detailed Breakdown](/epics/24-voice-conversation/)

---

### Epic 26: Project Management & Triage

**Goal:** Automated project management workflows -- issue triage, scrum management, release management, and priority configuration.

**Status:** Drafted -- created as part of the ADL Orchestrator redesign to support the new triage integration.

**Stories:** 26-1 through 26-4 (4 stories)
- 26-1 Issue Triage Workflow -- LLM-powered classification and labeling of untriaged issues
- 26-2 Scrum Management Workflow -- Sprint planning, standups, retrospectives
- 26-3 Release Management Workflow -- Changelog generation, deployment orchestration
- 26-4 Priority Configuration System -- Configurable priority rules for work item selection

[Detailed Breakdown](/epics/26-project-management/)

---

### Epic 25: Documentation & Wiki Site

**Goal:** Build and deploy a custom documentation/wiki site on Cloudflare Workers, accessible at wiki.tamma.dev.

**Status:** Completed. Wiki site rewritten as Vite + React + React Router SPA with React Flow interactive diagrams, deployed to Cloudflare Workers.

**Key Deliverables:**
- Vite + React SPA wiki site with React Flow interactive flowcharts (dagre auto-layout, draggable nodes, minimap)
- Designed pages: Home, Epics, Workflows, Stories with detail views
- Dark sidebar, collapsible navigation, prose styling
- Cloudflare Workers Static Assets deployment
- Content sync from wiki/ and docs/stories/ directories

**Stories:** 25-1 (1 story) -- Done

[Detailed Breakdown](/epics/25-wiki-site/)

---

## Timeline Visualization

```
Phase 1 (Completed):
  Epic 1   (Foundation)              [NEAR COMPLETE - 10/15 done, 2 in progress]
  Epic 1.5 (Infrastructure)         [NEAR COMPLETE - 9/10 done, 1 in progress]
  Epic 9   (Multi-Agent Management)  [COMPLETED]

Phase 2 (Near Complete):
  Epic 6   (Context & Knowledge)     [NEAR COMPLETE - 9/10 done]
  Epic 7   (Mentorship Workflow)     [NEAR COMPLETE - 8/9 core done]
  Epic 10  (Engine Core)             [DRAFTED - stories define new architecture]

Phase 3 (Completed):
  Epic 11  (Security Hardening)      [COMPLETED]
  Epic 12  (Agentic Tool Loop)       [COMPLETED]
  Epic 13  (Workflow Decomposition)  [COMPLETED]
  Epic 14  (Custom ELSA Studio)      [COMPLETED]
  Epic 15  (Log Aggregation)         [COMPLETED]
  Epic 16  (Unified Auth & Admin)    [COMPLETED]

Phase 4 (Active):
  Epic 2   (Autonomous Loop)         [NEAR COMPLETE - 13/16 done]
  Epic 3   (Quality Gates)           [NEAR COMPLETE - 8/12 done]
  Epic 4   (Event Sourcing)          [NEAR COMPLETE - 6/8 done]
  Epic 5   (Observability/Docs)      [PARTIAL - 4/14 done, 3 in progress]
  Epic 8   (Distribution)            [COMPLETED - 8/8 done]
  Epic 25  (Wiki Site)               [COMPLETED]

Phase 5 (Planned - SaaS):
  Epic 17  (Multi-Tenancy)           [Drafted]
  Epic 18  (End-User Auth)           [PARTIAL - 1 done, 2 in progress]
  Epic 19  (Agent Dispatch)          [PARTIAL - 1 done, 1 in progress]
  Epic 20  (Billing)                 [Drafted]
  Epic 21  (Marketing/Dashboard)     [PARTIAL - 1 done, 1 in progress]
  Epic 22  (CLI Preservation)        [Drafted]

Phase 6 (Planned - Advanced):
  Epic 23  (System Monitoring)       [Drafted - 26 task plans]
  Epic 24  (Voice Conversation)      [Drafted - 24 task plans]
  Epic 26  (Project Mgmt & Triage)   [Drafted - 4 stories]
```

---

## Key Success Metrics

- **Autonomous Completion Rate:** 70%+ (target)
- **Time to Resolution:** <24 hours for most issues
- **Quality Gate Pass Rate:** 95%+ (mandatory escalation for failures)
- **System Uptime:** 99.5%+ (orchestrator mode)
- **Test Coverage:** 80%+ line coverage, 75%+ branch coverage

---

_For detailed technical specifications, see the tech-spec documents in the [docs/](/epics/) directory._
