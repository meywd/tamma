# User Stories Index

This page provides an index of all user stories across all 27 epics. Each story links to its documentation in the repository.

## Story Structure

Each story includes:
- **Status:** planned, in-progress, done
- **Acceptance Criteria:** Measurable success conditions
- **Tasks/Subtasks:** Detailed checklist of work items
- **Dev Notes:** Context, architecture patterns, references

---

## Epic 1: Foundation & Core Infrastructure (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 1-0 | AI Provider Strategy Research | Done |
| 1-1 | AI Provider Interface Definition | Done |
| 1-2 | Claude Code Provider Implementation | Done |
| 1-3 | Provider Configuration Management | Done |
| 1-4 | Git Platform Interface Definition | Done |
| 1-5 | GitHub Platform Implementation | Done |
| 1-6 | GitLab Platform Implementation | Story ready |
| 1-7 | Git Platform Configuration Management | Done |
| 1-8 | Hybrid Orchestrator/Worker Architecture Design | Done |
| 1-9 | Basic CLI Scaffolding with Mode Selection | Done |
| 1-10 | Additional AI Provider Implementations | In Progress |
| 1-11 | Additional Git Platform Implementations | Ready for Dev |
| 1-12 | Initial Marketing Website | Done |
| 1-13 | Agent Customization System | In Progress |
| 1-14 | Performance Impact Analysis | Ready for Dev |

[Detailed Breakdown](Epics/Epic-1-Foundation) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-1)

---

## Epic 1.5: Infrastructure & Deployment (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 1.5-1 | Core Engine Separation | Done |
| 1.5-2 | CLI Mode Enhancement | Done |
| 1.5-3 | Service Mode Implementation | Done |
| 1.5-4 | Web Server & API | Done |
| 1.5-5 | Docker Packaging | Done |
| 1.5-6 | Webhook Integration | Done |
| 1.5-7 | System Configuration Management | Done |
| 1.5-8 | NPM Package Publishing | Done |
| 1.5-9 | Binary Releases & Installers | Done |
| 1.5-10 | Kubernetes Deployment | In Progress |
| 1.5-11 | GitHub App Authentication | Done |
| 1.5-12 | SaaS Coordinator | Done |
| 1.5-13 | GitHub Actions Worker Mode | Done |
| 1.5-14 | Multi-Tenant Task Queue & Webhook Routing | Done |
| 1.5-15 | SaaS API Key Provisioning | Done |

[Detailed Breakdown](Epics/Epic-1.5-Infrastructure) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-1.5)

---

## Epic 2: Autonomous Development Loop (Near Complete -- 13/20 Done)

| Story | Title | Status |
|-------|-------|--------|
| 2-1 | Issue Selection with Filtering | Done |
| 2-2 | Issue Context Analysis | Done |
| 2-3 | Development Plan Generation with Approval Checkpoint | Done |
| 2-4 | Git Branch Creation | Done |
| 2-5 | Test-First Development - Write Failing Tests | Done |
| 2-6 | Implementation Code Generation | Done |
| 2-7 | Code Refactoring Pass | Done |
| 2-8 | Pull Request Creation | Done |
| 2-9 | PR Status Monitoring | Done |
| 2-10 | PR Merge with Completion Checkpoint | Done |
| 2-11 | Auto-Next Issue Selection | Done |
| 2-12 | Intelligent Provider Selection | Done |
| 2-13 | Prompt Engineering Optimization | Done |
| 2-14 | Issue Decomposition Engine | Ready for Dev |
| 2-15 | Task Dependency Mapping | Ready for Dev |
| 2-16 | Incremental Task Sequencing | Ready for Dev |
| 2-17 | Context Gathering Redesign | Drafted |
| 2-18 | Plan Generation Redesign | Drafted |
| 2-19 | Plan Review Redesign | Drafted |
| 2-20 | Priority-Based Work Item Selection | Drafted |

[Detailed Breakdown](Epics/Epic-2-Autonomous-Loop) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-2)

---

## Epic 3: Quality Gates & Intelligence (Near Complete -- 8/12 Done)

