---
title: "Story 2: Provider Diagnostics Collector"
sidebar:
  order: 90
---

## Goal
Instrument every provider call to emit `UsageRecordInput` to `@tamma/cost-monitor`. Track per provider+model combo: costs, tokens, latency, errors, success rate. All data available for reporting via existing `ICostTracker.generateReport()`.

## Design

The `cost-monitor` package already has everything needed: `UsageRecord`, `ICostTracker.recordUsage()`, `generateReport()`, alerts, limits. What is missing is the **instrumentation layer** that sits between the caller and the provider.

**Important architecture note:** The queue/event system is **not** in `ToolHookRegistry` in `mcp-client`. It has been split into a standalone `DiagnosticsQueue` in `packages/shared/src/telemetry/`. The `ToolHookRegistry` in `mcp-client` handles MCP tool interceptors only. All diagnostics event queuing goes through `DiagnosticsQueue`.

### DiagnosticsEvent Canonical Design

**Aligned with Story 9-11.** `DiagnosticsEvent` is defined in its own file `packages/shared/src/telemetry/diagnostics-event.ts` (not inline in diagnostics-queue.ts). Context fields `agentType`, `projectId`, `engineId`, `taskId`, and `taskType` are **required** (not optional). Uses a discriminated union pattern with `ToolDiagnosticsEvent` and `ProviderDiagnosticsEvent` subtypes sharing a common base.

**New file: `packages/shared/src/telemetry/diagnostics-event.ts`**

```typescript
import type { AgentType } from '../types/knowledge.js';

export type DiagnosticsEventType =
  | 'tool:invoke' | 'tool:complete' | 'tool:error'
  | 'provider:call' | 'provider:complete' | 'provider:error';

/**
 * Typed error categories for diagnostics events.
 * Using a union type instead of plain string provides better type safety
 * and documents the expected error categories.
 */
export type DiagnosticsErrorCode =
  | 'RATE_LIMIT_EXCEEDED'
  | 'QUOTA_EXCEEDED'
  | 'TIMEOUT'
  | 'AUTH_FAILED'
  | 'NETWORK_ERROR'
  | 'TASK_FAILED'
  | 'UNKNOWN';

/** Base fields shared by all DiagnosticsEvent subtypes */
interface DiagnosticsEventBase {
  type: DiagnosticsEventType;
  timestamp: number;

  // Context -- required for mapping to UsageRecordInput
  agentType: AgentType;              // Typed as AgentType, not string
  projectId: string;
  engineId: string;
  taskId: string;
  taskType: string;

  // Outcome (populated for complete/error events)
  latencyMs?: number;
  success?: boolean;
  costUsd?: number;
  errorCode?: DiagnosticsErrorCode;
  errorMessage?: string;             // Sanitized: max 500 chars, API key patterns stripped
  tokens?: { input: number; output: number };
}

/** Tool-specific diagnostics event (tool:invoke, tool:complete, tool:error) */
export interface ToolDiagnosticsEvent extends DiagnosticsEventBase {
  type: 'tool:invoke' | 'tool:complete' | 'tool:error';
  toolName: string;
  serverName?: string;
  args?: Record<string, unknown>;
}

/** Provider-specific diagnostics event (provider:call, provider:complete, provider:error) */
export interface ProviderDiagnosticsEvent extends DiagnosticsEventBase {
  type: 'provider:call' | 'provider:complete' | 'provider:error';
  providerName: string;
  model?: string;
}

/** Discriminated union of all diagnostics event types */
export type DiagnosticsEvent = ToolDiagnosticsEvent | ProviderDiagnosticsEvent;
```

**New file: `packages/shared/src/telemetry/sanitize-error.ts`**

Utility to sanitize error messages before storing in diagnostics events. Truncates to 500 chars and strips patterns matching API keys (Bearer tokens, sk-*, key-*).

