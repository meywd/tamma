# Tamma Architecture Overview

Tamma's architecture is designed for **autonomous operation**, **multi-provider flexibility**, and **production-grade quality gates**.

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Tamma Platform                          │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌─────────────────┐         ┌─────────────────┐           │
│  │  Orchestrator   │◄───────►│  Worker Pool    │           │
│  │  (Coordinator)  │         │  (Executors)    │           │
│  └────────┬────────┘         └────────┬────────┘           │
│           │                           │                     │
│           ▼                           ▼                     │
│  ┌────────────────────────────────────────────────┐        │
│  │         Development Context Bus (DCB)          │        │
│  │         Event Sourcing & Audit Trail          │        │
│  └────────────────────────────────────────────────┘        │
│           │                           │                     │
│  ┌────────▼────────┐         ┌───────▼──────┐             │
│  │  AI Provider    │         │  Git Platform│             │
│  │  Abstraction    │         │  Abstraction │             │
│  └────────┬────────┘         └───────┬──────┘             │
│           │                           │                     │
└───────────┼───────────────────────────┼─────────────────────┘
            │                           │
            ▼                           ▼
    ┌───────────────┐          ┌───────────────┐
    │ Claude/GPT-4  │          │ GitHub/GitLab │
    │ Gemini/Local  │          │ Gitea/Forgejo │
    └───────────────┘          └───────────────┘
