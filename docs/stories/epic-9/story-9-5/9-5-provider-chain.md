# Story 5: Provider Chain with Fallback & Diagnostics

## Goal
Given a list of `ProviderChainEntry[]`, try each in order. Skip unhealthy (circuit open). On error, record failure, try next. All attempts recorded in diagnostics.

## Design

**New file: `packages/providers/src/provider-chain.ts`**

The factory (Story 4) now returns `IAgentProvider` only, so `provider.isAvailable()` compiles without a type guard. Diagnostics use `DiagnosticsQueue` from `@tamma/shared/src/telemetry/` (not `ToolHookRegistry` from mcp-client). Error helpers come from the extracted `packages/providers/src/errors.ts` (Story 3).

### Design notes

- **No per-entry retries:** Per-entry retries are not supported. Transient failures are handled by the circuit breaker's half-open recovery mechanism and by the caller retrying chain resolution.
- **Concurrency:** `ProviderChain` is safe for concurrent `getProvider()` calls. State management (health, budget) is delegated to injected services. No internal mutable state beyond the frozen entries array.

```typescript
import type { IAgentProvider } from './agent-types.js';
import type { AgentType } from '@tamma/shared';
import type { ProviderChainEntry } from '@tamma/shared/src/types/agent-config.js';
import type { ICostTracker, Provider } from '@tamma/cost-monitor';
import type { ILogger } from '@tamma/shared/contracts';
import type { IAgentProviderFactory } from './agent-provider-factory.js';
import type { IProviderHealthTracker } from './types.js';
import type { DiagnosticsQueue } from '@tamma/shared/src/telemetry/index.js';
import { ProviderHealthTracker } from './provider-health.js';
import { createProviderError, isProviderError } from './errors.js';
import { InstrumentedAgentProvider } from './instrumented-agent-provider.js';

/**
 * Interface for the provider chain.
 * Story 9-8's RoleBasedAgentResolver depends on this interface, not the concrete class.
 */
export interface IProviderChain {
  getProvider(context: { agentType: AgentType; projectId: string; engineId: string }): Promise<IAgentProvider>;
}

/** Options object for ProviderChain constructor. */
export interface ProviderChainOptions {
  entries: readonly ProviderChainEntry[];
  factory: IAgentProviderFactory;
  health: IProviderHealthTracker;
  diagnostics: DiagnosticsQueue;
  costTracker?: ICostTracker;
  logger?: ILogger;
}

/**
 * Module-level constant: known provider strings for safe budget check casting.
 * Moved out of the loop to avoid re-creation on every iteration.
 */
const KNOWN_PROVIDERS = new Set<string>([
  'anthropic', 'openai', 'openrouter', 'google', 'local',
  'claude-code', 'opencode', 'z-ai', 'zen-mcp',
] as const satisfies readonly string[]);

/**
 * ProviderChain is safe for concurrent getProvider() calls. State management
 * (health, budget) is delegated to injected services. No internal mutable
 * state beyond the frozen entries array.
 *
 * Per-entry retries are not supported. Transient failures are handled by the
 * circuit breaker's half-open recovery mechanism and by the caller retrying
 * chain resolution.
 */
export class ProviderChain implements IProviderChain {
  private readonly entries: readonly ProviderChainEntry[];
  private readonly factory: IAgentProviderFactory;
  private readonly health: IProviderHealthTracker;
  private readonly diagnostics: DiagnosticsQueue;
  private readonly costTracker?: ICostTracker;
  private readonly logger?: ILogger;

  constructor(options: ProviderChainOptions) {
    // Defensive copy and freeze to prevent external mutation
    this.entries = Object.freeze([...options.entries]);
    this.factory = options.factory;
    this.health = options.health;
    this.diagnostics = options.diagnostics;
    this.costTracker = options.costTracker;
    this.logger = options.logger;
  }

  /** Get the first available healthy provider, wrapped with instrumentation. */
  async getProvider(context: {
    agentType: AgentType;
    projectId: string;
    engineId: string;
  }): Promise<IAgentProvider> {
    // Guard: empty chain is a config error, not a runtime fallback
    if (this.entries.length === 0) {
      throw createProviderError(
        'EMPTY_PROVIDER_CHAIN',
        'providerChain is empty — configure at least one provider entry for this role.',
        false,
        'critical',
      );
    }

    const errors: Array<{ provider: string; message: string }> = [];

    for (const entry of this.entries) {
      // Use centralized key construction from ProviderHealthTracker
      const key = ProviderHealthTracker.buildKey(entry.provider, entry.model);

      if (!this.health.isHealthy(key)) {
        this.logger?.debug('Skipping unhealthy provider', { key });
        continue;
      }

      // Budget check before attempting — wrapped in try/catch with fail-closed policy
      if (this.costTracker) {
        try {
          if (KNOWN_PROVIDERS.has(entry.provider)) {
            const limit = await this.costTracker.checkLimit({
              provider: entry.provider as Provider,
              model: entry.model,
              agentType: context.agentType,
            });
            if (!limit.allowed) {
              this.logger?.warn('Budget exceeded for provider', { key, percentUsed: limit.percentUsed });
              continue;
            }
          }
        } catch (budgetErr) {
          // Fail-closed: if checkLimit() throws, skip this provider
          this.logger?.warn('Budget check failed, skipping provider (fail-closed)', {
            key,
            error: budgetErr instanceof Error ? budgetErr.message : String(budgetErr),
          });
          continue;
        }
      }

      let provider: IAgentProvider | undefined;
      try {
        provider = await this.factory.create(entry);

        if (await provider.isAvailable()) {
          // Do NOT call recordSuccess() here — success is only recorded
          // by InstrumentedAgentProvider when a task actually completes.

          // Wrap with instrumentation (uses shared diagnostics queue)
          return new InstrumentedAgentProvider(provider, this.diagnostics, {
            providerName: entry.provider,
            model: entry.model ?? 'default',
            agentType: context.agentType,
            projectId: context.projectId,
            engineId: context.engineId,
          });
        }

        // isAvailable() returned false — dispose to prevent resource leak
        await provider.dispose();
        this.health.recordFailure(key);
      } catch (err) {
        // Dispose on error to prevent resource leak — log dispose failures
        if (provider) {
          await provider.dispose().catch((disposeErr) => {
            this.logger?.warn('Failed to dispose provider', {
              key,
              error: disposeErr instanceof Error ? disposeErr.message : String(disposeErr),
            });
          });
        }

        // Type-safe recording: only pass ProviderError when it actually is one
        if (isProviderError(err)) {
          this.health.recordFailure(key, err);
        } else {
          this.health.recordFailure(key);
        }
        errors.push({
          provider: entry.provider,
          message: err instanceof Error ? err.message : String(err),
        });
      }
    }

    // NO_AVAILABLE_PROVIDER: retryable is false because the chain itself
    // does not retry. Callers (engine) should retry the entire chain
    // resolution with backoff if all providers are temporarily circuit-open.
    throw createProviderError(
      'NO_AVAILABLE_PROVIDER',
      `All ${this.entries.length} providers exhausted. Tried: ${this.entries.map(e => e.provider).join(', ')}`,
      false,
      'critical',
      { errors },
    );
  }
}
```

