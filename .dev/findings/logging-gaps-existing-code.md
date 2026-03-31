# Logging Gaps in Existing Code

**Date**: 2026-03-28
**Auditor**: Automated audit via Claude Code
**Scope**: All existing C# and TypeScript code (epics 11-14 excluded — already audited)

---

## Executive Summary

The codebase has **significant logging gaps** across both the C# ELSA server and the TypeScript packages.

**C# side**: Most activity files (~88 out of 90) properly inject AND use `ILogger`. The main gap is `RecordDiagnosticsInlineActivity` (imports `Microsoft.Extensions.Logging` but never uses ILogger — silently swallows errors) and `IntegrationService.cs` (no logging in a 15-method facade).

**TypeScript side**: This is where the critical gaps are. Across 14 packages with 344 source files, only 27 files have any form of logging. Entire packages have zero logging:
- `packages/platforms/` — 14 files, 0 with logging (GitHub API calls are invisible)
- `packages/cost-monitor/` — 12 files, 0 with logging
- `packages/mcp-client/` — 30 files, 0 with logging
- `packages/scrum-master/` — 12 files, 0 with logging
- `packages/api/` — 57 files, 1 with logging (20 route handlers have zero logging)
- `packages/intelligence/` — 94 files, 3 with logging

**Stories**: Only epics 11-14 (15 files) had `## Logging Requirements` sections. This audit added logging requirements to 140 additional story files across epics 1-10.

---

## Part 1: C# Code Gaps

### 1.1 Activities WITH ILogger (Properly Injected AND Used)

**Status: GOOD** — These activities inject ILogger via constructor DI and actively log start/success/failure/error:

| Category | Files | Logging Quality |
|----------|-------|-----------------|
| ADL | 9 files (SelectIssue, CreateBranch, CreatePR, MergePR, CheckLimits, AnalyzeReview, ApplyReviewFixes, WaitForPlanApproval, WaitForMergeApproval) | Good — log errors, outcomes, key parameters |
| LlmCall | CallLlmActivity, CallLlmInlineActivity, CheckBudgetActivity, CheckCircuitBreakerActivity, RecordDiagnosticsActivity, ResolveLlmPromptActivity, ResolveToolsActivity | Good — detailed provider/model/token logging |
| LlmCall | ResolveAgentConfigActivity | Good — uses `context.GetRequiredService<ILogger>()` pattern |
| Integration | EmailActivity, GitHubActivity, JiraActivity, SlackActivity | Good |
| Mentorship | All 7 activities | Good |
| Review | All 8 activities | Good |
| Testing | All 8 activities | Good |
| TDD | All 7 activities | Good |
| Debug | All 13 activities | Good |
| Blocker | All 6 activities | Good |
| Context | All 7 activities | Good |
| Assessment | All 6 activities | Good |
| AI | All 3 activities | Good |
| CodeIndex | UpdateCodeIndexActivity | Good |
| Mentorship/FlowNode | FlowNodeActivities (31 inner activities) | Good — uses `context.GetRequiredService<ILogger>()` |

**Total: ~88 activity files with ILogger usage**

### 1.2 Activities WITHOUT Logging — GAPS

| File | Issue | Priority | What Should Be Logged |
|------|-------|----------|----------------------|
| `Tamma.Activities/LlmCall/RecordDiagnosticsInlineActivity.cs` | Imports `Microsoft.Extensions.Logging` but NEVER injects or uses ILogger. Silently swallows JSON deserialization errors in 3 catch blocks. | **HIGH** | Log when diagnostic deserialization fails, when circuit breaker state is updated, when budget is updated, when cost estimation occurs |

### 1.3 Workflow Files (WorkflowBase subclasses) — Expected No Logger

These are declarative workflow definitions (no DI, no runtime logic), so lacking ILogger is **by design**:

