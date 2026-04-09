# Story 12-7c: Context Budget Manager -- Implementation Plan

## Overview

Implement a C# `ContextToolBudgetManager` class that tracks cumulative token usage from context tool results within a single tool loop session. Integrates with the tool loop in `CallLlmInlineActivity` to enforce context budget limits, apply priority-based result dropping, and report budget utilization.

---

## Step-by-Step Implementation Tasks

### Task 1: ContextToolBudgetManager Class (6 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolBudgetManager.cs`

```csharp
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Priority levels for context tool results.
/// Higher priority results are kept when budget is tight.
/// </summary>
public enum ContextPriority
{
    /// <summary>Must keep: direct task match, prohibited actions, blocking conventions.</summary>
    Critical = 4,

    /// <summary>Strong match: high relevance score, important learnings.</summary>
    High = 3,

    /// <summary>Moderate match: general context.</summary>
    Normal = 2,

    /// <summary>Weak match: tangential context, dropped first.</summary>
    Low = 1
}

/// <summary>
/// A single recorded context tool result for budget tracking.
/// </summary>
public record ContextToolUsage(
    string ToolName,
    string ToolCallId,
    int TokenCount,
    ContextPriority Priority,
    DateTimeOffset Timestamp);

/// <summary>
/// Tracks cumulative token usage from context tool results within a single
/// tool loop session. Enforces budget limits, applies priority-based dropping,
/// and reports utilization.
///
/// Scoped lifetime: one instance per tool loop execution.
///
/// Does NOT manage overall conversation compaction (that is ContextCompactor's job).
/// Only manages the budget for context retrieved via search_code_semantic,
/// search_findings, search_stories, search_conventions, and search_history.
/// </summary>
public class ContextToolBudgetManager
{
    private readonly ILogger<ContextToolBudgetManager> _logger;
    private readonly List<ContextToolUsage> _usages = new();
    private readonly object _lock = new();

    /// <summary>Total context budget in tokens (computed at initialization).</summary>
    public int TotalBudget { get; }

    /// <summary>Tokens consumed so far by context tool results.</summary>
    public int ConsumedTokens { get; private set; }

    /// <summary>Tokens remaining for additional context.</summary>
    public int RemainingTokens => Math.Max(0, TotalBudget - ConsumedTokens);

    /// <summary>Budget utilization as a fraction (0.0 - 1.0).</summary>
    public double Utilization => TotalBudget > 0
        ? (double)ConsumedTokens / TotalBudget
        : 0.0;

    /// <summary>Whether the budget is exhausted.</summary>
    public bool IsExhausted => RemainingTokens <= 0;

    /// <summary>Whether the budget is nearly full (< 500 tokens remaining).</summary>
    public bool IsNearlyFull => RemainingTokens < 500 && RemainingTokens > 0;

    /// <summary>Number of results dropped due to budget constraints.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>
    /// Create a budget manager for a tool loop session.
    /// </summary>
    /// <param name="contextWindowTokens">Provider's total context window size.</param>
    /// <param name="systemPromptTokens">Estimated tokens for system prompt.</param>
    /// <param name="userPromptTokens">Estimated tokens for user prompt.</param>
    /// <param name="reservedOutputTokens">Tokens reserved for LLM output (typically maxTokens).</param>
    /// <param name="contextBudgetFraction">Fraction of remaining space for context tools (default 0.5).
    /// The other half is for conversation messages and non-context tool results.</param>
    /// <param name="logger">Logger instance.</param>
    public ContextToolBudgetManager(
        int contextWindowTokens,
        int systemPromptTokens,
        int userPromptTokens,
        int reservedOutputTokens,
        double contextBudgetFraction,
        ILogger<ContextToolBudgetManager> logger)
    {
        _logger = logger;

        var availableTokens = contextWindowTokens
            - systemPromptTokens
            - userPromptTokens
            - reservedOutputTokens;

        TotalBudget = Math.Max(0, (int)(availableTokens * contextBudgetFraction));

        _logger.LogInformation(
            "ContextToolBudgetManager initialized: " +
            "ContextWindow={ContextWindowTokens}, SystemPrompt={SystemPromptTokens}, " +
            "UserPrompt={UserPromptTokens}, ReservedOutput={ReservedOutputTokens}, " +
            "ContextBudget={ContextBudget} ({BudgetFraction:P0})",
            contextWindowTokens, systemPromptTokens, userPromptTokens,
            reservedOutputTokens, TotalBudget, contextBudgetFraction);
    }

    /// <summary>
    /// Record a context tool result's token usage.
    /// Returns the number of tokens actually allocated (may be less than requested
    /// if budget is tight and priority allows dropping).
    /// </summary>
    public int RecordUsage(string toolName, string toolCallId, int tokenCount, ContextPriority priority)
    {
        lock (_lock)
        {
            var allocated = tokenCount;

            // If budget is tight, apply priority-based dropping
            if (RemainingTokens < tokenCount)
            {
                if (priority == ContextPriority.Low && Utilization > 0.7)
                {
                    DroppedCount++;
                    _logger.LogDebug(
                        "Context result dropped (LOW priority, budget at {Utilization:P0}): " +
                        "{ToolName} {ToolCallId}",
                        Utilization, toolName, toolCallId);
                    return 0;
                }

                if (priority == ContextPriority.Normal && Utilization > 0.85)
                {
                    DroppedCount++;
                    _logger.LogDebug(
                        "Context result dropped (NORMAL priority, budget at {Utilization:P0}): " +
                        "{ToolName} {ToolCallId}",
                        Utilization, toolName, toolCallId);
                    return 0;
                }

                // For HIGH and CRITICAL, allocate what we can
                allocated = Math.Min(tokenCount, RemainingTokens);
            }

            ConsumedTokens += allocated;

            _usages.Add(new ContextToolUsage(
                toolName, toolCallId, allocated, priority, DateTimeOffset.UtcNow));

            _logger.LogInformation(
                "Context budget updated: {ToolName} {ToolCallId} " +
                "tokens={AllocatedTokens}/{RequestedTokens} " +
                "budget={ConsumedTokens}/{TotalBudget} ({Utilization:P0})",
                toolName, toolCallId, allocated, tokenCount,
                ConsumedTokens, TotalBudget, Utilization);

            return allocated;
        }
    }

    /// <summary>
    /// Get a budget status message to append to tool results.
    /// Returns null if budget is healthy.
    /// </summary>
    public string? GetBudgetWarning()
    {
        if (IsExhausted)
            return $"[CONTEXT BUDGET EXHAUSTED] You have enough context to proceed. " +
                   $"No more context queries allowed this session. " +
                   $"Used: {ConsumedTokens}/{TotalBudget} tokens.";

        if (IsNearlyFull)
            return $"[CONTEXT BUDGET WARNING] {RemainingTokens} tokens remaining. " +
                   $"Consider using the context you already have instead of searching for more.";

        return null;
    }

    /// <summary>
    /// Determine priority for a search result based on its relevance score.
    /// </summary>
    public static ContextPriority ScoreToPriority(double score)
    {
        return score switch
        {
            > 0.9 => ContextPriority.Critical,
            > 0.7 => ContextPriority.High,
            > 0.5 => ContextPriority.Normal,
            _ => ContextPriority.Low
        };
    }

    /// <summary>
    /// Truncate a result string to fit within the given token allowance.
    /// Uses rough estimation (4 chars per token).
    /// </summary>
    public static string TruncateToFit(string content, int maxTokens)
    {
        var maxChars = maxTokens * 4;
        if (content.Length <= maxChars) return content;
        return content[..maxChars] + "\n...(truncated to fit context budget)";
    }

    /// <summary>
    /// Log final budget summary at session end.
    /// </summary>
    public void LogSummary(string? workflowInstanceId = null)
    {
        _logger.LogInformation(
            "Context budget summary: WorkflowInstanceId={WorkflowInstanceId}, " +
            "TotalBudget={TotalBudget}, ConsumedTokens={ConsumedTokens}, " +
            "Utilization={Utilization:P0}, ToolCalls={ToolCallCount}, " +
            "DroppedResults={DroppedCount}",
            workflowInstanceId, TotalBudget, ConsumedTokens,
            Utilization, _usages.Count, DroppedCount);
    }

    /// <summary>
    /// Get all recorded usages for reporting.
    /// </summary>
    public IReadOnlyList<ContextToolUsage> GetUsages() => _usages.AsReadOnly();
}
```

