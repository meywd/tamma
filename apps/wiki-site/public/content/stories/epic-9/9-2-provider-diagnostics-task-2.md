---
title: "Task 2: Create InstrumentedAgentProvider Decorator"
sidebar:
  order: 90
---

**Story:** 9-2-provider-diagnostics - Provider Diagnostics
**Epic:** 9

## Task Description

Create `InstrumentedAgentProvider`, a decorator class that wraps any `IAgentProvider` and emits `DiagnosticsEvent` objects to the shared `DiagnosticsQueue` (from `@tamma/shared/telemetry`). The decorator intercepts `executeTask()` to emit `provider:call` on entry, `provider:complete` on success, and `provider:error` on failure. It delegates `isAvailable()` and `dispose()` directly to the inner provider without instrumentation.

Key enhancements over initial design:
- **Per-call context**: Supports mutable `taskId`/`taskType` via `updateContext()` method instead of requiring a new instance per task change.
- **Error sanitization**: Uses `sanitizeErrorMessage()` for all `errorMessage` fields to prevent API key leakage.
- **Typed error codes**: Uses `DiagnosticsErrorCode` union type instead of plain string.
- **Token exposure**: Accesses `result.tokens` from `AgentTaskResult` for token tracking (requires `AgentTaskResult` to include optional `tokens` field).
- **Required context fields**: All context fields (`projectId`, `engineId`, `taskId`, `taskType`) are required, matching Story 9-11 canonical design.

## Acceptance Criteria

- `InstrumentedAgentProvider` implements `IAgentProvider`
- `executeTask()` emits `provider:call` before calling inner, `provider:complete` on success, `provider:error` on throw
- All events include `providerName`, `model`, `agentType` (typed as `AgentType`), `projectId`, `engineId`, `taskId`, `taskType`
- `provider:complete` events include `latencyMs`, `success`, `costUsd`, `tokens` (from `result.tokens`), and `errorCode` (typed as `DiagnosticsErrorCode`) if `result.error` is truthy
- `provider:error` events include `latencyMs`, `success=false`, `errorCode` (typed as `DiagnosticsErrorCode`), `errorMessage` (sanitized via `sanitizeErrorMessage()`)
- `updateContext()` method allows changing `taskId`/`taskType` between calls without creating a new instance
- `isAvailable()` delegates to `inner.isAvailable()` without emitting events
- `dispose()` delegates to `inner.dispose()` without emitting events
- Original error is re-thrown after emitting the error event
- `diagnostics.emit()` is called synchronously (not awaited)

## Implementation Details

### Technical Requirements

- [ ] Create class `InstrumentedAgentProvider implements IAgentProvider`
- [ ] Define `InstrumentedAgentContext` interface: `{ providerName: string; model: string; agentType: AgentType; projectId: string; engineId: string; taskId: string; taskType: string }`
- [ ] Constructor accepts: `inner: IAgentProvider`, `diagnostics: DiagnosticsQueue`, `context: InstrumentedAgentContext`
- [ ] Store context as mutable copy (`this.context = { ...context }`)
- [ ] `updateContext(updates: Partial<Pick<InstrumentedAgentContext, 'taskId' | 'taskType'>>): void`:
  - Updates `taskId` and/or `taskType` on the stored context
  - Allows reusing the same InstrumentedAgentProvider across multiple tasks
- [ ] `executeTask(config, onProgress?)`:
  - Emit `{ type: 'provider:call', timestamp: Date.now(), providerName, model, agentType, projectId, engineId, taskId, taskType }`
  - Record `start = Date.now()`
  - Call `this.inner.executeTask(config, onProgress)`
  - On success: emit `provider:complete` with `latencyMs`, `success: result.success`, `costUsd: result.costUsd`, `tokens: result.tokens`, `errorCode: result.error ? 'TASK_FAILED' : undefined`
  - On catch: emit `provider:error` with `latencyMs`, `success: false`, `errorCode: (err as any)?.code ?? 'UNKNOWN'` (typed as `DiagnosticsErrorCode`), `errorMessage: sanitizeErrorMessage((err as Error).message)`
  - Re-throw caught error
- [ ] `isAvailable()`: return `this.inner.isAvailable()`
- [ ] `dispose()`: return `this.inner.dispose()`

### Files to Modify/Create

- CREATE `packages/providers/src/instrumented-agent-provider.ts`
- CREATE `packages/providers/src/instrumented-agent-provider.test.ts`

### Dependencies

- [ ] Task 1: `DiagnosticsQueue`, `DiagnosticsEvent`, `DiagnosticsErrorCode`, `sanitizeErrorMessage` from `@tamma/shared/telemetry`
- [ ] `IAgentProvider`, `AgentTaskConfig`, `AgentProgressCallback` from `./agent-types.js`
- [ ] `AgentTaskResult` from `@tamma/shared`
- [ ] `AgentType` from `@tamma/shared`

### AgentTaskResult Token Exposure (Recommendation)

**Note:** `AgentTaskResult` (in `packages/shared/src/types/index.ts`) should include an optional `tokens?: { input: number; output: number }` field for diagnostics to access. This enables `InstrumentedAgentProvider` to record token counts alongside cost data. Without this field, token tracking for agent-based providers is limited to the `costUsd` field only.

