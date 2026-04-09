# Story 12-7: LLM Context Tool Access

Status: ready-for-dev

## Story

As an **LLM agent executing within the agentic tool loop**,
I want tool-callable functions to query the vector database, knowledge base, conventions, and historical results,
so that I can pull exactly the context I need on demand instead of receiving a static context blob that may be irrelevant or incomplete.

## Motivation

Currently, the LLM Call workflow gathers context in a static pre-processing step (ContextGathering workflow) and dumps it into the prompt as a single blob. This has several problems:

1. **Wasted tokens**: The static blob includes context the LLM may not need for this particular sub-task, consuming valuable context window budget.
2. **Missing context**: The static blob may omit context the LLM discovers it needs mid-task (e.g., after reading a file and realizing it depends on another module).
3. **No iterative refinement**: The LLM cannot refine its search query based on initial results. A semantic search for "authentication" might return generic results, but the LLM knows it specifically needs "JWT token refresh logic in the middleware layer."
4. **No cross-concern context**: A security reviewer cannot currently pull prior scan findings mid-review. A developer cannot check what conventions apply to the current file type.

The solution: expose the RAG/vector DB, knowledge base, conventions, and history as **tool calls** within the existing agentic tool loop. The LLM decides what context it needs and when, using the same tool-calling mechanism it already uses for file operations, search, and shell commands.

## Sub-Stories

| Sub-Story | Title | Priority | Effort | Dependencies |
|-----------|-------|----------|--------|-------------|
| 12-7a | Vector DB Search Tools | P0 | 24h | Epic 6 (vector store, RAG pipeline) |
| 12-7b | Convention & History Tools | P0 | 16h | Prompt store (Epic 27), Event store |
| 12-7c | Context Budget Manager | P1 | 20h | 12-7a, 12-7b, Agent config (Epic 9) |
| 12-7d | Tool Access Configuration Per Role | P1 | 12h | 12-7a, 12-7b, Prompt store (Epic 27) |
| 12-7e | Elsa Tool Loop Integration | P0 | 20h | 12-7a, 12-7b, 12-7c, 12-7d |
| **Total** | | | **92h** | |

