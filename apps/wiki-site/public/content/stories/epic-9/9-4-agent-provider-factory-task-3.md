---
title: "Task 3: Wire built-in provider registrations, exports, and tests"
sidebar:
  order: 90
---

**Story:** 9-4-agent-provider-factory - Agent Provider Factory
**Epic:** 9

## Task Description

Wire the four built-in provider registrations in the `AgentProviderFactory` constructor using `BUILTIN_PROVIDER_NAMES` constants, export the factory (class and interface), `wrapAsAgent()`, `resolveApiKey()`, and `BUILTIN_PROVIDER_NAMES` from the package index, and create the comprehensive test file covering all verification criteria from the story specification.

The four built-in providers are:
1. **claude-code** -- `ClaudeAgentProvider` (native IAgentProvider, no wrapping)
2. **opencode** -- `OpenCodeProvider` (native IAgentProvider, no wrapping)
3. **openrouter** -- `OpenRouterProvider` (IAIProvider, wrapped via wrapAsAgent)
4. **zen-mcp** -- `ZenMCPProvider` (IAIProvider, wrapped via wrapAsAgent)

## Acceptance Criteria

- [ ] Constructor registers all four built-in providers using `BUILTIN_PROVIDER_NAMES` constants
- [ ] `BUILTIN_PROVIDER_NAMES.CLAUDE_CODE` maps to `() => new ClaudeAgentProvider()`
- [ ] `BUILTIN_PROVIDER_NAMES.OPENCODE` maps to `() => new OpenCodeProvider()`
- [ ] `BUILTIN_PROVIDER_NAMES.OPENROUTER` maps to `() => new OpenRouterProvider()`
- [ ] `BUILTIN_PROVIDER_NAMES.ZEN_MCP` maps to `() => new ZenMCPProvider()`
- [ ] `this.lock()` called after registering all built-ins
- [ ] `AgentProviderFactory` exported from `packages/providers/src/index.ts`
- [ ] `IAgentProviderFactory` exported from `packages/providers/src/index.ts`
- [ ] `wrapAsAgent` exported from `packages/providers/src/index.ts`
- [ ] `resolveApiKey` exported from `packages/providers/src/index.ts`
- [ ] `BUILTIN_PROVIDER_NAMES` exported from `packages/providers/src/index.ts`
- [ ] Comprehensive test file at `packages/providers/src/agent-provider-factory.test.ts`
- [ ] All verification tests from the story specification pass
- [ ] Tests use mocks for all provider classes (no real CLI spawning or API calls)
- [ ] Test fixtures use `apiKeyRef` with `process.env` setup, NOT raw `apiKey` values

## Implementation Details

### Technical Requirements

- [ ] Import all four provider classes at the top of `agent-provider-factory.ts`
- [ ] Register providers in constructor using `BUILTIN_PROVIDER_NAMES` constants with arrow function creators
- [ ] CLI agent providers (claude-code, opencode) are registered with no-arg constructors
- [ ] LLM providers (openrouter, zen-mcp) are registered with no-arg constructors; wrapping happens in `create()`
- [ ] Call `this.lock()` after all four registrations
- [ ] Add to `packages/providers/src/index.ts`:
  ```typescript
  export {
    AgentProviderFactory,
    wrapAsAgent,
    resolveApiKey,
    BUILTIN_PROVIDER_NAMES,
  } from './agent-provider-factory.js';
  export type { IAgentProviderFactory } from './agent-provider-factory.js';
  ```

### Files to Modify/Create

- `packages/providers/src/agent-provider-factory.ts` -- MODIFY: ensure constructor registrations use BUILTIN_PROVIDER_NAMES and lock
- `packages/providers/src/index.ts` -- MODIFY: add all exports
- `packages/providers/src/agent-provider-factory.test.ts` -- CREATE: comprehensive unit tests

### Dependencies

- [ ] Task 1: AgentProviderFactory class with register(), create(), dispose(), resolveApiKey(), IAgentProviderFactory, BUILTIN_PROVIDER_NAMES
- [ ] Task 2: wrapAsAgent() function
- [ ] `packages/providers/src/claude-agent-provider.ts` -- ClaudeAgentProvider class
- [ ] `packages/providers/src/opencode-provider.ts` -- OpenCodeProvider class
- [ ] `packages/providers/src/openrouter-provider.ts` -- OpenRouterProvider class
- [ ] `packages/providers/src/zen-mcp-provider.ts` -- ZenMCPProvider class
- [ ] vitest -- test runner and mocking framework

