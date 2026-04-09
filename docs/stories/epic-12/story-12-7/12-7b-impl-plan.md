# Story 12-7b: Convention & History Tools -- Implementation Plan

## Overview

Implement two `IToolExecutor` C# classes (`SearchConventionsTool`, `SearchHistoryTool`) plus corresponding Node.js API endpoints. `SearchConventionsTool` resolves conventions from three priority layers (account overrides, repo config, language templates). `SearchHistoryTool` queries the event store for previous LLM call outputs scoped by issue, workflow, or repo.

---

## Step-by-Step Implementation Tasks

### Task 1: Node.js API Endpoints (3 hours)

**File to create**: `packages/api/src/routes/context-tools-conventions.ts`

```typescript
import type { FastifyInstance } from 'fastify';
import { CONVENTION_TEMPLATES } from '../services/convention-templates.js';

interface ConventionSearchRequest {
  accountId: string;
  repositoryId?: string;
  query?: string;
  category?: 'code_style' | 'testing' | 'error_handling' | 'logging' | 'imports' | 'all';
  language?: string;
}

interface ConventionSearchResponse {
  conventions: Array<{
    source: 'account_override' | 'repo_config' | 'template';
    category: string;
    content: string;
    priority: number;
  }>;
  tokenEstimate: number;
}

export async function conventionToolRoutes(app: FastifyInstance): Promise<void> {
  // POST /api/v1/context-tools/search-conventions
  app.post<{ Body: ConventionSearchRequest }>(
    '/api/v1/context-tools/search-conventions',
    async (request, reply) => {
      const { accountId, repositoryId, query, category, language } = request.body;

      const conventions: ConventionSearchResponse['conventions'] = [];

      // Layer 1: Account overrides (from prompt store or account config)
      // Query the prompt store for account-level convention overrides
      // SELECT conventions FROM account_config WHERE account_id = $1

      // Layer 2: Repository .tamma/config.json
      // Read from repo workspace if repositoryId is provided
      // Parse the "conventions" field

      // Layer 3: Convention templates (matched by language)
      const templateKey = language ?? 'typescript-node';
      const template = CONVENTION_TEMPLATES[templateKey];
      if (template) {
        conventions.push({
          source: 'template',
          category: 'all',
          content: template.conventions,
          priority: 0,
        });
      }

      // Filter by query (keyword matching) and category
      let filtered = conventions;
      if (query) {
        const queryLower = query.toLowerCase();
        filtered = conventions.filter(c =>
          c.content.toLowerCase().includes(queryLower) ||
          c.category.toLowerCase().includes(queryLower)
        );
      }
      if (category && category !== 'all') {
        filtered = filtered.filter(c =>
          c.category === category || c.category === 'all'
        );
      }

      const totalChars = filtered.reduce((sum, c) => sum + c.content.length, 0);
      return {
        conventions: filtered,
        tokenEstimate: Math.ceil(totalChars / 4),
      };
    }
  );
}
```

**File to create**: `packages/api/src/routes/context-tools-history.ts`

```typescript
import type { FastifyInstance } from 'fastify';

interface HistorySearchRequest {
  accountId: string;
  query: string;
  scope?: 'issue' | 'workflow' | 'repo';
  issueId?: string;
  workflowInstanceId?: string;
  repositoryId?: string;
  maxResults?: number;
  roleFilter?: string;
}

interface HistorySearchResponse {
  results: Array<{
    operationName: string;
    role: string;
    timestamp: string;
    excerpt: string;
    tokenCount: number;
    provider: string;
  }>;
  tokenEstimate: number;
  latencyMs: number;
}

export async function historyToolRoutes(app: FastifyInstance): Promise<void> {
  // POST /api/v1/context-tools/search-history
  app.post<{ Body: HistorySearchRequest }>(
    '/api/v1/context-tools/search-history',
    async (request, reply) => {
      const {
        accountId, query, scope = 'issue',
        issueId, workflowInstanceId, repositoryId,
        maxResults = 5, roleFilter
      } = request.body;

      // Query event store for LLM.CALL.COMPLETED events
      // Filter by scope:
      //   scope=issue -> tags.issueId = issueId AND tags.accountId = accountId
      //   scope=workflow -> tags.workflowInstanceId = workflowInstanceId
      //   scope=repo -> tags.repositoryId = repositoryId AND tags.accountId = accountId
      // If roleFilter, add tags.role = roleFilter
      // Order by relevance to query (semantic match on data.responseText) or recency
      // Limit to maxResults

      // Return HistorySearchResponse
    }
  );
}
```

