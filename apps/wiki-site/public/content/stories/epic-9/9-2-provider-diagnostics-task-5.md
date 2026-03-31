---
title: "Task 5: Create Diagnostics Processor for Cost Tracking"
sidebar:
  order: 90
---

**Story:** 9-2-provider-diagnostics - Provider Diagnostics
**Epic:** 9

## Task Description

Create the `DiagnosticsProcessor` that serves as the bridge between the `DiagnosticsQueue` and the `ICostTracker`. The processor is registered with the queue via `setProcessor()` and receives batches of `DiagnosticsEvent` objects on each drain cycle. For each `provider:complete` or `provider:error` event, the processor maps the event fields to a `UsageRecordInput` and calls `costTracker.recordUsage()`.

Key enhancements over initial design:
- **Safe provider name mapping**: Uses `mapProviderName()` from `provider-name-mapping.ts` instead of unsafe `as Provider` cast.
- **Structured error logging**: Processor errors are logged via `logger?.warn()` instead of being silently swallowed.
- **Error message sanitization**: Uses `sanitizeErrorMessage()` for any error messages passed through.
- **Value validation**: Validates costUsd, token counts, and errorCode before recording.
- **Required context fields**: Context fields (`projectId`, `engineId`, `taskId`, `taskType`, `agentType`) come directly from the event (they are required on `DiagnosticsEvent`).

## Acceptance Criteria

- Processor handles `provider:complete` events by mapping them to `UsageRecordInput` and calling `costTracker.recordUsage()`
- Processor handles `provider:error` events the same way (errors are tracked usage too)
- Processor ignores `provider:call`, `tool:invoke`, `tool:complete`, `tool:error` events (no cost tracking for those)
- `UsageRecordInput` mapping includes: `projectId`, `engineId`, `agentType`, `taskId`, `taskType`, `provider` (via `mapProviderName()`), `model`, `inputTokens`, `outputTokens`, `totalTokens`, `latencyMs`, `success`, `errorCode`
- Provider name mapping uses `mapProviderName()` (safe validation) instead of unsafe `as Provider` cast
- Value validation is applied: `costUsd >= 0`, token counts in `[0, 10_000_000]`, `errorCode` truncated to 100 chars
- Processor errors use structured logging (`logger?.warn()`) instead of being silently swallowed
- A single `recordUsage()` failure does not prevent processing of remaining events in the batch
- Factory function `createDiagnosticsProcessor(costTracker, logger?)` returns a `DiagnosticsEventProcessor`

## Implementation Details

### Technical Requirements

- [ ] Create `packages/providers/src/diagnostics-processor.ts` with:
  - `createDiagnosticsProcessor(costTracker: ICostTracker, logger?: ILogger): DiagnosticsEventProcessor` factory function
- [ ] The returned processor function:
  - Iterates through the batch of `DiagnosticsEvent` objects
  - Filters for `type === 'provider:complete'` or `type === 'provider:error'` (these are `ProviderDiagnosticsEvent` subtypes)
  - Also processes `type === 'tool:complete'` or `type === 'tool:error'` (aligned with Story 9-11 processor design)
  - Maps each event to a `UsageRecordInput`:
    ```typescript
    {
      projectId: event.projectId,                            // required on DiagnosticsEvent
      engineId: event.engineId,                              // required on DiagnosticsEvent
      agentType: event.agentType,                            // required, typed as AgentType
      taskId: event.taskId,                                  // required on DiagnosticsEvent
      taskType: event.taskType as TaskType,                  // required on DiagnosticsEvent
      provider: mapProviderName(event.providerName),         // safe mapping, NOT unsafe cast
      model: event.model ?? 'unknown',
      inputTokens: validateTokenCount(event.tokens?.input ?? 0),
      outputTokens: validateTokenCount(event.tokens?.output ?? 0),
      totalTokens: validateTokenCount((event.tokens?.input ?? 0) + (event.tokens?.output ?? 0)),
      latencyMs: event.latencyMs ?? 0,
      success: event.success ?? false,
      errorCode: validateErrorCode(event.errorCode),
      traceId: undefined,                                    // not available from diagnostics events
    }
    ```
  - Calls `costTracker.recordUsage(usageInput)` for each mapped event
  - Wraps each `recordUsage()` call in try/catch:
    - On failure: `logger?.warn('Diagnostics processor: failed to record', { type: event.type, error: err.message })` (structured logging, not silent swallow)
    - Continues processing remaining events in the batch
- [ ] Add `@tamma/cost-monitor` to `packages/providers/package.json` dependencies

### Files to Modify/Create

- CREATE `packages/providers/src/diagnostics-processor.ts`
- CREATE `packages/providers/src/diagnostics-processor.test.ts`
- MODIFY `packages/providers/package.json` -- add `@tamma/cost-monitor` dependency

### Dependencies

- [ ] Task 1: `DiagnosticsEvent`, `DiagnosticsEventProcessor`, `ProviderDiagnosticsEvent` from `@tamma/shared/telemetry`
- [ ] Task 1: `validateTokenCount`, `validateErrorCode` from `@tamma/shared/telemetry`
- [ ] Task 4: Updated `Provider`, `AgentType`, `UsageRecordInput`, `ICostTracker`, `TaskType` from `@tamma/cost-monitor`
- [ ] Task 4: `mapProviderName` from `./provider-name-mapping.js`
- [ ] `ILogger` from `@tamma/shared` (for structured error logging)

