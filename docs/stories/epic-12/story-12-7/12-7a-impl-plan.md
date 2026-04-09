# Story 12-7a: Vector DB Search Tools -- Implementation Plan

## Overview

Implement three `IToolExecutor` C# classes (`SearchCodeSemanticTool`, `SearchFindingsTool`, `SearchStoriesTool`) that query the vector database via HTTP calls to the Node.js API, plus the corresponding API endpoints in `packages/api/`. Each tool is registered in DI and discovered by the existing `ToolExecutorRegistry`.

---

## Step-by-Step Implementation Tasks

### Task 1: Node.js API Endpoints for Context Tools (4 hours)

**File to create**: `packages/api/src/routes/context-tools.ts`

These endpoints bridge the C# tool executors to the `@tamma/intelligence` services.

```typescript
import type { FastifyInstance } from 'fastify';
import type { IVectorStoreService, IRAGPipeline } from '../services/knowledge-base/types.js';

interface SearchRequest {
  accountId: string;
  query: string;
  filters?: Record<string, unknown>;
  maxResults?: number;
  scoreThreshold?: number;
}

interface SearchResponse {
  results: Array<{
    id: string;
    score: number;
    content: string;
    metadata: Record<string, unknown>;
  }>;
  tokenEstimate: number;
  latencyMs: number;
}

export async function contextToolRoutes(app: FastifyInstance): Promise<void> {
  // POST /api/v1/context-tools/search-code
  app.post<{ Body: SearchRequest }>('/api/v1/context-tools/search-code', async (request, reply) => {
    const { accountId, query, filters, maxResults = 10, scoreThreshold = 0.5 } = request.body;
    // Validate accountId matches authenticated tenant
    // Query vectorStore.search(`codebase_${accountId}`, { text: query, limit: maxResults })
    // Return SearchResponse
  });

  // POST /api/v1/context-tools/search-findings
  app.post<{ Body: SearchRequest }>('/api/v1/context-tools/search-findings', async (request, reply) => {
    const { accountId, query, filters, maxResults = 5 } = request.body;
    // Query vectorStore.search(`findings_${accountId}`, { text: query, limit: maxResults })
    // Apply filters for finding_type, severity
    // Return SearchResponse
  });

  // POST /api/v1/context-tools/search-stories
  app.post<{ Body: SearchRequest }>('/api/v1/context-tools/search-stories', async (request, reply) => {
    const { accountId, query, filters, maxResults = 5 } = request.body;
    // Query vectorStore.search(`docs_${accountId}`, { text: query, limit: maxResults })
    // Apply filters for doc_type
    // Return SearchResponse
  });
}
```

**Register routes** in `packages/api/src/routes/index.ts` (modify existing file).

Token estimation: count characters in result content, divide by 4 (rough token estimate).

---

### Task 2: SearchCodeSemanticTool (4 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchCodeSemanticTool.cs`

```csharp
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Semantic code search via vector database embeddings.
/// Complements the regex-based SearchCodeTool with conceptual/semantic matching.
/// Queries the Node.js API which bridges to @tamma/intelligence vector store.
/// </summary>
public class SearchCodeSemanticTool : IToolExecutor
{
    private readonly ILogger<SearchCodeSemanticTool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBaseUrl;
    private readonly int _timeoutMs;

    public string ToolName => "search_code_semantic";

    public string Description =>
        "Search the codebase using semantic/conceptual similarity. " +
        "Finds code related to your query even if it doesn't contain the exact words. " +
        "Use this when you need to find implementations of a concept, not a specific pattern.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["query"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Natural language description of the code you're looking for"
            },
            ["file_pattern"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional file glob filter (e.g. '*.cs', 'src/**/*.ts')"
            },
            ["language"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional programming language filter (e.g. 'typescript', 'csharp')"
            },
            ["max_results"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum results to return (default: 10, max: 20)"
            },
            ["score_threshold"] = new Dictionary<string, object>
            {
                ["type"] = "number",
                ["description"] = "Minimum relevance score 0.0-1.0 (default: 0.5)"
            }
        },
        ["required"] = new[] { "query" }
    };

    // Constructor, ExecuteAsync, helper methods...
}
```

Key implementation details:
- Reads `accountId` from `IConfiguration["CurrentAccountId"]` (set by the workflow context)
- Makes HTTP POST to `{apiBaseUrl}/api/v1/context-tools/search-code`
- Formats results as structured text with file paths, line ranges, scores, and code snippets
- Includes token estimate in a header comment for budget tracking
- Uses `IHttpClientFactory` for connection pooling and resilience

---

### Task 3: SearchFindingsTool (3 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchFindingsTool.cs`

```csharp
public class SearchFindingsTool : IToolExecutor
{
    public string ToolName => "search_findings";

    public string Description =>
        "Search previous scan findings (security vulnerabilities, code quality issues, " +
        "performance problems) from prior analysis of this repository.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["query"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "What kind of findings to search for"
            },
            ["finding_type"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "security", "quality", "performance", "all" },
                ["description"] = "Filter by finding type (default: all)"
            },
            ["severity"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "critical", "high", "medium", "low" },
                ["description"] = "Filter by minimum severity"
            },
            ["max_results"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum results to return (default: 5)"
            }
        },
        ["required"] = new[] { "query" }
    };

    // ExecuteAsync calls /api/v1/context-tools/search-findings
    // Formats results: [SEVERITY] Title - Description - File - Recommendation
}
```

---