```

## Core Components

### 1. Hybrid Orchestrator/Worker Architecture

**Orchestrator Mode** (Stateful Coordinator):
- Manages task queue and worker pool
- Coordinates autonomous development loop
- Tracks in-flight tasks and state
- Exposes REST API and WebSocket for monitoring
- Persists state for graceful restart

**Worker Mode** (Stateless Executor):
- Executes single tasks (issue analysis, code generation, test generation, PR creation)
- Reports progress and results to orchestrator
- Can run in CI/CD pipelines or as background workers
- No state persistence required

**Standalone Mode** (Direct Execution):
- CLI-driven execution without orchestrator
- Single-issue processing
- Local development and testing

[→ Detailed Architecture Doc](https://github.com/meywd/tamma/blob/main/docs/architecture.md)

---

### 2. AI Provider Abstraction (`@tamma/providers`)

**Interface-Based Design:**
- `IAIProvider` interface defines standard operations:
  - `generateCode()` - Code generation
  - `analyzeContext()` - Context analysis
  - `suggestFix()` - Error fix suggestions
  - `reviewChanges()` - Code review

**Supported Providers:**
- **Anthropic Claude** (reference implementation)
- OpenAI GPT-4 / GPT-3.5-turbo
- Google Gemini Pro
- GitHub Copilot
- OpenRouter (aggregator for 100+ models)
- Local LLMs (Ollama, LM Studio, vLLM)
- Zen MCP (Model Context Protocol)

**Provider Capabilities Discovery:**
- Dynamic capability query (streaming, models, token limits)
- Automatic provider selection based on task requirements
- Cost-aware routing (use cheaper models for simple tasks)

---

### 3. Git Platform Abstraction (`@tamma/platforms`)

**Interface-Based Design:**
- `IGitPlatform` interface defines standard operations:
  - `createPR()` - Pull request creation
  - `commentOnPR()` - Add PR comments
  - `mergePR()` - Merge pull requests
  - `getIssue()` - Fetch issue details
  - `createBranch()` - Create git branches
  - `triggerCI()` - Trigger CI/CD pipelines

**Supported Platforms:**
- **GitHub** (reference implementation)
- GitLab (self-hosted and cloud)
- Gitea (self-hosted)
- Forgejo (Gitea fork, self-hosted)
- Bitbucket (Cloud and Server)
- Azure DevOps (Services and Server)
- Plain Git (local repositories, no platform features)

**Platform Normalization:**
- Unified data models for PRs, issues, branches, CI status
- Abstraction over platform differences (GitHub PRs vs GitLab Merge Requests)
- Pagination and rate limit handling

---

### 4. Development Context Bus (DCB)

**Event Sourcing Architecture:**
- All state mutations emitted as events
- Complete audit trail for debugging and transparency
- Event replay for testing and rollback
- PostgreSQL + event store for persistence

**Event Types:**
- IssueAnalyzed
- CodeGenerated
- TestsGenerated
- ReviewCompleted
- PRCreated
- QualityGatePassed/Failed
- EscalationTriggered

---

### 5. Autonomous Development Loop

```
┌──────────────────────────────────────────────────────┐
│                Autonomous Loop                       │
├──────────────────────────────────────────────────────┤
│                                                       │
│  1. Issue Selection     ──►  Select from backlog    │
│  2. Issue Analysis      ──►  AI analyzes requirements│
│  3. Plan Generation     ──►  Break into steps       │
│  4. Code Generation     ──►  TDD: test-first impl   │
│  5. Test Execution      ──►  Run tests, validate    │
│  6. Code Review         ──►  AI reviews changes     │
│  7. Quality Gates       ──►  Security, perf, tests  │
│  8. PR Creation         ──►  Create pull request    │
│  9. CI/CD Integration   ──►  Trigger pipelines      │
│  10. Decision Point     ──►  Merge or Escalate      │
│                                                       │
└──────────────────────────────────────────────────────┘
```

**Quality Gates (Mandatory):**
- Test coverage ≥ 80%
- Security scanning (SAST, dependency scan)
- Performance regression detection
- Code review approval (AI + optional human)
- CI/CD pipeline success

**Escalation Triggers:**
- Ambiguous requirements detected
- Test coverage below threshold
- Security vulnerabilities found
- Breaking changes detected
- CI/CD pipeline failures

---

## Technology Stack

### Backend
- **Runtime:** Node.js 22 LTS
- **Language:** TypeScript 5.7+ (strict mode)
- **Framework:** Fastify (HTTP server, WebSocket)
- **Database:** PostgreSQL 17 (state, queue, events)
- **Event Store:** PostgreSQL + event sourcing library

### AI & ML
- **AI SDKs:**
  - `@anthropic-ai/sdk` - Claude API
  - `openai` - GPT-4 API
  - `@google/generative-ai` - Gemini API
  - `@modelcontextprotocol/sdk` - MCP integration
- **Tool Integration:** Native function calling / tool use

### Git & Platform Integration
- **GitHub:** `@octokit/rest` (REST API v4 + GraphQL)
- **GitLab:** `@gitbeaker/node` (GitLab SDK)
- **Bitbucket:** REST API v2 (Cloud), REST API (Server)
- **Azure DevOps:** `azure-devops-node-api` (official SDK)
- **Local Git:** `simple-git` (local operations)

### Observability
- **Metrics:** Prometheus + Grafana
- **Tracing:** OpenTelemetry
- **Logging:** Pino (structured JSON logs)
- **Health Checks:** `/health`, `/ready`, `/metrics` endpoints

### Deployment
- **Containers:** Docker multi-stage builds
- **Orchestration:** Kubernetes (Helm charts)
- **CI/CD:** GitHub Actions, GitLab CI
- **Package Management:** pnpm workspaces (monorepo)

---

## Security & Quality

### Authentication & Authorization
- API token-based authentication (GitHub PAT, GitLab PAT, etc.)
- OAuth2 support for user-facing flows
- Secure credential storage (OS keychain integration)

### Input Validation
- JSON Schema validation for all inputs
- Rate limiting and throttling
- Injection attack prevention

### Quality Assurance
- **Test Coverage:** 80% line, 75% branch, 85% function
- **Linting:** ESLint + Prettier
- **Type Safety:** TypeScript strict mode
- **CI/CD:** Automated testing on all PRs

---

## Deployment Modes

### 1. Orchestrator Mode (Production)
```bash
tamma --mode orchestrator --config ~/.tamma/config.json
```
- Runs as HTTP server with REST API
- Manages worker pool
- Persists state in PostgreSQL
- Suitable for multi-user, production deployments

### 2. Worker Mode (Distributed)
```bash
tamma --mode worker --orchestrator-url http://orchestrator:3000
```
- Stateless executor
- Polls orchestrator for tasks
- Can run in CI/CD or as background worker
- Horizontally scalable

### 3. Standalone Mode (Development)
```bash
tamma --mode standalone --issue 123
```
- Direct CLI execution
- No orchestrator required
- Suitable for local development and testing

---

## Monitoring & Observability

### Metrics (Prometheus)
- `tamma_tasks_completed_total` - Total tasks completed
- `tamma_tasks_duration_seconds` - Task execution time
- `tamma_quality_gate_failures_total` - Quality gate failures
- `tamma_escalations_total` - Human escalations triggered
- `tamma_ai_provider_requests_total` - AI provider API calls
- `tamma_ai_provider_tokens_total` - Token usage by provider

### Distributed Tracing (OpenTelemetry)
- End-to-end request tracing
- Service-to-service call visibility
- Performance bottleneck identification

### Logging (Pino)
- Structured JSON logs
- Context propagation (trace ID, span ID)
- Log aggregation (Elasticsearch, Loki)

---

## For More Details

- [Full Architecture Document](https://github.com/meywd/tamma/blob/main/docs/architecture.md)
- [Tech Spec Epic 1](https://github.com/meywd/tamma/blob/main/docs/tech-spec-epic-1.md)
- [Tech Spec Epic 2](https://github.com/meywd/tamma/blob/main/docs/tech-spec-epic-2.md)
- [PRD](https://github.com/meywd/tamma/blob/main/docs/PRD.md)