```typescript
const API_KEY_PATTERNS = [
  /Bearer\s+[A-Za-z0-9\-._~+/]+=*/g,     // Bearer tokens
  /sk-[A-Za-z0-9]{20,}/g,                  // OpenAI-style keys
  /key-[A-Za-z0-9]{20,}/g,                 // Generic key-* patterns
  /[A-Za-z0-9]{32,}/g,                     // Long alphanumeric strings (potential keys)
];

const MAX_ERROR_MESSAGE_LENGTH = 500;

/**
 * Sanitize error messages for diagnostics events.
 * Truncates to 500 characters and strips patterns matching API keys.
 */
export function sanitizeErrorMessage(message: string): string {
  let sanitized = message;
  for (const pattern of API_KEY_PATTERNS) {
    sanitized = sanitized.replace(pattern, '[REDACTED]');
  }
  if (sanitized.length > MAX_ERROR_MESSAGE_LENGTH) {
    sanitized = sanitized.slice(0, MAX_ERROR_MESSAGE_LENGTH) + '...';
  }
  return sanitized;
}
```

**New file: `packages/shared/src/telemetry/diagnostics-queue.ts`**

The queue that collects diagnostics events from all sources (providers, MCP tools, etc.) and drains them to a processor in the background. Aligned with Story 9-11's canonical design including `drainPromise` concurrency guard, `setProcessor()` warning guard, `droppedCount` counter, and structured error logging.

```typescript
import type { DiagnosticsEvent } from './diagnostics-event.js';

export type DiagnosticsEventProcessor = (events: DiagnosticsEvent[]) => Promise<void>;

export interface DiagnosticsQueueLogger {
  warn(message: string, context?: Record<string, unknown>): void;
}

export class DiagnosticsQueue {
  private queue: DiagnosticsEvent[] = [];
  private processor: DiagnosticsEventProcessor | null = null;
  private drainTimer: ReturnType<typeof setInterval> | null = null;
  private drainPromise: Promise<void> | null = null;  // in-flight drain guard
  private processorSet = false;                        // setProcessor() guard
  private droppedCount = 0;                            // overflow counter
  private readonly drainIntervalMs: number;
  private readonly maxQueueSize: number;

  constructor(
    private options?: {
      drainIntervalMs?: number;   // default: 5000
      maxQueueSize?: number;      // default: 1000
    },
    private logger?: DiagnosticsQueueLogger,
  ) {
    this.drainIntervalMs = options?.drainIntervalMs ?? 5000;
    this.maxQueueSize = options?.maxQueueSize ?? 1000;
  }

  /**
   * Register a processor and start the drain timer.
   * Logs a warning if a processor has already been set (does not silently replace).
   */
  setProcessor(processor: DiagnosticsEventProcessor): void {
    if (this.processorSet) {
      this.logger?.warn('DiagnosticsQueue: processor already set, replacing', {
        hadExistingProcessor: true,
      });
    }
    this.processor = processor;
    this.processorSet = true;
    if (!this.drainTimer) {
      this.drainTimer = setInterval(() => void this.drain(), this.drainIntervalMs);
      if (this.drainTimer.unref) this.drainTimer.unref();
    }
  }

  /** Synchronous push -- zero overhead in hot path */
  emit(event: DiagnosticsEvent): void {
    if (this.queue.length >= this.maxQueueSize) {
      this.queue.shift();
      this.droppedCount++;
    }
    this.queue.push(event);
  }

  /** Returns the number of events dropped due to queue overflow */
  getDroppedCount(): number {
    return this.droppedCount;
  }

  /**
   * Drain queue to processor. Guarded against concurrent execution.
   * Matches Story 9-11 canonical drain implementation.
   */
  private async drain(): Promise<void> {
    // If a drain is already in flight (from timer or dispose), wait for it
    if (this.drainPromise) {
      await this.drainPromise;
      return;
    }

    if (!this.processor || this.queue.length === 0) return;

    const batch = this.queue.splice(0);
    this.drainPromise = this.processor(batch)
      .catch((err: unknown) => {
        this.logger?.warn('Diagnostics drain failed', {
          error: err instanceof Error ? err.message : String(err),
          batchSize: batch.length,
        });
      })
      .finally(() => { this.drainPromise = null; });

    await this.drainPromise;
  }

  /** Flush remaining events and stop timer */
  async dispose(): Promise<void> {
    if (this.drainTimer) {
      clearInterval(this.drainTimer);
      this.drainTimer = null;
    }
    await this.drain();
  }
}
```

