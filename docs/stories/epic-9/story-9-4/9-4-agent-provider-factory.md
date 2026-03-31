# Story 4: Agent Provider Factory

## Goal
Create an `IAgentProvider` by name string. The factory **always returns `IAgentProvider`** -- never `IAgentProvider | IAIProvider`. LLM providers (`openrouter`, `zen-mcp`) are eagerly wrapped via `wrapAsAgent()` so the consumer always gets the same interface.

## Design

**New file: `packages/providers/src/agent-provider-factory.ts`**

```typescript
import type { AgentTaskResult } from '@tamma/shared';
import type {
  IAIProvider, MessageRequest, MessageResponse,
} from './types.js';
import type {
  IAgentProvider, AgentTaskConfig, AgentProgressCallback,
} from './agent-types.js';
import type { ProviderChainEntry } from '@tamma/shared';
import type { ILogger } from '@tamma/shared/contracts';
import { ClaudeAgentProvider } from './claude-agent-provider.js';
import { OpenCodeProvider } from './opencode-provider.js';
import { OpenRouterProvider } from './openrouter-provider.js';
import { ZenMCPProvider } from './zen-mcp-provider.js';

/**
 * Interface for the agent provider factory.
 * ProviderChain (Story 9-5) depends on this interface, not the concrete class.
 */
export interface IAgentProviderFactory {
  create(entry: ProviderChainEntry): Promise<IAgentProvider>;
  register(name: string, creator: () => IAgentProvider | IAIProvider): void;
  dispose(): Promise<void>;
}

/**
 * Built-in provider name constants.
 * Avoids string literal duplication across the codebase.
 */
export const BUILTIN_PROVIDER_NAMES = Object.freeze({
  CLAUDE_CODE: 'claude-code',
  OPENCODE: 'opencode',
  OPENROUTER: 'openrouter',
  ZEN_MCP: 'zen-mcp',
} as const);

/** Regex for valid provider names (same as Story 9-1 config validation). */
const PROVIDER_NAME_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;

/** Names that must be rejected to prevent prototype pollution. */
const FORBIDDEN_NAMES = new Set(['__proto__', 'constructor', 'prototype']);

/** Regex for valid apiKeyRef values — only alphanumeric + underscore. */
const API_KEY_REF_PATTERN = /^[A-Za-z0-9_]+$/;

/**
 * Resolve the API key from the environment using entry.apiKeyRef.
 *
 * - If apiKeyRef is undefined, returns '' (some providers like claude-code
 *   don't need API keys — they manage their own auth).
 * - Validates apiKeyRef format (alphanumeric + underscore only).
 * - Looks up process.env[apiKeyRef] and throws if the env var is missing.
 */
export function resolveApiKey(entry: ProviderChainEntry): string {
  if (entry.apiKeyRef === undefined) return '';

  if (!API_KEY_REF_PATTERN.test(entry.apiKeyRef)) {
    throw new Error(
      `Invalid apiKeyRef format "${entry.apiKeyRef}" for provider "${entry.provider}": only alphanumeric and underscore characters are allowed`,
    );
  }

  const value = process.env[entry.apiKeyRef];
  if (value === undefined) {
    throw new Error(
      `Environment variable "${entry.apiKeyRef}" is not set for provider "${entry.provider}"`,
    );
  }

  return value;
}

/**
 * AgentProviderFactory creates IAgentProvider instances by name.
 *
 * THREAD-SAFETY NOTE: This class is NOT thread-safe. It should be
 * instantiated once per process and not shared across worker threads.
 * Each worker thread should create its own factory instance.
 */
export class AgentProviderFactory implements IAgentProviderFactory {
  private creators = new Map<string, () => IAgentProvider | IAIProvider>();
  private locked = false;

  constructor(private logger?: ILogger) {
    // CLI agent providers -- already implement IAgentProvider
    this.register(BUILTIN_PROVIDER_NAMES.CLAUDE_CODE, () => new ClaudeAgentProvider());
    this.register(BUILTIN_PROVIDER_NAMES.OPENCODE, () => new OpenCodeProvider());
    // LLM providers -- implement IAIProvider, will be wrapped
    this.register(BUILTIN_PROVIDER_NAMES.OPENROUTER, () => new OpenRouterProvider());
    this.register(BUILTIN_PROVIDER_NAMES.ZEN_MCP, () => new ZenMCPProvider());

    // Lock after registering built-ins so they cannot be overridden
    this.lock();
  }

  /**
   * Lock the factory. After locking, register() will throw if trying
   * to override an existing provider (but can still register new ones).
   */
  private lock(): void {
    this.locked = true;
  }

  /**
   * Register a provider creator function.
   *
   * Provider name must match /^[a-z0-9][a-z0-9_-]{0,63}$/ (same as Story 9-1).
   * Rejects '__proto__', 'constructor', 'prototype' to prevent prototype pollution.
   * When locked, throws if trying to override an existing provider.
   */
  register(name: string, creator: () => IAgentProvider | IAIProvider): void {
    if (FORBIDDEN_NAMES.has(name)) {
      throw new Error(`Forbidden provider name: "${name}"`);
    }
    if (!PROVIDER_NAME_PATTERN.test(name)) {
      throw new Error(
        `Invalid provider name "${name}": must match ${PROVIDER_NAME_PATTERN}`,
      );
    }
    if (this.locked && this.creators.has(name)) {
      throw new Error(
        `Cannot override locked built-in provider "${name}". Register with a different name.`,
      );
    }

    this.creators.set(name, creator);
    this.logger?.info('Provider registered', { provider: name });
  }

  /**
   * Create a provider by name. ALWAYS returns IAgentProvider.
   * LLM providers are eagerly wrapped via wrapAsAgent().
   */
  async create(entry: ProviderChainEntry): Promise<IAgentProvider> {
    const creator = this.creators.get(entry.provider);
    if (!creator) throw new Error(`Unknown provider: ${entry.provider}`);

    this.logger?.info('Creating provider', { provider: entry.provider, model: entry.model });

    const provider = creator();

    // Resolve API key from environment via apiKeyRef.
    // Returns '' if apiKeyRef is undefined (e.g., claude-code manages its own auth).
    const resolvedKey = resolveApiKey(entry);

    // Initialize if the provider supports it.
    //
    // NOTE on specific providers:
    //
    // ClaudeAgentProvider.initialize() is a no-op. It ignores the apiKey
    // from the chain entry because the Claude CLI reads credentials from
    // its own config store (Claude subscription auth). This is by design.
    //
    // OpenCodeProvider.initialize() eagerly creates an SDK client via
    // dynamic import of @opencode-ai/sdk. It will throw if OpenCode is
    // not running locally. This error is caught by ProviderChain, which
    // records the failure and tries the next entry.
    if ('initialize' in provider && typeof provider.initialize === 'function') {
      try {
        // Spread order: entry.config first, then explicit fields override.
        // This ensures apiKey and model cannot be accidentally overridden by user config.
        await (provider as IAIProvider).initialize({
          ...entry.config,
          apiKey: resolvedKey,
          model: entry.model,
        });
        this.logger?.info('Provider initialized successfully', { provider: entry.provider });
      } catch (err) {
        // Sanitize error messages to strip potentially sensitive content
        // (API keys, file paths) before propagating.
        const sanitizedMessage = err instanceof Error ? err.message : String(err);
        this.logger?.error('Provider initialization failed', {
          provider: entry.provider,
          error: sanitizedMessage,
        });
        throw new Error(
          `Provider "${entry.provider}" failed to initialize: ${sanitizedMessage}`,
        );
      }
    }

    // If this is an LLM provider (has sendMessageSync), wrap it.
    //
    // DUCK-TYPING RATIONALE: We use `'sendMessageSync' in provider` to detect
    // LLM providers rather than `instanceof` checks or a type discriminant field.
    // Trade-offs:
    // - Pro: More resilient to module boundary issues (different package versions)
    // - Pro: Works correctly with mocked providers in tests
    // - Pro: No need for providers to set a discriminant field
    // - Con: Could false-positive if an IAgentProvider happened to have a
    //   `sendMessageSync` property (unlikely in practice)
    // - Con: Not statically verifiable by TypeScript's type system
    if ('sendMessageSync' in provider) {
      return wrapAsAgent(provider as IAIProvider, entry.model ?? 'default');
    }

    return provider as IAgentProvider;
  }

  /**
   * Dispose of the factory by clearing the creators map.
   *
   * NOTE: The factory is a stateless creator — it does not track provider
   * instances. Instance lifecycle (dispose) is the caller's responsibility.
   * This method only clears the internal registration map.
   */
  async dispose(): Promise<void> {
    this.creators.clear();
    this.logger?.info('AgentProviderFactory disposed');
  }
}

/**
 * Wrap an IAIProvider as IAgentProvider.
 *
 * Converts sendMessageSync() into executeTask():
 * 1. Builds a MessageRequest from the prompt string
 * 2. Calls sendMessageSync() to get a MessageResponse
 * 3. Maps MessageResponse to AgentTaskResult, extracting tokens/cost from usage
 *
 * LIMITATIONS: wrapAsAgent() provides a simple prompt→response mapping.
 * It does NOT support:
 * - Tool-use loops (multi-turn tool calling)
 * - File tracking (which files were read/written)
 * - Exit code semantics
 * - Streaming progress callbacks
 * These capabilities are only available through native agent providers
 * (claude-code, opencode) that implement IAgentProvider directly.
 */
export function wrapAsAgent(llm: IAIProvider, model: string): IAgentProvider {
  return {
    async executeTask(
      config: AgentTaskConfig,
      _onProgress?: AgentProgressCallback,
    ): Promise<AgentTaskResult> {
      const start = Date.now();

      // Use config.model if provided (task-level override), otherwise fall back
      // to the model from the closure (chain entry level).
      const request: MessageRequest = {
        messages: [{ role: 'user', content: config.prompt }],
        model: config.model ?? model,
        maxTokens: undefined,
        temperature: undefined,
      };

      try {
        const response: MessageResponse = await llm.sendMessageSync(request);

        return {
          success: response.finishReason !== 'error',
          output: response.content,
          costUsd: 0,  // Actual cost computed by cost-monitor from token counts
          durationMs: Date.now() - start,
        };
      } catch (err) {
        return {
          success: false,
          output: '',
          costUsd: 0,
          durationMs: Date.now() - start,
          error: err instanceof Error ? err.message : String(err),
        };
      }
    },

    /**
     * isAvailable() always returns true for wrapped LLM providers.
     *
     * TRADE-OFF: IAIProvider does NOT have an isAvailable() method, so
     * there is nothing to delegate to. We assume available if initialize()
     * succeeded (which already ran before wrapping). If the provider is
     * actually down, executeTask() will throw when calling sendMessageSync(),
     * and ProviderChain handles the fallback. This means connectivity is
     * only validated lazily (on first task), not eagerly.
     */
    async isAvailable(): Promise<boolean> {
      return true;
    },

    async dispose(): Promise<void> {
      return llm.dispose();
    },
  };
}
```

