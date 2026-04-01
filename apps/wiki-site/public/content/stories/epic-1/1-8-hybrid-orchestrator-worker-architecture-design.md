---
title: "Story 1.8: Hybrid Orchestrator/Worker Architecture Design"
sidebar:
  order: 10
---

Status: ready-for-dev

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:
- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

## Story

As a **system architect**,
I want documented architecture for orchestrator mode and worker mode,
so that system can operate both as autonomous coordinator and as CI/CD-invoked worker.

## Acceptance Criteria

1. Architecture document defines orchestrator mode responsibilities (issue selection, loop coordination, state management)
2. Architecture document defines worker mode responsibilities (CI/CD integration, single-task execution, exit codes)
3. Document includes sequence diagrams for both modes
4. Document specifies shared components (AI abstraction, Git abstraction, quality gates)
5. Document defines state persistence strategy for graceful shutdown/restart
6. Architecture reviewed and approved by technical lead
7. Document defines GitHub Actions as a worker type: triggered via `workflow_dispatch`, using installation tokens for auth, reporting results back to orchestrator via HTTP callback
8. Document includes sequence diagram for GitHub Actions worker flow: orchestrator dispatches `workflow_dispatch` → Actions runner starts → acquires installation token → runs `processOneIssue` → reports result via callback

## Tasks / Subtasks

- [ ] Task 1: Define orchestrator mode architecture (AC: 1)
  - [ ] Subtask 1.1: Document orchestrator responsibilities and scope
  - [ ] Subtask 1.2: Design orchestrator service architecture (Fastify server, REST API, WebSocket)
  - [ ] Subtask 1.3: Define task queue management and worker pool coordination
  - [ ] Subtask 1.4: Design state management and persistence strategy
  - [ ] Subtask 1.5: Document orchestrator startup and shutdown sequences

- [ ] Task 2: Define worker mode architecture (AC: 2)
  - [ ] Subtask 2.1: Document worker responsibilities and scope
  - [ ] Subtask 2.2: Design worker execution engine and task processing
  - [ ] Subtask 2.3: Define orchestrator communication protocols (registration, heartbeat, task polling)
  - [ ] Subtask 2.4: Design local resource management and isolation
  - [ ] Subtask 2.5: Document worker startup and shutdown sequences

- [ ] Task 3: Design shared components and interfaces (AC: 4)
  - [ ] Subtask 3.1: Define shared configuration management approach
  - [ ] Subtask 3.2: Design event emission and audit trail integration
  - [ ] Subtask 3.3: Document logging infrastructure and structured output
  - [ ] Subtask 3.4: Define health check endpoints and monitoring
  - [ ] Subtask 3.5: Design error handling and recovery patterns

- [ ] Task 4: Create sequence diagrams and workflows (AC: 3)
  - [ ] Subtask 4.1: Create orchestrator startup sequence diagram
  - [ ] Subtask 4.2: Create worker registration and task assignment sequence diagram
  - [ ] Subtask 4.3: Create task execution and progress reporting sequence diagram
  - [ ] Subtask 4.4: Create graceful shutdown and recovery sequence diagrams
  - [ ] Subtask 4.5: Document error handling and retry workflows

- [ ] Task 5: Define state persistence and recovery strategy (AC: 5)
  - [ ] Subtask 5.1: Design database schema for task queue and worker registry
  - [ ] Subtask 5.2: Define state persistence for orchestrator restart scenarios
  - [ ] Subtask 5.3: Design in-flight task recovery mechanisms
  - [ ] Subtask 5.4: Document data consistency and transaction handling
  - [ ] Subtask 5.5: Define backup and disaster recovery procedures

- [ ] Task 6: Document integration points and APIs (AC: 1, 2)
  - [ ] Subtask 6.1: Define orchestrator REST API specification
  - [ ] Subtask 6.2: Document WebSocket events for real-time progress
  - [ ] Subtask 6.3: Define worker-to-orchestrator communication protocols
  - [ ] Subtask 6.4: Document configuration schema for both modes
  - [ ] Subtask 6.5: Create integration testing guidelines

- [ ] Task 7: GitHub Actions Worker Type Architecture (AC: 7, 8)
  - [ ] Subtask 7.1: Document GitHub Actions as a worker type (alongside HTTP/WebSocket workers)
  - [ ] Subtask 7.2: Define `workflow_dispatch` as the dispatch mechanism: orchestrator triggers workflow via GitHub API, passing `installation_id`, `issue_number`, and `callback_url` as inputs
  - [ ] Subtask 7.3: Document installation token acquisition within Actions using `actions/create-github-app-token`
  - [ ] Subtask 7.4: Define result callback protocol: worker POSTs result (success/failure/skipped) to orchestrator's callback endpoint
  - [ ] Subtask 7.5: Create sequence diagram for Actions-based worker flow: SaaS Coordinator → workflow_dispatch → Actions runner → processOneIssue → callback → orchestrator updates state
  - [ ] Subtask 7.6: Document differences between HTTP workers and Actions workers: lifecycle, auth, compute ownership, timeout behavior
  - [ ] Subtask 7.7: Document concurrency control via GitHub Actions concurrency groups (one job per issue)