**New file: `packages/shared/src/telemetry/validate-diagnostics.ts`**

Value validation utilities for diagnostics event fields.

```typescript
/** Clamp costUsd to non-negative value */
export function validateCostUsd(value: number | undefined): number | undefined {
  if (value === undefined) return undefined;
  return Math.max(0, value);
}

/** Clamp token count to valid range [0, 10_000_000] */
export function validateTokenCount(value: number): number {
  return Math.max(0, Math.min(10_000_000, value));
}

/** Truncate errorCode to max 100 characters */
export function validateErrorCode(value: string | undefined): string | undefined {
  if (value === undefined) return undefined;
  return value.length > 100 ? value.slice(0, 100) : value;
}
```

**New file: `packages/providers/src/instrumented-agent-provider.ts`**

Decorator that wraps any `IAgentProvider`. Emits to `DiagnosticsQueue` (from `@tamma/shared`), not `ToolHookRegistry`. Supports per-call `taskId`/`taskType` via `executeTask()` options or mutable context via `updateContext()`.

```typescript
import type { AgentType } from '@tamma/shared';
import type { DiagnosticsQueue, DiagnosticsErrorCode } from '@tamma/shared/telemetry';
import { sanitizeErrorMessage } from '@tamma/shared/telemetry';
import { mapProviderName } from './provider-name-mapping.js';
import type { IAgentProvider, AgentTaskConfig, AgentProgressCallback } from './agent-types.js';

export interface InstrumentedAgentContext {
  providerName: string;
  model: string;
  agentType: AgentType;           // AgentType from @tamma/shared, not string
  projectId: string;
  engineId: string;
  taskId: string;
  taskType: string;
}

export class InstrumentedAgentProvider implements IAgentProvider {
  private context: InstrumentedAgentContext;

  constructor(
    private inner: IAgentProvider,
    private diagnostics: DiagnosticsQueue,
    context: InstrumentedAgentContext,
  ) {
    this.context = { ...context };
  }

  /**
   * Update mutable context fields (taskId, taskType) between calls.
   * This avoids needing a new InstrumentedAgentProvider instance per task.
   */
  updateContext(updates: Partial<Pick<InstrumentedAgentContext, 'taskId' | 'taskType'>>): void {
    if (updates.taskId !== undefined) this.context.taskId = updates.taskId;
    if (updates.taskType !== undefined) this.context.taskType = updates.taskType;
  }

  async executeTask(config: AgentTaskConfig, onProgress?: AgentProgressCallback): Promise<AgentTaskResult> {
    this.diagnostics.emit({
      type: 'provider:call',
      timestamp: Date.now(),
      providerName: this.context.providerName,
      model: this.context.model,
      agentType: this.context.agentType,
      projectId: this.context.projectId,
      engineId: this.context.engineId,
      taskId: this.context.taskId,
      taskType: this.context.taskType,
    });

    const start = Date.now();
    try {
      const result = await this.inner.executeTask(config, onProgress);
      this.diagnostics.emit({
        type: 'provider:complete',
        timestamp: Date.now(),
        providerName: this.context.providerName,
        model: this.context.model,
        agentType: this.context.agentType,
        projectId: this.context.projectId,
        engineId: this.context.engineId,
        taskId: this.context.taskId,
        taskType: this.context.taskType,
        latencyMs: Date.now() - start,
        success: result.success,
        costUsd: result.costUsd,
        // Note: AgentTaskResult should include optional tokens?: { input: number; output: number }
        // for diagnostics to access. See finding #14.
        tokens: result.tokens,
        errorCode: result.error ? 'TASK_FAILED' : undefined,
      });
      return result;
    } catch (err) {
      const errorCode: DiagnosticsErrorCode = (err as any)?.code ?? 'UNKNOWN';
      this.diagnostics.emit({
        type: 'provider:error',
        timestamp: Date.now(),
        providerName: this.context.providerName,
        model: this.context.model,
        agentType: this.context.agentType,
        projectId: this.context.projectId,
        engineId: this.context.engineId,
        taskId: this.context.taskId,
        taskType: this.context.taskType,
        latencyMs: Date.now() - start,
        success: false,
        errorCode,
        errorMessage: sanitizeErrorMessage((err as Error).message),
      });
      throw err;
    }
  }

  async isAvailable(): Promise<boolean> { return this.inner.isAvailable(); }
  async dispose(): Promise<void> { return this.inner.dispose(); }
}
```