| Story | Title | Status |
|-------|-------|--------|
| 3-1 | Build Automation Gate Implementation | Done |
| 3-2 | Test Execution Gate Implementation | Done |
| 3-3 | Escalation Workflow Implementation | Done |
| 3-4 | Research Workflow Implementation | Drafted |
| 3-5 | Clarifying Questions Workflow Implementation | Drafted |
| 3-6 | Ambiguity Scoring Implementation | Drafted |
| 3-7 | Design Proposal Workflow Implementation | Drafted |
| 3-8 | Static Analysis Gate Implementation | Done |
| 3-9 | Security Scanning Gate Implementation | Done |
| 3-10 | Agent Performance Monitoring | Done |
| 3-11 | Cost-Aware AI Usage | Done |
| 3-12 | Task Complexity Assessment | Done |

[Detailed Breakdown](Epics/Epic-3-Quality-Gates) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-3)

---

## Epic 4: Event Sourcing & Audit Trail (Near Complete -- 6/8 Done)

| Story | Title | Status |
|-------|-------|--------|
| 4-1 | Event Schema Design | Done |
| 4-2 | Event Store Backend Selection | In Progress |
| 4-3 | Event Capture - Issue Selection & Analysis | Done |
| 4-4 | Event Capture - AI Provider Interactions | Done |
| 4-5 | Event Capture - Code Changes & Git Operations | Done |
| 4-6 | Event Capture - Approvals & Escalations | Done |
| 4-7 | Event Query API for Time-Travel | Done |
| 4-8 | Black-Box Replay for Debugging | Drafted |

[Detailed Breakdown](Epics/Epic-4-Event-Sourcing) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-4)

---

## Epic 5: Observability Dashboard & Documentation (Partially Implemented -- 4/14 Done)

Dashboard exists at `@tamma/dashboard` with admin, settings, and knowledge base pages. Logging, health dashboard, integration tests, and alpha release prep are done.

| Story | Title | Status |
|-------|-------|--------|
| 5-1 | Structured Logging Implementation | Done |
| 5-2 | Metrics Collection Infrastructure | Drafted |
| 5-3 | Real-Time Dashboard - System Health | Done |
| 5-4 | Real-Time Dashboard - Development Velocity | Drafted |
| 5-5 | Event Trail Exploration UI | In Progress |
| 5-6 | Alert System for Critical Issues | In Progress |
| 5-7 | Feedback Collection System | In Progress |
| 5-8 | Integration Testing Suite | Done |
| 5-9a | Installation & Setup Documentation | Drafted |
| 5-9b | Usage & Configuration Documentation | Drafted |
| 5-9c | API Reference Documentation | Backlog |
| 5-9d | Full Documentation Website | Backlog |
| 5-9e | Video Walkthrough | Backlog |
| 5-10 | Alpha Release Preparation | Done |

[Detailed Breakdown](Epics/Epic-5-Observability) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-5)

---

## Epic 6: Context & Knowledge Management (Near Complete -- 10/11 Done)

| Story | Title | Package | Status |
|-------|-------|---------|--------|
| 6-1 | Codebase Indexer Implementation | intelligence | Done |
| 6-2 | Vector Database Integration | intelligence | In Progress |
| 6-3 | RAG Pipeline Implementation | intelligence | Done |
| 6-4 | MCP Client Integration | mcp-client | Done |
| 6-5 | Context Aggregator Service | intelligence | Done |
| 6-6 | Knowledge Base Management UI | dashboard, api | Done |
| 6-7 | LLM Cost Monitoring & Reporting | cost-monitor | Done |
| 6-8 | Agent Permissions System | gates | Done |
| 6-9 | Agent Knowledge Base | intelligence | Done |
| 6-10 | Scrum Master Task Loop | scrum-master | Done |
| 6-11 | Context API Wiring | api | Done |

[Detailed Breakdown](Epics/Epic-6-Context-Knowledge) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-6)

---

## Epic 7: Autonomous Mentorship Workflow (Near Complete)

| Story | Title | Status |
|-------|-------|--------|
| 7-1 | Mentorship State Machine Core | Done |
| 7-2 | Skill Assessment Activity | Done |
| 7-3 | Context Gathering Activity | Done |
| 7-4 | Claude Analysis Activity | Done |
| 7-5 | Plan Decomposition Activity | Done |
| 7-6 | Progress Monitoring & Pattern Detection | Done |
| 7-7 | Blocker Diagnosis & Resolution | Done |
| 7-8 | Quality Gate & Auto-Fix Pipeline | Done |
| 7-9 | Code Review & Merge Workflow | Done |
| 7-10 | TypeScript Engine Bridge & Session API | Done |
| 7-1A | Main Mentorship Workflow (Code-First) | Done |
| 7-1B | LLM Call Sub-Workflow | Done |
| 7-1C | Testing Sub-Workflow | Done |
| 7-1D | Code Review Sub-Workflow | Done |
| 7-1E | Assessment Sub-Workflow | Done |
| 7-1F | Context Gathering Sub-Workflow | Done |
| 7-1G | Blocker Diagnosis Sub-Workflow | Done |
| 7-1H | TDD Sub-Workflow | In Progress |
| 7-1I | Debugging Sub-Workflow | Done |

