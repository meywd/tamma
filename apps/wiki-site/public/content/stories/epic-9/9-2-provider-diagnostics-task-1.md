---
title: "Task 1: Create DiagnosticsEvent Type, Sanitization, Validation, and DiagnosticsQueue in Shared Telemetry Package"
sidebar:
  order: 90
---

**Story:** 9-2-provider-diagnostics - Provider Diagnostics
**Epic:** 9

## Task Description

Create the diagnostics event types, error sanitization utility, value validation utilities, and the `DiagnosticsQueue` class in `packages/shared/src/telemetry/`. This task covers:

1. **`diagnostics-event.ts`** -- DiagnosticsEvent as a discriminated union type (aligned with Story 9-11), with required context fields and typed error codes.
2. **`sanitize-error.ts`** -- `sanitizeErrorMessage()` utility that truncates to 500 chars and strips API key patterns.
3. **`validate-diagnostics.ts`** -- Value validation utilities for costUsd, token counts, and errorCode.
4. **`diagnostics-queue.ts`** -- The `DiagnosticsQueue` class with `drainPromise` concurrency guard, `setProcessor()` warning guard, `droppedCount` counter, and structured error logging.

The queue collects `DiagnosticsEvent` objects synchronously via `emit()` and drains them in batches to a registered processor on a configurable interval. The queue is designed to have zero overhead in the hot path -- `emit()` is synchronous, and the drain happens asynchronously in the background.

## Acceptance Criteria

- `DiagnosticsEvent` is defined in `diagnostics-event.ts` (NOT inline in `diagnostics-queue.ts`) -- aligned with Story 9-11
- `DiagnosticsEvent` uses discriminated union pattern: `ToolDiagnosticsEvent` (with `toolName`, `serverName`, `args`) and `ProviderDiagnosticsEvent` (with `providerName`, `model`)
- Context fields `agentType` (typed as `AgentType`), `projectId`, `engineId`, `taskId`, `taskType` are **required** (not optional)
- `errorCode` is typed as `DiagnosticsErrorCode` union (not plain string)
- `sanitizeErrorMessage()` truncates to 500 chars and strips Bearer tokens, `sk-*`, and `key-*` patterns
- `DiagnosticsQueue.emit()` is synchronous and does not block the caller
- Queue drains to the registered processor on a configurable interval (default 5000ms)
- Queue overflow drops the oldest event when `maxQueueSize` (default 1000) is reached and increments `droppedCount`
- `getDroppedCount()` exposes the number of dropped events
- `setProcessor()` logs a warning via optional logger if processor already set (does not silently replace)
- Drain timer is `unref()`'d so it does not prevent Node.js process exit
- `drainPromise` guard prevents concurrent drain from timer + `dispose()` racing -- matches Story 9-11 implementation
- `dispose()` flushes remaining events to the processor before clearing the timer
- Processor failures use structured error logging (`logger?.warn()`) instead of silent swallow
- Value validation: `costUsd >= 0`, token counts in `[0, 10_000_000]`, `errorCode` truncated to 100 chars
- All types (`DiagnosticsEvent`, `DiagnosticsEventType`, `DiagnosticsErrorCode`, `DiagnosticsEventProcessor`, `ToolDiagnosticsEvent`, `ProviderDiagnosticsEvent`) are exported
- `AgentType` is imported from `@tamma/shared` types, not redefined
- Barrel export at `packages/shared/src/telemetry/index.ts`
- Subpath export `@tamma/shared/telemetry` added to `packages/shared/package.json`

## Implementation Details

### Technical Requirements