### Task 4: SearchStoriesTool (3 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchStoriesTool.cs`

```csharp
public class SearchStoriesTool : IToolExecutor
{
    public string ToolName => "search_stories";

    public string Description =>
        "Search project stories, technical specs, architecture docs, and design documents. " +
        "Useful for understanding requirements, design decisions, and implementation guidelines.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["query"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "What to search for in project documentation"
            },
            ["doc_type"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "story", "spec", "architecture", "all" },
                ["description"] = "Filter by document type (default: all)"
            },
            ["max_results"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum results to return (default: 5)"
            }
        },
        ["required"] = new[] { "query" }
    };

    // ExecuteAsync calls /api/v1/context-tools/search-stories
    // Formats results: [TYPE] Title - Excerpt - Path
}
```

---

### Task 5: DI Registration (1 hour)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` (or equivalent DI setup file)

Register all three tools as `IToolExecutor` implementations:

```csharp
services.AddScoped<IToolExecutor, SearchCodeSemanticTool>();
services.AddScoped<IToolExecutor, SearchFindingsTool>();
services.AddScoped<IToolExecutor, SearchStoriesTool>();
```

Register a named `HttpClient` for context tool API calls:

```csharp
services.AddHttpClient("ContextToolsApi", client =>
{
    client.BaseAddress = new Uri(configuration["ContextTools:ApiBaseUrl"] ?? "http://localhost:3000");
    client.Timeout = TimeSpan.FromMilliseconds(
        int.Parse(configuration["ContextTools:SearchTimeoutMs"] ?? "3000"));
});
```

---

### Task 6: Unit Tests (4 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/SearchCodeSemanticToolTests.cs`

```csharp
// 4 tests:
// 1. ExecuteAsync_WithQuery_CallsApiAndReturnsFormattedResults
// 2. ExecuteAsync_WithFilters_PassesFiltersToApi
// 3. ExecuteAsync_ApiTimeout_ReturnsTimeoutMessage
// 4. ExecuteAsync_ApiError_ReturnsErrorMessageDoesNotThrow
```

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/SearchFindingsToolTests.cs`

```csharp
// 4 tests:
// 1. ExecuteAsync_WithQuery_ReturnsFormattedFindings
// 2. ExecuteAsync_WithSeverityFilter_FiltersResults
// 3. ExecuteAsync_NoFindingsCollection_ReturnsHelpfulMessage
// 4. ExecuteAsync_ApiError_ReturnsErrorMessageDoesNotThrow
```

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/SearchStoriesToolTests.cs`

```csharp
// 4 tests:
// 1. ExecuteAsync_WithQuery_ReturnsFormattedDocs
// 2. ExecuteAsync_WithDocTypeFilter_FiltersResults
// 3. ExecuteAsync_EmptyResults_ReturnsNoResultsMessage
// 4. ExecuteAsync_ApiError_ReturnsErrorMessageDoesNotThrow
```

**File to create**: `packages/api/src/routes/__tests__/context-tools.test.ts`

```typescript
// 6 tests:
// 1. POST /search-code returns results from vector store
// 2. POST /search-code validates accountId matches authenticated tenant
// 3. POST /search-findings filters by finding_type and severity
// 4. POST /search-findings returns empty array when collection missing
// 5. POST /search-stories filters by doc_type
// 6. All endpoints return tokenEstimate and latencyMs
```

---

### Task 7: Integration Tests (2 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ContextToolsIntegrationTests.cs`

```csharp
// 3 tests (require running API + vector DB):
// 1. SearchCodeSemanticTool end-to-end: index code, search, verify results
// 2. SearchFindingsTool end-to-end: index findings, search with filters
// 3. Account isolation: tool for account A cannot see account B's data
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/routes/context-tools.ts` | API endpoints for context tool queries |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchCodeSemanticTool.cs` | Semantic code search tool |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchFindingsTool.cs` | Findings search tool |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchStoriesTool.cs` | Stories/docs search tool |
| 5 | `apps/tamma-elsa/tests/.../SearchCodeSemanticToolTests.cs` | Unit tests |
| 6 | `apps/tamma-elsa/tests/.../SearchFindingsToolTests.cs` | Unit tests |
| 7 | `apps/tamma-elsa/tests/.../SearchStoriesToolTests.cs` | Unit tests |
| 8 | `packages/api/src/routes/__tests__/context-tools.test.ts` | API endpoint tests |
| 9 | `apps/tamma-elsa/tests/.../ContextToolsIntegrationTests.cs` | Integration tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/index.ts` | Register context-tools routes |
| 2 | `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` | Register tool executors in DI |
| 3 | `apps/tamma-elsa/src/Tamma.ElsaServer/appsettings.json` | Add `ContextTools` config section |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Vector DB not indexed for a repo | Return "No indexed code found. Run indexing first." instead of error |
| Embedding API latency adds to search time | Cache query embeddings with short TTL (60s); use fast embedding model |
| Large result sets consume too many tokens | Cap at 20 results; truncate individual snippets to 500 chars each |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Task 1: Node.js API endpoints | 4 |
| Task 2: SearchCodeSemanticTool | 4 |
| Task 3: SearchFindingsTool | 3 |
| Task 4: SearchStoriesTool | 3 |
| Task 5: DI registration | 1 |
| Task 6: Unit tests (12+ tests) | 4 |
| Task 7: Integration tests (3 tests) | 2 |
| Buffer (config, edge cases) | 3 |
| **Total** | **24 hours** |
