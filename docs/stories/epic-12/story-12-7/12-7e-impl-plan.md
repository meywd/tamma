# Story 12-7e: Elsa Tool Loop Integration -- Implementation Plan

## Overview

Wire all context tool components (12-7a through 12-7d) into the Elsa workflow engine. Register tools in DI, modify `CallLlmInlineActivity` to integrate the budget manager, update `ResolveToolsActivity` to merge context tools, propagate account context, add diagnostics, and ensure error resilience.

---

## Step-by-Step Implementation Tasks

### Task 1: DI Registration and Configuration (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs`

Add registrations for all context tool components:

```csharp
// Context Tools - Tool Executors
services.AddScoped<IToolExecutor, SearchCodeSemanticTool>();
services.AddScoped<IToolExecutor, SearchFindingsTool>();
services.AddScoped<IToolExecutor, SearchStoriesTool>();
services.AddScoped<IToolExecutor, SearchConventionsTool>();
services.AddScoped<IToolExecutor, SearchHistoryTool>();

// Context Tools - Budget & Priority
services.AddScoped<ContextResultPrioritizer>();

// Context Tools - HTTP Client
services.AddHttpClient("ContextToolsApi", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["ContextTools:ApiBaseUrl"] ?? "http://localhost:3000");
    client.Timeout = TimeSpan.FromMilliseconds(
        int.Parse(config["ContextTools:SearchTimeoutMs"] ?? "3000"));
});
```

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/appsettings.json`

Add context tools configuration section:

```json
{
  "ContextTools": {
    "Enabled": true,
    "ApiBaseUrl": "http://localhost:3000",
    "SearchTimeoutMs": 3000,
    "DisabledTools": [],
    "BudgetFraction": 0.5,
    "CircuitBreaker": {
      "FailureThreshold": 3,
      "CooldownSeconds": 60
    }
  }
}
```

---

### Task 2: CallLlmInlineActivity Integration (6 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

This is the core integration point. Changes in the tool loop section:

#### 2a: Initialize Budget Manager at Tool Loop Start

```csharp
// Inside the tool loop initialization block:
ContextToolBudgetManager? contextBudget = null;
ContextResultPrioritizer? prioritizer = null;

if (toolLoopConfig != null && IsContextToolsEnabled(configuration))
{
    prioritizer = serviceProvider.GetService<ContextResultPrioritizer>();
    contextBudget = new ContextToolBudgetManager(
        contextWindowTokens: toolLoopConfig.ContextWindowTokens,
        systemPromptTokens: TokenEstimator.EstimateTokens(resolvedPrompt.SystemPrompt),
        userPromptTokens: TokenEstimator.EstimateTokens(resolvedPrompt.UserPrompt),
        reservedOutputTokens: input.MaxTokens,
        contextBudgetFraction: double.Parse(
            configuration["ContextTools:BudgetFraction"] ?? "0.5"),
        logger: serviceProvider.GetRequiredService<ILogger<ContextToolBudgetManager>>());
}
```

#### 2b: Process Context Tool Results in the Tool Loop

After each tool execution, check if it's a context tool and record usage:

```csharp
// After tool execution returns result:
if (contextBudget != null && IsContextTool(toolCall.Name))
{
    var tokenCount = TokenEstimator.EstimateTokens(result.Output);
    var priority = prioritizer?.GetPriority(
        toolCall.Name, ExtractScore(result), input.Role) ?? ContextPriority.Normal;

    var allocated = contextBudget.RecordUsage(
        toolCall.Name, toolCall.Id, tokenCount, priority);

    // Truncate if budget limited the allocation
    if (allocated < tokenCount && allocated > 0)
    {
        result = result with
        {
            Output = ContextToolBudgetManager.TruncateToFit(result.Output, allocated)
        };
    }
    else if (allocated == 0)
    {
        result = result with
        {
            Output = "Result dropped due to context budget constraints (low priority)."
        };
    }

    // Append budget warning if needed
    var warning = contextBudget.GetBudgetWarning();
    if (warning != null)
    {
        result = result with { Output = result.Output + "\n\n" + warning };
    }
}

// Tag context tool results in conversation messages
var toolResultMessage = new ConversationMessage
{
    Role = "tool",
    Content = IsContextTool(toolCall.Name)
        ? $"[CONTEXT] {result.Output}"
        : result.Output,
    ToolCallId = toolCall.Id,
    ToolName = toolCall.Name
};
```

#### 2c: Helper Methods

```csharp
private static readonly HashSet<string> ContextToolNames =
    new(StringComparer.OrdinalIgnoreCase)
{
    "search_code_semantic", "search_findings", "search_stories",
    "search_conventions", "search_history"
};

private static bool IsContextTool(string toolName)
    => ContextToolNames.Contains(toolName);

private static bool IsContextToolsEnabled(IConfiguration config)
    => bool.Parse(config["ContextTools:Enabled"] ?? "true");

private static double ExtractScore(ToolExecutionResult result)
{
    // Try to extract relevance score from structured output header
    // Falls back to 0.5 (Normal priority) if no score found
    return 0.5;
}
```

---

### Task 3: ResolveToolsActivity Changes (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs`