[Detailed Breakdown](Epics/Epic-7-Mentorship) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-7)

---

## Epic 8: Distribution & Installation (Completed)

| Story | Title | Tier | Status |
|-------|-------|------|--------|
| 8-1 | esbuild Bundle & Package Structure | Tier 1 (npm) | Done |
| 8-2 | npm Publish CI/CD Pipeline | Tier 1 (npm) | Done |
| 8-3 | Standalone Binary Compilation | Tier 2 (binary) | Done |
| 8-4 | Install Scripts & GitHub Releases | Tier 2 (binary) | Done |
| 8-5 | Auto-Update & Package Manager Distribution | Tier 2 (binary) | Done |
| 8-6 | TypeScript & Dashboard Dockerfiles | Tier 3 (Docker) | Done |
| 8-7 | Docker Compose Full Stack | Tier 3 (Docker) | Done |
| 8-8 | Docker CI/CD & CLI Integration | Tier 3 (Docker) | Done |

[Detailed Breakdown](Epics/Epic-8-Distribution) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-8)

---

## Epic 9: Config-Driven Multi-Agent Management (Completed)

| Story | Title | Package(s) | Status |
|-------|-------|-----------|--------|
| 9-1 | Configuration Schema | shared, cli | Done |
| 9-2 | Provider Diagnostics | shared, providers | Done |
| 9-3 | Provider Health Tracker | providers | Done |
| 9-4 | Agent Provider Factory | providers | Done |
| 9-5 | Provider Chain | providers | Done |
| 9-6 | Agent Prompt Registry | providers | Done |
| 9-7 | Content Sanitization | shared | Done |
| 9-8 | Role-Based Agent Resolver | providers | Done |
| 9-9 | Engine Integration | orchestrator | Done |
| 9-10 | CLI Wiring | cli | Done |
| 9-11 | Diagnostics Queue & MCP Interceptors | shared, mcp-client | Done |

[Detailed Breakdown](Epics/Epic-9-Agent-Management) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-9)

---

## Epic 10: Engine Core -- Workflow-Driven Architecture (Drafted, 10-9 In Progress)

Engine exists as an imperative state machine (`packages/orchestrator/src/engine.ts`) but these stories define the target agentic architecture. Story 10-9 (TammaActivity base class) is actively being implemented.

| Story | Title | Status |
|-------|-------|--------|
| 10-1 | Engine Static Workflow & Brain | Drafted |
| 10-2 | Comprehensive Event Catalog & Typed Schema | Drafted |
| 10-3 | Event Store -- PostgreSQL/Emmett Implementation | Drafted |
| 10-4 | Smart Queue with State-Based Deduplication | Drafted |
| 10-5 | Workflow Provider Abstraction & ELSA Integration | Drafted |
| 10-6 | Input Channel Unification | Drafted |
| 10-7 | Event Store Security & Sanitization Pipeline | Drafted |
| 10-8 | State Reconstruction from Event Stream | Drafted |
| 10-9 | TammaActivity Base Class and Workflow Event Emission | In Progress |

[Detailed Breakdown](Epics/Epic-10-Engine-Core) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-10)

---

## Epic 11: Security Hardening (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 11-1 | ContentSanitizer C# Port | Done |
| 11-2 | LLM Input Sanitization | Done |
| 11-3 | Tool Call Validation | Done |
| 11-4 | Output Sanitization & Prompt Hardening | Done |
| 11-5 | Fail-Closed Guards & Provider Allowlist | Done |

[Detailed Breakdown](Epics/Epic-11-Security) | [Combined](Epics/Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-11)

---

## Epic 12: Agentic Tool Loop (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 12-1 | Tool Executor Interface & Registry | Done |
| 12-2 | Agentic Tool Loop in CallLlm | Done |
| 12-3 | Context Compaction | Done |
| 12-4 | Streaming & Parallel Tools | Done |
| 12-5 | Prompt Registry API | Done |

[Detailed Breakdown](Epics/Epic-12-Tool-Loop) | [Combined](Epics/Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-12)

---