Register both route files in `packages/api/src/routes/index.ts`.

---

### Task 2: SearchConventionsTool C# Implementation (4 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchConventionsTool.cs`

```csharp
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Retrieves project-specific coding conventions from three priority layers:
/// 1. Account-level overrides
/// 2. Repository .tamma/config.json
/// 3. Convention template library (matched by language/framework)
///
/// The LLM uses this to check coding standards mid-task instead of having
/// all conventions statically injected into every prompt.
/// </summary>
public class SearchConventionsTool : IToolExecutor
{
    private readonly ILogger<SearchConventionsTool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBaseUrl;

    public string ToolName => "search_conventions";

    public string Description =>
        "Look up project-specific coding conventions, style rules, and best practices. " +
        "Returns conventions for the current repository's language and framework. " +
        "Use this when you need to verify coding standards before writing or reviewing code.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["query"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional: specific convention topic to search for " +
                                  "(e.g. 'error handling', 'imports', 'testing'). " +
                                  "Omit to get all conventions."
            },
            ["category"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "code_style", "testing", "error_handling",
                                   "logging", "imports", "all" },
                ["description"] = "Filter by convention category (default: all)"
            }
        }
        // No required fields -- calling with no args returns all conventions
    };

    public SearchConventionsTool(
        ILogger<SearchConventionsTool> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _apiBaseUrl = configuration["ContextTools:ApiBaseUrl"] ?? "http://localhost:3000";
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Tool execution started: {ToolName} {ToolCallId}", ToolName, toolCallId);

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson ?? "{}");
            var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
            var category = args.TryGetProperty("category", out var c) ? c.GetString() ?? "all" : "all";

            var client = _httpClientFactory.CreateClient("ContextToolsApi");
            var response = await client.PostAsJsonAsync(
                $"{_apiBaseUrl}/api/v1/context-tools/search-conventions",
                new { query, category },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return new ToolExecutionResult(toolCallId, ToolName, false,
                    $"Convention lookup failed: {response.StatusCode}", sw.ElapsedMilliseconds);
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var output = FormatConventions(result);

            var successResult = new ToolExecutionResult(toolCallId, ToolName, true,
                output, sw.ElapsedMilliseconds);
            _logger.LogInformation(
                "Tool execution completed: {ToolName} {ToolCallId} duration={DurationMs}ms",
                ToolName, toolCallId, sw.ElapsedMilliseconds);
            return successResult;
        }
        catch (TaskCanceledException)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                "Convention lookup timed out.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tool execution failed: {ToolName} {ToolCallId}", ToolName, toolCallId);
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Convention lookup error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    private static string FormatConventions(JsonElement result)
    {
        // Format conventions as structured text
        // [SOURCE: account_override | repo_config | template]
        // ## Category
        // Convention content...
        return ""; // Placeholder
    }
}
```

---

### Task 3: SearchHistoryTool C# Implementation (4 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchHistoryTool.cs`

```csharp
public class SearchHistoryTool : IToolExecutor
{
    public string ToolName => "search_history";

    public string Description =>
        "Search previous LLM call results for this issue, workflow, or repository. " +
        "Useful for checking what a planner decided, what a previous implementer tried, " +
        "or what errors were encountered in prior attempts.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["query"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "What to search for in previous LLM call results"
            },
            ["scope"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "issue", "workflow", "repo" },
                ["description"] = "Search scope: 'issue' (same issue), " +
                                  "'workflow' (current workflow only), " +
                                  "'repo' (any workflow in this repo). Default: issue"
            },
            ["max_results"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum results to return (default: 5)"
            },
            ["role_filter"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional: filter by the role that produced the result " +
                                  "(e.g. 'developer', 'tester', 'planner')"
            }
        },
        ["required"] = new[] { "query" }
    };

    // ExecuteAsync:
    // 1. Read accountId, issueId, workflowInstanceId, repositoryId from workflow context
    // 2. POST to /api/v1/context-tools/search-history with scope filters
    // 3. Format results: [ROLE @ TIMESTAMP] OperationName\nExcerpt...
}
```

