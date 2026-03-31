---
title: "Task 1: Create DiagnosticsEvent Types and DiagnosticsQueue Class"
sidebar:
  order: 90
---

**Story:** 9-11-diagnostics-queue-mcp-interceptors - Diagnostics Queue & MCP Interceptors
**Epic:** 9

## Task Description

Create the core telemetry infrastructure in `packages/shared/src/telemetry/`: the `DiagnosticsEvent` type definitions and the `DiagnosticsQueue` class. The queue provides a synchronous `emit()` method for zero hot-path overhead and a timer-based drain that batches events to a processor. This is Part A of the story and has no dependency on `@tamma/mcp-client`.

## Acceptance Criteria

- `DiagnosticsEventType` union includes all 6 event types: `tool:invoke`, `tool:complete`, `tool:error`, `provider:call`, `provider:complete`, `provider:error`
- `DiagnosticsEvent` is a discriminated union of `ToolDiagnosticsEvent` and `ProviderDiagnosticsEvent` (F01)
- `ToolDiagnosticsEvent` has required `toolName`; `ProviderDiagnosticsEvent` has required `providerName` (F01)
- `DiagnosticsErrorCode` union type defined for typed error codes (F01)
- `DiagnosticsEventBase` includes `correlationId?: string` for start/end event pairing (F15)
- Context fields (`agentType`, `projectId`, `engineId`, `taskId`, `taskType`) are optional on the base (populated by bridge wiring)
- `DiagnosticsQueue` implements `IDiagnosticsQueue` interface with `emit()`, `setProcessor()`, `dispose()`, `getDroppedCount()` (F18)
- Constructor accepts `{ drainIntervalMs?, maxQueueSize?, logger?: DiagnosticsQueueLogger }` (F02)
- `DiagnosticsQueueLogger` interface defined with `warn()` and optional `debug()` (F02)
- `DiagnosticsQueue.emit()` is synchronous -- returns immediately without awaiting drain
- Queue drops oldest events when `maxQueueSize` is exceeded (default: 1000) and increments `droppedCount` (F02)
- `getDroppedCount()` accessor returns the number of dropped events (F02)
- Timer-based drain runs on configurable interval (default: 5000ms)
- Drain timer uses `.unref()` so it does not keep the Node.js process alive
- `drainPromise` guard prevents concurrent drain from timer + `dispose()` racing
- Processor errors are logged via `logger?.warn()` with structured context (not silently swallowed) (F05)
- `dispose()` clears the timer first, then re-drains in a loop until empty (max 10 iterations) (F12)
- Events MUST be delivered to processors in FIFO order; batching preserves insertion order (F17)
- Barrel export `packages/shared/src/telemetry/index.ts` re-exports all telemetry types
- MODIFY `packages/shared/package.json` to add `./telemetry` subpath export (F13)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/shared/src/telemetry/diagnostics-event.ts`
  - [ ] Import `AgentType` from `../types/knowledge.js` using `import type`
  - [ ] Define `DiagnosticsEventType` union: `'tool:invoke' | 'tool:complete' | 'tool:error' | 'provider:call' | 'provider:complete' | 'provider:error'`
  - [ ] Define `DiagnosticsErrorCode` union: `'RATE_LIMIT_EXCEEDED' | 'QUOTA_EXCEEDED' | 'AUTH_FAILED' | 'TIMEOUT' | 'NETWORK_ERROR' | 'TASK_FAILED' | 'UNKNOWN'` (F01)
  - [ ] Define `DiagnosticsEventBase` interface with shared fields (F01):
    - `type: DiagnosticsEventType` (required)
    - `timestamp: number` (required, `Date.now()` value)
    - `correlationId?: string` (for start/end event pairing, F15)
    - `agentType?: AgentType` (optional, populated by bridge wiring)
    - `projectId?: string` (optional)
    - `engineId?: string` (optional)
    - `taskId?: string` (optional)
    - `taskType?: string` (optional)
    - `latencyMs?: number`
    - `success?: boolean`
    - `costUsd?: number`
    - `errorCode?: DiagnosticsErrorCode`
    - `errorMessage?: string`
  - [ ] Define `ToolDiagnosticsEvent extends DiagnosticsEventBase` (F01):
    - `type: 'tool:invoke' | 'tool:complete' | 'tool:error'`
    - `toolName: string` (required for tool events)
    - `serverName?: string`
    - `args?: Record<string, unknown>`
  - [ ] Define `ProviderDiagnosticsEvent extends DiagnosticsEventBase` (F01):
    - `type: 'provider:call' | 'provider:complete' | 'provider:error'`
    - `providerName: string` (required for provider events)
    - `model?: string`
    - `tokens?: { input: number; output: number }`
  - [ ] Export `DiagnosticsEvent = ToolDiagnosticsEvent | ProviderDiagnosticsEvent` (F01)
- [ ] Create `packages/shared/src/telemetry/diagnostics-queue.ts`
  - [ ] Export `DiagnosticsEventProcessor` type: `(events: DiagnosticsEvent[]) => Promise<void>`
  - [ ] Export `DiagnosticsQueueLogger` interface with `warn(msg, context?)` and optional `debug?(msg, context?)` (F02)
  - [ ] Export `IDiagnosticsQueue` interface with `emit()`, `setProcessor()`, `dispose()`, `getDroppedCount()` (F18)
  - [ ] Implement `DiagnosticsQueue` class implementing `IDiagnosticsQueue`:
    - Private fields: `queue: DiagnosticsEvent[]`, `processor: DiagnosticsEventProcessor | null`, `drainTimer: ReturnType<typeof setInterval> | null`, `drainPromise: Promise<void> | null`, `drainIntervalMs: number`, `maxQueueSize: number`, `logger?: DiagnosticsQueueLogger`, `droppedCount: number`
    - Constructor accepts optional `{ drainIntervalMs?: number; maxQueueSize?: number; logger?: DiagnosticsQueueLogger }` (F02)
    - `setProcessor(processor)`: stores processor, starts drain timer with `.unref()`
    - `emit(event)`: synchronous push; drops oldest (`shift()`) when queue full; increments `droppedCount` (F02)
    - `getDroppedCount()`: returns `droppedCount` value (F02)
    - `drain()`: private async; guarded by `drainPromise`; splices queue, calls processor, logs errors via `logger?.warn()` with structured context (F05), clears `drainPromise` in `.finally()`
    - `dispose()`: clears interval, then re-drains in loop until `queue.length === 0 && !drainPromise` (max 10 iterations) (F12)
- [ ] Create `packages/shared/src/telemetry/index.ts`
  - [ ] Re-export all from `./diagnostics-event.js`
  - [ ] Re-export all from `./diagnostics-queue.js`

### Files to Modify/Create

- `packages/shared/src/telemetry/diagnostics-event.ts` -- **CREATE** -- Discriminated union event type definitions (F01)
- `packages/shared/src/telemetry/diagnostics-queue.ts` -- **CREATE** -- Queue class with IDiagnosticsQueue, DiagnosticsQueueLogger (F02, F05, F12, F17, F18)
- `packages/shared/src/telemetry/index.ts` -- **CREATE** -- Barrel export
- `packages/shared/package.json` -- **MODIFY** -- Add `./telemetry` subpath export (F13)

### Dependencies

- [ ] `packages/shared/src/types/knowledge.ts` must export `AgentType` (already does)
- [ ] No dependency on `@tamma/mcp-client` or `@tamma/cost-monitor`

## Testing Strategy

### Unit Tests

- [ ] Test `emit()` is synchronous -- call emit and verify no drain occurred yet
- [ ] Test queue drains to processor on interval (use `vi.useFakeTimers()`)
- [ ] Test `drainPromise` guard: trigger timer drain while a drain is in-flight, verify only one processor call runs at a time
- [ ] Test processor throwing an error does not lose subsequently emitted events
- [ ] Test queue drops oldest event when `maxQueueSize` is exceeded
- [ ] Test `getDroppedCount()` returns correct count after overflow (F02)
- [ ] Test `dispose()` re-drains until queue is empty (max 10 iterations) (F12)
- [ ] Test timer uses `.unref()` -- spy on `setInterval` return and verify `.unref()` is called
- [ ] Test `setProcessor()` does not start timer if no processor provided (edge case)
- [ ] Test `emit()` before `setProcessor()` queues events, then draining after `setProcessor()` delivers them
- [ ] Test `dispose()` on a queue with no processor is a no-op (does not throw)
- [ ] Test `drain()` logs structured warning via `logger?.warn()` on processor failure (F05)
- [ ] Test events are delivered in FIFO order within a batch (F17)

### Validation Steps

1. [ ] Create the telemetry directory and files
2. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
3. [ ] Verify `AgentType` import resolves correctly
4. [ ] Write unit tests in `packages/shared/src/telemetry/diagnostics-queue.test.ts` (created in Task 6)
5. [ ] Run `pnpm vitest run packages/shared/src/telemetry/diagnostics-queue`

## Notes & Considerations

- The `emit()` method is the critical hot-path function -- it must NEVER do any async work, logging, or allocations beyond the array push
- `drainPromise` is essential: if the timer fires while `dispose()` is draining, or vice versa, the guard ensures they do not run concurrently
- The `catch()` on the processor promise logs errors via `logger?.warn()` with structured context (`error`, `batchSize`) rather than swallowing silently (F05)
- `splice(0)` atomically removes all elements from the queue array, which is important for the batch semantics; FIFO order is preserved (F17)
- ESM requires `.js` extensions in import paths even for `.ts` source files
- The discriminated union approach (F01) ensures `toolName` is required for tool events and `providerName` is required for provider events at compile time
- `dispose()` re-drains in a loop (max 10 iterations) to handle events arriving during drain (F12)

## Completion Checklist

- [ ] `packages/shared/src/telemetry/diagnostics-event.ts` created with discriminated union `DiagnosticsEvent` (F01)
- [ ] `DiagnosticsErrorCode` union type exported (F01)
- [ ] `DiagnosticsEventBase` has `correlationId?: string` (F15)
- [ ] `ToolDiagnosticsEvent` has required `toolName`; `ProviderDiagnosticsEvent` has required `providerName` (F01)
- [ ] `packages/shared/src/telemetry/diagnostics-queue.ts` created with `DiagnosticsQueue` implementing `IDiagnosticsQueue` (F18)
- [ ] `DiagnosticsEventProcessor`, `DiagnosticsQueueLogger`, `IDiagnosticsQueue` types exported (F02, F18)
- [ ] Constructor accepts `{ drainIntervalMs?, maxQueueSize?, logger? }` (F02)
- [ ] `emit()` is synchronous, increments `droppedCount` on overflow (F02)
- [ ] `getDroppedCount()` accessor implemented (F02)
- [ ] Queue bounded with `maxQueueSize` (drops oldest)
- [ ] Timer uses `.unref()`
- [ ] `drainPromise` guard implemented
- [ ] Processor errors logged via `logger?.warn()` with structured context (F05)
- [ ] `dispose()` re-drains in loop until empty, max 10 iterations (F12)
- [ ] FIFO order preserved in batches (F17)
- [ ] `packages/shared/src/telemetry/index.ts` barrel created
- [ ] `packages/shared/package.json` has `./telemetry` subpath export (F13)
- [ ] TypeScript strict mode compilation passes