## Epic 13: Workflow Decomposition (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 13-1 | TDD Debug Retry Sub-Workflow | Done |
| 13-2 | CI Debug Retry Sub-Workflow | Done |
| 13-3 | Consolidate Finish Sequences | Done |

[Detailed Breakdown](Epics/Epic-13-Workflow-Decomposition) | [Combined](Epics/Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-13)

---

## Epic 14: Custom ELSA Studio (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 14-1 | Studio Blazor WASM Scaffold | Done |
| 14-2 | Studio Docker & CI | Done |
| 14-3 | Studio Custom UI Hints | Done |

[Detailed Breakdown](Epics/Epic-14-ELSA-Studio) | [Combined](Epics/Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-14)

---

## Epic 15: Observability & Log Aggregation (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 15-1 | OpenSearch Log Aggregation | Done |
| 15-2 | Structured Logging Gap Remediation | Planned |
| 15-3 | Advanced Dashboards & Alerting Tuning | Planned |

[Detailed Breakdown](Epics/Epic-15-Log-Aggregation) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-15)

---

## Epic 16: Unified Auth, User Management & Admin (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 16-1 | GitHub OAuth Unified Auth (oauth2-proxy removed) | Done |
| 16-2 | User Management API | Done |
| 16-3 | Admin Dashboard | Done |
| 16-4 | Unified Navigation | Done |
| 16-5 | Role-Based Access Control | Done |
| 16-6 | ELSA Studio Auto-Login | Done |

[Detailed Breakdown](Epics/Epic-16-Auth-Admin) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-16)

---

## Epic 17: Multi-Tenancy Foundation (Drafted)

| Story | Title | Status |
|-------|-------|--------|
| 17-1 | Tenant Model & Database Schema | Planned |
| 17-2 | Row-Level Security & Tenant Isolation | Planned |
| 17-3 | Tenant-Scoped Event Store | Planned |
| 17-4 | Tenant-Scoped Workflow Instances | Planned |
| 17-5 | API Tenant Context Middleware | Planned |

[Detailed Breakdown](Epics/Epic-17-Multi-Tenancy) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-17)

---

## Epic 18: End-User Auth & Registration (Partially Implemented)

| Story | Title | Status |
|-------|-------|--------|
| 18-1 | User Registration & Email Verification | Drafted |
| 18-2 | User Login & Session Management | In Progress |
| 18-3 | Organization/Tenant Creation | Drafted |
| 18-4 | GitHub App Installation Onboarding | Done |
| 18-5 | User-Facing Dashboard Shell | In Progress |

[Detailed Breakdown](Epics/Epic-18-User-Auth) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-18)

---

## Epic 19: GitHub App Agent Dispatch (Partially Implemented)

| Story | Title | Status |
|-------|-------|--------|
| 19-1 | Tamma Agent GitHub Actions Workflow Template | Done |
| 19-2 | Workflow Dispatch from ELSA | Drafted |
| 19-3 | Agent Execution Monitoring | Drafted |
| 19-4 | Result Collection | In Progress |
| 19-5 | CLI / SaaS Mode Abstraction (IAgentExecutor) | Drafted |

[Detailed Breakdown](Epics/Epic-19-Agent-Dispatch) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-19)

---

## Epic 20: Billing & Payments (Drafted)

| Story | Title | Status |
|-------|-------|--------|
| 20-1 | Stripe Integration & Plan Model | Planned |
| 20-2 | Subscription Management | Planned |
| 20-3 | Usage Metering | Planned |
| 20-4 | Usage Limits Enforcement | Planned |
| 20-5 | Billing Dashboard | Planned |

[Detailed Breakdown](Epics/Epic-20-Billing) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-20)

---

## Epic 21: Marketing Site & User Dashboard (Partially Implemented)

Marketing site exists at `apps/marketing-site/` with Midnight Ocean redesign. Wiki site at `apps/wiki-site/`. User dashboard not yet implemented.

| Story | Title | Status |
|-------|-------|--------|
| 21-1 | Marketing Landing Page | Done |
| 21-2 | Pricing Page + Stripe Checkout | Drafted |
| 21-3 | Documentation Site | In Progress |
| 21-4 | User Dashboard -- Repos & Workflow Runs | Drafted |
| 21-5 | User Dashboard -- Settings & Billing | Drafted |

[Detailed Breakdown](Epics/Epic-21-Marketing-Dashboard) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-21)

---

## Epic 22: CLI Mode Preservation (Drafted)

