# Story 9-11: Diagnostics Queue + Elsa Integration — Implementation Plan

## Overview

Wire Elsa's `LlmCallWorkflow` to use the unified Fastify API services instead of re-implementing provider chains, circuit breakers, diagnostics, and config resolution in C#. Simplify C# activities to thin HTTP callers. Also wire the `DiagnosticsQueue` in `@tamma/shared` to drain to the Postgres-backed diagnostics store. Create a shared `TammaApiClient` C# class for all API calls.

---

## Step-by-Step Implementation Tasks

### Task 1: Create DiagnosticsApiProcessor (TypeScript) (3 hours)

**File to create**: `packages/shared/src/telemetry/diagnostics-api-processor.ts`

A processor variant that POSTs diagnostics events to the Fastify API. Used in decoupled mode where the TS engine does not have direct database access (e.g., distributed worker pool).

```typescript
import type { ILogger } from '../contracts/index.js';
import type { DiagnosticsEvent } from './diagnostics-event.js';
import type { DiagnosticsEventProcessor } from './diagnostics-queue.js';

/** Options for the API-backed diagnostics processor. */
export interface DiagnosticsApiProcessorOptions {
  /** Base URL of the Tamma API (e.g., 'http://localhost:3000'). */
  apiBaseUrl: string;
  /** API key or JWT for authentication. */
  apiToken?: string;
  /** Account ID to associate with events. */
  accountId?: string;
  /** Logger for warnings on failed API calls. */
  logger?: ILogger;
  /** Request timeout in ms. Default: 10000. */
  timeoutMs?: number;
}

/**
 * Creates a DiagnosticsEventProcessor that POSTs batches to the Tamma API.
 *
 * Used when the engine runs in a separate process from the API server
 * (e.g., distributed worker pool or CLI connecting to remote API).
 *
 * Falls back to logging on HTTP errors (fire-and-forget -- diagnostics
 * should not block the engine).
 */
export function createDiagnosticsApiProcessor(
  options: DiagnosticsApiProcessorOptions,
): DiagnosticsEventProcessor {
  const { apiBaseUrl, apiToken, accountId, logger, timeoutMs } = options;
  const url = `${apiBaseUrl.replace(/\/$/, '')}/api/v1/diagnostics`;
  const timeout = timeoutMs ?? 10_000;

  return async (events: DiagnosticsEvent[]): Promise<void> => {
    if (events.length === 0) return;

    try {
      const headers: Record<string, string> = {
        'Content-Type': 'application/json',
      };
      if (apiToken) {
        headers['Authorization'] = `Bearer ${apiToken}`;
      }

      const body = JSON.stringify({
        events,
        accountId,
      });

      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeout);

      try {
        const response = await fetch(url, {
          method: 'POST',
          headers,
          body,
          signal: controller.signal,
        });

        if (!response.ok) {
          logger?.warn('Diagnostics API POST failed', {
            status: response.status,
            statusText: response.statusText,
            eventCount: events.length,
          });
        }
      } finally {
        clearTimeout(timer);
      }
    } catch (err) {
      logger?.warn('Diagnostics API POST error', {
        error: err instanceof Error ? err.message : String(err),
        eventCount: events.length,
      });
    }
  };
}
```

---

### Task 2: Update DiagnosticsProcessor for Dual-Write (2 hours)

**File to modify**: `packages/shared/src/telemetry/diagnostics-processor.ts`

The existing `createDiagnosticsProcessor()` writes to the in-memory cost tracker. Add an optional `diagnosticsStore` parameter for dual-write:

```typescript
export interface DiagnosticsProcessorOptions {
  costTracker: IDiagnosticsCostTracker;
  mapProviderName: ProviderNameMapper;
  mapTaskType: TaskTypeMapper;
  logger?: ILogger;
  /** Optional persistent store for dual-write (API-backed mode). */
  persistentStore?: {
    recordBatch(events: DiagnosticsEvent[]): Promise<unknown>;
  };
}

export function createDiagnosticsProcessor(
  options: DiagnosticsProcessorOptions,
): DiagnosticsEventProcessor {
  const { costTracker, mapProviderName, mapTaskType, logger, persistentStore } = options;

  return async (events: DiagnosticsEvent[]): Promise<void> => {
    // 1. Write to persistent store (if available)
    if (persistentStore) {
      try {
        await persistentStore.recordBatch(events);
      } catch (err) {
        logger?.warn('Failed to write to persistent diagnostics store', {
          error: err instanceof Error ? err.message : String(err),
          eventCount: events.length,
        });
      }
    }

    // 2. Existing cost tracker mapping (unchanged)
    for (const event of events) {
      if (!COMPLETION_EVENT_TYPES.has(event.type)) continue;
      try {
        // ... existing mapping logic (unchanged) ...
        await costTracker.recordUsage(input);
      } catch (err) {
        logger?.warn('Diagnostics processor: failed to record usage', {
          type: event.type,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }
  };
}
```

