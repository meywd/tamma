# User Stories Index

This page provides an index of all user stories across all 24 epics. Each story links to its documentation in the repository.

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
| 1-10 | Additional AI Provider Implementations | Done |
| 1-11 | Additional Git Platform Implementations | Story ready |
| 1-12 | Initial Marketing Website | Done |
| 1-13 | Agent Customization System | Story ready |
| 1-14 | Performance Impact Analysis | Story ready |

[Detailed Breakdown](Epic-1-Foundation) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-1)

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
| 1.5-8 | NPM Package Publishing | Story ready |
| 1.5-9 | Binary Releases & Installers | Story ready |
| 1.5-10 | Kubernetes Deployment | MVP Optional |
| 1.5-11 | GitHub App Authentication | Done |
| 1.5-12 | SaaS Coordinator | Done |
| 1.5-13 | GitHub Actions Worker Mode | Done |
| 1.5-14 | Multi-Tenant Task Queue & Webhook Routing | Done |
| 1.5-15 | SaaS API Key Provisioning | Done |

[Detailed Breakdown](Epic-1.5-Infrastructure) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-1.5)

---

## Epic 2: Autonomous Development Loop (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 2-1 | Issue Selection with Filtering | Planned |
| 2-2 | Issue Context Analysis | Planned |
| 2-3 | Development Plan Generation with Approval Checkpoint | Planned |
| 2-4 | Git Branch Creation | Planned |
| 2-5 | Test-First Development - Write Failing Tests | Planned |
| 2-6 | Implementation Code Generation | Planned |
| 2-7 | Code Refactoring Pass | Planned |
| 2-8 | Pull Request Creation | Planned |
| 2-9 | PR Status Monitoring | Planned |
| 2-10 | PR Merge with Completion Checkpoint | Planned |
| 2-11 | Auto-Next Issue Selection | Planned |
| 2-12 | Intelligent Provider Selection | Planned |
| 2-13 | Prompt Engineering Optimization | Planned |
| 2-14 | Issue Decomposition Engine | Planned |
| 2-15 | Task Dependency Mapping | Planned |
| 2-16 | Incremental Task Sequencing | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-2)

---

## Epic 3: Quality Gates & Intelligence (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 3-1 | Build Automation with Retry Logic | Planned |
| 3-2 | Test Execution with Retry Logic | Planned |
| 3-3 | Mandatory Escalation Workflow | Planned |
| 3-4 | Research Capability for Unfamiliar Concepts | Planned |
| 3-5 | Clarifying Questions for Ambiguous Requirements | Planned |
| 3-6 | Ambiguity Detection Scoring | Planned |
| 3-7 | Multi-Option Design Proposals | Planned |
| 3-8 | Static Analysis Integration | Planned |
| 3-9 | Security Scanning Integration | Planned |
| 3-10 | Agent Performance Monitoring | Planned |
| 3-11 | Cost-Aware AI Usage | Planned |
| 3-12 | Task Complexity Assessment | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-3)

---

## Epic 4: Event Sourcing & Audit Trail (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 4-1 | Event Schema Design | Planned |
| 4-2 | Event Store Backend Selection | Planned |
| 4-3 | Event Capture - Issue Selection & Analysis | Planned |
| 4-4 | Event Capture - AI Provider Interactions | Planned |
| 4-5 | Event Capture - Code Changes & Git Operations | Planned |
| 4-6 | Event Capture - Approvals & Escalations | Planned |
| 4-7 | Event Query API for Time-Travel | Planned |
| 4-8 | Black-Box Replay for Debugging | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-4)

---

## Epic 5: Observability Dashboard & Documentation (Partially Implemented)

Dashboard exists at `@tamma/dashboard` with admin, settings, and knowledge base pages. Not all stories are fully implemented.

| Story | Title | Status |
|-------|-------|--------|
| 5-1 | Dashboard Scaffolding and Routing | Partially done |
| 5-2 | Event Trail Visualization | Planned |
| 5-3 | Real-Time Dashboard - System Health | Planned |
| 5-4 | Real-Time Dashboard - Development Velocity | Planned |
| 5-5 | Event Trail Exploration UI | Planned |
| 5-6 | Alert System for Critical Issues | Planned |
| 5-7 | Feedback Collection System | Planned |
| 5-8 | Integration Testing Suite | Planned |
| 5-9a | Installation & Setup Documentation | Planned |
| 5-9b | Usage & Configuration Documentation | Planned |
| 5-9c | API Reference Documentation | Planned |
| 5-9d | Full Documentation Website | Planned |
| 5-9e | Video Walkthrough | Planned |
| 5-10 | Alpha Release Preparation | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-5)

