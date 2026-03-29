# Tamma Project Roadmap

Comprehensive roadmap covering all 22 epics from foundation through SaaS platform.

## Epic Overview

| Epic | Name | Stories | Status |
|------|------|---------|--------|
| **Epic 1** | Foundation & Core Infrastructure | 15 | Completed |
| **Epic 1.5** | Infrastructure & Deployment | 15 | Completed |
| **Epic 2** | Autonomous Development Loop | 16 | Planned |
| **Epic 3** | Quality Gates & Intelligence | 12 | Planned |
| **Epic 4** | Event Sourcing & Audit Trail | 8 | Planned |
| **Epic 5** | Observability Dashboard & Docs | 14 | Partially Implemented |
| **Epic 6** | Context & Knowledge Management | 10 | Completed |
| **Epic 7** | Autonomous Mentorship Workflow | 19 | Completed |
| **Epic 8** | Distribution & Installation | 8 | Planned |
| **Epic 9** | Config-Driven Multi-Agent Management | 11 | Completed |
| **Epic 10** | Engine Core -- Workflow-Driven Architecture | 8 | Completed |
| **Epic 11** | Security Hardening (ELSA) | 5 | Completed |
| **Epic 12** | Agentic Tool Loop | 4 | Completed |
| **Epic 13** | Workflow Decomposition | 3 | Completed |
| **Epic 14** | Custom ELSA Studio | 3 | Completed |
| **Epic 15** | Observability & Log Aggregation | 1 | Completed |
| **Epic 16** | Unified Auth, User Management & Admin | 5 | Completed |
| **Epic 17** | Multi-Tenancy Foundation | 5 | Planned |
| **Epic 18** | End-User Auth & Registration | 5 | Planned |
| **Epic 19** | GitHub App Agent Dispatch | 5 | Planned |
| **Epic 20** | Billing & Payments | 5 | Planned |
| **Epic 21** | Marketing Site & User Dashboard | 5 | Partially Implemented |
| **Epic 22** | CLI Mode Preservation | 5 | Planned |

---

## Completed Epics

### Epic 1: Foundation & Core Infrastructure

**Goal:** Establish multi-provider AI abstraction, multi-platform Git integration, and hybrid orchestrator/worker architecture.

**Key Deliverables:**
- `IAIProvider` and `IAgentProvider` interfaces in `@tamma/providers`
- Claude Code, OpenCode, OpenRouter, Zen MCP provider implementations
- `IGitPlatform` interface with GitHub platform implementation in `@tamma/platforms`
- Basic CLI scaffolding with mode selection (`@tamma/cli`)
- Provider configuration management
- Marketing website (Cloudflare Workers)

**Stories:** 1-0 through 1-14 (15 stories)

[Detailed Breakdown](Epic-1-Foundation)

---

### Epic 1.5: Infrastructure & Deployment

**Goal:** Production-ready deployment, Docker packaging, CI/CD, GitHub App auth, SaaS coordinator.

**Key Deliverables:**
- Docker Compose stack (PostgreSQL, RabbitMQ, ChromaDB, ELSA server, Tamma API, dashboard, nginx)
- CI/CD pipelines (GitHub Actions): ci.yml, deploy.yml, docker-publish.yml, docker-smoke-test.yml
- GitHub App authentication with installation management
- SaaS coordinator for multi-installation engine orchestration
- GitHub Actions worker mode
- Multi-tenant task queue and webhook routing
- API key provisioning and GitHub Secrets setup

**Stories:** 1.5-1 through 1.5-15 (15 stories)

[Detailed Breakdown](Epic-1.5-Infrastructure)

---

### Epic 6: Context & Knowledge Management

**Goal:** Advanced context gathering through vector databases, RAG systems, MCP servers, cost monitoring, permissions, and knowledge base.

