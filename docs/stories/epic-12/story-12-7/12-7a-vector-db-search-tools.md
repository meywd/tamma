# Story 12-7a: Vector DB Search Tools

Status: ready-for-dev

## Story

As an **LLM agent in the tool loop**,
I want `search_code_semantic`, `search_findings`, and `search_stories` tools,
so that I can query the vector database for relevant code, prior scan findings, and documentation on demand.

## Summary

Define tool schemas and implement `IToolExecutor` handlers for three semantic search tools. Each tool accepts a natural language query with optional filters, queries the vector DB (ChromaDB/pgvector) via the existing `IVectorStoreService` and `IRAGPipeline`, and returns ranked results with snippets and metadata. All queries are account-scoped for tenant isolation.

## Acceptance Criteria

### AC1: search_code_semantic Tool
- [ ] Tool registered as `search_code_semantic` in the `IToolExecutorRegistry`
- [ ] Input: `query` (string, required), `file_pattern` (string, optional glob), `language` (string, optional), `max_results` (int, optional, default 10), `score_threshold` (float, optional, default 0.5)
- [ ] Generates embedding for the query string via the embedding provider
- [ ] Queries the vector DB collection `codebase_{accountId}` with metadata filters for file_pattern and language
- [ ] Returns results as structured text: file path, line range, relevance score, and code snippet
- [ ] Each result includes token count estimate for budget tracking
- [ ] Complements existing `search_code` (regex-based) -- semantic search finds conceptually related code, not just pattern matches
- [ ] Timeout: 3s default, configurable via `ToolExecution:ContextTools:SearchTimeoutMs`

### AC2: search_findings Tool
- [ ] Tool registered as `search_findings` in the `IToolExecutorRegistry`
- [ ] Input: `query` (string, required), `finding_type` (string, optional: "security", "quality", "performance", "all"), `severity` (string, optional: "critical", "high", "medium", "low"), `max_results` (int, optional, default 5)
- [ ] Queries the vector DB collection `findings_{accountId}` for prior ContextGathering scan results
- [ ] Filters by finding type and severity when provided
- [ ] Returns: finding title, severity, description, file path, recommendation, and when it was found
- [ ] If no findings collection exists (repo never scanned), returns a helpful message instead of an error

### AC3: search_stories Tool
- [ ] Tool registered as `search_stories` in the `IToolExecutorRegistry`
- [ ] Input: `query` (string, required), `doc_type` (string, optional: "story", "spec", "architecture", "all"), `max_results` (int, optional, default 5)
- [ ] Queries the vector DB collection `docs_{accountId}` for stories, specs, and architecture documents
- [ ] Returns: document title, type, relevant excerpt, file path, and relevance score
- [ ] Useful for understanding requirements and design decisions during implementation tasks

### AC4: Account Scoping (Tenant Isolation)
- [ ] All three tools receive `accountId` from the workflow context (injected via constructor or execution context)
- [ ] Collection names are always prefixed with `{accountId}` -- no tool can query another tenant's data
- [ ] If the account's collection does not exist, the tool returns an empty result set with a message, not an error

### AC5: Node.js API Bridge
- [ ] Three new API endpoints in `packages/api/src/routes/`:
  - `POST /api/v1/context-tools/search-code` -- calls `IVectorStoreService.search()` or `IRAGPipeline.retrieve()`
  - `POST /api/v1/context-tools/search-findings` -- calls `IVectorStoreService.search()` with findings collection
  - `POST /api/v1/context-tools/search-stories` -- calls `IVectorStoreService.search()` with docs collection
- [ ] Each endpoint accepts `{ accountId, query, filters, maxResults }` and returns `{ results, tokenEstimate, latencyMs }`
- [ ] Endpoints validate `accountId` matches the authenticated tenant

### AC6: Error Handling
- [ ] Embedding generation failure: return error message to LLM, do not throw
- [ ] Vector DB unreachable: return "Context search unavailable, proceeding without context" message
- [ ] Timeout: return partial results if any were received before timeout, otherwise a timeout message
- [ ] All errors logged with structured context `{ toolName, accountId, query, error }`

## Technical Context

### Existing search_code (regex-based) vs new search_code_semantic

The existing `SearchCodeTool` at `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchCodeTool.cs` performs regex pattern matching on files in the workspace. It is fast and precise for known patterns but cannot find conceptually related code.

`search_code_semantic` uses vector embeddings to find code that is semantically similar to the query. For example, querying "rate limiting middleware" would find rate limiter implementations even if they don't contain those exact words.

Both tools coexist -- the LLM chooses which to use based on the task.

### Vector DB Collections

Per-account collections (namespaced):
- `codebase_{accountId}` -- code chunks from the codebase indexer (Story 6-1)
- `findings_{accountId}` -- scan findings from ContextGathering workflow
- `docs_{accountId}` -- stories, specs, architecture docs, wiki pages

### Key Files

| File | Role |
|------|------|
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/IToolExecutor.cs` | Interface to implement |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolExecutorRegistry.cs` | Registration point |
| `packages/intelligence/src/vector-store/interfaces.ts` | `IVectorStore` interface |
| `packages/intelligence/src/rag/rag-pipeline.ts` | `IRAGPipeline` implementation |
| `packages/api/src/services/knowledge-base/VectorDBManagementService.ts` | Existing vector DB API service |
| `packages/api/src/services/knowledge-base/RAGManagementService.ts` | Existing RAG API service |
| `packages/api/src/services/knowledge-base/types.ts` | Local interface definitions |

## Dependencies

- **Epic 6-1**: Codebase indexer (produces the `codebase_{accountId}` collection)
- **Epic 6-2**: Vector store (`IVectorStore`, ChromaDB/pgvector adapter)
- **Epic 6-3**: RAG pipeline (`IRAGPipeline`)
- **Story 12-1**: `IToolExecutor` interface and registry
- **Story 12-2**: Agentic tool loop in `CallLlmInlineActivity`

## Estimated Effort

| Task | Hours |
|------|-------|
| C# `SearchCodeSemanticTool` implementation | 4 |
| C# `SearchFindingsTool` implementation | 3 |
| C# `SearchStoriesTool` implementation | 3 |
| Node.js API endpoints (3 routes) | 4 |
| Embedding query integration | 2 |
| Account scoping and tenant isolation | 2 |
| Unit tests (12+ tests) | 4 |
| Integration tests (3 tests) | 2 |
| **Total** | **24 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |
