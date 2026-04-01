---
title: "Task 3: Create InstrumentedLLMProvider Decorator (Full IAIProvider)"
sidebar:
  order: 90
---

**Story:** 9-2-provider-diagnostics - Provider Diagnostics
**Epic:** 9

## Task Description

Create `InstrumentedLLMProvider`, a decorator class that wraps any `IAIProvider` and implements the **full `IAIProvider` interface**: `initialize()`, `sendMessage()` (streaming), `sendMessageSync()`, `getCapabilities()`, `getModels()`, and `dispose()`. Both `sendMessageSync()` and `sendMessage()` (streaming) are instrumented with diagnostics events. The streaming wrapper tracks tokens incrementally from chunks and emits events on stream completion.

Key enhancements over initial design:
- **Full IAIProvider implementation**: Implements ALL methods of `IAIProvider`, not just `sendMessageSync` and `dispose`.
- **Streaming instrumentation**: `sendMessage()` wraps the `AsyncIterable<MessageChunk>` to track tokens incrementally and emit `provider:complete` on stream end, `provider:error` on stream failure.
- **Context includes projectId and engineId**: Matching `InstrumentedAgentProvider` context shape.
- **Error sanitization**: Uses `sanitizeErrorMessage()` for all `errorMessage` fields.
- **Typed error codes**: Uses `DiagnosticsErrorCode` union type.
- **Value validation**: Token counts and costUsd are validated before recording.
- **Security boundary**: Comments note that this class has access to prompt content and must NOT log or store it.

> **SECURITY NOTE:** `InstrumentedLLMProvider` has access to prompt content via `MessageRequest`. It must NOT log or store prompt text. Only metadata (tokens, latency, cost, error codes) is recorded in diagnostics events.

## Acceptance Criteria

- `InstrumentedLLMProvider` **implements `IAIProvider`** (the full interface, not a partial wrapper)
- `initialize()` delegates to `inner.initialize()`
- `sendMessage()` is instrumented:
  - Emits `provider:call` before calling inner
  - Wraps the returned `AsyncIterable<MessageChunk>` to track tokens incrementally from chunk usage
  - Emits `provider:complete` when the stream completes (iterator returns `done: true`)
  - Emits `provider:error` when the stream throws
  - Emits `provider:error` when `inner.sendMessage()` itself throws (before stream starts)
- `sendMessageSync()` is instrumented with `provider:call`/`provider:complete`/`provider:error` events
- `getCapabilities()` delegates to `inner.getCapabilities()`
- `getModels()` delegates to `inner.getModels()`
- `dispose()` delegates to `inner.dispose()`
- `InstrumentedLLMProvider` does **NOT** expose `isAvailable()` -- it is not part of `IAIProvider`
- `provider:complete` events include `tokens: { input: response.usage.inputTokens, output: response.usage.outputTokens }`
- All events include `providerName`, `model`, `agentType` (typed as `AgentType`), `projectId`, `engineId`, `taskId`, `taskType`
- `errorMessage` is sanitized via `sanitizeErrorMessage()` (max 500 chars, API key patterns stripped)
- `errorCode` is typed as `DiagnosticsErrorCode`
- Original error is re-thrown after emitting the error event
- Security boundary comments are present documenting that prompt content must not be logged

## Implementation Details

### Technical Requirements

- [ ] Create class `InstrumentedLLMProvider implements IAIProvider`
- [ ] Define `InstrumentedLLMContext` interface: `{ providerName: string; model: string; agentType: AgentType; projectId: string; engineId: string; taskId: string; taskType: string }`
- [ ] Constructor accepts: `inner: IAIProvider`, `diagnostics: DiagnosticsQueue`, `context: InstrumentedLLMContext`
- [ ] Add security boundary comment at class level and on `sendMessage`/`sendMessageSync` noting prompt content access

#### `initialize(config: ProviderConfig): Promise<void>`
- [ ] Delegate to `this.inner.initialize(config)` -- no instrumentation needed