## Testing Strategy

### Unit Tests

The test file must cover all verification criteria from the story spec:

**IMPORTANT: Test fixture conventions for apiKeyRef:**
- All test entries that need an API key must use `apiKeyRef: 'TEST_API_KEY'` (NOT `apiKey: 'test-key'`)
- Tests must set `process.env.TEST_API_KEY = 'test-key'` in `beforeEach` and clean up in `afterEach`
- Use `delete process.env.TEST_API_KEY` in cleanup

**Factory creation tests:**
- [ ] `create({ provider: 'claude-code' })` returns an IAgentProvider (ClaudeAgentProvider)
  - Mock `ClaudeAgentProvider` to avoid spawning `claude` CLI
  - Verify returned object has `executeTask`, `isAvailable`, `dispose` methods
- [ ] `create({ provider: 'openrouter', model: 'z-ai/z1-mini', apiKeyRef: 'TEST_OPENROUTER_KEY' })` with `process.env.TEST_OPENROUTER_KEY = 'test-key'` returns IAgentProvider (wrapped via wrapAsAgent)
  - Mock `OpenRouterProvider.initialize()` to avoid real API calls
  - Verify returned object has `executeTask`, `isAvailable`, `dispose` methods
  - Verify `initialize()` was called with `{ apiKey: 'test-key', model: 'z-ai/z1-mini' }`
- [ ] `create({ provider: 'zen-mcp' })` returns IAgentProvider (wrapped via wrapAsAgent)
  - Mock `ZenMCPProvider.initialize()` to avoid spawning MCP server
  - Verify returned object has `executeTask`, `isAvailable`, `dispose` methods
- [ ] Unknown provider throws `Unknown provider: foo`
  - `expect(() => factory.create({ provider: 'foo' })).rejects.toThrow('Unknown provider: foo')`

**apiKeyRef resolution tests:**
- [ ] `resolveApiKey({ provider: 'claude-code' })` returns `''` when `apiKeyRef` is undefined
- [ ] `resolveApiKey({ provider: 'openrouter', apiKeyRef: 'MY_KEY' })` with `process.env.MY_KEY = 'secret'` returns `'secret'`
- [ ] `resolveApiKey({ provider: 'openrouter', apiKeyRef: 'MISSING_KEY' })` throws `Environment variable "MISSING_KEY" is not set for provider "openrouter"`
- [ ] `resolveApiKey({ provider: 'x', apiKeyRef: 'MY-KEY' })` throws on invalid format (hyphens not allowed)
- [ ] `resolveApiKey({ provider: 'x', apiKeyRef: 'MY KEY' })` throws on invalid format (spaces not allowed)