**New file: `packages/providers/src/provider-name-mapping.ts`**

Safe provider name mapping utility. Replaces unsafe `as Provider` cast with validated mapping.

```typescript
import type { Provider } from '@tamma/cost-monitor';

const KNOWN_PROVIDERS: ReadonlySet<string> = new Set<Provider>([
  'anthropic', 'openai', 'openrouter', 'google', 'local',
  'claude-code', 'opencode', 'z-ai', 'zen-mcp',
]);

const DEFAULT_PROVIDER: Provider = 'claude-code';

/**
 * Map a provider name string to the Provider type safely.
 * Returns the validated Provider value or defaults to 'claude-code'
 * if the name is not recognized.
 */
export function mapProviderName(name: string | undefined): Provider {
  if (name && KNOWN_PROVIDERS.has(name)) {
    return name as Provider;
  }
  return DEFAULT_PROVIDER;
}
```

**`InstrumentedLLMProvider`** wraps `IAIProvider` and implements the **full `IAIProvider` interface**: `initialize()`, `sendMessage()` (streaming), `sendMessageSync()`, `getCapabilities()`, `getModels()`, and `dispose()`. Both `sendMessageSync()` and `sendMessage()` (streaming) are instrumented. The streaming wrapper tracks tokens incrementally and emits events on completion.

> **SECURITY NOTE:** `InstrumentedLLMProvider` has access to prompt content via `MessageRequest`. It must NOT log or store prompt text. Only metadata (tokens, latency, cost, error codes) is recorded.

