---
title: "Task 6: Bridge MCPClient Events to DiagnosticsQueue and Write Tests"
sidebar:
  order: 90
---

**Story:** 9-11-diagnostics-queue-mcp-interceptors - Diagnostics Queue & MCP Interceptors
**Epic:** 9

## Task Description

Document and test the bridge wiring pattern that connects MCPClient's existing `EventEmitter` events (`tool:invoked`, `tool:completed`) to the `DiagnosticsQueue`. This bridge is wired at the application level (CLI startup or server initialization), not inside the MCPClient class itself. Also create all unit test files for the story: `diagnostics-queue.test.ts`, `diagnostics-processor.test.ts`, and `interceptors.test.ts`.

## Acceptance Criteria

- Bridge wiring maps `mcpClient.on('tool:invoked', ...)` to `diagnosticsQueue.emit({ type: 'tool:invoke', ... })`
- Bridge wiring maps `mcpClient.on('tool:completed', ...)` to `diagnosticsQueue.emit({ type: data.success ? 'tool:complete' : 'tool:error', ... })`
- Bridge generates `correlationId` via `crypto.randomUUID()` for start/end event pairing (F15)
- Tool arguments are truncated via `truncateArgs()` (max 10KB serialized) before queueing (F06)
- Sensitive keys (`password`, `token`, `apiKey`, `secret`, `authorization`) are redacted from args before queueing (F07)
- `errorMessage` values are sanitized via `sanitizeErrorMessage()` (from Story 9-2) before storing (F07)
- Context fields (`agentType`, `projectId`, `engineId`, `taskId`, `taskType`) are sourced from the runtime context of the caller
- All unit tests for Tasks 1-5 are written and passing
- Test coverage meets targets: 80% line, 75% branch, 85% function

## Implementation Details

### Technical Requirements

#### Bridge Wiring Pattern (Documentation + Tests)

The bridge is NOT a class -- it is a wiring pattern applied at the application level. The test validates the pattern:

```typescript
import { randomUUID } from 'node:crypto';
import { truncateArgs, redactSensitiveKeys } from '@tamma/shared/telemetry/utils.js';
import { sanitizeErrorMessage } from '@tamma/shared/telemetry/sanitize.js';

const MAX_DIAGNOSTICS_ARG_SIZE = 10_240; // 10KB
const SENSITIVE_KEYS = ['password', 'token', 'apiKey', 'secret', 'authorization'];

// Wiring pattern (to be used in CLI start.tsx or server.ts):
mcpClient.on('tool:invoked', (data: {
  serverName: string;
  toolName: string;
  args: Record<string, unknown>;
}) => {
  const correlationId = randomUUID();
  // Store correlationId for pairing with completion event
  pendingCorrelations.set(`${data.serverName}:${data.toolName}`, correlationId);

  diagnosticsQueue.emit({
    type: 'tool:invoke',
    timestamp: Date.now(),
    correlationId,
    serverName: data.serverName,
    toolName: data.toolName,
    args: truncateArgs(redactSensitiveKeys(data.args, SENSITIVE_KEYS), MAX_DIAGNOSTICS_ARG_SIZE),
    agentType: currentAgentType,
    projectId,
    engineId,
    taskId: currentTaskId,
    taskType: currentTaskType,
  });
});

mcpClient.on('tool:completed', (data: {
  serverName: string;
  toolName: string;
  success: boolean;
  latencyMs: number;
  errorMessage?: string;
}) => {
  const correlationId = pendingCorrelations.get(`${data.serverName}:${data.toolName}`);
  pendingCorrelations.delete(`${data.serverName}:${data.toolName}`);

  diagnosticsQueue.emit({
    type: data.success ? 'tool:complete' : 'tool:error',
    timestamp: Date.now(),
    correlationId,
    serverName: data.serverName,
    toolName: data.toolName,
    latencyMs: data.latencyMs,
    success: data.success,
    errorMessage: data.errorMessage ? sanitizeErrorMessage(data.errorMessage) : undefined,
    agentType: currentAgentType,
    projectId,
    engineId,
    taskId: currentTaskId,
    taskType: currentTaskType,
  });
});
```

#### Test Files to Create