- `SingleIssueCycleWorkflow.cs`
- `AdlOrchestratorWorkflow.cs`
- `MentorshipWorkflow.cs`
- `LlmCallWorkflow.cs`
- `AssessmentWorkflow.cs`
- `BlockerDiagnosisWorkflow.cs`
- `BranchCreationWorkflow.cs`
- `CodeReviewWorkflow.cs`
- `ContextGatheringWorkflow.cs`
- `DebuggingWorkflow.cs`
- `IssueSelectionWorkflow.cs`
- `MergeApprovalWorkflow.cs`
- `MergeWorkflow.cs`
- `PlanGenerationWorkflow.cs`
- `PullRequestWorkflow.cs`
- `ReviewFixWorkflow.cs`
- `TddWorkflow.cs`
- `TestingWorkflow.cs`

**No action needed** — activities within these workflows DO log.

### 1.4 Server/Infrastructure Files

| File | Status | Issue |
|------|--------|-------|
| `Tamma.ElsaServer/Program.cs` | GOOD | Serilog configured, startup logging present |
| `Tamma.ElsaServer/AgentSeeder.cs` | GOOD | Has ILogger<AgentSeeder> |
| `Tamma.ElsaServer/WorkflowSeeder.cs` | GOOD | Has ILogger<WorkflowSeeder> |

### 1.5 Tamma.Api Services

| File | ILogger Injected? | Actually Used? | Gap |
|------|-------------------|----------------|-----|
| `Services/GitHubIntegrationService.cs` | Yes | Yes (14 calls) | Good |
| `Services/ElsaWorkflowService.cs` | Yes | Yes (19 calls) | Good |
| `Services/WorkflowSyncService.cs` | Yes | Yes (17 calls) | Good |
| `Services/MentorshipService.cs` | Yes | Yes (7 calls) | Good |
| `Services/SlackIntegrationService.cs` | Yes | Yes (6 calls) | Good |
| `Services/CIIntegrationService.cs` | Yes | Yes (5 calls) | Good |
| `Services/JiraIntegrationService.cs` | Yes | Yes (5 calls) | Good |
| `Services/AnalyticsService.cs` | Yes | Minimal (2 Debug calls) | **MEDIUM** — No logging for aggregation queries, error paths, or pattern detection |
| `Services/EmailIntegrationService.cs` | Yes | Yes (1 call) | OK — stub implementation |
| `Services/IntegrationService.cs` | **NO** | **NO** | **HIGH** — Facade with 15+ delegation methods, throws InvalidOperationException on errors but never logs. Should log all failed delegations |
| `Controllers/MentorshipController.cs` | Yes | Yes (8 calls) | Good |

### 1.6 Tamma.Data Repository

| File | Issue | Priority |
|------|-------|----------|
| `Repositories/MentorshipSessionRepository.cs` | **No ILogger** — 20+ database operations with zero logging. State transitions, session creation/deletion, event logging all happen silently. | **MEDIUM** — Database queries typically don't need verbose logging, but state transitions and error cases should be logged |

### 1.7 Tamma.Api Program.cs

| Item | Status |
|------|--------|
| Serilog configured | Yes |
| Request logging | Yes (`UseSerilogRequestLogging()`) |
| Startup logging | Yes |
| Missing: No structured log for service registration failures | MEDIUM |

---

## Part 2: TypeScript Code Gaps

### 2.1 packages/cli/src/ — MIXED

| File | Logging Approach | Issue |
|------|-----------------|-------|
| `commands/process-issue.ts` | `createLogger` from `@tamma/observability` + ILogger | GOOD |
| `commands/server.ts` | `createLogger` from `@tamma/observability` | GOOD |
| `worker/result-callback.ts` | ILogger from `@tamma/shared/contracts` | GOOD |
| `log-emitter.ts` | Creates ILogger bridge for TUI | GOOD |
| `config.ts` | `console.warn` (2 calls) | **LOW** — Config warnings during parse; acceptable at startup |
| `commands/upgrade.ts` | `console.log` throughout | **MEDIUM** — CLI UX output, but error paths should use structured logger |
| `commands/init-fullstack.ts` | `console.log` throughout | **LOW** — One-off scaffolding command |
| `error-handler.ts` | No logging | **HIGH** — Global error handler should log to structured logger |
| `preflight.ts` | No logging | **MEDIUM** — Pre-flight checks should log results |
| `state.ts` | No logging | LOW |
| `file-logger.ts` | This IS the logger | N/A |
| `update-check.ts` | No logging | LOW |
| `utils.ts` | No logging | LOW |