```typescript
import type { AgentType } from '@tamma/shared';
import type { DiagnosticsQueue, DiagnosticsErrorCode } from '@tamma/shared/telemetry';
import { sanitizeErrorMessage } from '@tamma/shared/telemetry';
import type {
  IAIProvider, MessageRequest, MessageResponse, MessageChunk,
  ProviderConfig, ProviderCapabilities, ModelInfo,
} from './types.js';

export interface InstrumentedLLMContext {
  providerName: string;
  model: string;
  agentType: AgentType;
  projectId: string;
  engineId: string;
  taskId: string;
  taskType: string;
}

/**
 * Instrumented wrapper for IAIProvider that implements the full interface.
 *
 * SECURITY: This class has access to prompt content via MessageRequest.
 * It must NOT log or store prompt text. Only metadata (tokens, latency,
 * cost, error codes) is recorded in diagnostics events.
 */
export class InstrumentedLLMProvider implements IAIProvider {
  constructor(
    private inner: IAIProvider,
    private diagnostics: DiagnosticsQueue,
    private context: InstrumentedLLMContext,
  ) {}

  async initialize(config: ProviderConfig): Promise<void> {
    return this.inner.initialize(config);
  }

  /**
   * Instrumented streaming sendMessage.
   * Wraps the AsyncIterable to track tokens incrementally and emit
   * a diagnostics event on stream completion.
   */
  async sendMessage(request: MessageRequest, options?: Record<string, unknown>): Promise<AsyncIterable<MessageChunk>> {
    const start = Date.now();
    this.diagnostics.emit({
      type: 'provider:call', timestamp: start,
      providerName: this.context.providerName,
      model: this.context.model,
      agentType: this.context.agentType,
      projectId: this.context.projectId,
      engineId: this.context.engineId,
      taskId: this.context.taskId,
      taskType: this.context.taskType,
    });

    try {
      const stream = await this.inner.sendMessage(request, options);

      // Wrap the stream to track tokens and emit completion event
      const diagnostics = this.diagnostics;
      const context = this.context;
      const wrappedStream: AsyncIterable<MessageChunk> = {
        [Symbol.asyncIterator](): AsyncIterator<MessageChunk> {
          const iterator = stream[Symbol.asyncIterator]();
          let inputTokens = 0;
          let outputTokens = 0;

          return {
            async next(): Promise<IteratorResult<MessageChunk>> {
              try {
                const result = await iterator.next();
                if (result.done) {
                  // Stream complete -- emit provider:complete
                  diagnostics.emit({
                    type: 'provider:complete', timestamp: Date.now(),
                    providerName: context.providerName,
                    model: context.model,
                    agentType: context.agentType,
                    projectId: context.projectId,
                    engineId: context.engineId,
                    taskId: context.taskId,
                    taskType: context.taskType,
                    latencyMs: Date.now() - start,
                    success: true,
                    tokens: { input: inputTokens, output: outputTokens },
                  });
                  return result;
                }
                // Track tokens incrementally from chunk usage if available
                if (result.value.usage) {
                  inputTokens = result.value.usage.inputTokens ?? inputTokens;
                  outputTokens = result.value.usage.outputTokens ?? outputTokens;
                }
                return result;
              } catch (streamErr) {
                // Stream error -- emit provider:error
                diagnostics.emit({
                  type: 'provider:error', timestamp: Date.now(),
                  providerName: context.providerName,
                  model: context.model,
                  agentType: context.agentType,
                  projectId: context.projectId,
                  engineId: context.engineId,
                  taskId: context.taskId,
                  taskType: context.taskType,
                  latencyMs: Date.now() - start,
                  success: false,
                  errorCode: (streamErr as any)?.code ?? 'UNKNOWN',
                  errorMessage: sanitizeErrorMessage((streamErr as Error).message),
                });
                throw streamErr;
              }
            },
          };
        },
      };

      return wrappedStream;
    } catch (err) {
      this.diagnostics.emit({
        type: 'provider:error', timestamp: Date.now(),
        providerName: this.context.providerName,
        model: this.context.model,
        agentType: this.context.agentType,
        projectId: this.context.projectId,
        engineId: this.context.engineId,
        taskId: this.context.taskId,
        taskType: this.context.taskType,
        latencyMs: Date.now() - start,
        success: false,
        errorCode: (err as any)?.code ?? 'UNKNOWN',
        errorMessage: sanitizeErrorMessage((err as Error).message),
      });
      throw err;
    }
  }

  async sendMessageSync(request: MessageRequest): Promise<MessageResponse> {
    const start = Date.now();
    this.diagnostics.emit({
      type: 'provider:call', timestamp: start,
      providerName: this.context.providerName,
      model: this.context.model,
      agentType: this.context.agentType,
      projectId: this.context.projectId,
      engineId: this.context.engineId,
      taskId: this.context.taskId,
      taskType: this.context.taskType,
    });
    try {
      const response = await this.inner.sendMessageSync(request);
      this.diagnostics.emit({
        type: 'provider:complete', timestamp: Date.now(),
        providerName: this.context.providerName,
        model: this.context.model,
        agentType: this.context.agentType,
        projectId: this.context.projectId,
        engineId: this.context.engineId,
        taskId: this.context.taskId,
        taskType: this.context.taskType,
        latencyMs: Date.now() - start, success: true,
        tokens: { input: response.usage.inputTokens, output: response.usage.outputTokens },
      });
      return response;
    } catch (err) {
      this.diagnostics.emit({
        type: 'provider:error', timestamp: Date.now(),
        providerName: this.context.providerName,
        model: this.context.model,
        agentType: this.context.agentType,
        projectId: this.context.projectId,
        engineId: this.context.engineId,
        taskId: this.context.taskId,
        taskType: this.context.taskType,
        latencyMs: Date.now() - start, success: false,
        errorCode: (err as any)?.code ?? 'UNKNOWN',
        errorMessage: sanitizeErrorMessage((err as Error).message),
      });
      throw err;
    }
  }

  getCapabilities(): ProviderCapabilities {
    return this.inner.getCapabilities();
  }

  async getModels(): Promise<ModelInfo[]> {
    return this.inner.getModels();
  }

  // NOTE: No isAvailable() -- IAIProvider does not define it.
  async dispose(): Promise<void> { return this.inner.dispose(); }
}
```