Add context tool resolution after existing tool resolution:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    // ... existing tool resolution logic ...

    // Resolve context tools based on role
    var role = context.GetVariable<string>("Role") ?? "assistant";
    var accountId = context.GetVariable<string>("AccountId");
    var contextToolsEnabled = bool.Parse(
        _configuration?["ContextTools:Enabled"] ?? "true");

    if (contextToolsEnabled)
    {
        var contextTools = ResolveContextToolsForRole(role, accountId);
        foreach (var ctxTool in contextTools)
        {
            if (!resolved.Any(r =>
                string.Equals(r.Name, ctxTool.Name, StringComparison.OrdinalIgnoreCase)))
            {
                resolved.Add(ctxTool);
                _logger?.LogDebug(
                    "Added context tool '{Tool}' for role '{Role}'",
                    ctxTool.Name, role);
            }
        }
    }

    context.SetResult(resolved);
}

private List<ResolvedTool> ResolveContextToolsForRole(string role, string? accountId)
{
    var allowedNames = ContextToolDefaults.GetDefaults(role);
    var disabledTools = _configuration?.GetSection("ContextTools:DisabledTools")
        .Get<string[]>() ?? Array.Empty<string>();

    return allowedNames
        .Where(name => !disabledTools.Contains(name, StringComparer.OrdinalIgnoreCase))
        .Select(GetBuiltInContextTool)
        .Where(tool => tool != null)
        .ToList()!;
}

private static ResolvedTool? GetBuiltInContextTool(string toolName)
{
    return toolName switch
    {
        "search_code_semantic" => new ResolvedTool
        {
            Name = "search_code_semantic",
            Description = "Search the codebase using semantic similarity. " +
                          "Finds conceptually related code even without exact matches.",
            InputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "Natural language description of the code to find"
                    },
                    ["file_pattern"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "Optional file glob filter"
                    },
                    ["max_results"] = new Dictionary<string, object>
                    {
                        ["type"] = "integer",
                        ["description"] = "Max results (default: 10)"
                    }
                },
                ["required"] = new[] { "query" }
            }
        },
        "search_findings" => new ResolvedTool
        {
            Name = "search_findings",
            Description = "Search previous scan findings (security, quality, performance).",
            InputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "What findings to search for"
                    },
                    ["finding_type"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "security", "quality", "performance", "all" }
                    },
                    ["severity"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "critical", "high", "medium", "low" }
                    }
                },
                ["required"] = new[] { "query" }
            }
        },
        "search_stories" => new ResolvedTool
        {
            Name = "search_stories",
            Description = "Search project stories, specs, and architecture docs.",
            InputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "What to search for in documentation"
                    },
                    ["doc_type"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "story", "spec", "architecture", "all" }
                    }
                },
                ["required"] = new[] { "query" }
            }
        },
        "search_conventions" => new ResolvedTool
        {
            Name = "search_conventions",
            Description = "Look up project coding conventions and style rules.",
            InputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "Convention topic to look up (optional)"
                    },
                    ["category"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "code_style", "testing", "error_handling",
                                           "logging", "imports", "all" }
                    }
                }
            }
        },
        "search_history" => new ResolvedTool
        {
            Name = "search_history",
            Description = "Search previous LLM call results for this issue/workflow.",
            InputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["query"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "What to search for in previous results"
                    },
                    ["scope"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "issue", "workflow", "repo" }
                    }
                },
                ["required"] = new[] { "query" }
            }
        },
        _ => null
    };
}
```

---

### Task 4: Account Context Propagation (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`

Ensure workflow variables are set for context tools:

```csharp
// At workflow start, set context variables accessible to tool executors:
SetVariable("AccountId", input.AccountId);
SetVariable("IssueId", input.CorrelationId);  // or from workflow context
SetVariable("WorkflowInstanceId", WorkflowInstanceId);
SetVariable("RepositoryId", input.RepositoryId);
```

Each context tool reads these from the `IConfiguration` or from the Elsa `ActivityExecutionContext`.

For C# tools that need these values, add a `IWorkflowContextProvider` or pass them via a scoped service:

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolContext.cs`

```csharp
namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Scoped context data for context tool executors.
/// Set at the start of the tool loop, consumed by context tools.
/// </summary>
public class ContextToolContext
{
    public string? AccountId { get; set; }
    public string? IssueId { get; set; }
    public string? WorkflowInstanceId { get; set; }
    public string? RepositoryId { get; set; }
    public string? Role { get; set; }
    public string? Language { get; set; }
}
```

Register as scoped in DI:
```csharp
services.AddScoped<ContextToolContext>();
```

Set at tool loop start:
```csharp
var ctxContext = serviceProvider.GetRequiredService<ContextToolContext>();
ctxContext.AccountId = input.AccountId ?? configuration["CurrentAccountId"];
ctxContext.IssueId = input.CorrelationId;
ctxContext.WorkflowInstanceId = context.WorkflowInstanceId;
ctxContext.RepositoryId = input.RepositoryId;
ctxContext.Role = input.Role;
```

---

### Task 5: Diagnostics and RecordDiagnosticsActivity (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs`