Key implementation details:
- `issueId` and `workflowInstanceId` are read from `IConfiguration` or Elsa workflow variables
- The tool passes these IDs to the API, which queries the event store
- Results are formatted with role, timestamp, and relevant excerpt
- Excerpts are truncated to keep total output under 4KB

---

### Task 4: DI Registration (30 min)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs`

```csharp
services.AddScoped<IToolExecutor, SearchConventionsTool>();
services.AddScoped<IToolExecutor, SearchHistoryTool>();
```

---

### Task 5: Unit Tests (2 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/SearchConventionsToolTests.cs`

```csharp
// 4 tests:
// 1. ExecuteAsync_NoQuery_ReturnsAllConventions
// 2. ExecuteAsync_WithQuery_FiltersConventionsByKeyword
// 3. ExecuteAsync_WithCategory_FiltersConventionsByCategory
// 4. ExecuteAsync_ApiError_ReturnsErrorMessageDoesNotThrow
```

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/SearchHistoryToolTests.cs`

```csharp
// 4 tests:
// 1. ExecuteAsync_IssuScope_QueriesEventStoreByIssueId
// 2. ExecuteAsync_WorkflowScope_QueriesEventStoreByWorkflowInstanceId
// 3. ExecuteAsync_WithRoleFilter_FiltersResultsByRole
// 4. ExecuteAsync_NoResults_ReturnsHelpfulMessage
```

**File to create**: `packages/api/src/routes/__tests__/context-tools-conventions.test.ts`

```typescript
// 4 tests:
// 1. POST /search-conventions returns template conventions for language
// 2. POST /search-conventions filters by query keyword
// 3. POST /search-history returns events filtered by scope
// 4. POST /search-history respects roleFilter
```

---

### Task 6: Integration Tests (1 hour)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ConventionHistoryIntegrationTests.cs`

```csharp
// 2 tests (require running API + event store):
// 1. SearchConventionsTool resolves from template when no overrides exist
// 2. SearchHistoryTool returns results from event store filtered by issueId
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/routes/context-tools-conventions.ts` | Convention search API endpoint |
| 2 | `packages/api/src/routes/context-tools-history.ts` | History search API endpoint |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchConventionsTool.cs` | Convention search tool |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/SearchHistoryTool.cs` | History search tool |
| 5 | `apps/tamma-elsa/tests/.../SearchConventionsToolTests.cs` | Unit tests |
| 6 | `apps/tamma-elsa/tests/.../SearchHistoryToolTests.cs` | Unit tests |
| 7 | `packages/api/src/routes/__tests__/context-tools-conventions.test.ts` | API tests |
| 8 | `apps/tamma-elsa/tests/.../ConventionHistoryIntegrationTests.cs` | Integration tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/index.ts` | Register convention and history tool routes |
| 2 | `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` | Register tool executors in DI |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| `.tamma/config.json` not present in repo | Fall back to template conventions; return helpful message |
| Event store has many events, history query slow | Index on `tags.issueId` and `tags.accountId`; limit to recent 30 days by default |
| Convention text too large for context window | Truncate to 2KB per convention source; let budget manager handle limits |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Task 1: Node.js API endpoints (2 routes) | 3 |
| Task 2: SearchConventionsTool | 4 |
| Task 3: SearchHistoryTool | 4 |
| Task 4: DI registration | 0.5 |
| Task 5: Unit tests (12 tests) | 2 |
| Task 6: Integration tests (2 tests) | 1 |
| Buffer | 1.5 |
| **Total** | **16 hours** |