**Architecture diagram:**

```
                   +---------------------------+
  MCP tool call -->| ToolHookRegistry          |  (mcp-client -- interceptors only)
                   | .runPreInterceptors()     |
                   | .runPostInterceptors()    |
                   +---------------------------+
                              |
                         emit() to shared queue
                              v
                   +---------------------------+
  Provider call -->| DiagnosticsQueue          |  (packages/shared/src/telemetry/)
  LLM API call --->|     .emit() (sync)        |
                   | drainPromise guard         |
                   | droppedCount tracking      |
                   +---------------------------+
                              |
                        drain (every 5s)
                              v
                   +---------------------------+
                   | DiagnosticsProcessor      |-->  costTracker.recordUsage()
                   |   (single instance)       |-->  mapProviderName() validation
                   +---------------------------+
```

## What Gets Recorded

Every call records a `UsageRecordInput` with:
- `provider` + `model` = which provider config was used (validated via `mapProviderName()`)
- `agentType` = which role initiated (typed as `AgentType` from `@tamma/shared`)
- `projectId` + `engineId` = execution context (required)
- `taskId` + `taskType` = task context required by `UsageRecordInput` (required on DiagnosticsEvent)
- `inputTokens`, `outputTokens` = from `response.usage` or estimated
- `latencyMs` = wall clock time
- `success` = did it succeed
- `errorCode` = typed as `DiagnosticsErrorCode`: RATE_LIMIT_EXCEEDED, QUOTA_EXCEEDED, TIMEOUT, AUTH_FAILED, NETWORK_ERROR, TASK_FAILED, UNKNOWN
- `errorMessage` = sanitized (max 500 chars, API key patterns stripped via `sanitizeErrorMessage()`)
- `totalCostUsd` = calculated by cost-monitor from pricing config

### Value Validation

All numeric fields are validated before recording:
- `costUsd` >= 0
- Token counts >= 0 and <= 10,000,000
- `errorCode` truncated to 100 characters

### AgentTaskResult Token Exposure

**Recommendation:** `AgentTaskResult` (in `packages/shared/src/types/index.ts`) should include an optional `tokens?: { input: number; output: number }` field so that `InstrumentedAgentProvider` can access and record token counts. This enables full token tracking for agent-based providers alongside LLM providers.

## Fixes to `cost-monitor/src/types.ts`

1. Add missing providers to `Provider` type:

```typescript
export type Provider =
  | 'anthropic'
  | 'openai'
  | 'openrouter'
  | 'google'
  | 'local'
  | 'claude-code'
  | 'opencode'           // NEW
  | 'z-ai'               // NEW
  | 'zen-mcp';           // NEW
```