Add context budget diagnostics to the output:

```csharp
public class LlmCallWorkflowOutput
{
    // ... existing fields ...

    /// <summary>Number of context tool calls made during the tool loop.</summary>
    public int ContextToolCalls { get; set; }

    /// <summary>Total tokens consumed by context tool results.</summary>
    public int ContextToolTokens { get; set; }

    /// <summary>Context budget utilization (0.0-1.0).</summary>
    public double ContextBudgetUtilization { get; set; }

    /// <summary>Number of context results dropped due to budget constraints.</summary>
    public int ContextResultsDropped { get; set; }
}
```

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs`

Log context budget metrics alongside existing diagnostics.

---

### Task 6: Error Resilience and Circuit Breaker (2 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolCircuitBreaker.cs`

```csharp
namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Circuit breaker for the context tools API.
/// Opens after N consecutive failures, preventing further calls
/// to the API for a cooldown period.
///
/// Scoped per-session (not global) to prevent one failed session
/// from blocking all sessions.
/// </summary>
public class ContextToolCircuitBreaker
{
    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;

    public ContextToolCircuitBreaker(int failureThreshold = 3, int cooldownSeconds = 60)
    {
        _failureThreshold = failureThreshold;
        _cooldownPeriod = TimeSpan.FromSeconds(cooldownSeconds);
    }

    public bool IsOpen => _openedAt.HasValue
        && DateTimeOffset.UtcNow - _openedAt.Value < _cooldownPeriod;

    public void RecordSuccess() { _consecutiveFailures = 0; _openedAt = null; }

    public void RecordFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= _failureThreshold)
        {
            _openedAt = DateTimeOffset.UtcNow;
        }
    }
}
```

Integrate into context tool executors: before making the HTTP call, check the circuit breaker. If open, return "Context tools temporarily unavailable" without making the call.

---

### Task 7: Unit Tests (2 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ElsaToolLoopIntegrationUnitTests.cs`

```csharp
// 10 tests:
// 1. ToolLoop_ContextToolsRegistered_AppearsInGetAll
// 2. ToolLoop_RoleFiltering_OnlyAllowedContextToolsAvailable
// 3. ToolLoop_ContextToolResult_RecordedInBudgetManager
// 4. ToolLoop_BudgetWarning_AppendedToToolOutput
// 5. ToolLoop_ContextToolResult_TaggedWithContextPrefix
// 6. ToolLoop_ParallelContextTools_BothExecuteAndBudgetTracked
// 7. ToolLoop_ContextToolsDisabled_NoContextToolsAvailable
// 8. ToolLoop_AccountContextPropagated_ToolReceivesAccountId
// 9. ToolLoop_CircuitBreakerOpen_ReturnsUnavailableMessage
// 10. ToolLoop_Diagnostics_IncludesContextBudgetMetrics
```

---

### Task 8: Integration Tests (2 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ContextToolsE2ETests.cs`

```csharp
// 4 tests (require running API + vector DB + event store):
// 1. E2E: Developer role gets search_code_semantic but not search_findings
// 2. E2E: Security reviewer gets search_findings but not search_stories
// 3. E2E: Context budget exhaustion stops further context tool calls
// 4. E2E: Parallel context tool + regular tool calls execute correctly
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolContext.cs` | Scoped context data |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolCircuitBreaker.cs` | Error resilience |
| 3 | `apps/tamma-elsa/tests/.../ElsaToolLoopIntegrationUnitTests.cs` | Unit tests |
| 4 | `apps/tamma-elsa/tests/.../ContextToolsE2ETests.cs` | Integration tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` | Register all context tools, budget manager, prioritizer, circuit breaker |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Budget manager integration, context tagging, parallel support |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveToolsActivity.cs` | Context tool resolution per role |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` | Context budget diagnostics |
| 5 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | Context diagnostics fields |
| 6 | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Account context propagation |
| 7 | `apps/tamma-elsa/src/Tamma.ElsaServer/appsettings.json` | Context tools config section |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Modifying CallLlmInlineActivity is high-risk (65KB file) | Changes are additive (new if-blocks after tool execution); no existing behavior modified |
| Parallel budget tracking race conditions | ContextToolBudgetManager uses locking; tested with parallel execution |
| API unreachable at deploy time | Circuit breaker + graceful degradation; context tools are non-critical |
| Too many tools confuse the LLM | Role-based filtering limits to 3-4 context tools per role |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Task 1: DI registration and config | 2 |
| Task 2: CallLlmInlineActivity integration | 6 |
| Task 3: ResolveToolsActivity changes | 2 |
| Task 4: Account context propagation | 2 |
| Task 5: Diagnostics | 2 |
| Task 6: Error resilience / circuit breaker | 2 |
| Task 7: Unit tests (10 tests) | 2 |
| Task 8: Integration tests (4 tests) | 2 |
| **Total** | **20 hours** |