#### `sendMessage(request: MessageRequest, options?: Record<string, unknown>): Promise<AsyncIterable<MessageChunk>>`
- [ ] Emit `{ type: 'provider:call', timestamp: Date.now(), ...context }`
- [ ] Record `start = Date.now()`
- [ ] Call `this.inner.sendMessage(request, options)`
- [ ] On catch (before stream starts): emit `provider:error` with `sanitizeErrorMessage()` and re-throw
- [ ] Wrap returned `AsyncIterable<MessageChunk>`:
  - Track `inputTokens` and `outputTokens` incrementally from `chunk.usage` if available
  - On `done: true`: emit `provider:complete` with accumulated tokens and latencyMs
  - On iterator error: emit `provider:error` with sanitized error message and re-throw

#### `sendMessageSync(request: MessageRequest): Promise<MessageResponse>`
- [ ] Record `start = Date.now()`
- [ ] Emit `{ type: 'provider:call', timestamp: start, ...context }`
- [ ] Call `this.inner.sendMessageSync(request)`
- [ ] On success: emit `provider:complete` with `latencyMs`, `success: true`, `tokens: { input: response.usage.inputTokens, output: response.usage.outputTokens }`
- [ ] On catch: emit `provider:error` with `latencyMs`, `success: false`, `errorCode` (typed as `DiagnosticsErrorCode`), `errorMessage: sanitizeErrorMessage((err as Error).message)`
- [ ] Re-throw caught error

#### `getCapabilities(): ProviderCapabilities`
- [ ] Delegate to `this.inner.getCapabilities()` -- no instrumentation needed

#### `getModels(): Promise<ModelInfo[]>`
- [ ] Delegate to `this.inner.getModels()` -- no instrumentation needed

#### `dispose(): Promise<void>`
- [ ] Delegate to `this.inner.dispose()` -- no instrumentation needed

- [ ] Do **NOT** define `isAvailable()` -- `IAIProvider` does not have this method

### Files to Modify/Create

- CREATE `packages/providers/src/instrumented-llm-provider.ts`
- CREATE `packages/providers/src/instrumented-llm-provider.test.ts`

### Dependencies

- [ ] Task 1: `DiagnosticsQueue`, `DiagnosticsEvent`, `DiagnosticsErrorCode`, `sanitizeErrorMessage` from `@tamma/shared/telemetry`
- [ ] `IAIProvider`, `MessageRequest`, `MessageResponse`, `MessageChunk`, `ProviderConfig`, `ProviderCapabilities`, `ModelInfo` from `./types.js`
- [ ] `AgentType` from `@tamma/shared`

## Testing Strategy

### Unit Tests

#### Full IAIProvider implementation
- [ ] Test class implements `IAIProvider` interface (TypeScript compile check)
- [ ] Test `initialize()` delegates to `inner.initialize()`
- [ ] Test `getCapabilities()` delegates to `inner.getCapabilities()`
- [ ] Test `getModels()` delegates to `inner.getModels()`
- [ ] Test `dispose()` delegates to `inner.dispose()`
- [ ] Test `isAvailable` is NOT a property/method on the class (runtime check: `expect((instance as any).isAvailable).toBeUndefined()`)

#### sendMessageSync instrumentation
- [ ] Test `sendMessageSync()` emits `provider:call` event before inner is called
- [ ] Test `sendMessageSync()` emits `provider:complete` on successful inner call
- [ ] Test `provider:complete` event contains `latencyMs` > 0
- [ ] Test `provider:complete` event contains `success: true`
- [ ] Test `provider:complete` event contains `tokens.input` matching `response.usage.inputTokens`
- [ ] Test `provider:complete` event contains `tokens.output` matching `response.usage.outputTokens`
- [ ] Test `sendMessageSync()` emits `provider:error` when inner throws
- [ ] Test `provider:error` event contains `success: false`, `errorCode` (typed as `DiagnosticsErrorCode`), `errorMessage` (sanitized)
- [ ] Test error is re-thrown after emitting `provider:error`

#### sendMessage streaming instrumentation
- [ ] Test `sendMessage()` emits `provider:call` before calling inner
- [ ] Test `sendMessage()` returns wrapped `AsyncIterable<MessageChunk>`
- [ ] Test stream wrapper tracks tokens incrementally from `chunk.usage`
- [ ] Test stream wrapper emits `provider:complete` when stream completes (`done: true`) with accumulated tokens
- [ ] Test stream wrapper emits `provider:error` when stream iteration throws, with sanitized errorMessage
- [ ] Test `sendMessage()` emits `provider:error` when `inner.sendMessage()` itself throws (before stream starts)
- [ ] Test stream latencyMs measures from call start to stream completion
- [ ] Test stream wrapper re-throws errors after emitting provider:error

