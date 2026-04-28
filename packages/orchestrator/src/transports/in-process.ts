/**
 * In-process engine transport.
 *
 * Zero-overhead wrapper that connects a UI layer directly to a TammaEngine
 * instance running in the same Node.js process. Uses the EventEmitter pattern
 * for push-based notifications (state changes, logs, approval requests, events).
 *
 * This transport is used by the CLI to drive the engine without any network
 * serialization overhead.
 */

import { EventEmitter } from 'node:events';
import type {
  IEngineTransport,
  EngineCommand,
  CommandResult,
  EngineStateUpdate,
  EngineLogEntry,
} from '@tamma/shared/contracts';
import { EngineState } from '@tamma/shared';
import type { TammaEngine } from '../engine.js';
import type { DevelopmentPlan, EngineEvent } from '@tamma/shared';

type TransportEventMap = {
  stateUpdate: [EngineStateUpdate];
  log: [EngineLogEntry];
  approvalRequest: [DevelopmentPlan];
  event: [EngineEvent];
};

export class InProcessTransport implements IEngineTransport {
  private readonly emitter = new EventEmitter();
  private readonly engine: TammaEngine;
  private disposed = false;

  /** Resolve function for a pending approval. Set when the engine asks for approval. */
  private pendingApprovalResolve: ((decision: 'approve' | 'reject' | 'skip') => void) | null = null;

  /** Queued decision from an early resolveApproval call (before awaitApproval). */
  private queuedDecision: 'approve' | 'reject' | 'skip' | null = null;

  constructor(engine: TammaEngine) {
    this.engine = engine;
  }

  // ---------------------------------------------------------------------------
  // IEngineTransport — commands
  // ---------------------------------------------------------------------------

  async sendCommand(command: EngineCommand): Promise<CommandResult> {
    this.assertNotDisposed();

    try {
      switch (command.type) {
        case 'start': {
          // Guard against duplicate starts — engine is already running if state is not IDLE or ERROR
          const state = this.engine.getState();
          if (state !== EngineState.IDLE && state !== EngineState.ERROR) {
            this.emitLog('warn', 'Engine is already running, ignoring start command');
            return { ok: false, error: 'Engine is already running' };
          }
          if (command.options?.once) {
            // Fire-and-forget: run a single issue cycle
            void this.engine.processOneIssue().catch((err: unknown) => {
              this.emitLog('error', `processOneIssue failed: ${err instanceof Error ? err.message : String(err)}`);
            });
          } else {
            void this.engine.run().catch((err: unknown) => {
              this.emitLog('error', `Engine run loop failed: ${err instanceof Error ? err.message : String(err)}`);
            });
          }
          break;
        }
        case 'stop': {
          await this.engine.dispose();
          break;
        }
        case 'approve': {
          this.resolveApproval('approve');
          break;
        }
        case 'reject': {
          this.resolveApproval('reject');
          break;
        }
        case 'skip': {
          this.resolveApproval('skip');
          break;
        }
        default:
          // Other commands (pause, resume, process-issue, describe-work) are
          // stubs for future use when the engine supports them natively.
          this.emitLog('warn', `Command '${command.type}' is not yet supported by InProcessTransport`);
          return { ok: false, error: `Command '${command.type}' is not yet supported` };
      }
      return { ok: true };
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      return { ok: false, error: message };
    }
  }

  // ---------------------------------------------------------------------------
  // IEngineTransport — subscriptions
  // ---------------------------------------------------------------------------

  onStateUpdate(listener: (update: EngineStateUpdate) => void): () => void {
    return this.addListener('stateUpdate', listener);
  }

  onLog(listener: (entry: EngineLogEntry) => void): () => void {
    return this.addListener('log', listener);
  }

  onApprovalRequest(listener: (plan: DevelopmentPlan) => void): () => void {
    return this.addListener('approvalRequest', listener);
  }

  onEvent(listener: (event: EngineEvent) => void): () => void {
    return this.addListener('event', listener);
  }