---

### Task 2: Priority Tagging Logic (4 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextResultPrioritizer.cs`

```csharp
namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Assigns priority levels to context tool results based on relevance score,
/// result type, and the current agent role.
///
/// Examples:
///   - security reviewer: CVE findings are CRITICAL, code conventions are NORMAL
///   - developer: code search hits are HIGH, findings are NORMAL
///   - tester: test-related code is HIGH, architecture docs are LOW
/// </summary>
public class ContextResultPrioritizer
{
    /// <summary>
    /// Determine priority based on tool, score, and current role.
    /// </summary>
    public ContextPriority GetPriority(
        string toolName,
        double relevanceScore,
        string currentRole,
        Dictionary<string, object>? resultMetadata = null)
    {
        // Base priority from score
        var basePriority = ContextToolBudgetManager.ScoreToPriority(relevanceScore);

        // Role-specific boosts
        var boost = GetRoleBoost(toolName, currentRole, resultMetadata);

        var adjusted = (int)basePriority + boost;
        return (ContextPriority)Math.Clamp(adjusted, 1, 4);
    }

    private int GetRoleBoost(
        string toolName, string role,
        Dictionary<string, object>? metadata)
    {
        // Role-specific priority adjustments
        return (toolName, role) switch
        {
            ("search_findings", "security_reviewer") => 1,  // Findings more important for security
            ("search_findings", "developer") => 0,
            ("search_code_semantic", "developer") => 1,      // Code more important for developer
            ("search_code_semantic", "tester") => 0,
            ("search_stories", "planner") => 1,              // Specs more important for planner
            ("search_conventions", "developer") => 1,        // Conventions important for developer
            ("search_conventions", "security_reviewer") => 0,
            ("search_history", _) => 0,                      // History is always normal priority
            _ => 0
        };
    }
}
```