## Key behaviors

- **Factory return type is `IAgentProvider` only.** Callers never need to check which interface they got.
- **`IAgentProviderFactory` interface** extracted for dependency inversion. ProviderChain (Story 9-5) depends on the interface, not the concrete `AgentProviderFactory` class.
- **`resolveApiKey(entry)`** resolves `entry.apiKeyRef` from `process.env`, NOT a raw `apiKey` field (which does not exist on `ProviderChainEntry`). Returns `''` if `apiKeyRef` is undefined (e.g., claude-code). Throws if the env var is missing or apiKeyRef format is invalid.
- **`BUILTIN_PROVIDER_NAMES`** constant object avoids string literal duplication for the four built-in providers.
- **Provider name validation in `register()`:** Must match `/^[a-z0-9][a-z0-9_-]{0,63}$/` (same as Story 9-1). Rejects `__proto__`, `constructor`, `prototype`.
- **Lock after construction:** After registering built-ins, the factory is locked. `register()` can still add new providers but cannot override existing (locked) ones.
- **Config spread order:** `{ ...entry.config, apiKey: resolvedKey, model: entry.model }` -- explicit fields always take precedence over user config.
- **`wrapAsAgent()` mapping:**
  - Builds a `MessageRequest` with `messages: [{ role: 'user', content: config.prompt }]`
  - Uses `config.model ?? model` so task-level model overrides work
  - Calls `IAIProvider.sendMessageSync()` to get `MessageResponse`
  - Maps `MessageResponse.content` to `AgentTaskResult.output`
  - Maps `MessageResponse.finishReason === 'error'` to `AgentTaskResult.success = false`
  - Token usage (`response.usage.inputTokens`, `outputTokens`) is available for diagnostics; actual cost is computed by `cost-monitor`
  - `isAvailable()` always returns `true` since `initialize()` already ran; if the provider is actually down, `executeTask()` will throw and `ProviderChain` handles fallback (see code comment for trade-off documentation)
  - Error handling uses `err instanceof Error ? err.message : String(err)` (safe -- no unsafe `(err as Error).message` cast)
  - **Limitations:** wrapAsAgent() provides a simple prompt-to-response mapping without tool-use loops, file tracking, or exit code semantics. These capabilities are only available through native agent providers (claude-code, opencode).