## Key behavior
- Iterates entries in order (priority)
- Guards empty `providerChain: []` with an explicit `EMPTY_PROVIDER_CHAIN` error (retryable: false, severity: 'critical') before iterating
- Skips circuit-open providers via `IProviderHealthTracker.isHealthy(key)` (interface, not concrete class)
- Uses `ProviderHealthTracker.buildKey()` for centralized key construction (not inline template literal)
- Checks budget before attempting (with fail-closed policy: if `checkLimit()` throws, skip the provider)
- Uses module-level `KNOWN_PROVIDERS` constant for safe provider string validation
- Wraps returned provider with `InstrumentedAgentProvider` for diagnostics
- Does NOT call `recordSuccess()` after `isAvailable()` -- success recording is the responsibility of `InstrumentedAgentProvider` on actual task completion
- Calls `provider.dispose()` when `isAvailable()` returns false or on error to prevent resource leaks
- Logs dispose errors instead of silently swallowing them
- Uses `isProviderError()` type guard before passing errors to `recordFailure()` -- no unsafe casts
- Sanitizes error messages: `NO_AVAILABLE_PROVIDER` error message does not include raw `e.message` from provider errors; detailed error info is in structured context
- Imports `createProviderError` from the extracted `packages/providers/src/errors.ts` (Story 3)
- Uses `DiagnosticsQueue` from `@tamma/shared/src/telemetry/` instead of `ToolHookRegistry` from mcp-client
- Constructor uses `ProviderChainOptions` object pattern (not 6 positional params)
- Defensive copy and freeze of entries in constructor prevents external mutation
- `ProviderChain` implements `IProviderChain` interface for dependency inversion (Story 9-8 depends on the interface)
- Per-entry retries are not supported; transient failures handled by circuit breaker half-open recovery and caller retrying chain resolution
- `NO_AVAILABLE_PROVIDER` has retryable: false because the chain itself does not retry; callers (engine) should retry with backoff

## Files
- CREATE `packages/providers/src/provider-chain.ts`
- CREATE `packages/providers/src/provider-chain.test.ts`

## Verify
- Test: returns first healthy provider
- Test: skips unhealthy, returns second
- Test: all fail -> throws `NO_AVAILABLE_PROVIDER` with provider names (not raw error messages in message string)
- Test: `NO_AVAILABLE_PROVIDER` error has structured `context.errors` array with per-provider error details
- Test: empty chain -> throws `EMPTY_PROVIDER_CHAIN` error (retryable: false, severity: 'critical')
- Test: budget exceeded -> skips provider
- Test: budget check throws -> skips provider (fail-closed policy)
- Test: returned provider is instrumented (records usage)
- Test: `provider.dispose()` called when `isAvailable()` returns false
- Test: `provider.dispose()` called on error, dispose errors logged (not silently swallowed)
- Test: unknown provider string does not crash budget check
- Test: constructor uses `ProviderChainOptions` object
- Test: entries are defensively copied and frozen
- Test: `ProviderChain` implements `IProviderChain`
- Test: `ProviderHealthTracker.buildKey()` used for key construction
- Test: `InstrumentedAgentProvider` constructor throw disposes inner provider

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
