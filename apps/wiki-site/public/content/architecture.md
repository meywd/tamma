---
title: "Tamma Architecture Overview"
sidebar:
  order: 2
---

Tamma's architecture is a **dual-stack** system: TypeScript (Node.js 22 LTS) for AI providers, CLI, API, and dashboard; C# (.NET 8) for ELSA workflow orchestration. The two stacks communicate via HTTP APIs and shared PostgreSQL storage.

## High-Level Architecture

```
CLI / Web / Mobile / GitHub / GitLab / Gitea
                    |
               NORMALIZE TO EVENT
                    |
                    v
+----------------------------------------------------------+
|  TAMMA ENGINE (TypeScript)                                |
|                                                           |
|  +-- @tamma/cli ----------+  +-- @tamma/api -----------+ |
|  | tamma start (CLI)      |  | Fastify REST API        | |
|  | tamma server (HTTP)    |  | GitHub OAuth             | |
|  | tamma api (SaaS)       |  | Webhook handlers         | |
|  +------------------------+  | Settings/Admin routes     | |
|                               | Knowledge base routes     | |
|  +-- @tamma/orchestrator -+  | SaaS routes (LLM proxy)  | |
|  | Engine brain            |  +-------------------------+ |
|  | ElsaClient bridge       |                              |
|  | SaaS coordinator        |  +-- @tamma/dashboard ----+ |
|  +------------------------+  | React SPA (Vite)         | |
|                               | Admin panel              | |
|  +-- @tamma/providers ----+  | Settings management      | |
|  | Claude Code (CLI agent) |  | Knowledge base UI        | |
|  | OpenCode (CLI agent)    |  +-------------------------+ |
|  | OpenRouter (LLM API)    |                              |
|  | Zen MCP (LLM API)       |                              |
|  | Role-based resolver      |                              |
|  | Provider chain + circuit |                              |
|  | breaker health tracking  |                              |
|  +------------------------+                              |
|                                                           |
|  +-- @tamma/shared -------+  +-- @tamma/intelligence --+ |
|  | Content sanitizer       |  | Codebase indexer        | |
|  | URL validator            |  | Vector DB integration   | |
|  | Action gating            |  | RAG pipeline            | |
|  | Secure fetch             |  | Knowledge base          | |
|  | Diagnostics queue        |  | Context aggregator      | |
|  | Agent config types       |  +------------------------+ |
|  +------------------------+                              |
|                                                           |
|  +-- @tamma/platforms ----+  +-- @tamma/mcp-client ----+ |
|  | IGitPlatform interface  |  | MCP protocol client     | |
|  | GitHub implementation   |  | Tool interceptor chain  | |
|  | (GitLab, Gitea planned) |  | Transport layer         | |
|  +------------------------+  +------------------------+ |
|                                                           |
|  +-- @tamma/cost-monitor -+  +-- @tamma/gates ---------+ |
|  | Cost calculator         |  | Permission enforcer     | |
|  | Usage tracker            |  | Violation recorder      | |
|  | Alert manager            |  | Tool/command matchers   | |
|  +------------------------+  +------------------------+ |
|                                                           |
|  +-- @tamma/scrum-master -+  +-- @tamma/observability -+ |
|  | Task supervisor          |  | Pino structured logger  | |
|  | Approval workflow        |  +------------------------+ |
|  | Learning capture          |                              |
|  | Agent coordinator         |                              |
|  +------------------------+                              |
+----------------------------------------------------------+
                    |
          HTTP API (ElsaClient)
                    |
                    v
+----------------------------------------------------------+
|  ELSA WORKFLOW ENGINE (C# / .NET 8)                       |
|                                                           |
|  +-- Tamma.ElsaServer ----+  +-- Tamma.Studio ---------+ |
|  | 20+ code-first workflows|  | Custom Blazor WASM      | |
|  | Workflow seeder          |  | Tamma-branded UI        | |
|  | REST API                 |  | UI hint handlers        | |
|  +------------------------+  +------------------------+ |
|                                                           |
|  +-- Tamma.Activities ----+  +-- Tamma.Core -----------+ |
|  | ADL (issue selection,   |  | Enums (MentorshipState) | |
|  |   branch, PR, merge)   |  | Models                  | |
|  | AI (Claude, context,    |  +------------------------+ |
|  |   suggestions)          |                              |
|  | Assessment              |  +-- Tamma.Data -----------+ |
|  | Blocker diagnosis       |  | DB context               | |
|  | Context gathering       |  | Migrations               | |
|  | Debug pipeline          |  +------------------------+ |
|  | Integration (GitHub,    |                              |
|  |   Slack, Jira, Email)  |  +-- Tamma.Api ------------+ |
|  | LLM Call (inline,       |  | .NET REST API            | |
|  |   tools, budget,       |                              |
|  |   circuit breaker)     |                              |
|  | Mentorship              |                              |
|  | Review                  |                              |
|  | Security                |                              |
|  | TDD / Testing           |                              |
|  | Tool Execution          |                              |
|  +------------------------+                              |
|                                                           |
|  Code-First Workflows:                                    |
|  - AdlOrchestratorWorkflow (main ADL loop)               |
|  - SingleIssueCycleWorkflow (full issue lifecycle)        |
|  - LlmCallWorkflow (provider chain + budget + circuit)   |
|  - MentorshipWorkflow (28-state mentorship)              |
|  - TddWorkflow, TddWithDebugRetryWorkflow               |
|  - TestingWorkflow, CiWithDebugRetryWorkflow             |
|  - ContextGatheringWorkflow                               |
|  - PlanGenerationWorkflow                                 |
|  - CodeReviewWorkflow, ReviewFixWorkflow                  |
|  - BranchCreationWorkflow, PullRequestWorkflow            |
|  - MergeWorkflow, MergeApprovalWorkflow                  |
|  - IssueSelectionWorkflow                                 |
|  - AssessmentWorkflow, BlockerDiagnosisWorkflow           |
|  - DebuggingWorkflow                                      |
+----------------------------------------------------------+
                    |
                    v
+----------------------------------------------------------+
|  INFRASTRUCTURE                                           |
|                                                           |
|  PostgreSQL 17  |  RabbitMQ  |  ChromaDB  |  OpenSearch  |
|  (data, events, |  (message  |  (vector   |  (log        |
|   ELSA state)   |   broker)  |   store)   |   aggregation)|
|                                                           |
|  nginx-proxy    |  Cloudflare DNS                         |
|  (reverse proxy |  (app/api/elsa.tamma.dev, Full SSL)    |
|   + dashboard)  |                                         |
+----------------------------------------------------------+
```

