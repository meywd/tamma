/**
 * Diagnostics Processor
 *
 * Maps DiagnosticsEvent completion/error events to cost tracking records.
 * Uses dependency injection for the cost tracker and mapping functions
 * to avoid circular dependencies between @tamma/shared and @tamma/cost-monitor.
 *
 * The concrete wiring happens at the application level where both packages
 * are available (e.g., CLI start.tsx or orchestrator server.ts).
 */

import type { ILogger } from '../contracts/index.js';
import type {
  DiagnosticsEvent,
  ProviderDiagnosticsEvent,
} from './diagnostics-event.js';
import type { DiagnosticsEventProcessor } from './diagnostics-queue.js';

// --- Dependency Injection Types ---
// These mirror @tamma/cost-monitor types without creating a runtime import.
// The caller provides concrete implementations at wiring time.

/**
 * Minimal cost tracker interface for recording usage.
 * Mirrors ICostTracker.recordUsage() from @tamma/cost-monitor.
 */
export interface IDiagnosticsCostTracker {
  recordUsage(usage: DiagnosticsUsageRecordInput): Promise<unknown>;
}

/**
 * Usage record input for cost tracking.
 * Mirrors UsageRecordInput from @tamma/cost-monitor (subset of fields used).
 */
export interface DiagnosticsUsageRecordInput {
  projectId: string;
  engineId: string;
  agentType: string;
  taskId: string;
  taskType: string;
  provider: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  latencyMs: number;
  success: boolean;
  errorCode?: string;
}

/**
 * Provider name mapping function type.
 * Validates a provider name string and returns a safe value.
 */
export type ProviderNameMapper = (name: string) => string;

/**
 * Task type mapping function type.
 * Validates a task type string and returns a safe value.
 */
export type TaskTypeMapper = (taskType: string) => string;

// --- IDiagnosticsProcessor interface (F03) ---

/**
 * Interface for diagnostics processors.
 * Defined in @tamma/shared to avoid circular dependencies.
 * Concrete implementations live at the app level or in @tamma/cost-monitor.
 */
export interface IDiagnosticsProcessor {
  /** Process a batch of diagnostics events */
  process(events: DiagnosticsEvent[]): Promise<void>;
}

// --- Persistent Store Interface (optional) ---

/**
 * Minimal persistent diagnostics store interface for the processor.
 * Mirrors IDiagnosticsStore.insert() from @tamma/api without creating
 * a runtime import (avoids circular dependency).
 */
export interface IDiagnosticsPersistentStore {
  /** Insert a batch of diagnostics record inputs. Returns count inserted. */
  insert(records: ReadonlyArray<{
    eventType: string;
    providerName: string;
    model?: string | null;
    agentType?: string | null;
    projectId?: string | null;
    engineId?: string | null;
    taskId?: string | null;
    taskType?: string | null;
    inputTokens?: number;
    outputTokens?: number;
    latencyMs?: number;
    costUsd?: number;
    success: boolean;
    errorCode?: string | null;
    errorMessage?: string | null;
  }>): Promise<number>;
}

// --- Processor Options ---

/**
 * Options for creating a diagnostics processor.
 */
export interface DiagnosticsProcessorOptions {
  /** Cost tracker to record usage */
  costTracker: IDiagnosticsCostTracker;
  /** Maps raw provider name strings to validated provider identifiers (F08) */
  mapProviderName: ProviderNameMapper;
  /** Maps raw task type strings to validated task type identifiers (F08) */
  mapTaskType: TaskTypeMapper;
  /** Optional logger for warning on per-event errors */
  logger?: ILogger;
  /** Optional persistent diagnostics store for API-backed mode. */
  diagnosticsStore?: IDiagnosticsPersistentStore;
}

// --- Event type guards ---

const COMPLETION_EVENT_TYPES = new Set([
  'tool:complete',
  'tool:error',
  'provider:complete',
  'provider:error',
]);

function _isProviderEvent(event: DiagnosticsEvent): event is ProviderDiagnosticsEvent {
  return (
    event.type === 'provider:call' ||
    event.type === 'provider:complete' ||
    event.type === 'provider:error'
  );
}

/**
 * Creates a DiagnosticsEventProcessor that maps completion/error events
 * to cost tracking records via an injected cost tracker.
 *
 * Only processes `tool:complete`, `tool:error`, `provider:complete`,
 * and `provider:error` events. Skips `tool:invoke` and `provider:call`.
 *
 * Uses `mapProviderName()` and `mapTaskType()` for safe string validation
 * instead of unsafe type casts (F08).
 *
 * Per-event errors are caught and logged as warnings (not thrown),
 * so a single bad event does not prevent processing of the entire batch.
 *
 * @param options - Processor configuration with injected dependencies
 * @returns A DiagnosticsEventProcessor function for use with DiagnosticsQueue
 */
export function createDiagnosticsProcessor(
  options: DiagnosticsProcessorOptions,
): DiagnosticsEventProcessor {
  const { costTracker, mapProviderName, mapTaskType, logger, diagnosticsStore } = options;

  return async (events: DiagnosticsEvent[]): Promise<void> => {
    // Build persistent store records in parallel with cost tracker writes
    const storeRecords: Array<{
      eventType: string;
      providerName: string;
      model?: string | null;
      agentType?: string | null;
      projectId?: string | null;
      engineId?: string | null;
      taskId?: string | null;
      taskType?: string | null;
      inputTokens?: number;
      outputTokens?: number;
      latencyMs?: number;
      success: boolean;
      errorCode?: string | null;
    }> = [];

    for (const event of events) {
      // Only record completion and error events
      if (!COMPLETION_EVENT_TYPES.has(event.type)) {
        continue;
      }

      try {
        // Extract provider-specific fields using discriminated union (F01)
        let providerName = 'claude-code';
        let model = 'unknown';
        let tokens: { input: number; output: number } | undefined;

        if (_isProviderEvent(event)) {
          providerName = event.providerName;
          model = event.model ?? 'unknown';
          tokens = event.tokens;
        }

        // Construct usage record input per mapping table (F19)
        const input: DiagnosticsUsageRecordInput = {
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
        };

        // Conditionally assign errorCode to satisfy exactOptionalPropertyTypes
        if (event.errorCode !== undefined) {
          input.errorCode = event.errorCode;
        }

        await costTracker.recordUsage(input);

        // Collect record for persistent store batch write
        if (diagnosticsStore) {
          const record: typeof storeRecords[number] = {
            eventType: event.type,
            providerName: mapProviderName(providerName),
            model,
            agentType: event.agentType ?? null,
            projectId: event.projectId ?? null,
            engineId: event.engineId ?? null,
            taskId: event.taskId ?? null,
            taskType: event.taskType ?? null,
            inputTokens: tokens?.input ?? 0,
            outputTokens: tokens?.output ?? 0,
            latencyMs: event.latencyMs ?? 0,
            success: event.success ?? false,
          };
          if (event.errorCode !== undefined) {
            record.errorCode = event.errorCode;
          }
          storeRecords.push(record);
        }
      } catch (err: unknown) {
        logger?.warn('Diagnostics processor: failed to record usage', {
          type: event.type,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    // Batch write to persistent store if available
    if (diagnosticsStore && storeRecords.length > 0) {
      try {
        await diagnosticsStore.insert(storeRecords);
      } catch (err: unknown) {
        logger?.warn('Diagnostics processor: failed to write to persistent store', {
          count: storeRecords.length,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }
  };
}