#### Context and security
- [ ] Test all events contain `providerName`, `model`, `agentType`, `projectId`, `engineId`, `taskId`, `taskType` from context
- [ ] Test `agentType` is typed as `AgentType` (compile-time check)
- [ ] Test `projectId` and `engineId` are included in all emitted events
- [ ] Test `diagnostics.emit()` is called synchronously (not awaited)
- [ ] Test context fields spread correctly into events
- [ ] Test emitted events do NOT contain `MessageRequest` content (security boundary check)

### Validation Steps

1. [ ] Create `instrumented-llm-provider.ts` with the full `IAIProvider` implementation
2. [ ] Add security boundary comments at class level and on sendMessage/sendMessageSync
3. [ ] Create mock `IAIProvider` for testing (with all interface methods)
4. [ ] Create mock `DiagnosticsQueue` with `emit` spy
5. [ ] Create mock streaming response for `sendMessage` tests
6. [ ] Write all unit tests including streaming, full interface, and security checks
7. [ ] Verify TypeScript strict mode compilation
8. [ ] Verify that adding `isAvailable()` to the class would be a conscious choice (not inherited from interface)
9. [ ] Verify `sanitizeErrorMessage()` is used for all `errorMessage` assignments
10. [ ] Verify `DiagnosticsErrorCode` is used for all `errorCode` values

## Notes & Considerations

- **Full IAIProvider implementation**: Unlike the initial design which only wrapped `sendMessageSync()` and `dispose()`, this implementation covers ALL methods of `IAIProvider`. The `initialize()`, `getCapabilities()`, and `getModels()` methods are simple delegations without instrumentation. This makes `InstrumentedLLMProvider` a true drop-in replacement for any `IAIProvider`.
- **Streaming instrumentation**: The `sendMessage()` method wraps the returned `AsyncIterable<MessageChunk>` with a custom async iterator that:
  1. Tracks `inputTokens` and `outputTokens` incrementally from `chunk.usage` fields
  2. On stream completion (`done: true`), emits `provider:complete` with accumulated token counts
  3. On stream error, emits `provider:error` with sanitized error message and re-throws
  4. If `inner.sendMessage()` itself throws before returning the stream, a `provider:error` is emitted immediately
- **Security boundary**: This class has access to full prompt content via `MessageRequest`. The security boundary is enforced by convention: emitted events contain only metadata (tokens, latency, cost, error codes), never prompt text. Security boundary comments document this constraint for future maintainers.
- **Context shape**: The `InstrumentedLLMContext` includes `projectId` and `engineId` (matching `InstrumentedAgentProvider`), which were missing in the initial design.
- The `tokens` field in diagnostics events uses `{ input, output }` (short names) rather than `{ inputTokens, outputTokens }` to match the `DiagnosticsEvent` interface definition.
- `(err as any)?.code ?? 'UNKNOWN'` handles both structured errors (with `.code`) and plain `Error` objects.
- The key design difference from `InstrumentedAgentProvider`: LLM providers report token usage in `response.usage` (for sync) or incrementally via `chunk.usage` (for streaming), while agent providers report cost in `result.costUsd`. The two wrappers extract different metrics because the underlying providers return different response shapes.

## Completion Checklist

- [ ] `instrumented-llm-provider.ts` created implementing full `IAIProvider` interface
- [ ] `InstrumentedLLMContext` defined with `projectId` and `engineId`
- [ ] `initialize()` delegates to inner
- [ ] `sendMessage()` instrumented with streaming wrapper (token tracking, completion/error events)
- [ ] `sendMessageSync()` instrumented with call/complete/error events
- [ ] `getCapabilities()` delegates to inner
- [ ] `getModels()` delegates to inner
- [ ] `dispose()` delegates to inner
- [ ] `isAvailable()` is NOT defined on the class
- [ ] Security boundary comments present at class level
- [ ] `sanitizeErrorMessage()` used for all errorMessage assignments
- [ ] `DiagnosticsErrorCode` used for all errorCode values
- [ ] `projectId` and `engineId` included in all emitted events
- [ ] Token counts extracted from `response.usage` (sync) and `chunk.usage` (streaming)
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
- [ ] Code reviewed and approved