- [ ] Create `packages/shared/src/telemetry/diagnostics-event.ts` with:
  - `DiagnosticsEventType` type alias: `'tool:invoke' | 'tool:complete' | 'tool:error' | 'provider:call' | 'provider:complete' | 'provider:error'`
  - `DiagnosticsErrorCode` type alias: `'RATE_LIMIT_EXCEEDED' | 'QUOTA_EXCEEDED' | 'TIMEOUT' | 'AUTH_FAILED' | 'NETWORK_ERROR' | 'TASK_FAILED' | 'UNKNOWN'`
  - `DiagnosticsEventBase` interface with shared fields (type, timestamp, agentType, projectId, engineId, taskId, taskType, latencyMs?, success?, costUsd?, errorCode?, errorMessage?, tokens?)
  - `ToolDiagnosticsEvent` interface extending base with `type: 'tool:invoke' | 'tool:complete' | 'tool:error'`, `toolName: string`, `serverName?: string`, `args?: Record<string, unknown>`
  - `ProviderDiagnosticsEvent` interface extending base with `type: 'provider:call' | 'provider:complete' | 'provider:error'`, `providerName: string`, `model?: string`
  - `DiagnosticsEvent` type alias as discriminated union: `ToolDiagnosticsEvent | ProviderDiagnosticsEvent`
- [ ] Create `packages/shared/src/telemetry/sanitize-error.ts` with:
  - `sanitizeErrorMessage(message: string): string` function
  - API key pattern regexes: Bearer tokens, `sk-*`, `key-*`, long alphanumeric strings
  - `MAX_ERROR_MESSAGE_LENGTH = 500` constant
  - Truncation with `'...'` suffix when exceeding max length
- [ ] Create `packages/shared/src/telemetry/validate-diagnostics.ts` with:
  - `validateCostUsd(value: number | undefined): number | undefined` -- clamps to >= 0
  - `validateTokenCount(value: number): number` -- clamps to [0, 10_000_000]
  - `validateErrorCode(value: string | undefined): string | undefined` -- truncates to 100 chars
- [ ] Create `packages/shared/src/telemetry/diagnostics-queue.ts` with:
  - `DiagnosticsEventProcessor` type alias: `(events: DiagnosticsEvent[]) => Promise<void>`
  - `DiagnosticsQueueLogger` interface: `{ warn(message: string, context?: Record<string, unknown>): void }`
  - `DiagnosticsQueue` class with:
    - `private queue: DiagnosticsEvent[]`
    - `private processor: DiagnosticsEventProcessor | null`
    - `private drainTimer: ReturnType<typeof setInterval> | null`
    - `private drainPromise: Promise<void> | null` -- in-flight drain guard
    - `private processorSet = false` -- setProcessor() guard flag
    - `private droppedCount = 0` -- overflow counter
    - `private readonly drainIntervalMs: number`
    - `private readonly maxQueueSize: number`
    - Constructor: `(options?: { drainIntervalMs?: number; maxQueueSize?: number }, logger?: DiagnosticsQueueLogger)`
    - `setProcessor()`: logs warning if already set, starts drain timer
    - `emit()`: synchronous push, drops oldest on overflow, increments droppedCount
    - `getDroppedCount()`: returns droppedCount
    - `drain()`: private async with drainPromise concurrency guard, structured error logging on failure
    - `dispose()`: clears timer, final drain
- [ ] Create `packages/shared/src/telemetry/index.ts` barrel exporting all types, classes, and utilities
- [ ] Add `"./telemetry"` subpath export to `packages/shared/package.json` exports map
- [ ] Update `packages/shared/src/index.ts` to re-export from telemetry barrel

### Files to Modify/Create

- CREATE `packages/shared/src/telemetry/diagnostics-event.ts`
- CREATE `packages/shared/src/telemetry/sanitize-error.ts`
- CREATE `packages/shared/src/telemetry/validate-diagnostics.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-queue.ts`
- CREATE `packages/shared/src/telemetry/index.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-queue.test.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-event.test.ts`
- CREATE `packages/shared/src/telemetry/sanitize-error.test.ts`
- CREATE `packages/shared/src/telemetry/validate-diagnostics.test.ts`
- MODIFY `packages/shared/package.json` -- add telemetry subpath export
- MODIFY `packages/shared/src/index.ts` -- add telemetry re-export