| Story | Title | Status |
|-------|-------|--------|
| 22-1 | Agent Executor Abstraction | Drafted |
| 22-2 | CLI Standalone Workflow Engine | Drafted |
| 22-3 | Optional Cloud Sync | Drafted |
| 22-4 | Feature Parity Matrix | Drafted |
| 22-5 | CLI Docker Installation | Drafted |

[Detailed Breakdown](Epics/Epic-22-CLI-Standalone) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-22)

---

## Epic 23: System Monitoring & Observability Dashboard (Drafted -- 26 Task Plans)

All 12 stories now have detailed implementation-ready task plan breakdowns.

| Story | Title | Task Plans | Status |
|-------|-------|------------|--------|
| 23-1 | System Health Dashboard (Overview) | 2 | Planned |
| 23-2 | Agent Monitor (Realtime) | 2 | Planned |
| 23-3 | Event Store Explorer | 2 | Planned |
| 23-4 | Configuration Audit | 2 | Planned |
| 23-5 | Workflow Monitor | 2 | Planned |
| 23-6 | Provider Diagnostics (Deep) | 2 | Planned |
| 23-7 | Log Explorer (OpenSearch) | 2 | Planned |
| 23-8 | Infrastructure Monitor | 2 | Planned |
| 23-9 | Knowledge Base Monitor | 2 | Planned |
| 23-10 | Security & Access Audit | 2 | Planned |
| 23-11 | Monitoring API Foundation | 3 | Planned |
| 23-12 | Dashboard Navigation & Layout | 3 | Planned |

[Detailed Breakdown](Epics/Epic-23-System-Monitoring) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-23)

---

## Epic 24: Realtime Voice Conversation (Drafted -- 24 Task Plans)

Voice as a first-class input/output mode for the Tamma orchestrator. Research complete; all implementation stories have detailed task plan breakdowns.

| Story | Title | Task Plans | Status |
|-------|-------|------------|--------|
| 24-0 | Voice API Research | -- | Done |
| 24-1 | WebSocket Foundation | 5 | Planned |
| 24-2 | Speech-to-Text Integration | 4 | Planned |
| 24-3 | Text-to-Speech Integration | 4 | Planned |
| 24-4 | Intent Classification + Engine Integration | 3 | Planned |
| 24-5 | Dashboard Voice UI | 4 | Planned |
| 24-6 | Hardening + Production Readiness | 4 | Planned |

[Detailed Breakdown](Epics/Epic-24-Voice-Conversation) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-24)

---

## Epic 25: Documentation & Wiki Site (Completed)

Custom wiki site deployed as Vite + React + React Router SPA on Cloudflare Workers at wiki.tamma.dev with React Flow interactive diagrams.

| Story | Title | Status |
|-------|-------|--------|
| 25-1 | Custom Wiki Site | Done |

[Detailed Breakdown](Epics/Epic-25-Wiki-Site) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-25)

---

## Epic 26: Project Management & Triage (Partially Implemented)

Autonomous project management workflows integrating with the ADL Orchestrator's priority-based work item selection.

| Story | Title | Status |
|-------|-------|--------|
| 26-1 | Issue Triage Workflow | In Progress |
| 26-2 | Scrum Management Workflow | Drafted |
| 26-3 | Release Management Workflow | Drafted |
| 26-4 | Priority Configuration System | Drafted |

[Detailed Breakdown](Epics/Epic-26-Project-Management) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-26)

---

## Story Workflow

Stories progress through the following stages:

1. **planned** -- Story documented with acceptance criteria, ready for development
2. **in-progress** -- Developer actively working on story
3. **review** -- Code review in progress
4. **done** -- All acceptance criteria met, merged to main

---

## Story Statistics

| Category | Count |
|----------|-------|
| Total stories across all epics | ~232 |
| Stories done | 117 |
| Stories in progress | 22 |
| Stories drafted | 62 |
| Epics completed | 9 (8, 9, 11, 12, 13, 14, 15, 16, 25) |
| Epics near complete | 7 (1, 1.5, 2, 3, 4, 6, 7) |
| Epics partially implemented | 5 (5, 18, 19, 21, 26) |
| Epics drafted | 6 (10, 17, 20, 22, 23, 24) |
| Detailed task plans (Epic 23) | 26 |
| Detailed task plans (Epic 24) | 24 |
| TypeScript packages with code | 14 |
| C# ELSA activities | 70+ |
| ELSA code-first workflows | 30 |

---

For more details, see [Contributing Guidelines](Contributing).