Current `AgentTaskResult`:
```typescript
interface AgentTaskResult {
  success: boolean;
  output: string;
  costUsd: number;
  durationMs: number;
  error?: string;
}
```

Recommended `AgentTaskResult`:
```typescript
interface AgentTaskResult {
  success: boolean;
  output: string;
  costUsd: number;
  durationMs: number;
  error?: string;
  tokens?: { input: number; output: number };  // NEW: for diagnostics token tracking
}
```

## Testing Strategy

### Unit Tests

- [ ] Test `executeTask()` emits `provider:call` event before inner is called
- [ ] Test `provider:call` event contains `projectId`, `engineId`, `taskId`, `taskType` (all required)
- [ ] Test `executeTask()` emits `provider:complete` on successful inner call with correct fields
- [ ] Test `provider:complete` event contains `latencyMs` > 0
- [ ] Test `provider:complete` event contains `success: true` when `result.success` is true
- [ ] Test `provider:complete` event contains `success: false` when `result.success` is false (task failure, not exception)
- [ ] Test `provider:complete` event contains `costUsd` from `result.costUsd`
- [ ] Test `provider:complete` event contains `tokens` from `result.tokens` when present
- [ ] Test `provider:complete` event contains `errorCode: 'TASK_FAILED'` (typed as `DiagnosticsErrorCode`) when `result.error` is truthy
- [ ] Test `executeTask()` emits `provider:error` when inner throws an exception
- [ ] Test `provider:error` event contains `success: false`, `errorCode` (typed as `DiagnosticsErrorCode`), `errorMessage` (sanitized)
- [ ] Test `errorMessage` is sanitized: API keys stripped, truncated to 500 chars
- [ ] Test error is re-thrown after emitting `provider:error`
- [ ] Test all events contain `providerName`, `model`, `agentType`, `projectId`, `engineId`, `taskId`, `taskType` from context
- [ ] Test `agentType` field is typed as `AgentType` (not `string`)
- [ ] Test `updateContext({ taskId: 'new-task' })` changes taskId for subsequent executeTask calls
- [ ] Test `updateContext({ taskType: 'review' })` changes taskType for subsequent executeTask calls
- [ ] Test `updateContext()` does not affect other context fields
- [ ] Test `isAvailable()` delegates to `inner.isAvailable()` without emitting events
- [ ] Test `dispose()` delegates to `inner.dispose()` without emitting events
- [ ] Test `diagnostics.emit()` is called (not awaited -- it is synchronous)
- [ ] Test `onProgress` callback is forwarded to inner provider

### Validation Steps

1. [ ] Create `instrumented-agent-provider.ts` with the decorator class and `InstrumentedAgentContext` interface
2. [ ] Create mock `IAgentProvider` for testing
3. [ ] Create mock `DiagnosticsQueue` with `emit` spy
4. [ ] Write all unit tests including `updateContext()` tests
5. [ ] Verify TypeScript strict mode compilation
6. [ ] Verify `IAgentProvider` contract is fully satisfied
7. [ ] Verify `sanitizeErrorMessage()` is used for all `errorMessage` assignments
8. [ ] Verify `DiagnosticsErrorCode` is used for all `errorCode` values

## Notes & Considerations

- The `context` parameter is stored as a mutable copy at construction time. The `updateContext()` method allows changing `taskId` and `taskType` between calls, avoiding the need to create a new `InstrumentedAgentProvider` instance for each task.
- `costUsd` comes from `AgentTaskResult.costUsd`, which is the cost reported by the agent provider itself (e.g., Claude Code's `--output-format json` cost field).
- `tokens` comes from `AgentTaskResult.tokens` (recommended addition), which provides input/output token counts from the agent provider.
- The `errorCode` on `provider:error` uses `(err as any)?.code ?? 'UNKNOWN'` to extract error codes from structured errors, but is typed as `DiagnosticsErrorCode` for type safety.
- `errorMessage` is always sanitized via `sanitizeErrorMessage()` to prevent API key leakage into diagnostics storage.
- The decorator must not catch and swallow errors -- it must always re-throw so the caller sees the original failure.
- All context fields (`projectId`, `engineId`, `taskId`, `taskType`) are required (not optional) to match Story 9-11 canonical design.

## Completion Checklist

- [ ] `instrumented-agent-provider.ts` created
- [ ] `InstrumentedAgentContext` interface defined with all required fields
- [ ] Class implements `IAgentProvider` interface fully
- [ ] `provider:call` emitted on entry with all context fields
- [ ] `provider:complete` emitted on success with all required fields including `tokens`
- [ ] `provider:error` emitted on failure with `DiagnosticsErrorCode` and sanitized `errorMessage`
- [ ] `updateContext()` method implemented for mutable taskId/taskType
- [ ] `sanitizeErrorMessage()` used for all errorMessage assignments
- [ ] `isAvailable()` delegates to inner
- [ ] `dispose()` delegates to inner
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
- [ ] Code reviewed and approved
