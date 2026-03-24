/**
 * @tamma/mcp-client
 * WebSocket transport implementation
 *
 * Communicates with MCP servers via WebSocket for bidirectional communication.
 */

import {
  BaseTransport,
  type WebSocketTransportOptions,
} from './base.js';
import {
  type JSONRPCRequest,
  type JSONRPCNotification,
  serializeMessage,
  parseMessage,
} from '../utils/json-rpc.js';
import { MCPConnectionError, MCPTransportError } from '../errors.js';

/**
 * Default timeout for connection (ms)
 */
const DEFAULT_TIMEOUT = 30000;

/**
 * WebSocket transport for MCP servers
 *
 * Uses WebSocket for full-duplex communication with MCP servers.
 */
export class WebSocketTransport extends BaseTransport {
  private ws?: WebSocket;
  private readonly url: string;
  /** Headers for Node.js 'ws' library — unused until custom header support is added */
  readonly headers?: Record<string, string>;
  private readonly timeout: number;
  private reconnecting = false;

  constructor(options: WebSocketTransportOptions) {
    super(options.serverName);
    this.url = options.url;
    if (options.headers !== undefined) this.headers = options.headers;
    this.timeout = options.timeout ?? DEFAULT_TIMEOUT;
  }

  /**
   * Connect to the MCP server via WebSocket
   */
  async connect(): Promise<void> {
    if (this.connected) {
      return;
    }

    return new Promise<void>((resolve, reject) => {
      const timeoutId = setTimeout(() => {
        this.cleanup();
        reject(
          new MCPConnectionError(
            this.serverName,
            `Connection timed out after ${this.timeout}ms`
          )
        );
      }, this.timeout);

      try {
        // Note: Browser WebSocket doesn't support custom headers
        // For Node.js, you would use the 'ws' library which supports headers
        // This implementation is for compatibility with both environments
        this.ws = new WebSocket(this.url);

        this.ws.onopen = () => {
          clearTimeout(timeoutId);
          this.connected = true;
          this.reconnecting = false;
          resolve();
        };

        this.ws.onerror = (_event) => {
          const error = new MCPConnectionError(
            this.serverName,
            'WebSocket connection error'
          );

          if (!this.connected) {
            clearTimeout(timeoutId);
            reject(error);
          } else {
            this.emitError(error);
          }
        };

        this.ws.onclose = (event) => {
          this.connected = false;
          this.emitClose();

          if (event.code !== 1000) {
            // Abnormal closure
            this.emitError(
              new MCPTransportError(
                this.serverName,
                'websocket',
                `Connection closed: code=${event.code}, reason=${event.reason || 'unknown'}`
              )
            );
          }
        };

        this.ws.onmessage = (event) => {
          this.handleMessage(event.data as string);
        };
      } catch (error) {
        clearTimeout(timeoutId);
        const err = error instanceof Error ? error : new Error(String(error));
        reject(
          new MCPConnectionError(
            this.serverName,
            `Failed to connect: ${err.message}`,
            { cause: err }
          )
        );
      }
    });
  }

  /**
   * Disconnect from the MCP server
   */
  async disconnect(): Promise<void> {
    if (!this.connected) {
      return;
    }

    return new Promise<void>((resolve) => {
      if (this.ws) {
        const closeHandler = () => {
          this.cleanup();
          resolve();
        };

        // Set up one-time close handler
        this.ws.addEventListener('close', closeHandler, { once: true });

        // Close with normal closure code
        this.ws.close(1000, 'Client disconnecting');

        // Timeout for graceful close
        setTimeout(() => {
          if (this.ws) {
            this.cleanup();
            resolve();
          }
        }, 5000);
      } else {
        resolve();
      }
    });
  }

  /**
   * Send a message to the server
   */
  async send(message: JSONRPCRequest | JSONRPCNotification): Promise<void> {
    if (!this.connected || !this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new MCPTransportError(
        this.serverName,
        'websocket',
        'Transport not connected'
      );
    }

    const serialized = serializeMessage(message);

    try {
      this.ws.send(serialized);
    } catch (error) {
      const err = error instanceof Error ? error : new Error(String(error));
      throw new MCPTransportError(
        this.serverName,
        'websocket',
        `Failed to send message: ${err.message}`,
        { cause: err }
      );
    }
  }

  /**
   * Handle a message from the WebSocket
   */
  private handleMessage(data: string): void {
    const trimmed = data.trim();
    if (!trimmed) {
      return;
    }

    try {
      const message = parseMessage(trimmed);
      this.emitMessage(message);
    } catch (error) {
      const err = error instanceof Error ? error : new Error(String(error));
      this.emitError(
        new MCPTransportError(
          this.serverName,
          'websocket',
          `Failed to parse message: ${err.message}`,
          { cause: err }
        )
      );
    }
  }

  /**
   * Clean up resources
   */
  private cleanup(): void {
    this.connected = false;
    this.reconnecting = false;

    if (this.ws) {
      // Remove all listeners
      this.ws.onopen = null;
      this.ws.onerror = null;
      this.ws.onclose = null;
      this.ws.onmessage = null;

      // Close if still open
      if (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING) {
        this.ws.close();
      }

      delete this.ws;
    }
  }

  /**
   * Get the WebSocket ready state
   */
  getReadyState(): number | undefined {
    return this.ws?.readyState;
  }

  /**
   * Check if the transport is in a reconnecting state
   */
  isReconnecting(): boolean {
    return this.reconnecting;
  }
}
