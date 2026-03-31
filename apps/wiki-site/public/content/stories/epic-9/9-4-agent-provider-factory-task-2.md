---
title: "Task 2: Implement wrapAsAgent() LLM-to-Agent adapter"
sidebar:
  order: 90
---

**Story:** 9-4-agent-provider-factory - Agent Provider Factory
**Epic:** 9

## Task Description

Implement the `wrapAsAgent()` function that converts any `IAIProvider` into an `IAgentProvider`. This adapter bridges the gap between LLM providers (which expose `sendMessageSync()`) and the agent interface (which exposes `executeTask()`). The function returns a plain object implementing `IAgentProvider` with three methods: `executeTask()`, `isAvailable()`, and `dispose()`.

The adapter builds a `MessageRequest` from the task prompt, calls `sendMessageSync()` on the wrapped LLM provider, and maps the `MessageResponse` to an `AgentTaskResult`. Error handling catches `sendMessageSync()` failures and returns them as `{ success: false }` results rather than throwing, using safe error casting (`err instanceof Error ? err.message : String(err)`).

## Acceptance Criteria

- [ ] `wrapAsAgent(llm: IAIProvider, model: string): IAgentProvider` exported from `agent-provider-factory.ts`
- [ ] Limitations documented in code comment: no tool-use loops, file tracking, exit code semantics, streaming progress
- [ ] `executeTask(config, onProgress?)` implementation:
  - Builds `MessageRequest` with `messages: [{ role: 'user', content: config.prompt }]`
  - Uses `config.model ?? model` for the request model (task-level override takes precedence over closure model)
  - Sets `maxTokens: undefined` and `temperature: undefined` (no overrides)
  - Calls `llm.sendMessageSync(request)` to get `MessageResponse`
  - Maps `response.content` to `AgentTaskResult.output`
  - Maps `response.finishReason !== 'error'` to `AgentTaskResult.success`
  - Sets `costUsd: 0` (actual cost computed by cost-monitor from token counts)
  - Tracks `durationMs` using `Date.now()` delta
  - On `sendMessageSync()` error, returns `{ success: false, output: '', costUsd: 0, durationMs, error: message }`
  - Uses safe error casting: `err instanceof Error ? err.message : String(err)` (NOT `(err as Error).message`)
- [ ] `isAvailable()` always returns `Promise<boolean>` resolving to `true`, with trade-off documentation comment
- [ ] `dispose()` delegates to `llm.dispose()`
- [ ] Does not mutate the original `IAIProvider` instance
- [ ] The `_onProgress` parameter is accepted but not used (LLM providers do not emit progress)

## Implementation Details

### Technical Requirements

- [ ] Import `IAIProvider`, `MessageRequest`, `MessageResponse` from `./types.js`
- [ ] Import `IAgentProvider`, `AgentTaskConfig`, `AgentProgressCallback` from `./agent-types.js`
- [ ] Import `AgentTaskResult` from `@tamma/shared`
- [ ] Return a plain object literal implementing the `IAgentProvider` interface
- [ ] Use `Date.now()` for timing (no external date library needed for millisecond deltas)
- [ ] Use safe error casting: `err instanceof Error ? err.message : String(err)` for robustness
- [ ] The `model` parameter is captured in closure scope but `config.model` takes precedence when provided

### Mapping Logic

```
AgentTaskConfig.prompt       -> MessageRequest.messages[0].content
config.model ?? model        -> MessageRequest.model (task-level override supported)
MessageResponse.content      -> AgentTaskResult.output
MessageResponse.finishReason -> AgentTaskResult.success (true unless 'error')
0                            -> AgentTaskResult.costUsd (cost-monitor handles real cost)
Date.now() delta             -> AgentTaskResult.durationMs
Error.message or String(err) -> AgentTaskResult.error (only on failure, safe casting)
```

### Files to Modify/Create

- `packages/providers/src/agent-provider-factory.ts` -- ADD: `wrapAsAgent()` function (same file as factory class)

### Dependencies

- [ ] `packages/providers/src/types.ts` -- IAIProvider, MessageRequest, MessageResponse
- [ ] `packages/providers/src/agent-types.ts` -- IAgentProvider, AgentTaskConfig, AgentProgressCallback
- [ ] `packages/shared/src/types/index.ts` -- AgentTaskResult

## Testing Strategy

### Unit Tests

- [ ] Test `wrapAsAgent()` returns object with `executeTask`, `isAvailable`, and `dispose` methods
- [ ] Test `executeTask()` builds MessageRequest with correct prompt and uses `config.model` when provided
- [ ] Test `executeTask()` falls back to closure `model` when `config.model` is undefined
- [ ] Test `executeTask()` calls `sendMessageSync()` on the wrapped LLM provider
- [ ] Test `executeTask()` with successful response (finishReason: 'stop'):
  - `success` is `true`
  - `output` matches `response.content`
  - `costUsd` is `0`
  - `durationMs` is a positive number