---

### Task 3: Create TammaApiClient (C#) (4 hours)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs`

Shared HTTP client for all Tamma API calls from Elsa activities:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Shared HTTP client for calling the Tamma Fastify API.
/// Used by simplified Elsa activities to delegate logic to the TS engine.
///
/// Configuration:
///   TAMMA_API_URL - Base URL (default: http://localhost:3000)
///   TAMMA_API_TOKEN - Bearer token for authentication
/// </summary>
public class TammaApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TammaApiClient> _logger;
    private readonly string _baseUrl;

    public TammaApiClient(HttpClient httpClient, ILogger<TammaApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = Environment.GetEnvironmentVariable("TAMMA_API_URL") ?? "http://localhost:3000";

        var token = Environment.GetEnvironmentVariable("TAMMA_API_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
    }

    // --- Agent Resolution ---
    public async Task<AgentResolveResult?> ResolveAgent(string role, string? accountId = null)
    {
        var url = $"{_baseUrl}/api/v1/agents/{Uri.EscapeDataString(role)}/resolve";
        if (accountId != null) url += $"?accountId={Uri.EscapeDataString(accountId)}";
        return await GetAsync<AgentResolveResult>(url);
    }

    public async Task<AgentResolveResult?> ResolveForPhase(ResolveForPhaseRequest request)
    {
        return await PostAsync<AgentResolveResult>(
            $"{_baseUrl}/api/v1/agents/resolve-for-phase", request);
    }

    // --- Health ---
    public async Task<HealthStatus?> GetProviderHealth(string key)
    {
        return await GetAsync<HealthStatus>(
            $"{_baseUrl}/api/v1/health/providers/{Uri.EscapeDataString(key)}");
    }

    public async Task RecordFailure(string key, string? error = null)
    {
        await PostAsync<object>($"{_baseUrl}/api/v1/health/providers/{Uri.EscapeDataString(key)}/failure",
            new { error });
    }

    public async Task RecordSuccess(string key)
    {
        await PostAsync<object>($"{_baseUrl}/api/v1/health/providers/{Uri.EscapeDataString(key)}/success",
            new { });
    }

    // --- Diagnostics ---
    public async Task RecordDiagnostics(DiagnosticsRequest request)
    {
        await PostAsync<object>($"{_baseUrl}/api/v1/diagnostics", request);
    }

    // --- Budget ---
    public async Task<BudgetStatus?> CheckBudget(string accountId)
    {
        return await GetAsync<BudgetStatus>(
            $"{_baseUrl}/api/v1/diagnostics/budget/{Uri.EscapeDataString(accountId)}");
    }

    // --- Provider Sessions ---
    public async Task<ProviderSessionResult?> CreateProvider(ProviderCreateRequest request)
    {
        return await PostAsync<ProviderSessionResult>(
            $"{_baseUrl}/api/v1/providers/create", request);
    }

    public async Task<TaskResult?> ExecuteProvider(string handle, TaskExecuteRequest request)
    {
        return await PostAsync<TaskResult>(
            $"{_baseUrl}/api/v1/providers/{handle}/execute", request);
    }

    public async Task DisposeProvider(string handle)
    {
        await _httpClient.DeleteAsync($"{_baseUrl}/api/v1/providers/{handle}");
    }

    // --- Helpers ---
    private async Task<T?> GetAsync<T>(string url) where T : class
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tamma API GET failed: {Url}", url);
            return null;
        }
    }

    private async Task<T?> PostAsync<T>(string url, object body) where T : class
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, body);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tamma API POST failed: {Url}", url);
            return null;
        }
    }
}
```

---

### Task 4: Create C# API Models (1 hour)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs`