### Dependencies

- [ ] `AgentType` from `packages/shared/src/types/knowledge.ts` (already in the same package)
- [ ] No external dependencies required

## Testing Strategy

### Unit Tests — diagnostics-event.test.ts

- [ ] Test `ToolDiagnosticsEvent` requires `toolName` field
- [ ] Test `ProviderDiagnosticsEvent` requires `providerName` field
- [ ] Test discriminated union: `tool:invoke` type must have `toolName`, `provider:call` type must have `providerName`
- [ ] Test context fields `agentType`, `projectId`, `engineId`, `taskId`, `taskType` are required (not optional)
- [ ] Test `DiagnosticsErrorCode` only accepts defined values (type-level check)

### Unit Tests — sanitize-error.test.ts

- [ ] Test `sanitizeErrorMessage()` strips Bearer tokens
- [ ] Test `sanitizeErrorMessage()` strips `sk-*` keys
- [ ] Test `sanitizeErrorMessage()` strips `key-*` patterns
- [ ] Test `sanitizeErrorMessage()` truncates messages over 500 chars with `'...'` suffix
- [ ] Test `sanitizeErrorMessage()` leaves clean messages unchanged
- [ ] Test `sanitizeErrorMessage()` handles empty string
- [ ] Test `sanitizeErrorMessage()` handles message with multiple key patterns

### Unit Tests — validate-diagnostics.test.ts

- [ ] Test `validateCostUsd(undefined)` returns undefined
- [ ] Test `validateCostUsd(-1)` returns 0
- [ ] Test `validateCostUsd(5.5)` returns 5.5
- [ ] Test `validateTokenCount(-1)` returns 0
- [ ] Test `validateTokenCount(5000)` returns 5000
- [ ] Test `validateTokenCount(20_000_000)` returns 10_000_000
- [ ] Test `validateErrorCode(undefined)` returns undefined
- [ ] Test `validateErrorCode('SHORT')` returns 'SHORT'
- [ ] Test `validateErrorCode` with 200-char string returns 100-char truncated string

### Unit Tests — diagnostics-queue.test.ts

- [ ] Test `emit()` adds event to internal queue (verify queue length)
- [ ] Test `emit()` is synchronous (returns before any async work)
- [ ] Test `setProcessor()` starts the drain timer
- [ ] Test `setProcessor()` logs warning via logger if processor already set
- [ ] Test `setProcessor()` called twice does not create duplicate timers
- [ ] Test drain calls processor with batch of queued events
- [ ] Test drain clears the queue after passing batch to processor
- [ ] Test drain does nothing when queue is empty
- [ ] Test drain does nothing when no processor is set
- [ ] Test `drainPromise` guard prevents concurrent drain (timer + dispose racing)
- [ ] Test overflow behavior: pushing beyond `maxQueueSize` drops oldest event (shift) and increments droppedCount
- [ ] Test `getDroppedCount()` returns correct count after multiple overflows
- [ ] Test drain timer is `unref()`'d (mock `setInterval` and check `unref` call)
- [ ] Test `dispose()` clears the interval timer
- [ ] Test `dispose()` flushes remaining events to processor
- [ ] Test `dispose()` handles processor error during flush with structured logging (logger.warn)
- [ ] Test configurable `drainIntervalMs` (verify setInterval called with correct value)
- [ ] Test default values: `drainIntervalMs=5000`, `maxQueueSize=1000`
- [ ] Test processor error during drain calls `logger?.warn()` with error info (not silently swallowed)
- [ ] Test processor error during drain does not prevent subsequent drains

### Validation Steps

