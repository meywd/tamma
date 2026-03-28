# Story 11: Diagnostics Queue & MCP Tool Interceptors

## Goal
Split telemetry and tool interception into two separate concerns in two separate packages:

1. **`DiagnosticsQueue`** in `packages/shared/src/telemetry/` — the event queue + drain + processor. Used by providers AND mcp-client. No dependency on mcp-client.
2. **`ToolInterceptorChain`** in `packages/mcp-client/src/interceptors.ts` — blocking pre/post interceptors for MCP tools. No queue, no processor.

Bridge MCPClient's existing `EventEmitter` (already emits `tool:invoked` at client.ts line 315 and `tool:completed` at line 354) to `DiagnosticsQueue` instead of adding redundant observation.

## Design — Part A: DiagnosticsQueue (`@tamma/shared`)

**New files in `packages/shared/src/telemetry/`:**

| File | Exports |
|------|---------|
| `diagnostics-event.ts` | `DiagnosticsEvent`, `DiagnosticsEventType`, `DiagnosticsErrorCode`, `DiagnosticsEventBase`, `ToolDiagnosticsEvent`, `ProviderDiagnosticsEvent` |
| `diagnostics-queue.ts` | `DiagnosticsQueue` class, `IDiagnosticsQueue`, `DiagnosticsQueueLogger`, `DiagnosticsEventProcessor` |
| `diagnostics-processor.ts` | `IDiagnosticsProcessor`, `createDiagnosticsProcessor()` |
| `index.ts` | Barrel export |

**`diagnostics-event.ts`:**
```typescript
import type { AgentType } from '../types/knowledge.js';

export type DiagnosticsEventType =
  | 'tool:invoke' | 'tool:complete' | 'tool:error'
  | 'provider:call' | 'provider:complete' | 'provider:error';

export type DiagnosticsErrorCode =
  | 'RATE_LIMIT_EXCEEDED' | 'QUOTA_EXCEEDED' | 'AUTH_FAILED'
  | 'TIMEOUT' | 'NETWORK_ERROR' | 'TASK_FAILED' | 'UNKNOWN';

interface DiagnosticsEventBase {
  type: DiagnosticsEventType;
  timestamp: number;
  correlationId?: string;       // crypto.randomUUID() for start/end event pairing

  // Context — optional, populated by bridge wiring from runtime context
  agentType?: AgentType;
  projectId?: string;
  engineId?: string;
  taskId?: string;
  taskType?: string;

  // Outcome (populated for complete/error events)
  latencyMs?: number;
  success?: boolean;
  costUsd?: number;
  errorCode?: DiagnosticsErrorCode;
  errorMessage?: string;
}

interface ToolDiagnosticsEvent extends DiagnosticsEventBase {
  type: 'tool:invoke' | 'tool:complete' | 'tool:error';
  toolName: string;         // required for tool events
  serverName?: string;
  args?: Record<string, unknown>;
}

interface ProviderDiagnosticsEvent extends DiagnosticsEventBase {
  type: 'provider:call' | 'provider:complete' | 'provider:error';
  providerName: string;     // required for provider events
  model?: string;
  tokens?: { input: number; output: number };
}

export type DiagnosticsEvent = ToolDiagnosticsEvent | ProviderDiagnosticsEvent;
```

> **Design Note (F01):** The discriminated union approach (splitting into `ToolDiagnosticsEvent` and `ProviderDiagnosticsEvent`) aligns with Story 9-2's pattern. This ensures `toolName` is required for tool events and `providerName` is required for provider events, enabling exhaustive type checking at compile time.

> **Design Note (F15):** The `correlationId` field on `DiagnosticsEventBase` enables pairing of start/end events. The bridge wiring should generate a `crypto.randomUUID()` correlation ID before the tool call and include it in both the start (`tool:invoke`) and end (`tool:complete`/`tool:error`) events.

