---
title: "Task 2: Create DiagnosticsProcessor Mapping Events to Cost Tracker"
sidebar:
  order: 90
---

**Story:** 9-11-diagnostics-queue-mcp-interceptors - Diagnostics Queue & MCP Interceptors
**Epic:** 9

## Task Description

Create the `createDiagnosticsProcessor()` factory function in `packages/shared/src/telemetry/diagnostics-processor.ts`. This function accepts an `ICostTracker` instance and an optional `ILogger`, and returns a `DiagnosticsEventProcessor` that maps `DiagnosticsEvent` completion/error events to `UsageRecordInput` for cost tracking. Also update the barrel exports to include the processor and re-export the telemetry module from `packages/shared/src/index.ts`.

## Acceptance Criteria

- `createDiagnosticsProcessor()` accepts `ICostTracker` and optional `ILogger` and returns `DiagnosticsEventProcessor`
- Processor only records `tool:complete`, `tool:error`, `provider:complete`, and `provider:error` events -- skips `tool:invoke` and `provider:call`
- Maps `DiagnosticsEvent` fields to `UsageRecordInput` per the mapping table below (F19)
- Uses `mapProviderName()` and `mapTaskType()` (from Story 9-2's `provider-name-mapping.ts`) instead of unsafe `as Provider`/`as TaskType` casts (F08)
- Missing optional fields use sensible defaults: `provider` defaults to `'claude-code'`, `model` defaults to `'unknown'`, token counts default to `0`, `latencyMs` defaults to `0`
- Per-event errors are caught and logged as warnings (not thrown)
- Telemetry barrel is re-exported from `packages/shared/src/index.ts`
- `IDiagnosticsProcessor` interface defined in `@tamma/shared` to avoid circular dependencies; concrete processor implemented at app level (F03)
- Prerequisite: `@tamma/cost-monitor` must import `AgentType` from `@tamma/shared`, not redefine locally (F20)

**DiagnosticsEvent to UsageRecordInput mapping table (F19):**

| DiagnosticsEvent field | UsageRecordInput field | Notes |
|---|---|---|
| projectId | projectId | direct |
| engineId | engineId | direct |
| agentType | agentType | direct (from @tamma/shared) |
| taskId | taskId | direct |
| taskType | taskType | validated via mapTaskType() |
| providerName | provider | validated via mapProviderName() |
| model | model | direct, default 'unknown' |
| tokens.input | inputTokens | default 0 |
| tokens.output | outputTokens | default 0 |
| latencyMs | latencyMs | default 0 |
| success | success | direct |
| errorCode | errorCode | direct |

## Implementation Details

### Technical Requirements

- [ ] Create `packages/shared/src/telemetry/diagnostics-processor.ts`
  - [ ] Import types from `@tamma/cost-monitor`: `ICostTracker`, `UsageRecordInput`
  - [ ] Import `mapProviderName`, `mapTaskType` from `@tamma/cost-monitor/provider-name-mapping.js` (F08)
  - [ ] Import `ILogger` from `../contracts.js`
  - [ ] Import `DiagnosticsEvent` from `./diagnostics-event.js`
  - [ ] Import `DiagnosticsEventProcessor` from `./diagnostics-queue.js`
  - [ ] Define `IDiagnosticsProcessor` interface in this file for dependency inversion (F03)
  - [ ] Implement `createDiagnosticsProcessor(costTracker: ICostTracker, logger?: ILogger): DiagnosticsEventProcessor`
  - [ ] Return an async function that iterates over the event batch
  - [ ] Skip events where `type` is not `tool:complete`, `tool:error`, `provider:complete`, or `provider:error`
  - [ ] For each qualifying event, use discriminated union to extract provider-specific fields (F01):
    - Extract `providerName` from `ProviderDiagnosticsEvent`, default `'claude-code'`
    - Extract `model` from `ProviderDiagnosticsEvent`, default `'unknown'`
    - Extract `tokens` from `ProviderDiagnosticsEvent`, default `undefined`
  - [ ] Construct `UsageRecordInput` per mapping table (F19):
    - `projectId: event.projectId ?? ''`
    - `engineId: event.engineId ?? ''`
    - `agentType: event.agentType ?? 'implementer'`
    - `taskId: event.taskId ?? ''`
    - `taskType: mapTaskType(event.taskType ?? 'implementation')` (F08)
    - `provider: mapProviderName(providerName)` (F08)
    - `model`
    - `inputTokens: tokens?.input ?? 0`
    - `outputTokens: tokens?.output ?? 0`
    - `totalTokens: (tokens?.input ?? 0) + (tokens?.output ?? 0)`
    - `latencyMs: event.latencyMs ?? 0`
    - `success: event.success ?? false`
    - `errorCode: event.errorCode`
  - [ ] Call `costTracker.recordUsage(input)` for each mapped event
  - [ ] Wrap each `recordUsage` call in try/catch, log warning on failure
- [ ] Update `packages/shared/src/telemetry/index.ts` to re-export `./diagnostics-processor.js`
- [ ] Modify `packages/shared/src/index.ts` to add `export * from './telemetry/index.js';`

### Files to Modify/Create

- `packages/shared/src/telemetry/diagnostics-processor.ts` -- **CREATE** -- Processor factory
- `packages/shared/src/telemetry/index.ts` -- **MODIFY** -- Add diagnostics-processor re-export
- `packages/shared/src/index.ts` -- **MODIFY** -- Add telemetry barrel export

### Dependencies

- [ ] Task 1 must be completed first (diagnostics-event.ts and diagnostics-queue.ts must exist)
- [ ] `@tamma/cost-monitor` must be a dependency of `@tamma/shared` (or use `import type` if types-only)
- [ ] `@tamma/cost-monitor` must export `mapProviderName()` and `mapTaskType()` from Story 9-2 (F08)
- [ ] `@tamma/cost-monitor` must import `AgentType` from `@tamma/shared`, not redefine locally (F20)
- [ ] `packages/shared/src/contracts/index.ts` must export `ILogger` (already does)
- [ ] To avoid circular dependency (`shared -> cost-monitor -> shared`), define `IDiagnosticsProcessor` interface in `@tamma/shared` and implement concrete processor at app level (F03)

## Testing Strategy

### Unit Tests

- [ ] Test processor maps `tool:complete` event to `UsageRecordInput` with all fields
- [ ] Test processor maps `provider:complete` event to `UsageRecordInput` with all fields
- [ ] Test processor maps `tool:error` event with `success: false` and `errorCode`
- [ ] Test processor maps `provider:error` event with `success: false` and `errorCode`
- [ ] Test processor skips `tool:invoke` events (does not call `recordUsage`)
- [ ] Test processor skips `provider:call` events (does not call `recordUsage`)
- [ ] Test missing `providerName` defaults to `'claude-code'`
- [ ] Test missing `model` defaults to `'unknown'`
- [ ] Test missing `tokens` defaults to `{ input: 0, output: 0 }` and `totalTokens: 0`
- [ ] Test missing `latencyMs` defaults to `0`
- [ ] Test missing `success` defaults to `false`
- [ ] Test per-event `recordUsage` failure is caught, logged as warning, and does not prevent processing of remaining events
- [ ] Test logger warning is called with event type when `recordUsage` throws
- [ ] Test processor works without logger (no crash when logger is undefined)
- [ ] Test processor uses `mapProviderName()` for provider field (not unsafe cast) (F08)
- [ ] Test processor uses `mapTaskType()` for taskType field (not unsafe cast) (F08)

### Validation Steps

1. [ ] Create the processor file
2. [ ] Update barrel exports
3. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
4. [ ] Write unit tests in `packages/shared/src/telemetry/diagnostics-processor.test.ts` (created in Task 6)
5. [ ] Run `pnpm vitest run packages/shared/src/telemetry/diagnostics-processor`
6. [ ] Verify `createDiagnosticsProcessor` is importable from `@tamma/shared`

## Notes & Considerations

- The processor uses `mapProviderName()` and `mapTaskType()` instead of unsafe `as Provider`/`as TaskType` casts. These mapping functions validate the string and return a safe default if unrecognized (F08).
- The try/catch around each event's `recordUsage()` call is critical: a single bad event must not prevent processing of the entire batch.
- The logger is optional specifically for cases where telemetry is wired in lightweight contexts (e.g., CLI mode without full logging infrastructure).
- This file imports from `@tamma/cost-monitor` which creates a potential circular dependency. To mitigate, define `IDiagnosticsProcessor` in `@tamma/shared` and implement the concrete processor at the application level or in `@tamma/cost-monitor` (F03).
- `@tamma/cost-monitor` must import `AgentType` from `@tamma/shared` rather than redefining it locally, to avoid type conflicts (F20).
- With the discriminated union `DiagnosticsEvent` (F01), provider-specific fields (`providerName`, `model`, `tokens`) are accessed via type narrowing rather than optional access on a flat interface.

## Completion Checklist

- [ ] `packages/shared/src/telemetry/diagnostics-processor.ts` created
- [ ] `IDiagnosticsProcessor` interface defined in `@tamma/shared` (F03)
- [ ] `createDiagnosticsProcessor()` correctly maps events to `UsageRecordInput` per mapping table (F19)
- [ ] Uses `mapProviderName()` and `mapTaskType()` instead of unsafe type casts (F08)
- [ ] Only completion/error events are processed (invoke/call events skipped)
- [ ] Default values applied for missing optional fields
- [ ] Per-event errors caught and logged
- [ ] `packages/shared/src/telemetry/index.ts` updated with processor re-export
- [ ] `packages/shared/src/index.ts` updated with telemetry barrel export
- [ ] TypeScript strict mode compilation passes
- [ ] `createDiagnosticsProcessor` importable from `@tamma/shared`
- [ ] `@tamma/cost-monitor` imports `AgentType` from `@tamma/shared` (verified, F20)