```csharp
namespace Tamma.Activities.LlmCall.Models;

// --- Agent Resolution ---
public record AgentResolveResult(
    string Role,
    ProviderInfo? Provider,
    ResolvedTaskConfig TaskConfig,
    string? SystemPrompt,
    bool SanitizationEnabled,
    ChainEntryStatus[] ChainEntries,
    bool AllExhausted
);

public record ProviderInfo(string Name, string Model);
public record ResolvedTaskConfig(string[]? AllowedTools, decimal? MaxBudgetUsd, string? PermissionMode);
public record ChainEntryStatus(string Provider, string Model, bool Healthy, bool CircuitOpen, string? CircuitOpenUntil, bool BudgetAllowed, decimal BudgetSpent, bool Recommended);
public record ResolveForPhaseRequest(string Phase, string ProjectId, string EngineId, object? TaskOverrides);

// --- Health ---
public record HealthStatus(bool Healthy, int Failures, bool CircuitOpen, string? CircuitOpenUntil, bool HalfOpen);

// --- Diagnostics ---
public record DiagnosticsRequest(object[] Events, string? AccountId);
public record BudgetStatus(decimal Spent, decimal Limit, decimal Remaining, decimal PercentUsed);

// --- Provider Sessions ---
public record ProviderCreateRequest(string Provider, string? Model, string? ApiKeyRef, object? Config);
public record ProviderSessionResult(string Handle, string Provider, string Model);
public record TaskExecuteRequest(string Prompt, string? Cwd, string? Model);
public record TaskResult(bool Success, string Output, decimal CostUsd, int DurationMs, string? Error);
```

---

### Task 5: Simplify ResolveAgentConfigActivity (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs`

Replace the 141-line DB lookup with a thin API call:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    var apiClient = context.GetRequiredService<TammaApiClient>();
    var logger = context.GetRequiredService<ILogger<ResolveAgentConfigActivity>>();
    var role = AgentRoleProp.Get(context) ?? "assistant";
    var systemPromptOverride = SystemPromptOverrideProp.Get(context);

    // Priority 1: Caller override
    if (!string.IsNullOrWhiteSpace(systemPromptOverride))
    {
        context.SetVariable("ResolvedSystemPrompt", systemPromptOverride);
        context.Complete();
        return;
    }

    // Priority 2: API resolution
    var result = await apiClient.ResolveAgent(role);
    if (result != null)
    {
        context.SetVariable("ResolvedSystemPrompt", result.SystemPrompt ?? "");
        context.SetVariable("ResolvedProvider", result.Provider?.Name ?? "claude-code");
        context.SetVariable("ResolvedModel", result.Provider?.Model ?? "");
        context.SetVariable("ResolvedTaskConfig", result.TaskConfig);
        context.Complete();
        return;
    }

    // Fallback: hardcoded defaults (backward compat if API unreachable)
    logger.LogWarning("Tamma API unreachable for agent resolution; using fallback for role {Role}", role);
    context.SetVariable("ResolvedSystemPrompt", GetFallbackPrompt(role));
    context.Complete();
}
```

---

### Task 6: Simplify CheckCircuitBreakerActivity (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs`

Replace 209-line in-workflow circuit breaker with API call:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    var apiClient = context.GetRequiredService<TammaApiClient>();
    var providerName = ProviderName.Get(context);

    var health = await apiClient.GetProviderHealth(providerName);
    if (health == null)
    {
        // API unreachable: assume healthy (backward compat, with WARN)
        context.JournalData.Add("Warning", "Tamma API unreachable; assuming healthy");
        await context.CompleteActivityWithOutcomesAsync("Closed");
        return;
    }

    if (health.HalfOpen)
        await context.CompleteActivityWithOutcomesAsync("HalfOpen");
    else if (health.CircuitOpen)
        await context.CompleteActivityWithOutcomesAsync("Open");
    else
        await context.CompleteActivityWithOutcomesAsync("Closed");
}
```

---

### Task 7: Simplify RecordDiagnosticsActivity (2 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs`