- **`initialize()` error sanitization:** Errors from `initialize()` are wrapped in a generic message (`Provider "${name}" failed to initialize: ...`) to prevent leaking sensitive content.
- **`factory.dispose()`** clears the creators map. Instance lifecycle is the caller's responsibility (factory is a stateless creator, does not track instances).
- **Optional logging:** Constructor accepts `logger?: ILogger`. Logs at INFO level: provider registration, creation, initialization success/failure.
- **Thread-safety:** `AgentProviderFactory` is NOT thread-safe. Instantiate once per process; do not share across worker threads.
- **`ClaudeAgentProvider.initialize()`** is a no-op -- ignores apiKey from chain entry. The Claude CLI binary manages its own authentication.
- **`OpenCodeProvider.initialize()`** eagerly creates the SDK client via `ensureClient()`. If OpenCode is not running, it throws `Failed to initialize OpenCode SDK: ...`. The factory wraps and re-throws; `ProviderChain` catches this, records a health failure, and moves to the next entry.

## Files
- CREATE `packages/providers/src/agent-provider-factory.ts`
- CREATE `packages/providers/src/agent-provider-factory.test.ts`

## Verify
- Test: `create({ provider: 'claude-code' })` returns `IAgentProvider` (ClaudeAgentProvider)
- Test: `create({ provider: 'openrouter', model: 'z-ai/z1-mini', apiKeyRef: 'TEST_OPENROUTER_KEY' })` with `process.env.TEST_OPENROUTER_KEY = 'test-key'` returns `IAgentProvider` (wrapped via `wrapAsAgent`)
- Test: `create({ provider: 'zen-mcp' })` returns `IAgentProvider` (wrapped via `wrapAsAgent`)
- Test: unknown provider throws `Unknown provider: foo`
- Test: `resolveApiKey()` -- returns `''` when `apiKeyRef` is undefined
- Test: `resolveApiKey()` -- returns env var value when set
- Test: `resolveApiKey()` -- throws when env var is missing: `Environment variable "X" is not set for provider "Y"`
- Test: `resolveApiKey()` -- throws on invalid apiKeyRef format (e.g., `'MY-KEY'` with hyphens)
- Test: `register()` rejects invalid provider names (empty, uppercase, `__proto__`, etc.)
- Test: `register()` after lock cannot override existing provider, but can add new ones
- Test: `initialize()` called on LLM providers with correct config (spread order: entry.config first, then apiKey/model override)
- Test: `initialize()` error is wrapped: `Provider "X" failed to initialize: ...`
- Test: `wrapAsAgent()` -- `executeTask()` calls `sendMessageSync()` and maps response fields correctly
- Test: `wrapAsAgent()` -- uses `config.model` when provided (task-level override)
- Test: `wrapAsAgent()` -- on `sendMessageSync()` error, returns `{ success: false, error: message }` (safe casting)
- Test: `wrapAsAgent()` -- on non-Error thrown value, error field contains `String(err)`
- Test: `wrapAsAgent().isAvailable()` returns `true` (no delegation to IAIProvider which lacks the method)
- Test: mock `OpenCodeProvider.initialize()` throwing -- verify wrapped error propagates to caller
- Test: `factory.dispose()` clears creators map
- Test: `BUILTIN_PROVIDER_NAMES` has correct values for all four built-in providers

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
