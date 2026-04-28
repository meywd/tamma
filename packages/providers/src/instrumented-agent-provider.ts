/**
 * Instrumented Agent Provider Decorator
 *
 * Wraps any IAgentProvider to emit DiagnosticsEvent objects to the
 * shared DiagnosticsQueue. Intercepts executeTask() to emit:
 *   - provider:call on entry
 *   - provider:complete on success
 *   - provider:error on failure
 *
 * Delegates isAvailable() and dispose() directly to the inner provider
 * without instrumentation.
 */

import type { AgentType, AgentTaskResult } from '@tamma/shared';
import type { DiagnosticsQueue } from '@tamma/shared/telemetry';
import type { DiagnosticsErrorCode } from '@tamma/shared/telemetry';
import { sanitizeErrorMessage } from '@tamma/shared/telemetry';
import type {
  IAgentProvider,
  AgentTaskConfig,
  AgentProgressCallback,
} from './agent-types.js';

/**
 * Context required by InstrumentedAgentProvider to populate diagnostics events.
 * All fields are required to match Story 9-11 canonical design.
 */
export interface InstrumentedAgentContext {
  /** Provider name identifier */
  providerName: string;
  /** Model identifier */
  model: string;
  /** Agent role type (typed as AgentType, not string) */
  agentType: AgentType;
  /** Project identifier */
  projectId: string;
  /** Engine run identifier */
  engineId: string;
  /** Task identifier */
  taskId: string;
  /** Task type classification */
  taskType: string;
}

/**
 * Decorator that wraps any IAgentProvider and emits diagnostics events
 * to a shared DiagnosticsQueue for telemetry tracking.
 *
 * The decorator intercepts executeTask() to record:
 * - Timing (latencyMs)
 * - Success/failure status
 * - Cost (costUsd from AgentTaskResult)
 * - Token counts (from AgentTaskResult.tokens when available)
 * - Error codes (typed as DiagnosticsErrorCode)
 * - Sanitized error messages (API keys stripped, truncated)
 *
 * The updateContext() method allows changing taskId/taskType between
 * calls without creating a new instance.
 */
export class InstrumentedAgentProvider implements IAgentProvider {
  private context: InstrumentedAgentContext;

  constructor(
    private readonly inner: IAgentProvider,
    private readonly diagnostics: DiagnosticsQueue,
    context: InstrumentedAgentContext,
  ) {
    this.context = { ...context };
  }

  /**
   * Update mutable context fields (taskId, taskType) between calls.
   * This avoids needing a new InstrumentedAgentProvider instance per task.
   */
  updateContext(
    updates: Partial<Pick<InstrumentedAgentContext, 'taskId' | 'taskType'>>,
  ): void {
    if (updates.taskId !== undefined) {
      this.context.taskId = updates.taskId;
    }
    if (updates.taskType !== undefined) {
      this.context.taskType = updates.taskType;
    }
  }

  /**
   * Execute a task on the inner provider with full diagnostics instrumentation.
   *
   * Emits provider:call before the inner call, provider:complete on success,
   * and provider:error on failure. The onProgress callback is forwarded to
   * the inner provider unchanged.
   */
  async executeTask(
    config: AgentTaskConfig,
    onProgress?: AgentProgressCallback,
  ): Promise<AgentTaskResult> {
    // Emit provider:call before inner execution
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

      // Build the complete event, conditionally adding optional fields
      // to comply with exactOptionalPropertyTypes
      const completeEvent: Parameters<DiagnosticsQueue['emit']>[0] = {
        type: 'provider:complete' as const,
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
      };

      // Access result.tokens if available (AgentTaskResult may include
      // optional tokens field for diagnostics token tracking)
      const tokens = (result as unknown as Record<string, unknown>)['tokens'] as
        | { input: number; output: number }
        | undefined;
      if (tokens !== undefined) {
        completeEvent.tokens = tokens;
      }

      // Set errorCode when result indicates task failure (not an exception)
      if (result.error) {
        completeEvent.errorCode = 'TASK_FAILED';
      }

      this.diagnostics.emit(completeEvent);

      return result;
    } catch (err) {
      const errRecord = err as Record<string, unknown>;
      const errorCode: DiagnosticsErrorCode =
        (errRecord?.['code'] as DiagnosticsErrorCode | undefined) ?? 'UNKNOWN';

      // Extract error message safely for non-Error thrown values
      const rawMessage = err instanceof Error
        ? err.message
        : typeof err === 'string'
          ? err
          : String(err);

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
        errorMessage: sanitizeErrorMessage(rawMessage),
      });

      throw err;
    }
  }

  /**
   * Delegate isAvailable() to inner provider without emitting events.
   */
  async isAvailable(): Promise<boolean> {
    return this.inner.isAvailable();
  }

  /**
   * Delegate dispose() to inner provider without emitting events.
   */
  async dispose(): Promise<void> {
    return this.inner.dispose();
  }
}
