/**
 * Instrumented LLM Provider Decorator
 *
 * Wraps any IAIProvider to emit DiagnosticsEvent objects to the
 * shared DiagnosticsQueue. Implements the full IAIProvider interface
 * as a drop-in replacement. Instruments both sendMessageSync() and
 * sendMessage() (streaming) with diagnostics events:
 *   - provider:call on entry
 *   - provider:complete on success / stream completion
 *   - provider:error on failure / stream error
 *
 * Delegates initialize(), getCapabilities(), getModels(), and dispose()
 * directly to the inner provider without instrumentation.
 *
 * SECURITY: This class has access to prompt content via MessageRequest.
 * It must NOT log or store prompt text. Only metadata (tokens, latency,
 * cost, error codes) is recorded in diagnostics events.
 */

import type { AgentType } from '@tamma/shared';
import type { DiagnosticsQueue } from '@tamma/shared/telemetry';
import type { DiagnosticsErrorCode } from '@tamma/shared/telemetry';
import { sanitizeErrorMessage } from '@tamma/shared/telemetry';
import type {
  IAIProvider,
  MessageRequest,
  MessageResponse,
  MessageChunk,
  ProviderConfig,
  ProviderCapabilities,
  ModelInfo,
  StreamOptions,
} from './types.js';

/**
 * Context required by InstrumentedLLMProvider to populate diagnostics events.
 * All fields are required to match InstrumentedAgentContext shape.
 */
export interface InstrumentedLLMContext {
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
 * Instrumented wrapper for IAIProvider that implements the full interface.
 *
 * SECURITY: This class has access to prompt content via MessageRequest.
 * It must NOT log or store prompt text. Only metadata (tokens, latency,
 * cost, error codes) is recorded in diagnostics events. Emitted events
 * contain only providerName, model, agentType, projectId, engineId,
 * taskId, taskType, latencyMs, success, tokens, errorCode, and errorMessage.
 * No MessageRequest content is ever included.
 */
export class InstrumentedLLMProvider implements IAIProvider {
  private readonly context: InstrumentedLLMContext;

  constructor(
    private readonly inner: IAIProvider,
    private readonly diagnostics: DiagnosticsQueue,
    context: InstrumentedLLMContext,
  ) {
    // Copy context to prevent external mutation
    this.context = { ...context };
  }

  /**
   * Delegate initialize() to inner provider without instrumentation.
   */
  async initialize(config: ProviderConfig): Promise<void> {
    return this.inner.initialize(config);
  }

  /**
   * Instrumented streaming sendMessage.
   *
   * Wraps the AsyncIterable<MessageChunk> returned by the inner provider
   * to track tokens incrementally from chunk.usage and emit diagnostics
   * events on stream completion or error.
   *
   * SECURITY: This method receives MessageRequest containing prompt content.
   * The request content is forwarded to the inner provider but is NEVER
   * included in emitted diagnostics events.
   */
  async sendMessage(
    request: MessageRequest,
    options?: StreamOptions,
  ): Promise<AsyncIterable<MessageChunk>> {
    const start = Date.now();

    // Emit provider:call before calling inner
    this.diagnostics.emit({
      type: 'provider:call',
      timestamp: start,
      providerName: this.context.providerName,
      model: this.context.model,
      agentType: this.context.agentType,
      projectId: this.context.projectId,
      engineId: this.context.engineId,
      taskId: this.context.taskId,
      taskType: this.context.taskType,
    });

    let stream: AsyncIterable<MessageChunk>;
    try {
      stream = await this.inner.sendMessage(request, options);
    } catch (err) {
      // inner.sendMessage() itself threw before returning a stream
      this._emitError(err, start);
      throw err;
    }

    // Wrap the stream to track tokens and emit completion event
    const diagnostics = this.diagnostics;
    const ctx = this.context;

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
                  type: 'provider:complete',
                  timestamp: Date.now(),
                  providerName: ctx.providerName,
                  model: ctx.model,
                  agentType: ctx.agentType,
                  projectId: ctx.projectId,
                  engineId: ctx.engineId,
                  taskId: ctx.taskId,
                  taskType: ctx.taskType,
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
              const errRecord = streamErr as Record<string, unknown>;
              const errorCode: DiagnosticsErrorCode =
                (errRecord?.['code'] as DiagnosticsErrorCode | undefined) ?? 'UNKNOWN';

              const rawMessage = streamErr instanceof Error
                ? streamErr.message
                : typeof streamErr === 'string'
                  ? streamErr
                  : String(streamErr);

              diagnostics.emit({
                type: 'provider:error',
                timestamp: Date.now(),
                providerName: ctx.providerName,
                model: ctx.model,
                agentType: ctx.agentType,
                projectId: ctx.projectId,
                engineId: ctx.engineId,
                taskId: ctx.taskId,
                taskType: ctx.taskType,
                latencyMs: Date.now() - start,
                success: false,
                errorCode,
                errorMessage: sanitizeErrorMessage(rawMessage),
              });
              throw streamErr;
            }
          },
        };
      },
    };

    return wrappedStream;
  }

  /**
   * Instrumented synchronous sendMessageSync.
   *
   * SECURITY: This method receives MessageRequest containing prompt content.
   * The request content is forwarded to the inner provider but is NEVER
   * included in emitted diagnostics events.
   */
  async sendMessageSync(request: MessageRequest): Promise<MessageResponse> {
    const start = Date.now();

    // Emit provider:call before inner execution
    this.diagnostics.emit({
      type: 'provider:call',
      timestamp: start,
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

      // Emit provider:complete with token usage from response
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
        success: true,
        tokens: {
          input: response.usage.inputTokens,
          output: response.usage.outputTokens,
        },
      });

      return response;
    } catch (err) {
      this._emitError(err, start);
      throw err;
    }
  }

  /**
   * Delegate getCapabilities() to inner provider without instrumentation.
   */
  getCapabilities(): ProviderCapabilities {
    return this.inner.getCapabilities();
  }

  /**
   * Delegate getModels() to inner provider without instrumentation.
   */
  async getModels(): Promise<ModelInfo[]> {
    return this.inner.getModels();
  }

  // NOTE: No isAvailable() -- IAIProvider does not define it.

  /**
   * Delegate dispose() to inner provider without instrumentation.
   */
  async dispose(): Promise<void> {
    return this.inner.dispose();
  }

  /**
   * Emit a provider:error diagnostics event for a caught error.
   * Extracts error code and sanitizes the error message.
   */
  private _emitError(err: unknown, start: number): void {
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
  }
}