## Core Components

### 1. TypeScript Layer

#### AI Provider Abstraction (`@tamma/providers`)

**Interface-Based Design:**
- `IAIProvider` interface defines standard LLM operations (synchronous and streaming messages)
- `IAgentProvider` interface defines task-based agent operations (tool-calling CLI agents)
- `ICLIAgentProvider` for providers that manage their own subprocess execution

**Implemented Providers:**

| Provider | Class | Type | Status |
|----------|-------|------|--------|
| Anthropic Claude Code | `ClaudeAgentProvider` | CLI agent (IAgentProvider) | Implemented |
| OpenCode | `OpenCodeProvider` | CLI agent (IAgentProvider) | Implemented |
| OpenRouter | `OpenRouterProvider` | LLM provider (IAIProvider) | Implemented |
| Zen MCP | `ZenMCPProvider` | LLM provider (IAIProvider) | Implemented |

LLM providers are auto-wrapped via `wrapAsAgent()` to satisfy the `IAgentProvider` contract.

#### Config-Driven Multi-Agent System (Epic 9)

See [Epic 9 wiki page](/epics/9-agent-management/) for full details. Key components:
- `RoleBasedAgentResolver` -- maps workflow phases to agent roles to provider chains
- `ProviderChain` -- ordered fallback with health and budget checks
- `ProviderHealthTracker` -- three-state circuit breaker per provider+model
- `AgentPromptRegistry` -- 6-level resolution chain with template interpolation
- `AgentProviderFactory` -- creates providers by name from configuration

#### Security Layer (`@tamma/shared/security`)

Defense-in-depth with four components:
- **ContentSanitizer**: HTML stripping, zero-width char removal, prompt injection detection (4 categories), NFKD normalization
- **URL Validator**: Numeric octet parsing for RFC 1918 ranges, IPv6 support, SSRF protection
- **Action Gating**: Shell command blocklist with normalization (no regex, no ReDoS risk)
- **Secure Fetch**: SSRF-protected HTTP with redirect re-validation, Content-Type allowlist, size limits

#### Context & Knowledge (`@tamma/intelligence`)

