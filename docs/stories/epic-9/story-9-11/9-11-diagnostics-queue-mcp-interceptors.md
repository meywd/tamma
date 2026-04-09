# Story 9-11: Diagnostics Queue + Elsa Integration

## User Story

As a platform operator, I want Elsa's LlmCallWorkflow to report diagnostics and use health/config services via the Fastify API, so that Elsa becomes a thin orchestrator and all logic lives in one place.

## Goal

Wire Elsa's `LlmCallWorkflow` to use the unified API services instead of re-implementing provider chains, circuit breakers, diagnostics, and config resolution in C#. Simplify C# activities to thin HTTP callers. Also wire the `DiagnosticsQueue` in `@tamma/shared` to drain to the Postgres-backed diagnostics store.

## Acceptance Criteria

1. **DiagnosticsQueue wiring**: The `DiagnosticsQueue` in `@tamma/shared/src/telemetry/` drains to the diagnostics store (Story 9-2) instead of only the in-memory cost tracker. The existing `createDiagnosticsProcessor()` is updated to POST events to the diagnostics API.
2. **Elsa activity simplification**: The following C# activities are simplified to thin API callers:
   - `ResolveAgentConfigActivity.cs` -> calls `GET /api/v1/agents/:role/resolve` (Story 9-8)
   - `CheckCircuitBreakerActivity.cs` -> calls `GET /api/v1/health/providers/:key` (Story 9-3)
   - `RecordDiagnosticsActivity.cs` -> calls `POST /api/v1/diagnostics` (Story 9-2)
   - `CheckBudgetActivity.cs` -> calls `GET /api/v1/diagnostics/budget/:accountId` (Story 9-2)
   - `CallLlmActivity.cs` -> calls `POST /api/v1/providers/:handle/execute` (Story 9-4) instead of making direct HTTP calls to LLM providers
3. **LlmCallWorkflow simplification**: The workflow becomes a thin orchestrator:
   ```
   Input → Resolve Agent Config (API) → Check Health (API) → Check Budget (API)
        → Execute via Provider API → Record Diagnostics (API) → Output
   ```
4. **MCP tool interceptors**: The `ToolInterceptorChain` in `packages/mcp-client/src/interceptors.ts` continues to work for MCP tool sanitization. Bridge from MCPClient events to `DiagnosticsQueue` is preserved.
5. **Backward compatibility**: Activities still function if the API is unreachable (fall back to local behavior with a WARN log).

## Technical Context

### Existing Files (TypeScript)

- `packages/shared/src/telemetry/diagnostics-queue.ts` -- `DiagnosticsQueue` class
- `packages/shared/src/telemetry/diagnostics-processor.ts` -- `createDiagnosticsProcessor()`
- `packages/shared/src/telemetry/diagnostics-event.ts` -- `DiagnosticsEvent` types
- `packages/mcp-client/src/interceptors.ts` -- `ToolInterceptorChain` (if implemented)
- `packages/mcp-client/src/client.ts` -- MCPClient with EventEmitter

### Existing Files (C# -- to be simplified)

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` -- 676 lines of direct HTTP calls to LLM providers, provider config loading, response parsing
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs` -- 209 lines of in-workflow circuit breaker state management
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` -- 229 lines of in-workflow diagnostics recording, budget tracking, circuit breaker updates
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs` -- 141 lines of agent config resolution from ELSA Agents DB
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs` -- budget check activity
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` -- C# models (ProviderAttemptDiagnostic, CircuitBreakerState, BudgetState, LlmProviderConfig, etc.)

### Simplification Plan

**Before (current C# activities do everything locally):**
```
ResolveAgentConfigActivity
  → Reads from ELSA Agents DB
  → Hardcoded fallback prompts
  → 6-level resolution chain

CheckCircuitBreakerActivity
  → Deserializes JSON state from workflow variable
  → Manages circuit state transitions in-workflow
  → Tracks consecutive failures, cooldown periods

CallLlmActivity
  → Loads provider config from IConfiguration
  → Builds HTTP requests for Anthropic/OpenAI APIs
  → Parses responses, extracts tokens
  → Handles retries, timeouts

RecordDiagnosticsActivity
  → Manages diagnostics list in workflow variables
  → Estimates costs using hardcoded rates
  → Updates circuit breaker state
  → Updates budget state
```

**After (thin API callers):**
```
ResolveAgentConfigActivity
  → HTTP GET /api/v1/agents/:role/resolve
  → Sets workflow variables from API response

CheckCircuitBreakerActivity
  → HTTP GET /api/v1/health/providers/:key
  → Returns outcome based on API response

CallLlmActivity
  → HTTP POST /api/v1/providers/create (get handle)
  → HTTP POST /api/v1/providers/:handle/execute
  → HTTP DELETE /api/v1/providers/:handle
  → Response already normalized by TS provider

RecordDiagnosticsActivity
  → HTTP POST /api/v1/diagnostics
  → Single API call replaces 4 workflow variable updates
```

### DiagnosticsQueue Update

```typescript
// Updated createDiagnosticsProcessor that writes to API store
export function createDiagnosticsProcessor(
  diagnosticsStore: IDiagnosticsStore,  // new: Postgres-backed store
  costTracker?: ICostTracker,           // optional: legacy cost tracker
  logger?: ILogger,
): DiagnosticsEventProcessor {
  return async (events: DiagnosticsEvent[]) => {
    // Write to persistent store
    await diagnosticsStore.recordBatch(events);
    // Optionally still update in-memory cost tracker
    if (costTracker) {
      for (const event of events) {
        // ... existing mapping logic ...
      }
    }
  };
}
```

### Architecture

```
                   ┌─── TS Engine ───┐
                   │                 │
                   │ DiagnosticsQueue│
                   │    .emit()      │
                   │       │         │
                   │  drain every 5s │
                   │       │         │
                   │       ▼         │
                   │ DiagnosticsStore│──► Postgres
                   │                 │
                   └─────────────────┘
                           ▲
                           │ HTTP POST /api/v1/diagnostics
                           │
                   ┌─── Elsa ────────┐
                   │                 │
                   │ Thin C# callers │
                   │ (no local state)│
                   └─────────────────┘
```

## Files

### TypeScript

- MODIFY `packages/shared/src/telemetry/diagnostics-processor.ts` -- update to write to diagnostics store
- MODIFY `packages/shared/src/telemetry/diagnostics-queue.ts` -- no structural changes (queue is consumer-agnostic)
- CREATE `packages/shared/src/telemetry/diagnostics-api-processor.ts` -- processor variant that POSTs to API (for decoupled mode)

### C# (Simplified Activities)

- MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` -- replace direct HTTP calls with API proxy calls
- MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs` -- replace local state with API call
- MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` -- replace local state with API call
- MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs` -- replace DB lookup with API call
- MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckBudgetActivity.cs` -- replace local state with API call
- CREATE `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` -- shared HTTP client for Tamma API calls

## Dependencies

- **Story 9-2** (diagnostics store and API for recording/querying)
- **Story 9-3** (health store and API for circuit breaker)

## Effort Estimate

**20 hours**

- 4h: Update DiagnosticsProcessor to write to persistent store
- 6h: Simplify C# activities to thin API callers (5 activities)
- 3h: Create shared TammaApiClient for C# HTTP calls
- 3h: Backward compatibility fallback logic
- 4h: Tests (TS processor, C# integration, fallback behavior)
