/**
 * Remote engine transport.
 *
 * Connects to a Tamma engine running on a remote server. Commands are sent
 * via HTTP POST. Events (state updates, logs, approval requests) are received
 * via Server-Sent Events (SSE).
 *
 * This transport is used by the web dashboard and any client that
 * communicates with the engine over the network.
 */

import { EventEmitter } from 'node:events';
import type {
  IEngineTransport,
  EngineCommand,
  CommandResult,
  EngineStateUpdate,
  EngineLogEntry,
} from '@tamma/shared/contracts';
import type { DevelopmentPlan, EngineEvent } from '@tamma/shared';

export interface RemoteTransportConfig {
  serverUrl: string;
  authToken: string;
}

/** Default SSE reconnection delay in milliseconds. */
const INITIAL_RECONNECT_MS = 1000;
const MAX_RECONNECT_MS = 30_000;

type TransportEventMap = {
  stateUpdate: [EngineStateUpdate];
  log: [EngineLogEntry];
  approvalRequest: [DevelopmentPlan];
  event: [EngineEvent];
};

export class RemoteTransport implements IEngineTransport {
  private readonly emitter = new EventEmitter();
  private readonly serverUrl: string;
  private readonly authToken: string;
  private disposed = false;

  /** Active SSE connection (abort controller). */
  private sseAbort: AbortController | null = null;
  private reconnectMs = INITIAL_RECONNECT_MS;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(config: RemoteTransportConfig) {
    // Strip trailing slashes for consistent URL construction.
    // Avoid regex /\/+$/ which is O(n^2) on strings with many '/' characters.
    let url = config.serverUrl;
    while (url.length > 0 && url.endsWith('/')) {
      url = url.slice(0, -1);
    }
    this.serverUrl = url;
    this.authToken = config.authToken;
  }

  /**
   * Explicitly open the SSE connection for receiving server-pushed events.
   * Must be called after construction; the constructor no longer auto-connects.
   */
  connect(): void {
    this.connectSSE();
  }

  // ---------------------------------------------------------------------------
  // IEngineTransport — commands
  // ---------------------------------------------------------------------------

  async sendCommand(command: EngineCommand): Promise<CommandResult> {
    this.assertNotDisposed();

    const maxRetries = 3;
    let lastError: Error | undefined;

    for (let attempt = 0; attempt < maxRetries; attempt++) {
      try {
        const url = `${this.serverUrl}/api/engine/command`;
        const response = await fetch(url, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${this.authToken}`,
          },
          body: JSON.stringify(command),
        });

        if (!response.ok) {
          const text = await response.text().catch(() => '');
          const err = new Error(
            `Engine command '${command.type}' failed (${response.status}): ${text}`,
          );
          // Only retry on server errors (5xx) or rate limiting (429)
          if (response.status >= 500 || response.status === 429) {
            lastError = err;
            await new Promise((r) => setTimeout(r, Math.pow(2, attempt) * 1000));
            continue;
          }
          return { ok: false, error: err.message };
        }

        return { ok: true };
      } catch (err: unknown) {
        // Re-throw non-retryable errors (e.g. 4xx wrapped above)
        if (err instanceof Error && err.message.includes('failed (4')) {
          return { ok: false, error: err.message };
        }
        // Network errors are retryable
        lastError = err instanceof Error ? err : new Error(String(err));
        if (attempt < maxRetries - 1) {
          await new Promise((r) => setTimeout(r, Math.pow(2, attempt) * 1000));
          continue;
        }
      }
    }

    const errorMsg = lastError?.message ?? `Engine command '${command.type}' failed after ${maxRetries} retries`;
    return { ok: false, error: errorMsg };
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

    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    if (this.sseAbort) {
      this.sseAbort.abort();
      this.sseAbort = null;
    }

    this.emitter.removeAllListeners();
  }

  // ---------------------------------------------------------------------------
  // SSE connection with manual fetch-based streaming
  // ---------------------------------------------------------------------------

  private connectSSE(): void {
    if (this.disposed) return;

    const abort = new AbortController();
    this.sseAbort = abort;

    const url = `${this.serverUrl}/api/engine/events`;

    void this.runSSELoop(url, abort.signal).catch(() => {
      // Error already handled inside runSSELoop
    });
  }

  private async runSSELoop(url: string, signal: AbortSignal): Promise<void> {
    try {
      const response = await fetch(url, {
        method: 'GET',
        headers: {
          'Accept': 'text/event-stream',
          'Authorization': `Bearer ${this.authToken}`,
        },
        signal,
      });

      if (!response.ok || !response.body) {
        throw new Error(`SSE connection failed: ${response.status}`);
      }

      // Reset backoff on successful connection
      this.reconnectMs = INITIAL_RECONNECT_MS;

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

       
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const messages = buffer.split('\n\n');
        // Last element may be an incomplete message; keep it in the buffer
        buffer = messages.pop() ?? '';

        for (const msg of messages) {
          this.parseSSEMessage(msg);
        }
      }
    } catch (err: unknown) {
      if (signal.aborted) return; // Intentional disconnect
      // Schedule reconnect
      this.scheduleReconnect();
    }
  }

  private parseSSEMessage(raw: string): void {
    let eventType = 'message';
    let data = '';

    for (const line of raw.split('\n')) {
      if (line.startsWith('event:')) {
        eventType = line.slice(6).trim();
      } else if (line.startsWith('data:')) {
        if (data.length > 0) {
          data += '\n';
        }
        data += line.slice(5).trim();
      }
    }

    if (!data) return;

    try {
      const parsed = JSON.parse(data);
      switch (eventType) {
        case 'stateUpdate':
          this.emit('stateUpdate', parsed as EngineStateUpdate);
          break;
        case 'log':
          this.emit('log', parsed as EngineLogEntry);
          break;
        case 'approvalRequest':
          this.emit('approvalRequest', parsed as DevelopmentPlan);
          break;
        case 'event':
          this.emit('event', parsed as EngineEvent);
          break;
        default:
          // Unknown event type — ignore
          break;
      }
    } catch {
      // Malformed JSON — skip
    }
  }

  private scheduleReconnect(): void {
    if (this.disposed) return;

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connectSSE();
    }, this.reconnectMs);

    // Exponential backoff capped at MAX_RECONNECT_MS
    this.reconnectMs = Math.min(this.reconnectMs * 2, MAX_RECONNECT_MS);
  }

  // ---------------------------------------------------------------------------
  // Internal helpers
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

  private assertNotDisposed(): void {
    if (this.disposed) {
      throw new Error('RemoteTransport has been disposed');
    }
  }
}
