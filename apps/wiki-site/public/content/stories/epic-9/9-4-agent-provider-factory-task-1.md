---
title: "Task 1: Implement AgentProviderFactory with provider registration and apiKeyRef resolution"
sidebar:
  order: 90
---

**Story:** 9-4-agent-provider-factory - Agent Provider Factory
**Epic:** 9

## Task Description

Create the `AgentProviderFactory` class in `packages/providers/src/agent-provider-factory.ts`. The factory maintains a map of provider name strings to creator functions, and exposes a `create(entry)` method that always returns `IAgentProvider`. When the created provider is an LLM provider (implements `IAIProvider` with `sendMessageSync`), the factory wraps it via `wrapAsAgent()` before returning. When the created provider is already an `IAgentProvider` (like `ClaudeAgentProvider` or `OpenCodeProvider`), it is returned directly.

This task also includes:
- Extracting `IAgentProviderFactory` interface for dependency inversion (ProviderChain depends on the interface, not the concrete class)
- Implementing `resolveApiKey(entry)` to resolve `entry.apiKeyRef` from `process.env` (NOT a raw `apiKey` field)
- Provider name validation in `register()` matching Story 9-1 patterns
- Lock-after-construction to prevent overriding built-in providers
- `dispose()` method to clear the creators map
- Optional `logger` for structured logging
- Thread-safety documentation

## Acceptance Criteria