**Provider name validation tests:**
- [ ] `register('', ...)` throws (empty string doesn't match pattern)
- [ ] `register('MyProvider', ...)` throws (uppercase not allowed)
- [ ] `register('__proto__', ...)` throws (forbidden name)
- [ ] `register('constructor', ...)` throws (forbidden name)
- [ ] `register('prototype', ...)` throws (forbidden name)
- [ ] `register('valid-name', ...)` succeeds after lock (new name, not overriding)
- [ ] `register('claude-code', ...)` throws after lock (cannot override existing)

**Initialization tests:**
- [ ] `initialize()` called on LLM providers with correct config spread order
  - Spy on `OpenRouterProvider.prototype.initialize`
  - Call `create({ provider: 'openrouter', apiKeyRef: 'TEST_KEY', model: 'model', config: { baseUrl: 'url' } })` with `process.env.TEST_KEY = 'key'`
  - Verify `initialize` called with `{ baseUrl: 'url', apiKey: 'key', model: 'model' }` (entry.config spread first, explicit fields override)
- [ ] `initialize()` error is wrapped with provider name
  - Mock `OpenRouterProvider.prototype.initialize` to throw `new Error('connection refused')`
  - Verify `create(...)` rejects with `Provider "openrouter" failed to initialize: connection refused`
- [ ] Mock `OpenCodeProvider.initialize()` throwing -- verify wrapped error propagates to caller
  - Mock `OpenCodeProvider.prototype.initialize` to throw `new Error('Failed to initialize OpenCode SDK: connection refused')`
  - Verify `create({ provider: 'opencode' })` rejects with `Provider "opencode" failed to initialize: Failed to initialize OpenCode SDK: connection refused`

**wrapAsAgent tests:**
- [ ] `executeTask()` calls `sendMessageSync()` and maps response fields correctly
  - Create mock IAIProvider with `sendMessageSync` returning `{ content: 'result', finishReason: 'stop', usage: { inputTokens: 10, outputTokens: 20, totalTokens: 30 }, id: 'id', model: 'model' }`
  - Call `wrapAsAgent(mock, 'test-model').executeTask({ prompt: 'test', cwd: '.' })`
  - Verify result: `{ success: true, output: 'result', costUsd: 0, durationMs: expect.any(Number) }`
  - Verify `sendMessageSync` called with `{ messages: [{ role: 'user', content: 'test' }], model: 'test-model', maxTokens: undefined, temperature: undefined }`
- [ ] `executeTask()` uses `config.model` when provided (task-level override)
  - Call `wrapAsAgent(mock, 'default-model').executeTask({ prompt: 'test', cwd: '.', model: 'override-model' })`
  - Verify `sendMessageSync` called with `model: 'override-model'`
- [ ] `executeTask()` falls back to closure model when `config.model` is undefined
  - Call `wrapAsAgent(mock, 'fallback-model').executeTask({ prompt: 'test', cwd: '.' })`
  - Verify `sendMessageSync` called with `model: 'fallback-model'`
- [ ] On `sendMessageSync()` error (Error instance), returns `{ success: false, error: message }`
  - Create mock IAIProvider with `sendMessageSync` throwing `new Error('API error')`
  - Call `wrapAsAgent(mock, 'model').executeTask({ prompt: 'test', cwd: '.' })`
  - Verify result: `{ success: false, output: '', costUsd: 0, error: 'API error', durationMs: expect.any(Number) }`
- [ ] On `sendMessageSync()` throwing non-Error value, returns `{ success: false, error: String(value) }`
  - Create mock IAIProvider with `sendMessageSync` throwing `'string error'`
  - Verify result error field is `'string error'` (not `undefined`)
- [ ] `isAvailable()` returns `true`
  - `expect(await wrapAsAgent(mock, 'model').isAvailable()).toBe(true)`
- [ ] `dispose()` delegates to `llm.dispose()`
  - Create mock with `dispose` spy
  - Call `wrapAsAgent(mock, 'model').dispose()`
  - Verify `mock.dispose` called once

**Factory dispose tests:**
- [ ] `factory.dispose()` clears creators map
  - Call `factory.dispose()`
  - Verify subsequent `factory.create({ provider: 'claude-code' })` throws `Unknown provider: claude-code`

**Constants tests:**
- [ ] `BUILTIN_PROVIDER_NAMES.CLAUDE_CODE` equals `'claude-code'`
- [ ] `BUILTIN_PROVIDER_NAMES.OPENCODE` equals `'opencode'`
- [ ] `BUILTIN_PROVIDER_NAMES.OPENROUTER` equals `'openrouter'`
- [ ] `BUILTIN_PROVIDER_NAMES.ZEN_MCP` equals `'zen-mcp'`

**Custom registration test:**
- [ ] `register()` allows adding a custom provider (new name, after lock)
  - Register a mock provider under `'custom'`
  - Call `create({ provider: 'custom' })`
  - Verify it returns the mock

**Mock strategy:**
- Use `vi.mock()` to mock the provider module imports
- Use `vi.spyOn()` for initialization verification
- Create inline mock objects implementing IAIProvider for wrapAsAgent tests
- Never spawn real CLI processes or make real API calls
- Set up `process.env` values in `beforeEach` and clean up in `afterEach`

### Validation Steps

1. [ ] Add exports to `packages/providers/src/index.ts`
2. [ ] Create `agent-provider-factory.test.ts` with test structure
3. [ ] Write apiKeyRef resolution tests
4. [ ] Write provider name validation tests
5. [ ] Write factory creation tests for all four built-in providers (using apiKeyRef)
6. [ ] Write initialization verification tests (spread order, error wrapping)
7. [ ] Write wrapAsAgent mapping tests (including config.model override and safe error casting)
8. [ ] Write error handling tests (non-Error thrown values)
9. [ ] Write lock behavior tests
10. [ ] Write factory dispose tests
11. [ ] Write constants tests
12. [ ] Write custom registration test
13. [ ] Run `pnpm vitest run packages/providers/src/agent-provider-factory`
14. [ ] Verify all tests pass
15. [ ] Run `pnpm --filter @tamma/providers run typecheck` to verify exports compile

## Notes & Considerations

- **Mocking provider constructors**: The test file should mock the provider module imports (`vi.mock('./claude-agent-provider.js', ...)`) to prevent real instantiation. This avoids spawning Claude CLI, connecting to OpenCode, creating OpenAI clients, or starting MCP processes.

- **Test isolation**: Each test should create a fresh `AgentProviderFactory` instance to avoid state leaking between tests. Clean up `process.env` modifications in `afterEach`.

- **apiKeyRef test setup pattern**:
  ```typescript
  beforeEach(() => {
    process.env.TEST_API_KEY = 'test-key';
    process.env.TEST_OPENROUTER_KEY = 'test-openrouter-key';
  });
  afterEach(() => {
    delete process.env.TEST_API_KEY;
    delete process.env.TEST_OPENROUTER_KEY;
  });
  ```

- **ProviderChainEntry type**: Story 9-1 defines `ProviderChainEntry` with `apiKeyRef` (NOT `apiKey`). If Story 9-1 is not yet implemented, define a minimal local interface for tests:
  ```typescript
  interface ProviderChainEntry {
    provider: string;
    model?: string;
    apiKeyRef?: string;              // env var name, NOT raw key
    config?: Record<string, unknown>;
  }
  ```

- **Verifying the wrapped provider**: For openrouter and zen-mcp, the returned IAgentProvider is a plain object from `wrapAsAgent()`. Tests should verify it has the correct methods and that `executeTask()` delegates to `sendMessageSync()`.

- **Error message format**: The story spec requires exactly `Unknown provider: foo` (no prefix like "Error:"). Tests should match this exact string. Initialize errors are wrapped: `Provider "openrouter" failed to initialize: connection refused`.

- **finishReason mapping**: Only `'error'` maps to `success: false`. All other values ('stop', 'length', 'tool_calls', 'content_filter') map to `success: true`.

- **Safe error casting**: Tests must verify that non-Error thrown values (e.g., `throw 'string'`) are handled correctly via `String(err)`, not `undefined` from unsafe `(err as Error).message`.

- **Config spread order verification**: Tests must verify that `initialize()` receives config in the correct spread order: `{ ...entry.config, apiKey: resolvedKey, model: entry.model }`. This means if `entry.config` contains an `apiKey` or `model` field, the explicit values override them.

## Completion Checklist

- [ ] All four built-in providers registered in constructor using BUILTIN_PROVIDER_NAMES
- [ ] Factory locked after registration
- [ ] `AgentProviderFactory` exported from index.ts
- [ ] `IAgentProviderFactory` exported from index.ts
- [ ] `wrapAsAgent` exported from index.ts
- [ ] `resolveApiKey` exported from index.ts
- [ ] `BUILTIN_PROVIDER_NAMES` exported from index.ts
- [ ] Test file created at `agent-provider-factory.test.ts`
- [ ] apiKeyRef resolution tests (undefined, valid, missing env var, invalid format)
- [ ] Provider name validation tests (invalid names, forbidden names, lock behavior)
- [ ] Factory creation tests for claude-code, opencode, openrouter, zen-mcp
- [ ] Unknown provider error test
- [ ] Initialization config spread order verification tests
- [ ] Initialization error wrapping tests
- [ ] OpenCode initialize failure propagation test (wrapped error)
- [ ] wrapAsAgent mapping tests (success, error response, exception, non-Error)
- [ ] wrapAsAgent config.model override tests
- [ ] wrapAsAgent isAvailable and dispose tests
- [ ] Factory dispose tests
- [ ] Constants tests
- [ ] Custom registration test
- [ ] All tests passing with `pnpm vitest run`
- [ ] TypeScript compilation verified
- [ ] Code reviewed and approved