> **Note on estimates**: The 92h total covers implementation of each sub-story individually. It does **not** include integration testing overhead -- testing the full chain (LLM calls context tool -> C# executor -> HTTP to Node.js API -> vector DB/event store -> results back through tool loop) across all 5 tool types, multiple roles, and multiple providers. Suggest adding **+30h** for cross-sub-story integration testing, bringing the realistic total to approximately **122h**.

## Architecture

```
                         LLM Call Tool Loop
                         (CallLlmInlineActivity)
                                |
                    +-----------+-----------+
                    |                       |
              Existing Tools          New Context Tools (12-7)
              - file_read             - search_code_semantic (12-7a)
              - file_write            - search_findings (12-7a)
              - shell_execute         - search_stories (12-7a)
              - search_code           - search_conventions (12-7b)
              - git_operations        - search_history (12-7b)
              - run_tests
              - list_directory
              - batch_operations
                                      |
                              Context Budget Manager (12-7c)
                              - Tracks token usage
                              - Enforces provider limits
                              - Priority-based dropping
                                      |
                              Role-Based Access Config (12-7d)
                              - Per-role tool whitelist
                              - Account-level overrides
                                      |
                    +--------+---------+---------+
                    |        |         |         |
              Vector DB   RAG      Knowledge   Event
              (6-2)     Pipeline    Base       Store
                         (6-3)     (6-9)
```

### How It Works

1. **LlmCallWorkflow** resolves which context tools are available based on the agent's role and account config (12-7d).
2. Available tools are registered in the `IToolExecutorRegistry` alongside existing tools (12-7e).
3. During the tool loop, when the LLM calls `search_code_semantic`, the tool handler:
   - Checks the context budget (12-7c) to see how many tokens remain for context
   - Queries the vector DB via `IVectorStoreService` or the RAG pipeline via `IRAGPipeline`
   - Returns ranked results with snippets and metadata
   - Records token usage against the context budget
4. The LLM can call multiple context tools across turns, refining its queries based on earlier results.
5. When the context budget is nearly full, the budget manager signals the LLM via a system message or tool response warning.

### Tenant Isolation

All context tools are account-scoped:
- Vector DB collections are namespaced by `accountId` (e.g., `codebase_{accountId}`)
- Knowledge base queries filter by `accountId`
- Convention lookups use the account's repo configuration
- History queries filter by `accountId` and `workflowInstanceId`

## Context Tools Summary

| Tool Name | Description | Source | Epic |
|-----------|-------------|--------|------|
| `search_code_semantic` | Semantic search across codebase via vector DB | Vector DB (6-2) / RAG (6-3) | 12-7a |
| `search_findings` | Retrieve prior scan findings for this repo/issue | Vector DB (6-2) / Event Store | 12-7a |
| `search_stories` | Find related stories, specs, architecture docs | Vector DB (6-2) / RAG (6-3) | 12-7a |
| `search_conventions` | Get project-specific conventions for current repo | Convention store / `.tamma/config.json` | 12-7b |
| `search_history` | Previous LLM call results for this issue/workflow | Event Store / Vector DB | 12-7b |

## Technical Context

### Existing Infrastructure (already implemented)

| Component | Location | Status |
|-----------|----------|--------|
| `IToolExecutor` interface | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs` | Done |
| `IToolExecutorRegistry` | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutorRegistry.cs` | Done |
| `ToolExecutorRegistry` | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs` | Done |
| Tool loop in `CallLlmInlineActivity` | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Done |
| `ToolLoopConfig` (AllowedTools) | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | Done |
| `ContextCompactor` | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextCompactor.cs` | Done |
| `SearchCodeTool` (regex-based) | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchCodeTool.cs` | Done |
| `ResolveToolsActivity` | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` | Done |
| Vector Store (`IVectorStore`) | `packages/intelligence/src/vector-store/interfaces.ts` | Done |
| RAG Pipeline (`IRAGPipeline`) | `packages/intelligence/src/rag/rag-pipeline.ts` | Done |
| Knowledge Base service | `packages/intelligence/src/knowledge-base/knowledge-service.ts` | Done |
| Context Budget Manager | `packages/intelligence/src/context/budget-manager.ts` | Done |
| Convention templates | `packages/api/src/services/convention-templates.ts` | Done |
| KB API services (mocks) | `packages/api/src/services/knowledge-base/` | Done (mock) |

### What Needs Building

1. **C# IToolExecutor implementations** for each context tool (calls the Node.js API services via HTTP)
2. **Node.js API endpoints** that bridge tool requests to `@tamma/intelligence` services
3. **Context budget tracking** within the Elsa tool loop (token accounting across tool results)
4. **Role-based tool configuration** extending `ToolLoopConfig.AllowedTools`
5. **Integration wiring** in `LlmCallWorkflow` to pass context tool availability based on role

## Success Metrics

- Context retrieval latency p95 < 300ms per tool call
- Token savings: 30%+ reduction in prompt tokens vs static context blob (measured by A/B)
- Agent task success rate improvement > 10% (compared to static context)
- Context tool usage: >50% of tool loop sessions use at least one context tool
- Tenant isolation: 0 cross-account data leaks

## Dependencies

- **Epic 6**: Vector store (6-2), RAG pipeline (6-3), knowledge base (6-9) must be operational
- **Epic 12**: Tool loop (12-1, 12-2) already implemented
- **Epic 27**: Prompt store for role/action prompt templates
- **Epic 9**: Agent configuration for provider-specific context window limits

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Vector DB latency spikes under load | Per-tool timeout (2s default), graceful degradation returning "search timed out" |
| LLM over-uses context tools, wasting turns | Context budget manager limits total context tokens; turn limit in tool loop config |
| Embedding API costs for query embedding | Cache query embeddings; batch similar queries; use lighter embedding models for search |
| Cross-tenant data leakage via search | Collection namespacing by accountId; filter at query level AND result level |
| Context tools return stale data | TTL-based cache invalidation; re-index triggers on push events |

---

**Last Updated**: 2026-04-08
**Epic**: 12 (Agentic Tool Loop)
**Status**: Ready for dev -- sub-stories below