- [ ] `IAgentProviderFactory` interface exported with `create()`, `register()`, `dispose()` methods
- [ ] `BUILTIN_PROVIDER_NAMES` constant exported with `CLAUDE_CODE`, `OPENCODE`, `OPENROUTER`, `ZEN_MCP` values
- [ ] `resolveApiKey(entry: ProviderChainEntry): string` exported helper:
  - Returns `''` if `entry.apiKeyRef` is undefined (some providers like claude-code don't need keys)
  - Validates `apiKeyRef` format: only alphanumeric + underscore allowed (`/^[A-Za-z0-9_]+$/`)
  - Looks up `process.env[entry.apiKeyRef]`
  - Throws `Environment variable "${entry.apiKeyRef}" is not set for provider "${entry.provider}"` if env var missing
  - Throws on invalid apiKeyRef format
- [ ] `AgentProviderFactory` class implementing `IAgentProviderFactory` exported from `packages/providers/src/agent-provider-factory.ts`
- [ ] Private `creators` map of `string -> () => IAgentProvider | IAIProvider`
- [ ] Private `locked` flag, defaults to `false`; private `lock()` method sets it to `true`
- [ ] `register(name: string, creator: () => IAgentProvider | IAIProvider): void` method:
  - Validates name matches `/^[a-z0-9][a-z0-9_-]{0,63}$/` (same as Story 9-1)
  - Rejects `__proto__`, `constructor`, `prototype` (prototype pollution guard)
  - When locked, throws if trying to override an existing provider (but can still register new ones)
  - Logs at INFO level when logger is available
- [ ] `create(entry: ProviderChainEntry): Promise<IAgentProvider>` method:
  - Looks up creator by `entry.provider`
  - Throws `Error('Unknown provider: {name}')` if not found
  - Calls `resolveApiKey(entry)` to get API key from environment
  - Calls `initialize()` if the provider supports it
  - Passes `{ ...entry.config, apiKey: resolvedKey, model: entry.model }` to `initialize()` (spread order: entry.config first, explicit fields override)
  - Wraps `initialize()` errors: `Provider "${entry.provider}" failed to initialize: ${sanitizedMessage}`
  - Detects LLM providers by checking `'sendMessageSync' in provider` (duck-typing)
  - Wraps LLM providers via `wrapAsAgent(provider, entry.model ?? 'default')`
  - Returns agent providers directly as `IAgentProvider`
  - Logs creation and initialization at INFO level
- [ ] `dispose(): Promise<void>` method clears the creators map
- [ ] Constructor accepts optional `logger?: ILogger` parameter
- [ ] Return type of `create()` is always `Promise<IAgentProvider>` -- never a union
- [ ] Built-in providers registered in constructor using `BUILTIN_PROVIDER_NAMES` constants
- [ ] `this.lock()` called after registering built-ins
- [ ] Thread-safety comment on the class
- [ ] All code compiles under TypeScript strict mode
- [ ] Uses .js extensions in import paths for ESM compatibility

## Implementation Details

### Technical Requirements

- [ ] Import `IAgentProvider`, `AgentTaskConfig`, `AgentProgressCallback` from `./agent-types.js`
- [ ] Import `IAIProvider`, `MessageRequest`, `MessageResponse` from `./types.js`
- [ ] Import `AgentTaskResult` from `@tamma/shared`
- [ ] Import `ProviderChainEntry` from `@tamma/shared` (Story 9-1 type with `apiKeyRef`)
- [ ] Import `ILogger` from `@tamma/shared/contracts`
- [ ] Import concrete provider classes: `ClaudeAgentProvider`, `OpenCodeProvider`, `OpenRouterProvider`, `ZenMCPProvider`
- [ ] Define `PROVIDER_NAME_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/`
- [ ] Define `FORBIDDEN_NAMES = new Set(['__proto__', 'constructor', 'prototype'])`
- [ ] Define `API_KEY_REF_PATTERN = /^[A-Za-z0-9_]+$/`
- [ ] Use duck-typing (`'sendMessageSync' in provider`) to detect IAIProvider vs IAgentProvider
  - **DUCK-TYPING RATIONALE**: More resilient to module boundary issues (different package versions), works correctly with mocked providers in tests, no need for providers to set a discriminant field. Trade-off: could false-positive if an IAgentProvider happened to have a `sendMessageSync` property (unlikely in practice); not statically verifiable by TypeScript's type system.
- [ ] Use duck-typing (`'initialize' in provider`) to detect if provider supports initialization
- [ ] Cast provider to `IAIProvider` only after duck-type check confirms the method exists

### Files to Modify/Create

- `packages/providers/src/agent-provider-factory.ts` -- CREATE: AgentProviderFactory class, IAgentProviderFactory interface, resolveApiKey(), BUILTIN_PROVIDER_NAMES
- `packages/providers/src/index.ts` -- MODIFY: add exports for AgentProviderFactory, IAgentProviderFactory, wrapAsAgent, resolveApiKey, BUILTIN_PROVIDER_NAMES

### Dependencies

- [ ] `packages/providers/src/agent-types.ts` -- IAgentProvider, AgentTaskConfig
- [ ] `packages/providers/src/types.ts` -- IAIProvider, ProviderConfig, MessageRequest, MessageResponse
- [ ] `packages/shared/src/types/index.ts` -- AgentTaskResult
- [ ] `packages/shared/src/types/agent-config.ts` -- ProviderChainEntry with `apiKeyRef` field (Story 9-1)
- [ ] `packages/shared/contracts` -- ILogger interface
- [ ] `packages/providers/src/claude-agent-provider.ts` -- ClaudeAgentProvider
- [ ] `packages/providers/src/opencode-provider.ts` -- OpenCodeProvider
- [ ] `packages/providers/src/openrouter-provider.ts` -- OpenRouterProvider
- [ ] `packages/providers/src/zen-mcp-provider.ts` -- ZenMCPProvider

## Testing Strategy

### Unit Tests

- [ ] Test `create({ provider: 'claude-code' })` returns object with `executeTask` method
- [ ] Test `create({ provider: 'opencode' })` returns object with `executeTask` method
- [ ] Test `create({ provider: 'openrouter', model: 'z-ai/z1-mini', apiKeyRef: 'TEST_OPENROUTER_KEY' })` with `process.env.TEST_OPENROUTER_KEY = 'test-key'` returns wrapped IAgentProvider
- [ ] Test `create({ provider: 'zen-mcp' })` returns wrapped IAgentProvider
- [ ] Test `create({ provider: 'unknown' })` throws `Error('Unknown provider: unknown')`
- [ ] Test `create({ provider: '' })` throws with appropriate error (invalid provider name won't be in map)
- [ ] Test `resolveApiKey()` returns `''` when `apiKeyRef` is undefined
- [ ] Test `resolveApiKey()` returns `process.env` value when `apiKeyRef` is set
- [ ] Test `resolveApiKey()` throws when env var is missing
- [ ] Test `resolveApiKey()` throws on invalid apiKeyRef format (e.g., hyphens, spaces)
- [ ] Test `register()` rejects invalid provider names (empty, uppercase, `__proto__`, `constructor`, `prototype`)
- [ ] Test `register()` after lock cannot override existing provider
- [ ] Test `register()` after lock can still add new providers
- [ ] Test `initialize()` is called on providers that have the method
- [ ] Test `initialize()` receives correct config with spread order: `{ ...entry.config, apiKey: resolvedKey, model: entry.model }`
- [ ] Test `initialize()` error is wrapped: `Provider "opencode" failed to initialize: ...`
- [ ] Test `ClaudeAgentProvider.initialize()` is called but does nothing (no-op)
- [ ] Test `OpenCodeProvider.initialize()` throwing propagates wrapped error to create() caller
- [ ] Test `register()` adds a custom provider that `create()` can instantiate
- [ ] Test `factory.dispose()` clears creators map (subsequent `create()` throws `Unknown provider`)
- [ ] Test `BUILTIN_PROVIDER_NAMES` has correct values

### Validation Steps

1. [ ] Create the `agent-provider-factory.ts` file with IAgentProviderFactory interface, resolveApiKey(), BUILTIN_PROVIDER_NAMES
2. [ ] Implement the `register()` method with name validation and lock checks
3. [ ] Implement the `create()` method with apiKeyRef resolution and duck-type detection
4. [ ] Add initialization logic with correct spread order and error wrapping
5. [ ] Add LLM provider detection and `wrapAsAgent()` delegation
6. [ ] Implement `dispose()` method
7. [ ] Register built-in providers in constructor using constants, then lock
8. [ ] Verify TypeScript strict mode compilation
9. [ ] Run unit tests

## Notes & Considerations

- **apiKeyRef, NOT apiKey**: `ProviderChainEntry` (Story 9-1) uses `apiKeyRef` to reference an environment variable name. The factory resolves this via `process.env[apiKeyRef]`. Raw API keys are NEVER stored in config files. The `resolveApiKey()` helper encapsulates this logic.

- **ClaudeAgentProvider.initialize()** is a no-op by design. The Claude CLI binary manages its own authentication through its config store (Claude subscription auth). The factory still calls `initialize()` because it uses duck-typing, but the call has no effect. `resolveApiKey()` returns `''` since claude-code entries typically have no `apiKeyRef`.

- **OpenCodeProvider.initialize()** eagerly creates the SDK client via `ensureClient()`. If OpenCode is not running locally, it throws `Failed to initialize OpenCode SDK: ...`. The factory wraps this error and re-throws. ProviderChain catches the wrapped error, records the failure, and tries the next entry.

- **Duck-typing approach**: We use `'sendMessageSync' in provider` to detect LLM providers rather than `instanceof` checks. This is more resilient to module boundary issues and works correctly with mocked providers in tests. See the code comment for full trade-off analysis.

- **ProviderChainEntry dependency**: This type is defined in Story 9-1 with `apiKeyRef` (NOT `apiKey`). If implementing before 9-1, define a local interface that matches.

- **Config spread order**: `{ ...entry.config, apiKey: resolvedKey, model: entry.model }` ensures explicit fields (apiKey, model) always take precedence and can't be accidentally overridden by user config values in `entry.config`.

- **Lock mechanism**: After registering built-ins, the factory is locked. This prevents accidental or malicious overriding of built-in providers. New providers can still be registered.

- **Thread-safety**: The factory is NOT thread-safe. Each worker thread should create its own instance.

- The `creators` map stores functions that return `IAgentProvider | IAIProvider` (the union). The `create()` method narrows the return type to `IAgentProvider` by wrapping LLM providers.

## Completion Checklist

- [ ] `IAgentProviderFactory` interface implemented and exported
- [ ] `BUILTIN_PROVIDER_NAMES` constant implemented and exported
- [ ] `resolveApiKey()` helper implemented and exported
- [ ] `AgentProviderFactory` class implemented with `IAgentProviderFactory`
- [ ] `register()` method with name validation and lock checks
- [ ] `create()` method with apiKeyRef resolution, duck-type detection, error wrapping
- [ ] `initialize()` call with correct spread order (`...entry.config` first, then explicit fields)
- [ ] `dispose()` method clears creators map
- [ ] Optional `logger` parameter in constructor
- [ ] LLM-to-agent wrapping via `wrapAsAgent()` delegation
- [ ] Built-in providers registered using `BUILTIN_PROVIDER_NAMES` constants
- [ ] Factory locked after construction
- [ ] Thread-safety comment on class
- [ ] Export added to `packages/providers/src/index.ts`
- [ ] TypeScript strict mode compiles cleanly
- [ ] All unit tests passing
- [ ] Code reviewed and approved