- **Codebase Indexer**: File discovery, TypeScript-aware chunking, embedding service (OpenAI, Cohere, Ollama)
- **Vector Store**: Base interface with 5 providers (ChromaDB, pgvector, Pinecone, Qdrant, Weaviate)
- **RAG Pipeline**: Query processing, retrieval, ranking, assembly with hybrid search
- **Knowledge Base**: Recommendations, prohibitions, learnings with pattern matching and relevance ranking
- **Context Aggregator**: Multi-source context assembly with token budget management

#### MCP Client (`@tamma/mcp-client`)

- Full MCP protocol client with stdio, SSE, and WebSocket transports
- Connection pooling and health monitoring
- `ToolInterceptorChain` with pre/post hooks for sanitization and URL validation
- Server registry and capability caching

#### Git Platform (`@tamma/platforms`)

- `IGitPlatform` interface with GitHub implementation
- Operations: PR creation, comments, merge, issue management, branch creation, CI triggering
- Rate limiting, pagination, error mapping

#### Cost Monitoring (`@tamma/cost-monitor`)

- Usage tracking per provider+model
- Cost calculation with configurable pricing
- Limit management with budget alerts
- File and in-memory storage backends

#### Permissions (`@tamma/gates`)

- Permission enforcer with tool, command, and glob matchers
- Violation recording and alerting
- Configurable default permission sets

#### Scrum Master (`@tamma/scrum-master`)

- Task supervisor for agent coordination
- Approval workflow management
- Learning capture from completed tasks
- Alert management

### 2. C# / ELSA Layer

The ELSA workflow engine (apps/tamma-elsa/) provides the orchestration backbone. ELSA 3 is a .NET workflow engine that supports code-first workflow definitions, visual designer (ELSA Studio), persistence, and bookmarks for long-running processes.

**Activity Categories:**

| Category | Activities | Purpose |
|----------|-----------|---------|
| ADL | SelectIssue, CreateBranch, CreatePR, MergePR, WaitForApproval, AnalyzeReview, CheckLimits | Autonomous Development Loop steps |
| AI | ClaudeAnalysis, ContextGathering, SuggestionGenerator | AI-powered analysis |
| Assessment | GenerateQuestions, DeliverQuestions, WaitForResponse, AnalyzeResponse, ClassifyResult, UpdateSkillProfile | Developer skill assessment |
| Blocker | ClassifyBlocker, CollectCIStatus, CollectGitActivity, CollectInactivity, DetectProgress, EscalateToSenior | Blocker diagnosis and resolution |
| Context | FetchFileContents, FetchRecentCommits, FetchSessionHistory, FetchSimilarPatterns, FetchStoryMetadata, FetchTestResults, AssembleContext, ApplyBudget | Context gathering pipeline |
| Debug | CollectErrorMessages, CollectGitHistory, CollectRelevantCode, CollectReproductionSteps, CollectTestResults, ClassifyDebugContext, SelectHypothesis, RefineHypothesis, AIDiagnosis, WriteRegressionTest, CompileDebugReport, RecordResolution | Systematic debugging pipeline |
| Integration | GitHub, Slack, Jira, Email | External service integration |
| LlmCall | CallLlm, CallLlmInline, CheckBudget, CheckCircuitBreaker, RecordDiagnostics, ResolveAgentConfig, ResolveLlmPrompt, ResolveTools | LLM interaction with agentic tool loop |
| Mentorship | AssessJuniorCapability, MonitorImplementation, DiagnoseBlocker, ProvideGuidance, QualityGateCheck, CodeReview, MergeComplete | Mentorship workflow steps |
| Review | (via ADL) AnalyzeReview, ApplyReviewFixes | Code review cycle |
| Security | (in LlmCall) CommandValidator, PathValidator | Security enforcement in workflows |
| TDD / Testing | (via workflows) TDD cycle management, test execution | Test-first development |
| Tool Execution | FileRead, FileWrite, SearchCode, ShellExecute, RunTests, GitOperations, ToolExecutorRegistry, ContextCompactor, TokenEstimator | In-process tool execution for agentic LLM calls |

### 3. Infrastructure

#### Docker Compose Stack

The production deployment runs on Hetzner CPX42 (16GB RAM):

| Service | Technology | Memory | Purpose |
|---------|-----------|--------|---------|
| PostgreSQL | 17 | 2GB | Data, events, ELSA state |
| RabbitMQ | Latest | 512MB | Message broker |
| ChromaDB | Latest | 1GB | Vector store |
| elsa-server | .NET 8 | 1GB | ELSA workflow engine |
| tamma-api-dotnet | .NET 8 | 512MB | .NET REST API |
| tamma-api | Node.js 22 | 512MB | Fastify REST API |
| tamma-engine | Node.js 22 | 1GB | TypeScript engine |
| tamma-dashboard | nginx | 256MB | React SPA |
| elsa-studio | nginx | 128MB | Custom Blazor WASM |
| nginx-proxy | nginx | 128MB | Reverse proxy |
| OpenSearch (opt-in) | 2.x | 3GB | Log aggregation |

