# Welcome to the Tamma Wiki

**Tamma** is an autonomous development platform that maintains itself. This wiki provides comprehensive documentation for understanding, contributing to, and using Tamma.

## Quick Links

- [Project Roadmap](Roadmap) - All 26 epics with timeline and status
- [Architecture](Architecture) - System architecture overview
- [Epics](Epics) - All 26 epics organized by phase
- [Epic 1: Foundation](Epics/Epic-1-Foundation) - Core infrastructure (AI providers, Git platforms, CLI)
- [Epic 1.5: Infrastructure & Deployment](Epics/Epic-1.5-Infrastructure) - Docker, CI/CD, SaaS coordinator
- [Epic 2: Autonomous Loop](Epics/Epic-2-Autonomous-Loop) - 14-step autonomous development loop (13/16 done)
- [Epic 6: Context & Knowledge Management](Epics/Epic-6-Context-Knowledge) - Vector DB, RAG, MCP, cost monitoring, permissions, knowledge base
- [Epic 7: Mentorship Workflow](Epics/Epic-7-Mentorship) - ELSA workflow activities for autonomous mentorship
- [Epic 9: Agent Management](Epics/Epic-9-Agent-Management) - Config-driven multi-agent system
- [Epic 10: Engine Core](Epics/Epic-10-Engine-Core) - Workflow-driven architecture
- [Epic 11-14: ELSA Hardening](Epics/Epic-11-14-ELSA) - Security, agentic tool loop, workflow decomposition, custom studio
- [Epic 23: System Monitoring](Epics/Epic-23-System-Monitoring) - Production-grade monitoring & observability dashboard
- [Epic 24: Voice Conversation](Epics/Epic-24-Voice-Conversation) - Realtime voice conversation with orchestrator
- [Epic 25: Wiki Site](Epics/Epic-25-Wiki-Site) - Custom documentation site on Cloudflare Workers
- [Workflows](Workflows) - All 20 ELSA workflows with flow diagrams and dependency map
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
**Last Audit:** 2026-04-01 (sprint-status.yaml audited against codebase for all 26 epics)
**Audit Results:** 115 done, 20 in-progress, 56 drafted

### Recent Progress

- **PR #325 merged** -- Workflow enhancements, 10 bug fixes, wiki site rewrite as Vite+React SPA, Midnight Ocean marketing redesign
- **PR #326 created** -- CodeQL ReDoS fix in EpicDetailPage regex
- **New stories added** -- 2-17, 2-18, 2-19, 3-13, 7-8 overhaul, 7-11, 7-12, 11-6, 12-5, 12-6
- **Deep audit completed** -- all 26 epics verified against codebase; Epic 3, 4, 5, 8 statuses significantly corrected
- **Epic 8 completed** -- All 8 distribution stories done (esbuild, npm publish, binary, Docker, Homebrew)
- **Epic 3 near complete** -- 8/12 quality gate stories done (build, test, escalation, static analysis, security, monitoring, cost, complexity)
- **Epic 4 near complete** -- 6/8 event sourcing stories done; PostgreSQL backend in progress
- **Wiki site rewritten** -- Vite + React + React Router SPA with React Flow diagrams, deployed to wiki.tamma.dev
- **Marketing site redesigned** -- Midnight Ocean theme applied as main landing page
- **CodeQL security alerts fixed** -- ReDoS vulnerability patched

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
| Epic 2 | Autonomous Development Loop | 13/16 | Issue decomposition, task dependencies, sequencing (ready for dev) |
| Epic 3 | Quality Gates & Intelligence | 8/12 | 4 drafted (research, clarifying questions, ambiguity, design proposals) |
| Epic 4 | Event Sourcing & Audit Trail | 6/8 | PostgreSQL backend in progress, replay drafted |
| Epic 6 | Context & Knowledge Management | 9/10 | Vector DB stubs (Pinecone, Qdrant, Weaviate) in progress |
| Epic 7 | Mentorship Workflow | 8/9 core | TDD sub-workflow in progress (test execution mocked) |

### Partially Implemented (4)

| Epic | Name | Done | In Progress | Drafted |
|------|------|------|-------------|---------|
| Epic 5 | Observability Dashboard & Docs | 4 | 3 | 7 |
| Epic 18 | End-User Auth & Registration | 1 | 2 | 2 |
| Epic 19 | GitHub App Agent Dispatch | 1 | 1 | 3 |
| Epic 21 | Marketing Site & User Dashboard | 1 | 1 | 3 |

### Planned / Drafted

| Epic | Name | Stories | Status |
|------|------|---------|--------|
| Epic 10 | Engine Core | 8 | Drafted (engine exists but stories define new architecture) |
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
- [Stories](https://github.com/meywd/tamma/tree/main/docs/stories) - User story documentation (26 epics, 220+ stories, 50+ task plans)

---

_Last updated: 2026-04-01 | Maintained by: meywd_