## Testing Strategy

### Unit Tests

- [ ] Test processor maps `provider:complete` event to correct `UsageRecordInput` fields
- [ ] Test processor maps `provider:error` event to correct `UsageRecordInput` fields
- [ ] Test processor maps `tool:complete` event to `UsageRecordInput` (aligned with Story 9-11)
- [ ] Test processor maps `tool:error` event to `UsageRecordInput` (aligned with Story 9-11)
- [ ] Test processor calls `costTracker.recordUsage()` for each mappable event
- [ ] Test processor ignores `provider:call` events (no `recordUsage` call)
- [ ] Test processor ignores `tool:invoke` events (no `recordUsage` call)
- [ ] Test processor handles batch with mixed event types (only processes complete and error events)
- [ ] Test processor handles empty batch without error
- [ ] Test `provider` field uses `mapProviderName()` for safe mapping (not unsafe cast)
- [ ] Test `mapProviderName()` receives `event.providerName` as input
- [ ] Test unknown provider name results in `'claude-code'` default via `mapProviderName()`
- [ ] Test context fields (`projectId`, `engineId`, `taskId`, `taskType`, `agentType`) come from event directly (they are required)
- [ ] Test `tokens` defaults to `{ input: 0, output: 0 }` when event has no token data
- [ ] Test `totalTokens` is computed as `input + output`
- [ ] Test token counts are validated via `validateTokenCount()` (clamped to [0, 10_000_000])
- [ ] Test `errorCode` is validated via `validateErrorCode()` (truncated to 100 chars)
- [ ] Test single `recordUsage()` failure does not prevent processing remaining events
- [ ] Test processor catches errors from `recordUsage()` and calls `logger?.warn()` with error info
- [ ] Test processor works without logger (logger is optional)
- [ ] Test processor function signature matches `DiagnosticsEventProcessor` type
- [ ] Test `model` defaults to `'unknown'` when event has no model

### Validation Steps

1. [ ] Create `diagnostics-processor.ts` with factory function
2. [ ] Add `@tamma/cost-monitor` dependency to providers `package.json`
3. [ ] Create mock `ICostTracker` for testing
4. [ ] Create mock logger for testing
5. [ ] Write all unit tests including mapProviderName usage, value validation, and structured logging
6. [ ] Verify TypeScript strict mode compilation
7. [ ] Verify end-to-end flow: `DiagnosticsQueue` -> processor -> `costTracker.recordUsage()`
8. [ ] Run `pnpm install` to update lockfile after adding dependency

## Notes & Considerations

- The processor is a factory function (not a class) because it is stateless -- it only needs the `costTracker` and optional `logger` references, which are captured in the closure.
- **Safe provider mapping**: The `mapProviderName()` function (from Task 4) replaces the unsafe `event.providerName as Provider` cast. It validates the name against the known `Provider` values and returns a safe default (`'claude-code'`) for unrecognized names.
- **Context fields from event**: Since `DiagnosticsEvent` context fields (`projectId`, `engineId`, `taskId`, `taskType`, `agentType`) are now required (not optional), the processor no longer needs a `DiagnosticsProcessorContext` with defaults. Context comes directly from the event.
- **Structured error logging**: Processor errors call `logger?.warn()` with the event type and error message, rather than being silently swallowed. This provides observability into processor failures while still ensuring they do not affect the queue or caller.
- **Value validation**: Token counts are clamped to `[0, 10_000_000]` and `errorCode` is truncated to 100 characters to prevent corrupt data from reaching the cost tracker.
- The `taskType` cast to `TaskType` is necessary because `event.taskType` is `string` but `UsageRecordInput` expects `TaskType`.
- Cache token fields (`cacheReadTokens`, `cacheWriteTokens`) are not populated by the diagnostics events. They will be `undefined` in the `UsageRecordInput`, which is acceptable since they are optional on `UsageRecord`.
- The processor processes events sequentially (not concurrently) to avoid overwhelming the cost tracker with parallel writes. Each `recordUsage()` is awaited before moving to the next event.
- Error logging is critical: if the cost tracker's storage is down, the diagnostics queue must continue to function and provider calls must not be affected. The logger provides visibility into these failures.

## Completion Checklist

- [ ] `diagnostics-processor.ts` created with factory function
- [ ] Uses `mapProviderName()` for safe provider name mapping (no unsafe `as Provider` cast)
- [ ] Uses `validateTokenCount()` and `validateErrorCode()` for value validation
- [ ] Event-to-UsageRecordInput mapping uses required context fields from event
- [ ] Error handling: individual `recordUsage()` failures logged via `logger?.warn()` and processing continues
- [ ] Event filtering: processes `provider:complete`, `provider:error`, `tool:complete`, `tool:error`
- [ ] `@tamma/cost-monitor` dependency added to `packages/providers/package.json`
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
- [ ] Code reviewed and approved