  // ---------------------------------------------------------------------------
  // IEngineTransport — lifecycle
  // ---------------------------------------------------------------------------

  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.emitter.removeAllListeners();
    if (this.pendingApprovalResolve) {
      // Resolve with 'skip' to cleanly unblock the engine without crashing it
      this.pendingApprovalResolve('skip');
      this.pendingApprovalResolve = null;
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring helpers — called by the host to connect engine callbacks
  // ---------------------------------------------------------------------------

  /**
   * Returns an `OnStateChangeCallback` that can be passed to the EngineContext.
   * When the engine transitions state, this callback pushes an
   * `EngineStateUpdate` to all subscribed listeners.
   */
  createStateChangeHandler(): (
    newState: import('@tamma/shared').EngineState,
    issue: import('@tamma/shared').IssueData | null,
    stats: import('../engine.js').EngineStats,
  ) => void {
    return (newState, issue, stats) => {
      const update: EngineStateUpdate = {
        state: newState,
        issue,
        stats: {
          issuesProcessed: stats.issuesProcessed,
          totalCostUsd: stats.totalCostUsd,
          startedAt: stats.startedAt,
        },
      };
      this.emit('stateUpdate', update);
    };
  }

  /**
   * Returns an `ApprovalHandler` that can be passed to the EngineContext.
   * When the engine needs plan approval, this handler emits an
   * `approvalRequest` event and waits for a command (`approve` / `reject` /
   * `skip`) from the UI.
   */
  createApprovalHandler(): (plan: DevelopmentPlan) => Promise<'approve' | 'reject' | 'skip'> {
    return async (plan: DevelopmentPlan) => {
      // If a decision was queued before the approval request arrived, resolve immediately
      if (this.queuedDecision !== null) {
        const decision = this.queuedDecision;
        this.queuedDecision = null;
        return Promise.resolve(decision);
      }
      return new Promise<'approve' | 'reject' | 'skip'>((resolve) => {
        this.pendingApprovalResolve = resolve;
        this.emit('approvalRequest', plan);
      });
    };
  }

  /**
   * Returns an `ILogger`-compatible object whose calls are forwarded as
   * `EngineLogEntry` events.
   */
  createLoggerProxy(): import('@tamma/shared/contracts').ILogger {
    return {
      debug: (message: string, context?: Record<string, unknown>) =>
        this.emitLog('debug', message, context),
      info: (message: string, context?: Record<string, unknown>) =>
        this.emitLog('info', message, context),
      warn: (message: string, context?: Record<string, unknown>) =>
        this.emitLog('warn', message, context),
      error: (message: string, context?: Record<string, unknown>) =>
        this.emitLog('error', message, context),
    };
  }

  // ---------------------------------------------------------------------------
  // Internal
  // ---------------------------------------------------------------------------

  private emit<K extends keyof TransportEventMap>(event: K, ...args: TransportEventMap[K]): void {
    if (!this.disposed) {
      this.emitter.emit(event, ...args);
    }
  }

  private addListener<K extends keyof TransportEventMap>(
    event: K,
    listener: (...args: TransportEventMap[K]) => void,
  ): () => void {
    this.emitter.on(event, listener as (...args: unknown[]) => void);
    return () => {
      this.emitter.off(event, listener as (...args: unknown[]) => void);
    };
  }

  private emitLog(
    level: EngineLogEntry['level'],
    message: string,
    context?: Record<string, unknown>,
  ): void {
    const entry: EngineLogEntry = {
      level,
      message,
      timestamp: Date.now(),
      ...(context !== undefined ? { context } : {}),
    };
    this.emit('log', entry);
  }

  private resolveApproval(decision: 'approve' | 'reject' | 'skip'): void {
    if (this.pendingApprovalResolve) {
      const resolve = this.pendingApprovalResolve;
      this.pendingApprovalResolve = null;
      resolve(decision);
    } else {
      // No pending approval — queue the decision so the next awaitApproval picks it up
      this.queuedDecision = decision;
    }
  }

  private assertNotDisposed(): void {
    if (this.disposed) {
      throw new Error('InProcessTransport has been disposed');
    }
  }
}