### 2.2 packages/api/src/ — CRITICAL GAPS

**Severity: HIGH** — The entire API package uses `console.log/warn/error` instead of structured logging.

| Area | Files Without Structured Logging | What Should Be Logged |
|------|----------------------------------|----------------------|
| **serve.ts** (entrypoint) | Uses `console.log/warn` (15 calls) | Startup config, DB mode, GitHub App config, OAuth status, shutdown |
| **20 route handlers** (all routes except engine/index.ts) | ZERO logging in: github-webhook, github-callback, github-oauth, engine-callback, all knowledge-base routes (6), all settings routes (6), all saas routes (4) | Request received, input validation failures, service call errors, response status, auth failures |
| **engine/index.ts** | 3 calls via `fastify.log` | Only route with any logging |
| **Services** (non-C#, TS) | `IndexManagementService.ts` has 1 `console.error` | All 6 knowledge-base services (AnalyticsService, ContextTestingService, IndexManagementService, MCPManagementService, RAGManagementService, VectorDBManagementService) have no or minimal logging |
| **settings services** | ConfigService, DiagnosticsService, HealthService, ElsaAgentsClient, repo-config-reader | No structured logging |
| **Persistence** | workflow-store, installation-store, pg-installation-store, user-store, pg-user-store | Zero logging for DB operations |
| **Auth** | api-key-auth.ts, api-key.ts | Zero logging for auth decisions |
| **Engine registry** | engine-registry.ts | No logging |

### 2.3 packages/orchestrator/src/ — MIXED

| File | Status | Issue |
|------|--------|-------|
| `engine.ts` | GOOD — Uses ILogger throughout | Core engine logs state transitions |
| `saas-coordinator.ts` | GOOD — Uses ILogger | Logs coordination events |
| `transports/in-process.ts` | GOOD — Uses ILogger | Logs transport events |
| `elsa-client.ts` | **NO LOGGING** | **HIGH** — HTTP client for ELSA server API, should log requests, responses, errors |
| `workflow-engine.ts` | **NO LOGGING** | **HIGH** — Workflow engine adapter, should log workflow dispatches, status checks |
| `transports/remote.ts` | **NO LOGGING** | **MEDIUM** — Remote transport, should log connection events, message passing |
| `index.ts` | N/A | Just exports |

### 2.4 packages/providers/src/ — CRITICAL GAPS

**Severity: HIGH** — No AI provider implementation has logging.

| File | Issue | What Should Be Logged |
|------|-------|----------------------|
| `claude-agent-provider.ts` | **NO LOGGING** | Session start/stop, message send/receive, tool calls, errors, token usage |
| `opencode-provider.ts` | **NO LOGGING** | Same as above |
| `openrouter-provider.ts` | **NO LOGGING** | Same as above |
| `zen-mcp-provider.ts` | **NO LOGGING** | Same as above |
| `provider-chain.ts` | **NO LOGGING** | Chain selection, fallback events, all providers failed |
| `provider-health.ts` | **NO LOGGING** | Health check results, circuit breaker state changes |
| `factory.ts` | **NO LOGGING** | Provider instantiation, config loading |
| `instrumented-agent-provider.ts` | **NO LOGGING** | Ironic — the "instrumented" wrapper has no logging |
| `instrumented-llm-provider.ts` | **NO LOGGING** | Same irony |
| `agent-provider-factory.ts` | Uses ILogger from `@tamma/shared/contracts` | GOOD |
| `secure-agent-provider.ts` | Uses ILogger from `@tamma/shared/contracts` | GOOD |
| `agent-prompt-registry.ts` | Uses `console.warn` | **MEDIUM** — Should use structured logger |
| `role-based-agent-resolver.ts` | Uses `console.warn` | **MEDIUM** — Should use structured logger |
| `diagnostics-processor.ts` | Uses `console.warn` | **MEDIUM** — Should use structured logger |
| `registry.ts` | Uses `console.warn` | **MEDIUM** — Should use structured logger |

### 2.5 packages/platforms/src/ — CRITICAL GAPS

**Severity: HIGH** — Entire package has zero logging.

| File | What Should Be Logged |
|------|----------------------|
| `github/github-platform.ts` | API calls, rate limiting events, pagination, errors |
| `github/github-platform-factory.ts` | Platform instantiation, auth type used |
| `github/github-rate-limiter.ts` | Rate limit hits, backoff events, remaining quota |
| `github/github-error-mapper.ts` | Error classification decisions |
| `github/github-mappers.ts` | Data transformation warnings (missing fields) |

### 2.6 packages/shared/src/security/ — PARTIAL

| File | Status | Issue |
|------|--------|-------|
| `content-sanitizer.ts` | Has optional ILogger | GOOD |
| `url-validator.ts` | **NO LOGGING** | **MEDIUM** — Should log blocked URLs, validation failures |
| `action-gating.ts` | **NO LOGGING** | **HIGH** — Should log gating decisions (allow/deny), escalation triggers |
| `secure-fetch.ts` | **NO LOGGING** | **HIGH** — Should log fetch attempts, blocked domains, redirect following |

### 2.7 packages/intelligence/src/ — CRITICAL GAPS

**Severity: HIGH** — 55+ source files with no logging out of ~60 total.

**Files WITH logging (5):**
- `context/aggregator.ts` — Uses ILogger
- `indexer/codebase-indexer.ts` — Uses ILogger
- `indexer/discovery/git-diff-detector.ts` — Uses ILogger
- `indexer/triggers/file-watcher.ts` — Uses console
- `indexer/triggers/scheduler.ts` — Uses console
- `knowledge-base/knowledge-service.ts` — Uses console
- `rag/query-processor.ts` — Uses ILogger
- `rag/retriever.ts` — Uses ILogger
- `vector-store/base-vector-store.ts` — Uses ILogger

**Files WITHOUT logging (55+) — by sub-module:**

| Sub-module | Files Missing Logging | Priority | What Should Be Logged |
|------------|----------------------|----------|----------------------|
| **RAG pipeline** | rag-pipeline.ts, assembler.ts, ranker.ts, cache.ts, feedback.ts, all 5 sources/ | HIGH | Query processing, source retrieval, ranking decisions, cache hits/misses |
| **Indexer/embedding** | All 5 embedding providers (openai, ollama, cohere, base, mock), embedding-service.ts | HIGH | Embedding generation, batch processing, API calls, errors |
| **Indexer/chunking** | All 4 chunkers (generic, typescript, base, factory) | MEDIUM | Chunk boundaries, file processing, language detection |
| **Indexer/discovery** | file-discovery.ts, gitignore-parser.ts | MEDIUM | File scan results, ignored paths |
| **Indexer/triggers** | git-hook-installer.ts | LOW | Hook installation success/failure |
| **Indexer/metadata** | hash-calculator.ts, token-counter.ts | LOW | Pure utility functions |
| **Context** | All 5 sources, assembler, budget-manager, deduplicator, ranker, cache (redis, memory) | HIGH | Context assembly, budget allocation, dedup decisions, source fetching |
| **Knowledge base** | All matchers (keyword, pattern, semantic, relevance-ranker), pre-task-checker, stores, capture, prompt builder | HIGH | Knowledge retrieval, matching decisions, learning capture events |
| **Vector store** | All 5 providers (pgvector, chromadb, qdrant, weaviate, pinecone), factory, cache, utils | HIGH | Vector operations, similarity searches, index management |

### 2.8 packages/observability/src/ — GOOD (this IS the logging package)

- `logger.ts` — Creates pino-based ILogger instances
- `simple-logger.ts` — Lightweight logger alternative

### 2.9 Additional Packages — CRITICAL GAPS

| Package | Source Files | With Logging | Severity | What Should Be Logged |
|---------|-------------|-------------|----------|----------------------|
| `packages/cost-monitor/src/` | 12 | 0 | **HIGH** | Token usage tracking, cost calculations, budget alerts, provider cost comparisons |
| `packages/mcp-client/src/` | 30 | 0 | **HIGH** | MCP tool invocations, server connections, tool results, protocol errors |
| `packages/gates/src/` | 15 | 4 | **MEDIUM** | Gate evaluations, check results (11 files missing logging) |
| `packages/scrum-master/src/` | 12 | 0 | **HIGH** | Task assignment, sprint planning, standup generation, blocker detection |
| `packages/dashboard/src/` | 17 | 0 | **MEDIUM** | Dashboard is UI/React — logging less critical, but API calls and error boundaries should log |
| `packages/events/src/` | 1 | 0 | **HIGH** | Event store operations — this is core infrastructure |
| `packages/workers/src/` | 1 | 0 | **HIGH** | Worker task processing, completion callbacks |

---

## Part 3: Story Logging Requirements Gaps

### Stories WITH `## Logging Requirements` section: **15 files** (all in epics 11-14)

### Stories WITHOUT `## Logging Requirements` section that SHOULD have one:

The following main stories (excluding task-level breakdowns, implementation plans, and documentation-only stories) need a `## Logging Requirements` section added:

**Epic 1 — Foundation (8 main stories + 1.5 stories):**
- Stories 1-1 through 1-11: AI provider interfaces, implementations, Git platforms
- Stories 1.5-1 through 1.5-15: Engine separation, CLI modes, deployment, SaaS

**Epic 2 — Autonomous Development (16 stories):**
- Stories 2-1 through 2-16: Issue selection, context analysis, plan generation, TDD, PR creation

**Epic 3 — Quality Gates (12 stories):**
- Stories 3-1 through 3-12: Build gates, test gates, escalation, research, security scanning

**Epic 4 — Event Sourcing (8 stories):**
- Stories 4-1 through 4-8: Event schema, event store, event capture, time-travel

**Epic 5 — Observability (11 stories):**
- Stories 5-1 through 5-10: Structured logging (5-1 IS about logging), metrics, dashboards, alerts
- Note: Story 5-1 is specifically about structured logging implementation — it should have requirements for WHAT to log

**Epic 6 — Intelligence (10 stories):**
- Stories 6-1 through 6-10: Codebase indexer, vector DB, RAG, MCP, context aggregator, knowledge base

**Epic 7 — Mentorship (19 stories):**
- Stories 7-1 through 7-10, 7-1A through 7-1I: State machine, workflows, activities

**Epic 8 — Packaging (8 stories):**
- Stories 8-1 through 8-8: Bundling, Docker, CI/CD (some don't need logging)

**Epic 9 — Provider System (11 stories):**
- Stories 9-1 through 9-11: Config, diagnostics, health, factory, chain, prompts, sanitization

**Epic 10 — Engine Core (8 stories):**
- Stories 10-1 through 10-8: Engine workflow, event catalog, event store, queue, workflow abstraction

**Total: ~100+ main stories need `## Logging Requirements` added**

Note: Sub-task files (e.g., `1-3-provider-configuration-management-task-1.md`) do NOT need their own logging section — the parent story covers it. Research-only stories (1-0) and pure documentation stories (5-9a, 5-9b, etc.) can be skipped.

---

## Recommended Actions

### Priority 1 (Critical — Fix Now)

1. **`RecordDiagnosticsInlineActivity.cs`** — Add ILogger via `context.GetRequiredService<ILogger<RecordDiagnosticsInlineActivity>>()` and log:
   - Diagnostic deserialization failures (currently silent catch blocks)
   - Circuit breaker state changes
   - Budget updates with cost estimates

2. **`packages/api/src/serve.ts`** — Replace all `console.log/warn/error` with structured logger from `@tamma/observability`

3. **`packages/api/src/routes/`** — Add `request.log` or `fastify.log` calls to all 20 route handler files

4. **`packages/providers/src/` AI providers** — Add ILogger to claude-agent-provider, opencode-provider, openrouter-provider, zen-mcp-provider

### Priority 2 (High — Next Sprint)

5. **`packages/platforms/src/`** — Add ILogger to github-platform.ts and all GitHub modules
6. **`packages/orchestrator/src/`** — Add ILogger to elsa-client.ts, workflow-engine.ts, transports/remote.ts
7. **`packages/shared/src/security/`** — Add ILogger to action-gating.ts, secure-fetch.ts, url-validator.ts
8. **`Tamma.Api/Services/IntegrationService.cs`** — Add ILogger, log all delegation failures
9. **`packages/intelligence/src/`** — Add ILogger to RAG pipeline, embedding providers, vector store providers, context sources

### Priority 3 (Medium — When Touching These Files)

10. **`packages/api/src/persistence/`** — Add logging to store implementations
11. **`packages/api/src/auth/`** — Add logging for auth decisions
12. **`packages/api/src/services/`** — Add logging to knowledge-base services, settings services
13. **`Tamma.Data/Repositories/MentorshipSessionRepository.cs`** — Add ILogger for state transitions and error cases
14. **`packages/providers/src/`** — Replace `console.warn` with ILogger in registry, diagnostics-processor, agent-prompt-registry, role-based-agent-resolver
15. **`packages/intelligence/src/`** — Add ILogger to chunkers, matchers, knowledge base stores, metadata utilities

### Priority 4 (Low — Informational)

16. **CLI commands** — `upgrade.ts`, `init-fullstack.ts` use `console.log` for UX output; acceptable for CLI commands
17. **Workflow files** — WorkflowBase subclasses don't need logging (declarative definitions)
18. **Model/Types files** — Don't need logging
19. **`packages/cli/src/error-handler.ts`** — Should pipe errors to structured logger when available

---

## Logging Standards Reference

### C# Pattern (Tamma.Activities)
```csharp
// Constructor injection (for Activity subclass):
private readonly ILogger<MyActivity>? _logger;
public MyActivity(ILogger<MyActivity> logger) { _logger = logger; }

// Service locator (for CodeActivity subclass):
var logger = context.GetRequiredService<ILogger<MyActivity>>();

// Usage:
_logger?.LogInformation("Activity started: {Param}", param);
_logger?.LogWarning("Retryable failure: {Error}", error);
_logger?.LogError(ex, "Fatal error in {Activity}", nameof(MyActivity));
```

### TypeScript Pattern
```typescript
import type { ILogger } from '@tamma/shared/contracts';

class MyService {
  constructor(private readonly logger: ILogger) {}

  async doWork(): Promise<void> {
    this.logger.info('Starting work', { issueId, provider });
    try {
      // ...
      this.logger.info('Work completed', { duration, result });
    } catch (err) {
      this.logger.error('Work failed', { error: String(err), issueId });
      throw err;
    }
  }
}
```

### What to Log at Each Level
- **DEBUG**: Input parameters, intermediate state, cache hits/misses
- **INFO**: Activity start/complete, API calls made, key decisions, state transitions
- **WARN**: Retryable errors, degraded mode, fallback triggered, rate limiting
- **ERROR**: Unrecoverable errors, API failures, data corruption, security violations