2. Replace local `AgentType` with import from `@tamma/shared` to avoid divergence:

```typescript
import type { AgentType } from '@tamma/shared';
// DELETE the local AgentType definition
export type { AgentType };
```

## Files
- CREATE `packages/shared/src/telemetry/diagnostics-event.ts` -- `DiagnosticsEvent`, `DiagnosticsEventType`, `DiagnosticsErrorCode`, discriminated union types
- CREATE `packages/shared/src/telemetry/diagnostics-queue.ts` -- `DiagnosticsQueue` class with drainPromise guard, setProcessor guard, droppedCount
- CREATE `packages/shared/src/telemetry/sanitize-error.ts` -- `sanitizeErrorMessage()` utility
- CREATE `packages/shared/src/telemetry/validate-diagnostics.ts` -- `validateCostUsd()`, `validateTokenCount()`, `validateErrorCode()` utilities
- CREATE `packages/shared/src/telemetry/index.ts` (barrel export)
- CREATE `packages/providers/src/instrumented-agent-provider.ts`
- CREATE `packages/providers/src/instrumented-llm-provider.ts` -- implements full `IAIProvider` interface
- CREATE `packages/providers/src/provider-name-mapping.ts` -- `mapProviderName()` safe validation
- CREATE `packages/providers/src/instrumented-agent-provider.test.ts`
- CREATE `packages/providers/src/instrumented-llm-provider.test.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-event.test.ts`
- CREATE `packages/shared/src/telemetry/sanitize-error.test.ts`
- MODIFY `packages/providers/package.json` -- add `@tamma/cost-monitor` dep
- MODIFY `packages/cost-monitor/src/types.ts` -- add `'opencode' | 'z-ai' | 'zen-mcp'` to `Provider`; import `AgentType` from `@tamma/shared` instead of redefining
- MODIFY `packages/shared/src/index.ts` -- export telemetry barrel
- MODIFY `packages/shared/src/types/index.ts` -- add optional `tokens` field to `AgentTaskResult` (recommendation)

## Verify
- Test: mock inner provider, verify `diagnostics.emit()` called with correct `DiagnosticsEvent` (including `projectId`, `engineId`, `taskId`, `taskType`, and `agentType` typed as `AgentType`)
- Test: `emit()` is synchronous -- `executeTask` returns before queue drains
- Test: error calls emit `provider:error` event with typed `DiagnosticsErrorCode`
- Test: `errorMessage` is sanitized via `sanitizeErrorMessage()` (max 500 chars, API keys stripped)
- Test: latencyMs measured correctly
- Test: `InstrumentedLLMProvider` implements full `IAIProvider` interface (initialize, sendMessage, sendMessageSync, getCapabilities, getModels, dispose)
- Test: `InstrumentedLLMProvider` does NOT expose `isAvailable()` (compile-time check)
- Test: `sendMessage()` streaming instrumentation wraps AsyncIterable and emits provider:complete on stream end
- Test: `sendMessage()` streaming instrumentation emits provider:error on stream failure
- Test: processor maps `DiagnosticsEvent` to `UsageRecordInput` and calls `costTracker.recordUsage()`
- Test: processor uses `mapProviderName()` instead of unsafe `as Provider` cast
- Test: processor failure does NOT affect queued events (uses structured error logging, not silent swallow)
- Test: `setProcessor()` logs warning if processor already set
- Test: `drainPromise` guard prevents concurrent drain from timer + dispose() racing
- Test: `getDroppedCount()` returns count of dropped events on overflow
- Test: `InstrumentedAgentProvider.updateContext()` updates taskId/taskType for subsequent calls
- Test: value validation: costUsd >= 0, token counts in [0, 10_000_000], errorCode max 100 chars
- Test: discriminated union: `ToolDiagnosticsEvent` has toolName/serverName/args, `ProviderDiagnosticsEvent` has providerName/model

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