Total: ~7.1GB without observability, ~11.8GB with OpenSearch.

#### CI/CD Pipelines (GitHub Actions)

| Workflow | Purpose |
|----------|---------|
| ci.yml | Build, lint, test on PRs |
| deploy.yml | Deploy to VPS via SSH |
| docker-publish.yml | Build and push Docker images to GHCR |
| docker-smoke-test.yml | Smoke test Docker Compose stack |
| release.yml | Create GitHub releases |
| tamma-worker.yml | GitHub Actions worker template |
| codeql.yml | Code security scanning |

---

## Deployment Modes

### 1. CLI Mode (`tamma start`)
```bash
tamma start --config ~/.tamma/config.json
```
- Self-hosted engine running locally
- Agents execute on user's machine (Claude Code, OpenCode)
- No cloud dependencies required
- ELSA workflow engine embedded or connected locally

### 2. Server Mode (`tamma server`)
```bash
tamma server --port 3000
```
- Self-hosted HTTP server with REST API
- Dashboard accessible at configured URL
- Manages worker pool locally
- Suitable for small team deployments

### 3. SaaS Mode (`tamma api`)
```bash
# Entrypoint: packages/api/src/serve.ts
```
- Multi-tenant SaaS deployment
- GitHub App authentication
- Agents dispatched to user's GitHub Actions runners
- User code never leaves their infrastructure
- LLM proxy for users without their own API keys

---

## Security Model

```
External Input (issue body, PR comments, MCP tool results)
         |
         v
MCP ToolInterceptorChain
  Pre: URL validation (block private IPs in tool args)
  Post: ContentSanitizer.sanitizeOutput() (strip HTML from results)
         |
         v
SecureAgentProvider (ContentSanitizer.sanitize())
  - Null byte removal (always)
  - HTML stripping
  - Zero-width char removal (bidi override protection)
  - Prompt injection detection (4 categories + encoding evasion)
         |
         v
IAgentProvider.executeTask()  [claude-code, opencode, openrouter, zen-mcp]
         |
         v
SecureAgentProvider post-call (ContentSanitizer.sanitizeOutput())
  - Strip HTML outside code blocks
  - Remove zero-width chars
         |
         v
AgentTaskResult -> engine logic

Shell commands: evaluateAction() with substring blocklist (no regex, no ReDoS)
Outbound HTTP: secureFetch() with SSRF protection and redirect re-validation
```

C# ELSA layer has its own security pipeline (Epic 11):
- LLM input sanitization in prompt resolution activities
- Tool call validation (name allowlist, argument schema, size cap)
- Output sanitization before storage/display
- Fail-closed guards on circuit breaker and budget checks

---

## Technology Stack

### TypeScript (Node.js 22 LTS)
- **Language:** TypeScript 5.7+ (strict mode)
- **Framework:** Fastify 5.x (HTTP server)
- **Package Manager:** pnpm 9+ (monorepo)
- **Testing:** Vitest 3.x
- **Build:** esbuild + tsc
- **CLI:** Custom command system
- **Logging:** Pino
- **Date/Time:** dayjs

### C# (.NET 8)
- **Workflow Engine:** ELSA Workflows 3.x
- **Studio:** Custom Blazor WASM (MudBlazor)
- **ORM:** Entity Framework Core
- **Database:** PostgreSQL (Npgsql)

### Infrastructure
- **Database:** PostgreSQL 17
- **Message Broker:** RabbitMQ
- **Vector Store:** ChromaDB (production), pgvector/Pinecone/Qdrant/Weaviate (supported)
- **Log Aggregation:** OpenSearch (optional)
- **Reverse Proxy:** nginx
- **DNS/SSL:** Cloudflare
- **Container Registry:** GHCR
- **CI/CD:** GitHub Actions

---

## For More Details

- [Full Architecture Document](/architecture/)
- [Epic 9: Agent Management](/epics/9-agent-management/)
- [Epic 10: Engine Core](/epics/10-engine-core/)
- [Stories Index](/stories/)
- [PRD](https://github.com/meywd/tamma/blob/main/docs/PRD.md)