---

## Epic 6: Context & Knowledge Management (Completed)

| Story | Title | Package | Status |
|-------|-------|---------|--------|
| 6-1 | Codebase Indexer Implementation | intelligence | Done |
| 6-2 | Vector Database Integration | intelligence | Done |
| 6-3 | RAG Pipeline Implementation | intelligence | Done |
| 6-4 | MCP Client Integration | mcp-client | Done |
| 6-5 | Context Aggregator Service | intelligence | Done |
| 6-6 | Knowledge Base Management UI | dashboard, api | Done |
| 6-7 | LLM Cost Monitoring & Reporting | cost-monitor | Done |
| 6-8 | Agent Permissions System | gates | Done |
| 6-9 | Agent Knowledge Base | intelligence | Done |
| 6-10 | Scrum Master Task Loop | scrum-master | Done |

[Detailed Breakdown](Epic-6-Context-Knowledge) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-6)

---

## Epic 7: Autonomous Mentorship Workflow (Completed)

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
| 7-1H | TDD Sub-Workflow | Done |
| 7-1I | Debugging Sub-Workflow | Done |

[Detailed Breakdown](Epic-7-Mentorship) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-7)

---

## Epic 8: Distribution & Installation (Planned)

| Story | Title | Tier | Status |
|-------|-------|------|--------|
| 8-1 | esbuild Bundle & Package Structure | Tier 1 (npm) | Planned |
| 8-2 | npm Publish CI/CD Pipeline | Tier 1 (npm) | Planned |
| 8-3 | Standalone Binary Compilation | Tier 2 (binary) | Planned |
| 8-4 | Install Scripts & GitHub Releases | Tier 2 (binary) | Planned |
| 8-5 | Auto-Update & Package Manager Distribution | Tier 2 (binary) | Planned |
| 8-6 | TypeScript & Dashboard Dockerfiles | Tier 3 (Docker) | Planned |
| 8-7 | Docker Compose Full Stack | Tier 3 (Docker) | Planned |
| 8-8 | Docker CI/CD & CLI Integration | Tier 3 (Docker) | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-8)

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

[Detailed Breakdown](Epic-9-Agent-Management) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-9)

---

## Epic 10: Engine Core -- Workflow-Driven Architecture (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 10-1 | Engine Static Workflow & Brain | Done |
| 10-2 | Comprehensive Event Catalog & Typed Schema | Done |
| 10-3 | Event Store -- PostgreSQL/Emmett Implementation | Done |
| 10-4 | Smart Queue with State-Based Deduplication | Done |
| 10-5 | Workflow Provider Abstraction & ELSA Integration | Done |
| 10-6 | Input Channel Unification | Done |
| 10-7 | Event Store Security & Sanitization Pipeline | Done |
| 10-8 | State Reconstruction from Event Stream | Done |

[Detailed Breakdown](Epic-10-Engine-Core) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-10)

---

## Epic 11: Security Hardening (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 11-1 | ContentSanitizer C# Port | Done |
| 11-2 | LLM Input Sanitization | Done |
| 11-3 | Tool Call Validation | Done |
| 11-4 | Output Sanitization & Prompt Hardening | Done |
| 11-5 | Fail-Closed Guards & Provider Allowlist | Done |

[Detailed Breakdown](Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-11)

---

## Epic 12: Agentic Tool Loop (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 12-1 | Tool Executor Interface & Registry | Done |
| 12-2 | Agentic Tool Loop in CallLlm | Done |
| 12-3 | Context Compaction | Done |
| 12-4 | Streaming & Parallel Tools | Done |

[Detailed Breakdown](Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-12)

---

## Epic 13: Workflow Decomposition (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 13-1 | TDD Debug Retry Sub-Workflow | Done |
| 13-2 | CI Debug Retry Sub-Workflow | Done |
| 13-3 | Consolidate Finish Sequences | Done |

[Detailed Breakdown](Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-13)

---

## Epic 14: Custom ELSA Studio (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 14-1 | Studio Blazor WASM Scaffold | Done |
| 14-2 | Studio Docker & CI | Done |
| 14-3 | Studio Custom UI Hints | Done |

[Detailed Breakdown](Epic-11-14-ELSA) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-14)

---

## Epic 15: Observability & Log Aggregation (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 15-1 | OpenSearch Log Aggregation | Done |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-15)

---

## Epic 16: Unified Auth, User Management & Admin (Completed)