**`diagnostics-queue.ts`:**
```typescript
export type DiagnosticsEventProcessor = (events: DiagnosticsEvent[]) => Promise<void>;

export interface DiagnosticsQueueLogger {
  warn(msg: string, context?: Record<string, unknown>): void;
  debug?(msg: string, context?: Record<string, unknown>): void;
}

export interface IDiagnosticsQueue {
  emit(event: DiagnosticsEvent): void;
  setProcessor(processor: DiagnosticsEventProcessor): void;
  dispose(): Promise<void>;
  getDroppedCount(): number;
}

export class DiagnosticsQueue implements IDiagnosticsQueue {
  private queue: DiagnosticsEvent[] = [];
  private processor: DiagnosticsEventProcessor | null = null;
  private drainTimer: ReturnType<typeof setInterval> | null = null;
  private drainPromise: Promise<void> | null = null;  // in-flight drain guard
  private readonly drainIntervalMs: number;
  private readonly maxQueueSize: number;
  private readonly logger?: DiagnosticsQueueLogger;
  private droppedCount: number = 0;

  constructor(options?: {
    drainIntervalMs?: number;   // default: 5000
    maxQueueSize?: number;      // default: 1000, oldest dropped when full
    logger?: DiagnosticsQueueLogger;
  }) {
    this.drainIntervalMs = options?.drainIntervalMs ?? 5000;
    this.maxQueueSize = options?.maxQueueSize ?? 1000;
    this.logger = options?.logger;
  }

  setProcessor(processor: DiagnosticsEventProcessor): void {
    this.processor = processor;
    if (!this.drainTimer) {
      this.drainTimer = setInterval(() => void this.drain(), this.drainIntervalMs);
      if (this.drainTimer.unref) this.drainTimer.unref();
    }
  }

  /** Synchronous push — zero overhead in the hot path */
  emit(event: DiagnosticsEvent): void {
    if (this.queue.length >= this.maxQueueSize) {
      this.queue.shift(); // drop oldest
      this.droppedCount++;
    }
    this.queue.push(event);
  }

  /** Returns the number of events dropped due to queue overflow */
  getDroppedCount(): number {
    return this.droppedCount;
  }

  /** Drain queue to processor. Guarded against concurrent execution. */
  private async drain(): Promise<void> {
    // If a drain is already in flight (from timer or dispose), wait for it
    if (this.drainPromise) {
      await this.drainPromise;
      return;
    }

    if (!this.processor || this.queue.length === 0) return;

    const batch = this.queue.splice(0);
    this.drainPromise = this.processor(batch)
      .catch((err) => {
        this.logger?.warn('Diagnostics processor drain failed', {
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
    // Drain until empty — events may arrive during drain
    let maxIterations = 10;
    while ((this.queue.length > 0 || this.drainPromise) && maxIterations-- > 0) {
      await this.drain();
    }
  }
}
```

Key design points:
- `drainPromise` guard prevents concurrent drain from timer + `dispose()` racing
- `emit()` is synchronous — never blocks the caller
- Timer uses `.unref()` so it does not keep the process alive
- `dispose()` re-drains in a loop (up to 10 iterations) to handle events arriving during drain
- `droppedCount` tracks events lost due to queue overflow (per Story 9-2)
- `logger` is passed via options (not as a separate positional param) for a cleaner API
- Events are delivered to processors in FIFO order; batching preserves insertion order

**`diagnostics-processor.ts`:**

> **Design Note (F03):** The `diagnostics-processor.ts` file imports from `@tamma/cost-monitor`. To avoid circular dependencies (`shared -> cost-monitor -> shared`), define an `IDiagnosticsProcessor` interface in `@tamma/shared` and implement the concrete processor in `@tamma/cost-monitor` or at the application level (CLI wiring). The `DiagnosticsQueue` and `DiagnosticsEvent` types remain in `@tamma/shared` since they have no external dependencies.

> **Design Note (F20):** Prerequisite: `@tamma/cost-monitor` must import `AgentType` from `@tamma/shared` (not redefine locally) before Story 9-11 implementation.

```typescript
import type { ICostTracker, UsageRecordInput } from '@tamma/cost-monitor';
import type { ILogger } from '../contracts.js';
import type { DiagnosticsEvent, DiagnosticsEventProcessor } from './diagnostics-queue.js';
import { mapProviderName, mapTaskType } from '@tamma/cost-monitor/provider-name-mapping.js';

export function createDiagnosticsProcessor(
  costTracker: ICostTracker,
  logger?: ILogger,
): DiagnosticsEventProcessor {
  return async (events: DiagnosticsEvent[]) => {
    for (const event of events) {
      // Only record completion and error events
      if (event.type !== 'tool:complete' && event.type !== 'provider:complete'
          && event.type !== 'tool:error' && event.type !== 'provider:error') {
        continue;
      }

      try {
        const providerName = event.type.startsWith('provider:')
          ? (event as { providerName: string }).providerName
          : 'claude-code';
        const model = event.type.startsWith('provider:')
          ? (event as { model?: string }).model ?? 'unknown'
          : 'unknown';
        const tokens = event.type.startsWith('provider:')
          ? (event as { tokens?: { input: number; output: number } }).tokens
          : undefined;

        const input: UsageRecordInput = {
          projectId: event.projectId ?? '',
          engineId: event.engineId ?? '',
          agentType: event.agentType ?? 'implementer',
          taskId: event.taskId ?? '',
          taskType: mapTaskType(event.taskType ?? 'implementation'),
          provider: mapProviderName(providerName),
          model,
          inputTokens: tokens?.input ?? 0,
          outputTokens: tokens?.output ?? 0,
          totalTokens: (tokens?.input ?? 0) + (tokens?.output ?? 0),
          latencyMs: event.latencyMs ?? 0,
          success: event.success ?? false,
          errorCode: event.errorCode,
        };
        await costTracker.recordUsage(input);
      } catch {
        logger?.warn('Diagnostics processor: failed to record', { type: event.type });
      }
    }
  };
}
```

