# Welcome to the Tamma Wiki

**Tamma** is an autonomous development platform that maintains itself. This wiki provides comprehensive documentation for understanding, contributing to, and using Tamma.

## Quick Links

- [Project Roadmap](Roadmap) - All 25 epics with timeline and status
- [Architecture](Architecture) - System architecture overview
- [Epics](Epics) - All 25 epics organized by phase
- [Epic 1: Foundation](Epics/Epic-1-Foundation) - Core infrastructure (AI providers, Git platforms, CLI)
- [Epic 1.5: Infrastructure & Deployment](Epics/Epic-1.5-Infrastructure) - Docker, CI/CD, SaaS coordinator
- [Epic 6: Context & Knowledge Management](Epics/Epic-6-Context-Knowledge) - Vector DB, RAG, MCP, cost monitoring, permissions, knowledge base
- [Epic 7: Mentorship Workflow](Epics/Epic-7-Mentorship) - ELSA workflow activities for autonomous mentorship
- [Epic 9: Agent Management](Epics/Epic-9-Agent-Management) - Config-driven multi-agent system
- [Epic 10: Engine Core](Epics/Epic-10-Engine-Core) - Workflow-driven architecture
- [Epic 11-14: ELSA Hardening](Epics/Epic-11-14-ELSA) - Security, agentic tool loop, workflow decomposition, custom studio
- [Epic 23: System Monitoring](Epics/Epic-23-System-Monitoring) - Production-grade monitoring & observability dashboard
- [Epic 24: Voice Conversation](Epics/Epic-24-Voice-Conversation) - Realtime voice conversation with orchestrator
- [Epic 25: Wiki Site](Epics/Epic-25-Wiki-Site) - Custom documentation site on Cloudflare Workers
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
**GitHub Issues:** 101 closed as completed

### Recent Changes

- **Epic 25** added -- Custom Wiki Site on Cloudflare Workers (Astro Starlight, wiki.tamma.dev / wiki.its-done.dev)
- **Video production plans** created for two explainer videos (ELI5 ~75s, Deep Dive ~4 min)
- **All scene images generated** at 4K Pro (Gemini 3 Pro, 5504x3072 16:9) -- 79 images across 28 scenes
- **ElevenLabs TTS narration** generated for ELI5 video
- **Production plans finalized** with Runway 4.5 (via Freepik API) + ElevenLabs TTS pipeline
- **CodeQL security alerts fixed** -- 9 files patched (log forging, incomplete URL sanitization, SQL wildcard escaping, API key validation)
- **Leaked Gemini API key** removed and added to .gitignore
- **Story 16-6** (ELSA Studio Auto-Login) implemented -- bypass internal ELSA Identity login page
- **OpenSearch log shipping** fixed (3 bugs: ESM `require` in Node.js, wrong Serilog sink version, Fastify logger instance mismatch)

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
| Epic 15 | Observability | OpenSearch log aggregation (3 bug fixes for ESM/Serilog/Fastify) |
| Epic 16 | Unified Auth & Admin | GitHub OAuth SSO (oauth2-proxy removed), user management, admin panel, RBAC, ELSA Studio auto-login |

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
| Epic 23 | System Monitoring & Observability Dashboard | Planned (26 task plans ready) |
| Epic 24 | Realtime Voice Conversation | Partially implemented (24 task plans ready) |
| Epic 25 | Documentation & Wiki Site | Planned (Astro Starlight on Cloudflare Workers) |

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
- [Stories](https://github.com/meywd/tamma/tree/main/docs/stories) - User story documentation (25 epics, 220+ stories, 50+ task plans)

---

_Last updated: 2026-03-30 | Maintained by: meywd_