Replace 229-line local state management with API call:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    var apiClient = context.GetRequiredService<TammaApiClient>();
    var diagnostic = DiagnosticProp.Get(context);

    if (diagnostic == null)
    {
        context.Complete();
        return;
    }

    // Post to Tamma API -- replaces 4 workflow variable updates
    await apiClient.RecordDiagnostics(new DiagnosticsRequest(
        Events: new object[] { MapToApiEvent(diagnostic) },
        AccountId: context.GetVariable<string>("AccountId")
    ));

    // Record success/failure to health tracker via API
    var providerKey = diagnostic.ProviderName;
    if (diagnostic.Success)
        await apiClient.RecordSuccess(providerKey);
    else
        await apiClient.RecordFailure(providerKey, diagnostic.ErrorMessage);

    context.Complete();
}
```

---

### Task 8: Simplify CheckBudgetActivity (1 hour)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs`

Replace local budget tracking with API call:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    var apiClient = context.GetRequiredService<TammaApiClient>();
    var accountId = context.GetVariable<string>("AccountId") ?? "";

    var budget = await apiClient.CheckBudget(accountId);
    if (budget == null)
    {
        // API unreachable: allow (fail-open for budget)
        await context.CompleteActivityWithOutcomesAsync("WithinBudget");
        return;
    }

    if (budget.PercentUsed >= 100)
        await context.CompleteActivityWithOutcomesAsync("BudgetExceeded");
    else
        await context.CompleteActivityWithOutcomesAsync("WithinBudget");
}
```

---

### Task 9: Simplify CallLlmActivity (3 hours)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs`

Replace 676 lines of direct HTTP calls to LLM providers with the three-step session pattern:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    var apiClient = context.GetRequiredService<TammaApiClient>();
    var providerName = context.GetVariable<string>("ResolvedProvider") ?? "claude-code";
    var model = context.GetVariable<string>("ResolvedModel");
    var prompt = PromptProp.Get(context);

    string? handle = null;
    try
    {
        // 1. Create provider session via API
        var session = await apiClient.CreateProvider(new ProviderCreateRequest(
            Provider: providerName, Model: model, ApiKeyRef: null, Config: null));

        if (session == null)
        {
            // Fallback or throw
            throw new InvalidOperationException($"Failed to create provider session for {providerName}");
        }

        handle = session.Handle;

        // 2. Execute task via API
        var result = await apiClient.ExecuteProvider(handle, new TaskExecuteRequest(
            Prompt: prompt, Cwd: null, Model: model));

        // 3. Set workflow variables from result
        context.SetVariable("LlmResponse", result?.Output ?? "");
        context.SetVariable("LlmSuccess", result?.Success ?? false);
        context.SetVariable("LlmCostUsd", result?.CostUsd ?? 0);
        context.SetVariable("LlmDurationMs", result?.DurationMs ?? 0);

        if (result?.Success == true)
            await context.CompleteActivityWithOutcomesAsync("Success");
        else
            await context.CompleteActivityWithOutcomesAsync("Failed");
    }
    finally
    {
        // 3. Always dispose session
        if (handle != null)
        {
            try { await apiClient.DisposeProvider(handle); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose provider session {Handle}", handle); }
        }
    }
}
```

---

### Task 10: Register TammaApiClient in DI (1 hour)

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/DependencyInjection.cs` (or equivalent DI registration file)

```csharp
services.AddHttpClient<TammaApiClient>();
```

---

### Task 11: Tests (4 hours)

**TypeScript tests:**

**File to create**: `packages/shared/src/telemetry/diagnostics-api-processor.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | Posts batch to API URL | fetch called with correct URL and body |
| 2 | Includes Authorization header when token provided | Header present |
| 3 | Handles HTTP error gracefully | Logged, not thrown |
| 4 | Handles network error gracefully | Logged, not thrown |
| 5 | Skips empty batch | No fetch call |
| 6 | Respects timeout | AbortController triggered |

**File to modify**: `packages/shared/src/telemetry/diagnostics-processor.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 7 | Dual-write: writes to persistentStore when provided | recordBatch called |
| 8 | Dual-write: writes to costTracker as before | recordUsage called |
| 9 | persistentStore error does not block costTracker | Both called, store error logged |

**C# tests:**

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/TammaApiClientTests.cs`

| # | Test | Assertion |
|---|------|-----------|
| 10 | ResolveAgent returns parsed result | Correct deserialization |
| 11 | GetProviderHealth returns null on HTTP error | Graceful fallback |
| 12 | RecordDiagnostics posts to correct endpoint | URL matches |

**File to create**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/SimplifiedActivitiesTests.cs`