- [ ] Task 8: Architecture review and approval (AC: 6)
  - [ ] Subtask 7.1: Conduct technical architecture review
  - [ ] Subtask 7.2: Validate against PRD requirements and constraints
  - [ ] Subtask 7.3: Review security and performance considerations
  - [ ] Subtask 7.4: Document architecture decisions and trade-offs
  - [ ] Subtask 7.5: Obtain technical lead approval and sign-off

## Dev Notes

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**
1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)


### Architecture Context
- Implements hybrid architecture pattern supporting both orchestrator (stateful coordinator) and worker (stateless executor) modes
- Orchestrator mode: Fastify HTTP server, REST API, WebSocket streaming, PostgreSQL task queue, worker pool management
- Worker mode: Stateless execution engine, task polling, local filesystem access, result reporting via HTTP callbacks
- **GitHub Actions worker type**: Extends worker mode to run inside GitHub Actions, triggered via `workflow_dispatch`. The SaaS Coordinator dispatches work to user repos, Actions runners acquire installation tokens, execute `processOneIssue`, and report results back via HTTP callback. This enables SaaS deployment where compute runs on GitHub's infrastructure.
- Shared components: Configuration loader, event emitter, logging infrastructure, health check endpoints
- Integrates with AI provider abstraction from Stories 1.1-1.3 and Git platform abstraction from Stories 1.4-1.7

### GitHub Actions Worker Type
- **Dispatch mechanism**: `workflow_dispatch` API call from SaaS Coordinator
- **Auth**: Installation tokens via `actions/create-github-app-token` (not PAT)
- **Compute**: GitHub-hosted runners (user's Actions minutes)
- **Communication**: One-way callback (worker → orchestrator) via HTTP POST
- **Isolation**: Each installation runs in its own repo's Actions environment
- **Concurrency**: GitHub Actions concurrency groups prevent duplicate issue processing
- **Timeout**: Configurable per-workflow (default 30 minutes, max 6 hours)
- **Related stories**: 1.5-11 (GitHub App auth), 1.5-12 (SaaS Coordinator), 1.5-13 (Actions worker implementation)

### Project Structure Notes
- Implementation location: packages/orchestrator/ and packages/worker/
- Orchestrator components: src/index.ts, src/orchestrator.ts, src/task-queue.ts, src/worker-pool.ts
- Worker components: src/index.ts, src/worker.ts, src/task-executor.ts
- Shared components: packages/config/, packages/events/, packages/logger/
- API routes: packages/orchestrator/src/routes/
- Database migrations: packages/orchestrator/migrations/

### Technology Stack
- Fastify web framework for HTTP server and WebSocket support
- PostgreSQL for task queue persistence and worker registry
- Event bus integration for DCB event sourcing
- Structured logging with Pino
- Health check endpoints for monitoring and observability

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
- [Source: docs/epics.md#Story-1.8](../epics.md#story-18-hybrid-orchestrator-worker-architecture-design)
- [Source: docs/tech-spec-epic-1.md#Hybrid-Architecture-Design](../tech-spec-epic-1.md#hybrid-architecture-design-story-1-8)
- [Source: docs/tech-spec-epic-1.md#Orchestrator-Startup-Sequence](../tech-spec-epic-1.md#orchestrator-startup-sequence)
- [Source: docs/tech-spec-epic-1.md#Worker-Startup-Sequence](../tech-spec-epic-1.md#worker-startup-sequence)
- [Source: docs/architecture.md#Project-Structure](../architecture.md#project-structure)
- [Reference: Stories 1.1-1.7 - Provider and Platform Abstractions](1-1-ai-provider-interface-definition.md)

## Dev Agent Record

### Context Reference
- [F:\Code\Repos\Tamma\docs\stories\1-8-hybrid-orchestrator-worker-architecture-design.context.xml](1-8-hybrid-orchestrator-worker-architecture-design.context.xml)

### Agent Model Used
<!-- Model information will be added by development agent -->

### Debug Log References
<!-- Debug references will be added during development -->

### Completion Notes List
<!-- Completion notes will be added during development -->

### File List
<!-- File list will be added during development -->