- [ ] Create `packages/shared/src/telemetry/diagnostics-queue.test.ts`
- [ ] Create `packages/shared/src/telemetry/diagnostics-processor.test.ts`
- [ ] Create `packages/mcp-client/src/interceptors.test.ts`

### DiagnosticsQueue Tests (`diagnostics-queue.test.ts`)

- [ ] Test `emit()` is synchronous -- returns before queue drains
- [ ] Test queue drains to processor on interval (use `vi.useFakeTimers()` and `vi.advanceTimersByTime()`)
- [ ] Test `drainPromise` guard: invoke `dispose()` while a slow processor is running, verify no concurrent drain
- [ ] Test processor error does NOT crash queue and does not lose subsequent events
- [ ] Test queue drops oldest event when `maxQueueSize` is exceeded
- [ ] Test `dispose()` clears timer then flushes remaining events
- [ ] Test timer uses `.unref()` -- spy on setInterval and verify `.unref()` called on returned timer
- [ ] Test `emit()` before `setProcessor()` queues events, then `setProcessor()` + timer advance delivers them
- [ ] Test `dispose()` on queue with no processor does not throw
- [ ] Test configurable `drainIntervalMs` and `maxQueueSize`
- [ ] Test empty queue drain is a no-op (processor not called)
- [ ] Test batch semantics: processor receives all events emitted between drains as a single array

### DiagnosticsProcessor Tests (`diagnostics-processor.test.ts`)

- [ ] Test maps `tool:complete` event to `UsageRecordInput` with correct field values
- [ ] Test maps `provider:complete` event to `UsageRecordInput`
- [ ] Test maps `tool:error` event with `success: false` and `errorCode`
- [ ] Test maps `provider:error` event with `success: false`
- [ ] Test skips `tool:invoke` events (does not call `recordUsage`)
- [ ] Test skips `provider:call` events (does not call `recordUsage`)
- [ ] Test missing `providerName` defaults to `'claude-code'`
- [ ] Test missing `model` defaults to `'unknown'`
- [ ] Test missing `tokens` defaults to `0` for input, output, and total
- [ ] Test missing `latencyMs` defaults to `0`
- [ ] Test missing `success` defaults to `false`
- [ ] Test per-event `recordUsage` failure is caught, remaining events still processed
- [ ] Test logger warning called when `recordUsage` throws
- [ ] Test processor works without logger (no crash when logger is undefined)

### Interceptors Tests (`interceptors.test.ts`)

- [ ] Test `runPre()` with empty chain returns original args and empty warnings
- [ ] Test `runPost()` with empty chain returns original result and empty warnings
- [ ] Test single pre-interceptor modifies args
- [ ] Test single post-interceptor modifies result
- [ ] Test multiple pre-interceptors run in order, piped
- [ ] Test multiple post-interceptors run in order, piped
- [ ] Test warnings accumulated from all interceptors
- [ ] Test `createSanitizationInterceptor` sanitizes text content in ToolResult
- [ ] Test `createSanitizationInterceptor` does not modify non-text content
- [ ] Test `createSanitizationInterceptor` adds warning when content changed
- [ ] Test `createSanitizationInterceptor` returns empty warnings when nothing changed
- [ ] Test `createUrlValidationInterceptor` adds warning for blocked URL
- [ ] Test `createUrlValidationInterceptor` no warnings for allowed URLs
- [ ] Test `createUrlValidationInterceptor` ignores non-string args
- [ ] Test `createUrlValidationInterceptor` returns original args unchanged

### Bridge Wiring Tests (in `interceptors.test.ts` or separate file)

- [ ] Test `tool:invoked` event maps to `diagnosticsQueue.emit()` with `type: 'tool:invoke'`
- [ ] Test `tool:completed` event with `success: true` maps to `type: 'tool:complete'`
- [ ] Test `tool:completed` event with `success: false` maps to `type: 'tool:error'`
- [ ] Test context fields are correctly passed through from runtime context
- [ ] Test `timestamp` is populated with current time
- [ ] Test `correlationId` is generated and included in both start and end events (F15)
- [ ] Test args are truncated via `truncateArgs()` before queueing (F06)
- [ ] Test sensitive keys are redacted from args before queueing (F07)
- [ ] Test `errorMessage` is sanitized via `sanitizeErrorMessage()` (F07)