---

### Task 3: Integration with Tool Loop (4 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

Changes needed in the tool loop section:

1. Instantiate `ContextToolBudgetManager` at the start of the tool loop
2. After each context tool execution, record token usage with the budget manager
3. Append budget warnings to context tool outputs when budget is tight
4. Log budget summary when the tool loop completes

```csharp
// At tool loop start:
var contextBudget = new ContextToolBudgetManager(
    contextWindowTokens: toolLoopConfig.ContextWindowTokens,
    systemPromptTokens: TokenEstimator.EstimateTokens(systemPrompt),
    userPromptTokens: TokenEstimator.EstimateTokens(userPrompt),
    reservedOutputTokens: maxTokens,
    contextBudgetFraction: 0.5,
    logger: _contextBudgetLogger);

// After context tool execution:
if (IsContextTool(toolName))
{
    var tokenCount = TokenEstimator.EstimateTokens(result.Output);
    var priority = _prioritizer.GetPriority(toolName, resultScore, role);
    var allocated = contextBudget.RecordUsage(toolName, toolCallId, tokenCount, priority);

    if (allocated < tokenCount)
    {
        result = result with
        {
            Output = ContextToolBudgetManager.TruncateToFit(result.Output, allocated)
        };
    }

    var warning = contextBudget.GetBudgetWarning();
    if (warning != null)
    {
        result = result with { Output = result.Output + "\n\n" + warning };
    }
}

// At tool loop end:
contextBudget.LogSummary(workflowInstanceId);
```

Helper to identify context tools:

```csharp
private static readonly HashSet<string> ContextToolNames = new(StringComparer.OrdinalIgnoreCase)
{
    "search_code_semantic", "search_findings", "search_stories",
    "search_conventions", "search_history"
};

private static bool IsContextTool(string toolName)
    => ContextToolNames.Contains(toolName);
```

---

### Task 4: Unit Tests (4 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ContextToolBudgetManagerTests.cs`

```csharp
// 10 tests:
// 1. Constructor_ComputesBudgetCorrectly
// 2. RecordUsage_TracksConsumedTokens
// 3. RemainingTokens_DecreasesAfterUsage
// 4. IsExhausted_TrueWhenBudgetConsumed
// 5. IsNearlyFull_TrueWhenLessThan500Remaining
// 6. RecordUsage_DropsLowPriorityWhenBudgetTight
// 7. RecordUsage_DropsNormalPriorityWhenBudgetVeryTight
// 8. RecordUsage_KeepsCriticalPriorityAlways
// 9. GetBudgetWarning_ReturnsNullWhenBudgetHealthy
// 10. GetBudgetWarning_ReturnsWarningWhenNearlyFull
```

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ContextResultPrioritizerTests.cs`

```csharp
// 5 tests:
// 1. GetPriority_HighScore_ReturnsCritical
// 2. GetPriority_SecurityReviewer_BoostsFindingsPriority
// 3. GetPriority_Developer_BoostsCodeSearchPriority
// 4. GetPriority_LowScore_ReturnsLow
// 5. GetPriority_UnknownRole_NoBoost
```

---

### Task 5: Integration Tests (2 hours)

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/Tools/ContextBudgetIntegrationTests.cs`

```csharp
// 2 tests:
// 1. ToolLoop_ContextBudgetExhausted_ContextToolsReturnWarning
// 2. ToolLoop_PriorityDropping_LowResultsDroppedFirst
```

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextToolBudgetManager.cs` | Budget tracking |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ContextResultPrioritizer.cs` | Priority assignment |
| 3 | `apps/tamma-elsa/tests/.../ContextToolBudgetManagerTests.cs` | Unit tests |
| 4 | `apps/tamma-elsa/tests/.../ContextResultPrioritizerTests.cs` | Unit tests |
| 5 | `apps/tamma-elsa/tests/.../ContextBudgetIntegrationTests.cs` | Integration tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Wire budget manager into tool loop |
| 2 | `apps/tamma-elsa/src/Tamma.ElsaServer/ServiceRegistration.cs` | Register budget manager and prioritizer in DI |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Token estimation inaccurate (4 chars/token) | Use `TokenEstimator.EstimateTokens()` which already accounts for different content types; refine later with tiktoken |
| Budget fraction (0.5) may be wrong | Make configurable via `ToolLoopConfig`; start with 0.5 and tune based on real usage data |
| Priority boosting may be wrong for some roles | Make role-priority mapping configurable; start with sensible defaults |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Task 1: ContextToolBudgetManager class | 6 |
| Task 2: ContextResultPrioritizer | 4 |
| Task 3: Integration with tool loop | 4 |
| Task 4: Unit tests (15 tests) | 4 |
| Task 5: Integration tests (2 tests) | 2 |
| **Total** | **20 hours** |