> **Design Note (F08):** The processor uses `mapProviderName()` and `mapTaskType()` (from Story 9-2's `provider-name-mapping.ts`) instead of unsafe `as Provider` / `as TaskType` casts. These mapping functions validate the string and return a safe default if unrecognized.

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

## Design — Part B: ToolInterceptorChain (`@tamma/mcp-client`)

**New file: `packages/mcp-client/src/interceptors.ts`**

Blocking pre/post interceptors for MCP tool calls. No queue, no processor — purely synchronous transformation chain.

```typescript
import type { ToolResult } from './types.js';

export type PreInterceptor = (toolName: string, args: Record<string, unknown>) =>
  Promise<{ args: Record<string, unknown>; warnings: string[] }>;

export type PostInterceptor = (toolName: string, result: ToolResult) =>
  Promise<{ result: ToolResult; warnings: string[] }>;

export class ToolInterceptorChain {
  private preInterceptors: PreInterceptor[] = [];
  private postInterceptors: PostInterceptor[] = [];

  addPreInterceptor(fn: PreInterceptor): void {
    this.preInterceptors.push(fn);
  }

  addPostInterceptor(fn: PostInterceptor): void {
    this.postInterceptors.push(fn);
  }

  /** Blocking — awaits each interceptor in order, pipes args through */
  async runPre(toolName: string, args: Record<string, unknown>):
    Promise<{ args: Record<string, unknown>; warnings: string[] }> {
    let current = args;
    const warnings: string[] = [];
    for (const fn of this.preInterceptors) {
      try {
        const result = await fn(toolName, current);
        // Validate returned args for prototype pollution keys
        if (result.args && typeof result.args === 'object') {
          for (const key of ['__proto__', 'constructor', 'prototype']) {
            if (key in result.args) {
              delete result.args[key];
              warnings.push(`Prototype pollution key "${key}" removed from interceptor output`);
            }
          }
        }
        current = result.args;
        warnings.push(...result.warnings);
      } catch (err) {
        warnings.push(`Pre-interceptor failed: ${err instanceof Error ? err.message : String(err)}`);
        // Continue with unmodified args (fail-open for non-security interceptors)
      }
    }
    return { args: current, warnings };
  }

  /** Blocking — awaits each interceptor in order, pipes result through */
  async runPost(toolName: string, result: ToolResult):
    Promise<{ result: ToolResult; warnings: string[] }> {
    let current = result;
    const warnings: string[] = [];
    for (const fn of this.postInterceptors) {
      try {
        const out = await fn(toolName, current);
        current = out.result;
        warnings.push(...out.warnings);
      } catch (err) {
        warnings.push(`Post-interceptor failed: ${err instanceof Error ? err.message : String(err)}`);
        // Continue with unmodified result (fail-open for non-security interceptors)
      }
    }
    return { result: current, warnings };
  }
}

// Built-in interceptor factories

/**
 * Uses IContentSanitizer from Story 9-7 which returns { result: string; warnings: string[] }
 */
export function createSanitizationInterceptor(sanitizer: IContentSanitizer): PostInterceptor {
  return async (toolName: string, result: ToolResult) => {
    const warnings: string[] = [];
    const sanitizedContent = result.content.map(item => {
      if (item.type === 'text') {
        const { result: sanitized, warnings: w } = sanitizer.sanitize(item.text);
        warnings.push(...w);
        return { ...item, text: sanitized };
      }
      return item;
    });
    return { result: { ...result, content: sanitizedContent }, warnings };
  };
}

/**
 * Uses validateUrl() from Story 9-7 which returns { valid: boolean; warnings: string[] }
 */
export function createUrlValidationInterceptor(validateUrl: (url: string) => { valid: boolean; warnings: string[] }): PreInterceptor {
  return async (toolName: string, args: Record<string, unknown>) => {
    const warnings: string[] = [];
    for (const [key, value] of Object.entries(args)) {
      if (typeof value === 'string' && (value.includes('://') || value.startsWith('http'))) {
        const { valid, warnings: w } = validateUrl(value);
        warnings.push(...w);
        if (!valid) {
          warnings.push(`URL blocked by policy: ${value}`);
        }
      }
    }
    return { args, warnings };
  };
}
```

> **Design Note (F09):** Interceptor errors are isolated with try/catch per interceptor in `runPre()` and `runPost()`. Security-critical interceptors (sanitization) should use fail-closed behavior where the tool call is aborted. Non-security interceptors (diagnostics, URL warnings) use fail-open. The chain itself always continues.

> **Design Note (F10):** `createSanitizationInterceptor` uses `IContentSanitizer` (with `I` prefix) from Story 9-7, which returns `{ result: string; warnings: string[] }` instead of a plain string. The local `ContentSanitizer` interface is removed in favor of the shared one.

> **Design Note (F11):** `createUrlValidationInterceptor` aligns with Story 9-7's `validateUrl` function which returns `{ valid: boolean; warnings: string[] }`. A separate `UrlValidator` interface is not needed; the function signature is used directly.

> **Design Note (F16):** Interceptor return values are validated for prototype pollution keys (`__proto__`, `constructor`, `prototype`). The chain removes these forbidden keys from returned args objects and emits a warning.

## Design — Part C: Bridge MCPClient events to DiagnosticsQueue

MCPClient already emits `tool:invoked` (line 315) and `tool:completed` (line 354) via its internal `EventEmitter`. Instead of modifying `invokeTool()`, bridge these existing events to the `DiagnosticsQueue`:

> **Design Note (F06):** Tool arguments stored in `DiagnosticsEvent.args` MUST be truncated to prevent memory exhaustion. Apply `truncateArgs(data.args, MAX_DIAGNOSTICS_ARG_SIZE)` (default: 10KB serialized) before queueing. The `truncateArgs` helper performs `JSON.stringify` and truncates values exceeding the limit.

> **Design Note (F07):** All `errorMessage` values MUST be sanitized via `sanitizeErrorMessage()` (from Story 9-2) before storing in DiagnosticsEvent. Tool arguments MUST have known-sensitive keys (`password`, `token`, `apiKey`, `secret`, `authorization`) redacted before queueing.

```typescript
import { randomUUID } from 'node:crypto';
import { truncateArgs, redactSensitiveKeys } from '@tamma/shared/telemetry/utils.js';
import { sanitizeErrorMessage } from '@tamma/shared/telemetry/sanitize.js';

const MAX_DIAGNOSTICS_ARG_SIZE = 10_240; // 10KB
const SENSITIVE_KEYS = ['password', 'token', 'apiKey', 'secret', 'authorization'];

// In the CLI wiring (start.tsx / server.ts), after creating mcpClient and diagnosticsQueue:

mcpClient.on('tool:invoked', (data: { serverName: string; toolName: string; args: Record<string, unknown> }) => {
  const correlationId = randomUUID();
  // Store correlationId for pairing with completion event (e.g., in a Map keyed by serverName+toolName)
  pendingCorrelations.set(`${data.serverName}:${data.toolName}`, correlationId);

  diagnosticsQueue.emit({
    type: 'tool:invoke',
    timestamp: Date.now(),
    correlationId,
    serverName: data.serverName,
    toolName: data.toolName,
    args: truncateArgs(redactSensitiveKeys(data.args, SENSITIVE_KEYS), MAX_DIAGNOSTICS_ARG_SIZE),
    agentType: currentAgentType,
    projectId,
    engineId,
    taskId: currentTaskId,
    taskType: currentTaskType,
  });
});

mcpClient.on('tool:completed', (data: { serverName: string; toolName: string; success: boolean; latencyMs: number; errorMessage?: string }) => {
  const correlationId = pendingCorrelations.get(`${data.serverName}:${data.toolName}`);
  pendingCorrelations.delete(`${data.serverName}:${data.toolName}`);

  diagnosticsQueue.emit({
    type: data.success ? 'tool:complete' : 'tool:error',
    timestamp: Date.now(),
    correlationId,
    serverName: data.serverName,
    toolName: data.toolName,
    latencyMs: data.latencyMs,
    success: data.success,
    errorMessage: data.errorMessage ? sanitizeErrorMessage(data.errorMessage) : undefined,
    agentType: currentAgentType,
    projectId,
    engineId,
    taskId: currentTaskId,
    taskType: currentTaskType,
  });
});
```

For blocking interceptors, modify `invokeTool()` to accept an optional `ToolInterceptorChain`. The actual `invokeTool` signature is `(serverName: string, toolName: string, args: Record<string, unknown>, options?: ToolInvocationOptions)` -- four parameters, not two:

> **Design Note (F04):** Either add `setInterceptorChain(chain: ToolInterceptorChain): void` to the `IMCPClient` interface, or accept the interceptor chain in `MCPClientOptions` at construction time. The preferred approach is adding it to `MCPClientOptions` to avoid mutability concerns. The current spec uses a setter for backward compatibility, but the constructor option is the recommended path for new integrations.

```typescript
// Inside invokeTool(), after argument validation and before execution:
async invokeTool(
  serverName: string,
  toolName: string,
  args: Record<string, unknown>,
  options?: ToolInvocationOptions
): Promise<ToolResult> {
  // ... existing validation ...

  // Blocking: run pre-interceptors (sanitize/validate args)
  let finalArgs = argsWithDefaults;
  if (this.interceptorChain) {
    const { args: intercepted, warnings: preWarnings } = await this.interceptorChain.runPre(toolName, argsWithDefaults);
    finalArgs = intercepted;
    if (preWarnings.length > 0) {
      this.logger?.warn('Pre-interceptor warnings', { toolName, warnings: preWarnings });
    }
  }

  // ... existing emit('tool:invoked') ...

  // ... execute with retry using finalArgs ...

  // Blocking: run post-interceptors (sanitize result)
  let finalResult = toolResult;
  if (this.interceptorChain) {
    const { result: intercepted, warnings: postWarnings } = await this.interceptorChain.runPost(toolName, toolResult);
    finalResult = intercepted;
    if (postWarnings.length > 0) {
      this.logger?.warn('Post-interceptor warnings', { toolName, warnings: postWarnings });
    }
  }

  // ... existing emit('tool:completed') ...

  return finalResult;
}
```

> **Design Note (F14):** Interceptor warnings are logged via `this.logger?.warn()` rather than being silently discarded. This ensures visibility into sanitization actions, blocked URLs, and interceptor failures without requiring callers to handle warnings.

## Files

**Part A (DiagnosticsQueue in `@tamma/shared`):**
- CREATE `packages/shared/src/telemetry/diagnostics-event.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-queue.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-processor.ts`
- CREATE `packages/shared/src/telemetry/index.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-queue.test.ts`
- CREATE `packages/shared/src/telemetry/diagnostics-processor.test.ts`
- MODIFY `packages/shared/src/index.ts` -- export telemetry barrel
- MODIFY `packages/shared/package.json` -- add `./telemetry` entry to the exports map (F13)

**Part B (ToolInterceptorChain in `@tamma/mcp-client`):**
- CREATE `packages/mcp-client/src/interceptors.ts`
- CREATE `packages/mcp-client/src/interceptors.test.ts`
- MODIFY `packages/mcp-client/src/client.ts` — add interceptor chain support to `invokeTool()`
- MODIFY `packages/mcp-client/src/index.ts` — export interceptors

## Verify

**DiagnosticsQueue:**
- Test: `emit()` is synchronous -- returns before queue drains
- Test: queue drains to processor on interval (5s default)
- Test: `drainPromise` guard prevents concurrent drain from timer + dispose
- Test: processor error does NOT affect queued events
- Test: queue drops oldest when full (`maxQueueSize`)
- Test: `dispose()` stops timer then flushes remaining events (re-drains until empty)
- Test: timer uses `.unref()` (does not keep process alive)
- Test: `createDiagnosticsProcessor` maps `DiagnosticsEvent` to `UsageRecordInput` with all required fields (projectId, engineId, taskId, taskType, agentType)
- Test: `DiagnosticsEvent.agentType` is typed as `AgentType` from `@tamma/shared`, not `string`
- Test: events are delivered to processors in FIFO order; batching preserves insertion order (F17)
- Test: `getDroppedCount()` returns the number of events dropped due to queue overflow (F02)
- Test: `drain()` logs structured warning on processor failure instead of swallowing silently (F05)

**ToolInterceptorChain:**
- Test: pre-interceptor modifies args (blocking)
- Test: post-interceptor sanitizes result (blocking)
- Test: interceptors run in order, piped
- Test: empty chain is a no-op passthrough
- Test: `createSanitizationInterceptor` produces a valid `PostInterceptor` (uses `IContentSanitizer`)
- Test: `createUrlValidationInterceptor` produces a valid `PreInterceptor` (uses `validateUrl`)
- Test: `invokeTool` calls `runPre` before execution and `runPost` after
- Test: interceptor errors are caught per-interceptor and chain continues (fail-open) (F09)
- Test: prototype pollution keys are stripped from interceptor return values (F16)
- Test: interceptor warnings are logged via `this.logger?.warn()` in `invokeTool()` (F14)

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