| # | Test | Assertion |
|---|------|-----------|
| 13 | ResolveAgentConfigActivity sets workflow variables from API | Variables set correctly |
| 14 | ResolveAgentConfigActivity falls back when API unreachable | Hardcoded default used |
| 15 | CheckCircuitBreakerActivity returns correct outcome | Closed/HalfOpen/Open |
| 16 | CheckCircuitBreakerActivity assumes healthy when API down | Outcome = Closed |
| 17 | RecordDiagnosticsActivity posts to API | ApiClient called |
| 18 | CallLlmActivity uses create/execute/dispose pattern | All three calls made |
| 19 | CallLlmActivity disposes session on error | DeleteAsync called in finally |

**Total tests**: ~19

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/shared/src/telemetry/diagnostics-api-processor.ts` | API-backed diagnostics processor |
| 2 | `packages/shared/src/telemetry/diagnostics-api-processor.test.ts` | Tests |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` | Shared C# HTTP client |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs` | C# API models |
| 5 | `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/TammaApiClientTests.cs` | Client tests |
| 6 | `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/SimplifiedActivitiesTests.cs` | Activity tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/shared/src/telemetry/diagnostics-processor.ts` | Add optional persistentStore for dual-write |
| 2 | `packages/shared/src/telemetry/diagnostics-processor.test.ts` | Add dual-write tests |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs` | Simplify to API call |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs` | Simplify to API call |
| 5 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` | Simplify to API call |
| 6 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs` | Simplify to API call |
| 7 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` | Replace direct HTTP with session API |
| 8 | DI registration file (e.g., `DependencyInjection.cs`) | Register TammaApiClient |

---

## Dependencies

- **Story 9-2** (diagnostics store and API for recording/querying)
- **Story 9-3** (health store and API for circuit breaker)
- **Story 9-4** (provider session API for create/execute/dispose)
- **Story 9-8** (agent resolver API for config resolution)

## Migration from Existing Code

### TypeScript Side

1. `DiagnosticsProcessor` in `packages/shared/src/telemetry/diagnostics-processor.ts` gains an optional `persistentStore` parameter. Backward compatible -- when not provided, behavior is identical to current.
2. New `DiagnosticsApiProcessor` provides an HTTP-based alternative for distributed setups.
3. `DiagnosticsQueue` itself is unchanged -- it is consumer-agnostic.

### C# Side

1. **ResolveAgentConfigActivity**: 141 lines -> ~40 lines. Replaces DB lookup with HTTP GET.
2. **CheckCircuitBreakerActivity**: 209 lines -> ~30 lines. Replaces JSON state management with HTTP GET.
3. **RecordDiagnosticsActivity**: 229 lines -> ~30 lines. Replaces 4 workflow variable updates with single HTTP POST.
4. **CheckBudgetActivity**: Similar simplification to API call.
5. **CallLlmActivity**: 676 lines -> ~50 lines. Replaces direct HTTP calls to 4+ LLM providers with create/execute/dispose session pattern.

### Backward Compatibility

All simplified activities fall back to local behavior with a WARN log when the Tamma API is unreachable:
- `ResolveAgentConfigActivity`: uses hardcoded fallback prompts
- `CheckCircuitBreakerActivity`: assumes healthy (fail-open)
- `CheckBudgetActivity`: assumes within budget (fail-open)
- `RecordDiagnosticsActivity`: drops the event (non-critical)
- `CallLlmActivity`: no fallback -- throws (cannot proceed without provider)

---

## Estimated Effort

| Task | Hours |
|------|-------|
| DiagnosticsApiProcessor (TypeScript) | 3 |
| DiagnosticsProcessor dual-write update | 2 |
| TammaApiClient (C#) | 4 |
| C# API models | 1 |
| Simplify ResolveAgentConfigActivity | 2 |
| Simplify CheckCircuitBreakerActivity | 2 |
| Simplify RecordDiagnosticsActivity | 2 |
| Simplify CheckBudgetActivity | 1 |
| Simplify CallLlmActivity | 3 |
| DI registration | 1 |
| Tests (19 tests) | 4 |
| **Total** | **25 hours** |