1. [ ] Create `diagnostics-event.ts` with discriminated union types and `DiagnosticsErrorCode`
2. [ ] Create `sanitize-error.ts` with `sanitizeErrorMessage()`
3. [ ] Create `validate-diagnostics.ts` with validation utilities
4. [ ] Create `diagnostics-queue.ts` with `DiagnosticsQueue` class (drainPromise, setProcessor guard, droppedCount, structured logging)
5. [ ] Create `index.ts` barrel export
6. [ ] Add telemetry subpath to `package.json`
7. [ ] Update shared `index.ts` with re-export
8. [ ] Write and run all unit tests
9. [ ] Verify TypeScript strict mode compilation passes
10. [ ] Verify `agentType` field is typed as `AgentType`, not `string`
11. [ ] Verify `errorCode` field is typed as `DiagnosticsErrorCode`, not `string`
12. [ ] Verify context fields are required (not optional) on `DiagnosticsEvent`

## Notes & Considerations

- The `emit()` method must remain synchronous. Do not add `async` or return a `Promise`. The zero-overhead design is critical because `emit()` sits in the hot path of every provider call.
- The drain timer uses `setInterval` with `.unref()` so that if the main application is shutting down, the timer does not keep the process alive.
- The `splice(0)` pattern atomically takes all events from the queue, preventing race conditions between emit and drain.
- The `drainPromise` guard (from Story 9-11) prevents concurrent drain from timer + dispose() racing. If a drain is already in flight, subsequent drain calls wait for it instead of starting a new one.
- The `setProcessor()` guard logs a warning via optional logger if a processor has already been set, preventing silent replacement that could lead to lost event routing.
- The `droppedCount` counter tracks how many events were dropped due to queue overflow, exposed via `getDroppedCount()` for observability.
- Processor errors use structured logging (`logger?.warn()`) instead of being silently swallowed, providing better observability while still preventing diagnostics failures from affecting the application.
- The `DiagnosticsEvent` is defined in its own file (`diagnostics-event.ts`) aligned with Story 9-11 canonical design, not inline in `diagnostics-queue.ts`.
- The discriminated union pattern (`ToolDiagnosticsEvent | ProviderDiagnosticsEvent`) ensures tool events always have `toolName` and provider events always have `providerName`, improving type safety.
- Context fields (`agentType`, `projectId`, `engineId`, `taskId`, `taskType`) are required (not optional) to ensure all events have full context for cost tracking and reporting.
- `DiagnosticsErrorCode` is a typed union instead of plain string, documenting expected error categories and preventing typos.
- `sanitizeErrorMessage()` prevents API keys from leaking into diagnostics storage. The patterns cover Bearer tokens, OpenAI-style `sk-*` keys, and generic `key-*` patterns.
- Value validation utilities (`validateCostUsd`, `validateTokenCount`, `validateErrorCode`) provide bounds checking for diagnostics fields to prevent corrupt data.

## Completion Checklist

- [ ] `diagnostics-event.ts` created with discriminated union types, DiagnosticsErrorCode, required context fields
- [ ] `sanitize-error.ts` created with `sanitizeErrorMessage()` utility
- [ ] `validate-diagnostics.ts` created with validation utilities
- [ ] `diagnostics-queue.ts` created with drainPromise guard, setProcessor guard, droppedCount, structured logging
- [ ] `index.ts` barrel export created
- [ ] `package.json` updated with telemetry subpath export
- [ ] `shared/src/index.ts` updated with telemetry re-export
- [ ] All unit tests written and passing (diagnostics-event, sanitize-error, validate-diagnostics, diagnostics-queue)
- [ ] TypeScript strict mode compiles without errors
- [ ] `agentType` typed as `AgentType` from `../types/knowledge.js`
- [ ] `errorCode` typed as `DiagnosticsErrorCode`, not `string`
- [ ] Context fields are required, not optional
- [ ] `drainPromise` guard matches Story 9-11 implementation
- [ ] `setProcessor()` warns on replacement via optional logger
- [ ] `droppedCount` tracked and exposed via `getDroppedCount()`
- [ ] Processor errors logged via `logger?.warn()`, not silently swallowed
- [ ] Code reviewed and approved