- [ ] Test `executeTask()` with error response (finishReason: 'error'):
  - `success` is `false`
  - `output` still contains `response.content`
- [ ] Test `executeTask()` with other finishReasons ('length', 'tool_calls', 'content_filter'):
  - `success` is `true` (only 'error' maps to false)
- [ ] Test `executeTask()` when `sendMessageSync()` throws an Error:
  - `success` is `false`
  - `output` is `''`
  - `error` contains the exception message (via `err.message`)
  - `costUsd` is `0`
  - `durationMs` is tracked correctly
- [ ] Test `executeTask()` when `sendMessageSync()` throws a non-Error value (e.g., a string):
  - `error` field contains `String(err)` (safe casting, not `undefined`)
- [ ] Test `isAvailable()` returns `true` unconditionally
- [ ] Test `dispose()` calls `llm.dispose()` exactly once
- [ ] Test `dispose()` returns a resolved promise
- [ ] Test that the original IAIProvider is not mutated
- [ ] Test that `_onProgress` callback is accepted but not invoked

### Validation Steps

1. [ ] Implement the `wrapAsAgent()` function signature with limitations doc comment
2. [ ] Implement `executeTask()` with MessageRequest construction using `config.model ?? model`
3. [ ] Implement `executeTask()` with response mapping
4. [ ] Implement `executeTask()` error handling with safe casting (try/catch around sendMessageSync)
5. [ ] Implement `isAvailable()` returning true with trade-off doc comment
6. [ ] Implement `dispose()` delegating to llm.dispose()
7. [ ] Verify the function compiles under strict TypeScript
8. [ ] Write and run unit tests with mocked IAIProvider
9. [ ] Verify durationMs tracking with time mocks (vi.useFakeTimers or similar)
10. [ ] Test non-Error thrown values are handled correctly

## Notes & Considerations

- **Cost tracking**: `costUsd` is always `0` in the adapter. The real cost is computed by `cost-monitor` from the token usage (`response.usage.inputTokens`, `outputTokens`) reported separately via diagnostics. This is a deliberate design choice to keep the adapter thin.

- **Progress callback**: The `_onProgress` parameter is accepted for interface compatibility but not used. LLM providers (IAIProvider) do not emit progress events -- they return a complete response. If streaming progress is needed in the future, a streaming variant of `wrapAsAgent()` could be created that uses `sendMessage()` instead of `sendMessageSync()`.

- **isAvailable() always returns true (TRADE-OFF DOCUMENTATION)**: This is intentional. The IAIProvider interface does not have an `isAvailable()` method, so there is nothing to delegate to. If the provider is actually down, `executeTask()` will throw when calling `sendMessageSync()`, and `ProviderChain` handles the fallback. This means connectivity is only validated lazily (on first task), not eagerly. The trade-off is acceptable because: (1) providers already validated connectivity during `initialize()`, and (2) eager checks would add latency without guaranteeing the provider stays available.

- **Safe error casting**: Use `err instanceof Error ? err.message : String(err)` instead of the unsafe `(err as Error).message`. The unsafe cast returns `undefined` if the thrown value is not an Error (e.g., a string `throw 'boom'`), while the safe version always produces a string. This matches the pattern used in `OpenCodeProvider`.

- **Task-level model override**: The request model uses `config.model ?? model` so that callers can override the model at the task level (e.g., for specific workflow phases that need a different model). The closure `model` serves as the default from the chain entry.

- **Limitations**: wrapAsAgent() provides a simple prompt-to-response mapping. It does NOT support tool-use loops (multi-turn tool calling), file tracking (which files were read/written), exit code semantics, or streaming progress callbacks. These capabilities are only available through native agent providers (claude-code, opencode) that implement IAgentProvider directly. This limitation should be documented in the code comment above the function.

- **No retry logic**: The adapter does not implement retries. Retry logic is the responsibility of the individual IAIProvider implementations (e.g., `OpenRouterProvider.withRetry()`) and/or the `ProviderChain`.

## Completion Checklist

- [ ] `wrapAsAgent()` function implemented and exported
- [ ] Limitations documented in code comment
- [ ] `executeTask()` builds correct MessageRequest with `config.model ?? model`
- [ ] `executeTask()` maps MessageResponse to AgentTaskResult correctly
- [ ] `executeTask()` handles sendMessageSync errors with safe casting
- [ ] `executeTask()` handles non-Error thrown values correctly
- [ ] `isAvailable()` always returns true with trade-off doc comment
- [ ] `dispose()` delegates to llm.dispose()
- [ ] No mutation of the original IAIProvider
- [ ] TypeScript strict mode compiles cleanly
- [ ] All unit tests passing with mocked providers
- [ ] Code reviewed and approved