| Story | Title | Status |
|-------|-------|--------|
| 16-1 | OAuth2 Proxy Unified Auth | Done |
| 16-2 | User Management API | Done |
| 16-3 | Admin Dashboard | Done |
| 16-4 | Unified Navigation | Done |
| 16-5 | Role-Based Access Control | Done |
| 16-6 | ELSA Studio Auto-Login | Done |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-16)

---

## Epic 17: Multi-Tenancy Foundation (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 17-1 | Tenant Model & Database Schema | Planned |
| 17-2 | Row-Level Security & Tenant Isolation | Planned |
| 17-3 | Tenant-Scoped Event Store | Planned |
| 17-4 | Tenant-Scoped Workflow Instances | Planned |
| 17-5 | API Tenant Context Middleware | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-17)

---

## Epic 18: End-User Auth & Registration (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 18-1 | User Registration & Email Verification | Planned |
| 18-2 | User Login & Session Management | Planned |
| 18-3 | Organization/Tenant Creation | Planned |
| 18-4 | GitHub App Installation Onboarding | Planned |
| 18-5 | User-Facing Dashboard Shell | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-18)

---

## Epic 19: GitHub App Agent Dispatch (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 19-1 | Agent Dispatch via workflow_dispatch | Planned |
| 19-2 | Runner Monitoring | Planned |
| 19-3 | Result Collection | Planned |
| 19-4 | Execution Isolation | Planned |
| 19-5 | Security & Audit | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-19)

---

## Epic 20: Billing & Payments (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 20-1 | Stripe Integration & Subscription Management | Planned |
| 20-2 | Usage Metering (Workflow Runs, Tokens, Repos) | Planned |
| 20-3 | Limits Enforcement at Orchestrator | Planned |
| 20-4 | Billing Dashboard | Planned |
| 20-5 | Billing Admin & Reporting | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-20)

---

## Epic 21: Marketing Site & User Dashboard (Partially Implemented)

Marketing site exists at `apps/marketing-site/` (Cloudflare Workers). User dashboard partially implemented.

| Story | Title | Status |
|-------|-------|--------|
| 21-1 | Marketing Landing Page | Done |
| 21-2 | Pricing Page | Planned |
| 21-3 | Documentation Site | Planned |
| 21-4 | User Dashboard - Repos & Runs | Planned |
| 21-5 | User Dashboard - Settings & Billing | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-21)

---

## Epic 22: CLI Mode Preservation (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 22-1 | Agent Executor Abstraction | Planned |
| 22-2 | CLI Standalone Workflow Engine | Planned |
| 22-3 | Optional Cloud Sync | Planned |
| 22-4 | Feature Parity Matrix | Planned |
| 22-5 | CLI Docker Installation | Planned |

[Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-22)

---

## Epic 23: System Monitoring & Observability Dashboard (Planned)

| Story | Title | Status |
|-------|-------|--------|
| 23-1 | System Health Dashboard (Overview) | Planned |
| 23-2 | Agent Monitor (Realtime) | Planned |
| 23-3 | Event Store Explorer | Planned |
| 23-4 | Configuration Audit | Planned |
| 23-5 | Workflow Monitor | Planned |
| 23-6 | Provider Diagnostics (Deep) | Planned |
| 23-7 | Log Explorer (OpenSearch) | Planned |
| 23-8 | Infrastructure Monitor | Planned |
| 23-9 | Knowledge Base Monitor | Planned |
| 23-10 | Security & Access Audit | Planned |
| 23-11 | Monitoring API Foundation | Planned |
| 23-12 | Dashboard Navigation & Layout | Planned |

[Detailed Breakdown](Epic-23-System-Monitoring) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-23)

---

## Epic 24: Realtime Voice Conversation (Partially Implemented)

Voice as a first-class input/output mode for the Tamma orchestrator. Research complete; implementation planned.

| Story | Title | Status |
|-------|-------|--------|
| 24-0 | Voice API Research | Done |
| 24-1 | WebSocket Foundation | Planned |
| 24-2 | Speech-to-Text Integration | Planned |
| 24-3 | Text-to-Speech Integration | Planned |
| 24-4 | Intent Classification + Engine Integration | Planned |
| 24-5 | Dashboard Voice UI | Planned |
| 24-6 | Hardening + Production Readiness | Planned |

[Detailed Breakdown](Epic-24-Voice-Conversation) | [Story Files](https://github.com/meywd/tamma/tree/main/docs/stories/epic-24)

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
| Total stories across all epics | ~220 |
| Epics completed | 12 |
| Epics partially implemented | 3 |
| Epics planned (stories ready) | 9 |
| TypeScript packages with code | 14 |
| C# ELSA activities | 70+ |
| ELSA code-first workflows | 20+ |

---

For more details, see [Contributing Guidelines](Contributing).