### Files to Modify/Create

- `packages/shared/src/telemetry/diagnostics-queue.test.ts` -- **CREATE** -- Queue unit tests
- `packages/shared/src/telemetry/diagnostics-processor.test.ts` -- **CREATE** -- Processor unit tests
- `packages/mcp-client/src/interceptors.test.ts` -- **CREATE** -- Interceptor and bridge unit tests

### Dependencies

- [ ] Tasks 1-5 must be completed first (all source files must exist)

## Testing Strategy

### Test Infrastructure

- Use `vi.useFakeTimers()` for timer-based tests in DiagnosticsQueue
- Use `vi.fn()` for mock processors, cost trackers, loggers, sanitizers, and validators
- Use `vi.spyOn()` to verify `.unref()` is called on timer handles
- Mock `ICostTracker` with `{ recordUsage: vi.fn() }` for processor tests
- Mock `ILogger` with `{ warn: vi.fn(), info: vi.fn(), error: vi.fn(), debug: vi.fn() }` for processor tests
- Create helper functions for constructing valid `DiagnosticsEvent` objects

### Validation Steps

1. [ ] Create all three test files
2. [ ] Run `pnpm vitest run packages/shared/src/telemetry/diagnostics-queue` -- must pass
3. [ ] Run `pnpm vitest run packages/shared/src/telemetry/diagnostics-processor` -- must pass
4. [ ] Run `pnpm vitest run packages/mcp-client/src/interceptors` -- must pass
5. [ ] Run `pnpm vitest run --coverage` and verify coverage targets met
6. [ ] Run full test suites: `pnpm --filter @tamma/shared test` and `pnpm --filter @tamma/mcp-client test`

## Notes & Considerations

- The bridge wiring is intentionally NOT a class or module -- it is a pattern that the CLI or server applies at startup. This keeps `@tamma/mcp-client` and `@tamma/shared` independent: neither depends on the other.
- The context fields (`agentType`, `projectId`, `engineId`, `taskId`, `taskType`) are dynamic and come from the runtime state of the orchestration engine. The bridge tests use fixed values to verify the wiring, but in production these come from the engine context.
- The bridge generates a `correlationId` via `crypto.randomUUID()` before the tool call and includes it in both start and end events for pairing (F15). A `pendingCorrelations` Map tracks in-flight calls.
- Tool arguments are truncated to MAX_DIAGNOSTICS_ARG_SIZE (10KB) via `truncateArgs()` to prevent memory exhaustion (F06). The helper performs `JSON.stringify` and truncates values exceeding the limit.
- Sensitive keys (`password`, `token`, `apiKey`, `secret`, `authorization`) are redacted from args before queueing to prevent information leakage (F07). Error messages are sanitized via `sanitizeErrorMessage()` from Story 9-2 (F07).
- Fake timers are essential for testing `DiagnosticsQueue` drain behavior without waiting for real 5-second intervals.
- The `drainPromise` guard test is the most complex: it needs to create a slow processor (using a deferred promise), trigger a drain via timer, then call `dispose()` before the first drain completes, and verify only one processor call executes at a time.
- For interceptor tests, create simple mock interceptors that append to args or prepend to result content, making it easy to verify ordering.

## Completion Checklist

- [ ] `packages/shared/src/telemetry/diagnostics-queue.test.ts` created with all queue tests
- [ ] `packages/shared/src/telemetry/diagnostics-processor.test.ts` created with all processor tests
- [ ] `packages/mcp-client/src/interceptors.test.ts` created with all interceptor and bridge tests
- [ ] All tests passing
- [ ] Coverage targets met (80% line, 75% branch, 85% function)
- [ ] Bridge wiring pattern documented and tested with `correlationId` (F15)
- [ ] Bridge wiring truncates args and redacts sensitive keys (F06, F07)
- [ ] Bridge wiring sanitizes `errorMessage` (F07)
- [ ] Fake timers used correctly for queue tests
- [ ] Mocks used for external dependencies (cost tracker, logger, sanitizer, validator)
