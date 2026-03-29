# Welcome to the Tamma Wiki

**Tamma** is an autonomous development platform that maintains itself. This wiki provides comprehensive documentation for understanding, contributing to, and using Tamma.

## Quick Links

- [Project Roadmap](Roadmap) - All 22 epics with timeline and status
- [Architecture](Architecture) - System architecture overview
- [Epic 1: Foundation](Epic-1-Foundation) - Core infrastructure (AI providers, Git platforms, CLI)
- [Epic 1.5: Infrastructure & Deployment](Epic-1.5-Infrastructure) - Docker, CI/CD, SaaS coordinator
- [Epic 6: Context & Knowledge Management](Epic-6-Context-Knowledge) - Vector DB, RAG, MCP, cost monitoring, permissions, knowledge base
- [Epic 7: Mentorship Workflow](Epic-7-Mentorship) - ELSA workflow activities for autonomous mentorship
- [Epic 9: Agent Management](Epic-9-Agent-Management) - Config-driven multi-agent system
- [Epic 10: Engine Core](Epic-10-Engine-Core) - Workflow-driven architecture
- [Epic 11-14: ELSA Hardening](Epic-11-14-ELSA) - Security, agentic tool loop, workflow decomposition, custom studio
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

### Completed Epics

| Epic | Name | Key Deliverables |
|------|------|------------------|
| Epic 1 | Foundation & Core Infrastructure | AI providers (Claude, OpenCode, OpenRouter, Zen MCP), GitHub platform, CLI |
| Epic 1.5 | Infrastructure & Deployment | Docker Compose stack, CI/CD pipelines, GitHub App, SaaS coordinator |
| Epic 6 | Context & Knowledge Management | Codebase indexer, vector DB, RAG pipeline, MCP client, cost monitor, permissions, knowledge base, scrum master |
| Epic 7 | Mentorship Workflow | 28-state mentorship workflow, 12+ ELSA activities, TypeScript bridge |
| Epic 9 | Agent Management | Config-driven multi-agent, circuit breakers, diagnostics, security layer |
| Epic 10 | Engine Core | Workflow-driven engine, ELSA integration, event store |
| Epic 11 | Security Hardening | C# security pipeline, LLM input/output sanitization, tool validation |
| Epic 12 | Agentic Tool Loop | Multi-turn tool execution, context compaction, streaming |
| Epic 13 | Workflow Decomposition | TDD/CI retry sub-workflows, consolidated finish sequences |
| Epic 14 | Custom ELSA Studio | Custom Blazor WASM studio, Tamma branding, UI hints |
| Epic 15 | Observability | OpenSearch log aggregation |
| Epic 16 | Unified Auth & Admin | GitHub OAuth SSO, user management, admin panel, RBAC |

### In Progress / Planned

| Epic | Name | Status |
|------|------|--------|
| Epic 2 | Autonomous Development Loop | Planned (stories ready) |
| Epic 3 | Quality Gates & Intelligence | Planned (stories ready) |
| Epic 4 | Event Sourcing & Audit Trail | Planned (stories ready) |
| Epic 5 | Observability Dashboard & Docs | Partially implemented (dashboard exists) |
| Epic 8 | Distribution & Installation | Planned (stories ready) |
| Epic 17 | Multi-Tenancy Foundation | Planned (stories ready) |
| Epic 18 | End-User Auth & Registration | Planned (stories ready) |
| Epic 19 | GitHub App Agent Dispatch | Planned (stories ready) |
| Epic 20 | Billing & Payments | Planned (stories ready) |
| Epic 21 | Marketing Site & User Dashboard | Partially implemented (marketing site exists) |
| Epic 22 | CLI Mode Preservation | Planned (stories ready) |

## Getting Started

1. Read the [Architecture](Architecture) overview
2. Review the [Roadmap](Roadmap) to understand the project timeline
3. Check out [Epic 1](Epic-1-Foundation) to see foundational work
4. See [Epic 9](Epic-9-Agent-Management) for the multi-agent system
5. Visit [Contributing](Contributing) to learn how to help

## Documentation

All technical documentation is maintained in the [/docs](https://github.com/meywd/tamma/tree/main/docs) directory:

- [PRD](https://github.com/meywd/tamma/blob/main/docs/PRD.md) - Product requirements
- [Architecture](https://github.com/meywd/tamma/blob/main/docs/architecture.md) - Technical architecture
- [Epics](https://github.com/meywd/tamma/blob/main/docs/epics.md) - Epic breakdown
- [Tech Specs](https://github.com/meywd/tamma/tree/main/docs) - Technical specifications per epic
- [Stories](https://github.com/meywd/tamma/tree/main/docs/stories) - User story documentation (22 epics, 200+ stories)

---

_Last updated: 2026-03-29 | Maintained by: meywd_