**Key Deliverables:**
- Codebase indexer with TypeScript-aware chunking (`@tamma/intelligence`)
- Vector database integration (ChromaDB, pgvector, Pinecone, Qdrant, Weaviate) (`@tamma/intelligence`)
- RAG pipeline with hybrid search (`@tamma/intelligence`)
- MCP client with transport support (stdio, SSE, WebSocket) (`@tamma/mcp-client`)
- Context aggregator service with token budget management (`@tamma/intelligence`)
- Knowledge base management dashboard (`@tamma/dashboard`)
- LLM cost monitoring and reporting (`@tamma/cost-monitor`)
- Agent permissions system (`@tamma/gates`)
- Agent knowledge base with recommendations, prohibitions, and learnings (`@tamma/intelligence`)
- Scrum master task loop (`@tamma/scrum-master`)

**Stories:** 6-1 through 6-10 (10 stories)

[Detailed Breakdown](Epic-6-Context-Knowledge)

---

### Epic 7: Autonomous Mentorship Workflow

**Goal:** AI-powered mentorship workflow guiding developers through story implementation using a 28-state state machine.

**Key Deliverables:**
- 28-state mentorship state machine in ELSA (.NET/C#)
- 12+ ELSA activities (assessment, context gathering, Claude analysis, monitoring, blocker diagnosis, quality gates, code review, merge)
- TypeScript bridge layer (`@tamma/orchestrator` ElsaClient)
- 9 code-first ELSA sub-workflows (LLM Call, Testing, Assessment, Context, Code Review, Blocker, TDD, Debugging, Main Mentorship)

**Stories:** 7-1 through 7-10, 7-1A through 7-1I (19 stories)

[Detailed Breakdown](Epic-7-Mentorship)

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

[Detailed Breakdown](Epic-9-Agent-Management)

---

### Epic 10: Engine Core -- Workflow-Driven Architecture

**Goal:** Refactor the engine from a hardcoded state machine into a workflow-driven orchestration service with ELSA as the workflow provider.

**Key Deliverables:**
- Engine brain with static workflow (`@tamma/orchestrator`)
- 20+ code-first ELSA workflows (ADL orchestrator, single issue cycle, LLM call, TDD, CI retry, etc.)
- Workflow engine TypeScript bridge
- ELSA integration through `IWorkflowProvider` abstraction

**Stories:** 10-1 through 10-8 (8 stories)

[Detailed Breakdown](Epic-10-Engine-Core)

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

**Stories:** 15-1 (1 story)

---

### Epic 16: Unified Auth, User Management & Admin

**Goal:** Single GitHub OAuth flow across all services with user management and RBAC.

**Key Deliverables:**
- GitHub OAuth SSO via oauth2-proxy (Dashboard, ELSA Studio, OpenSearch Dashboards)
- User management API with invite flow and role assignment (`@tamma/api`)
- Admin dashboard with system health, user management, API keys, quick links (`@tamma/dashboard`)
- Cross-service navigation header
- RBAC enforcement (member, admin, owner) at API and nginx levels

**Stories:** 16-1 through 16-5 (5 stories)

---

## Planned / In-Progress Epics

### Epic 2: Autonomous Development Loop

**Goal:** Implement the 14-step autonomous development loop.

**Stories:** 2-1 through 2-16 (16 stories)
- Core loop: Issue selection, context analysis, plan generation, branch creation, TDD, code generation, refactoring, PR creation, status monitoring, merge, auto-next
- Advanced: Intelligent provider selection, prompt engineering, issue decomposition, task dependency mapping, incremental sequencing

---

### Epic 3: Quality Gates & Intelligence

**Goal:** Build automation, test execution, CI/CD integration with retry limits and mandatory escalation.

**Stories:** 3-1 through 3-12 (12 stories)
- Build/test automation with retry logic
- Mandatory escalation workflow
- Research capability, clarifying questions, ambiguity detection
- Static analysis and security scanning integration
- Agent performance monitoring, cost-aware AI usage, task complexity assessment

---

### Epic 4: Event Sourcing & Audit Trail

**Goal:** CQRS event sourcing for complete audit trail.

**Stories:** 4-1 through 4-8 (8 stories)
- Event schema design and store backend
- Event capture for all operations (issues, AI interactions, code changes, approvals)
- Event query API for time-travel debugging
- Black-box replay for debugging

---

### Epic 5: Observability Dashboard & Docs

**Goal:** Real-time observability and comprehensive documentation.

**Status:** Partially implemented -- dashboard exists with settings/admin/knowledge-base pages but not all stories complete.

**Stories:** 5-1 through 5-10 (14 stories)
- Dashboard scaffolding and routing (partially done)
- Event trail visualization
- System health and development velocity dashboards
- Alert system, feedback collection
- Documentation (installation, usage, API reference, website)
- Alpha release preparation

---

### Epic 8: Distribution & Installation

**Goal:** Three-tier distribution: npm, standalone binary, Docker full-stack.

**Stories:** 8-1 through 8-8 (8 stories)
- Tier 1: esbuild bundle + npm publish CI/CD
- Tier 2: Standalone binary (Bun) + install scripts + auto-update
- Tier 3: Docker Compose full stack + CLI integration

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

**Stories:** 18-1 through 18-5 (5 stories)
- User registration with email verification
- Login and session management
- Organization/tenant creation
- GitHub App installation onboarding
- User-facing dashboard shell

---

### Epic 19: GitHub App Agent Dispatch

**Goal:** Orchestrate agents on user's GitHub Actions runners.

**Stories:** 19-1 through 19-5 (5 stories)
- Agent dispatch via `workflow_dispatch` events
- Runner monitoring and result collection
- Execution isolation and security

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

**Status:** Marketing site exists at `apps/marketing-site/` (Cloudflare Workers), user dashboard partially implemented.

**Stories:** 21-1 through 21-5 (5 stories)
- Marketing landing page, pricing page, documentation site
- User dashboard: repos, runs, settings, billing

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

## Timeline Visualization

```
Phase 1 (Completed):
  Epic 1   (Foundation)              [COMPLETED]
  Epic 1.5 (Infrastructure)         [COMPLETED]
  Epic 9   (Multi-Agent Management)  [COMPLETED]

Phase 2 (Completed):
  Epic 6   (Context & Knowledge)     [COMPLETED]
  Epic 7   (Mentorship Workflow)     [COMPLETED]
  Epic 10  (Engine Core)             [COMPLETED]

Phase 3 (Completed):
  Epic 11  (Security Hardening)      [COMPLETED]
  Epic 12  (Agentic Tool Loop)       [COMPLETED]
  Epic 13  (Workflow Decomposition)  [COMPLETED]
  Epic 14  (Custom ELSA Studio)      [COMPLETED]
  Epic 15  (Log Aggregation)         [COMPLETED]
  Epic 16  (Unified Auth & Admin)    [COMPLETED]

Phase 4 (Planned):
  Epic 2   (Autonomous Loop)         [Planned]
  Epic 3   (Quality Gates)           [Planned]
  Epic 4   (Event Sourcing)          [Planned]
  Epic 5   (Observability/Docs)      [Partially Implemented]

Phase 5 (Planned - SaaS):
  Epic 17  (Multi-Tenancy)           [Planned]
  Epic 18  (End-User Auth)           [Planned]
  Epic 19  (Agent Dispatch)          [Planned]
  Epic 20  (Billing)                 [Planned]
  Epic 21  (Marketing/Dashboard)     [Partially Implemented]
  Epic 22  (CLI Preservation)        [Planned]
```

---

## Key Success Metrics

- **Autonomous Completion Rate:** 70%+ (target)
- **Time to Resolution:** <24 hours for most issues
- **Quality Gate Pass Rate:** 95%+ (mandatory escalation for failures)
- **System Uptime:** 99.5%+ (orchestrator mode)
- **Test Coverage:** 80%+ line coverage, 75%+ branch coverage

---

_For detailed technical specifications, see the tech-spec documents in the [docs/](https://github.com/meywd/tamma/tree/main/docs) directory._
