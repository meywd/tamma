/**
 * @tamma/mcp-client
 * Server connection manager
 */

import type {
  MCPServerConfig,
  ServerStatus,
  ServerCapabilities,
  ServerMetrics,
  MCPTool,
  MCPResource,
  MCPPrompt,
  JSONSchema,
} from '../types.js';
import type { IMCPTransport, TransportMessage } from '../transports/base.js';
import {
  type JSONRPCRequest,
  type JSONRPCResponse,
  type JSONRPCNotification,
  RequestIdGenerator,
  createRequest,
  createNotification,
  isJSONRPCResponse,
  isErrorResponse,
} from '../utils/json-rpc.js';
import { StdioTransport } from '../transports/stdio.js';
import { SSETransport } from '../transports/sse.js';
import { WebSocketTransport } from '../transports/websocket.js';
import {
  MCPConnectionError,
  MCPTimeoutError,
  MCPProtocolError,
} from '../errors.js';
import { withRetry } from '../utils/retry.js';

/**
 * MCP Protocol version
 */
const MCP_PROTOCOL_VERSION = '2024-11-05';

/**
 * Client information sent during initialization
 */
const CLIENT_INFO = {
  name: 'tamma-mcp-client',
  version: '0.1.0',
};

/**
 * Pending request entry
 */
interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timeout: ReturnType<typeof setTimeout>;
}

/**
 * Server connection manages communication with a single MCP server
 */
export class ServerConnection {
  private transport?: IMCPTransport;
  private status: ServerStatus = 'disconnected';
  private capabilities: ServerCapabilities = {};
  private tools: MCPTool[] = [];
  private resources: MCPResource[] = [];
  private prompts: MCPPrompt[] = [];
  private lastError?: Error;
  private lastConnected?: Date;
  private reconnectAttempts = 0;
  private reconnectTimeout?: ReturnType<typeof setTimeout>;

  private readonly pendingRequests = new Map<number | string, PendingRequest>();
  private readonly idGenerator = new RequestIdGenerator();
  private readonly metrics: ServerMetrics = {
    totalRequests: 0,
    successfulRequests: 0,
    failedRequests: 0,
    averageLatencyMs: 0,
  };
  private latencySum = 0;

  // Event handlers
  private onStatusChange?: (status: ServerStatus) => void;
  private onToolsChanged?: (tools: MCPTool[]) => void;
  private onResourcesChanged?: (resources: MCPResource[]) => void;

  constructor(
    public readonly config: MCPServerConfig,
    private readonly defaultTimeout: number = 30000
  ) {}

  /**
   * Get the current connection status
   */
  getStatus(): ServerStatus {
    return this.status;
  }

  /**
   * Get server capabilities
   */
  getCapabilities(): ServerCapabilities {
    return this.capabilities;
  }

  /**
   * Get discovered tools
   */
  getTools(): MCPTool[] {
    return this.tools;
  }

  /**
   * Get discovered resources
   */
  getResources(): MCPResource[] {
    return this.resources;
  }

  /**
   * Get discovered prompts
   */
  getPrompts(): MCPPrompt[] {
    return this.prompts;
  }

  /**
   * Get server metrics
   */
  getMetrics(): ServerMetrics {
    return { ...this.metrics };
  }

  /**
   * Get last error
   */
  getLastError(): Error | undefined {
    return this.lastError;
  }

  /**
   * Get last connected time
   */
  getLastConnected(): Date | undefined {
    return this.lastConnected;
  }

  /**
   * Set status change handler
   */
  setOnStatusChange(handler: (status: ServerStatus) => void): void {
    this.onStatusChange = handler;
  }

  /**
   * Set tools changed handler
   */
  setOnToolsChanged(handler: (tools: MCPTool[]) => void): void {
    this.onToolsChanged = handler;
  }

  /**
   * Set resources changed handler
   */
  setOnResourcesChanged(handler: (resources: MCPResource[]) => void): void {
    this.onResourcesChanged = handler;
  }

  /**
   * Connect to the server
   */
  async connect(): Promise<void> {
    if (this.status === 'connected') {
      return;
    }

    this.setStatus('connecting');

    try {
      // Create transport based on config
      this.transport = this.createTransport();

      // Set up message handling
      this.transport.onMessage((message) => this.handleMessage(message));
      this.transport.onError((error) => this.handleTransportError(error));
      this.transport.onClose(() => this.handleTransportClose());

      // Connect transport
      await this.transport.connect();

      // Perform MCP initialization handshake
      await this.initialize();

      // Discover capabilities
      await this.discoverCapabilities();

      this.setStatus('connected');
      this.lastConnected = new Date();
      this.reconnectAttempts = 0;
    } catch (error) {
      this.lastError = error instanceof Error ? error : new Error(String(error));
      this.setStatus('error');
      throw new MCPConnectionError(
        this.config.name,
        `Failed to connect: ${this.lastError.message}`,
        { cause: this.lastError }
      );
    }
  }

  /**
   * Disconnect from the server
   */
  async disconnect(): Promise<void> {
    // Clear reconnect timeout
    if (this.reconnectTimeout) {
      clearTimeout(this.reconnectTimeout);
      this.reconnectTimeout = undefined;
    }

    // Cancel pending requests
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timeout);
      pending.reject(new Error('Connection closed'));
      this.pendingRequests.delete(id);
    }

    // Disconnect transport
    if (this.transport) {
      await this.transport.disconnect();
      this.transport = undefined;
    }

    this.setStatus('disconnected');
  }

  /**
   * Send a request and wait for response
   */
  async sendRequest<T>(
    method: string,
    params?: Record<string, unknown>,
    timeout?: number
  ): Promise<T> {
    if (this.status !== 'connected' || !this.transport) {
      throw new MCPConnectionError(this.config.name, 'Not connected');
    }

    const id = this.idGenerator.next();
    const request = createRequest(id, method, params);
    const timeoutMs = timeout ?? this.config.timeout ?? this.defaultTimeout;

    const startTime = Date.now();

    return new Promise<T>((resolve, reject) => {
      // Set up timeout
      const timeoutHandle = setTimeout(() => {
        this.pendingRequests.delete(id);
        this.recordRequest(false, Date.now() - startTime);
        reject(new MCPTimeoutError(this.config.name, method, timeoutMs));
      }, timeoutMs);

      // Store pending request
      this.pendingRequests.set(id, {
        resolve: (value) => {
          clearTimeout(timeoutHandle);
          this.recordRequest(true, Date.now() - startTime);
          resolve(value as T);
        },
        reject: (error) => {
          clearTimeout(timeoutHandle);
          this.recordRequest(false, Date.now() - startTime);
          reject(error);
        },
        timeout: timeoutHandle,
      });

      // Send request
      this.transport!.send(request).catch((error) => {
        this.pendingRequests.delete(id);
        clearTimeout(timeoutHandle);
        this.recordRequest(false, Date.now() - startTime);
        reject(error);
      });
    });
  }

  /**
   * Send a notification (no response expected)
   */
  async sendNotification(
    method: string,
    params?: Record<string, unknown>
  ): Promise<void> {
    if (this.status !== 'connected' || !this.transport) {
      throw new MCPConnectionError(this.config.name, 'Not connected');
    }

    const notification = createNotification(method, params);
    await this.transport.send(notification);
  }

  /**
   * Invoke a tool
   */
  async invokeTool(
    toolName: string,
    args: Record<string, unknown>,
    timeout?: number
  ): Promise<{
    content: Array<{ type: string; text?: string; data?: string; mimeType?: string }>;
    isError?: boolean;
  }> {
    const result = await this.sendRequest<{
      content: Array<{ type: string; text?: string; data?: string; mimeType?: string }>;
      isError?: boolean;
    }>(
      'tools/call',
      { name: toolName, arguments: args },
      timeout
    );

    return result;
  }

  /**
   * Read a resource
   */
  async readResource(uri: string, timeout?: number): Promise<{
    contents: Array<{ uri: string; mimeType?: string; text?: string; blob?: string }>;
  }> {
    return this.sendRequest('resources/read', { uri }, timeout);
  }

  /**
   * Get a specific tool schema
   */
  getToolSchema(toolName: string): JSONSchema | undefined {
    const tool = this.tools.find((t) => t.name === toolName);
    return tool?.inputSchema;
  }

  /**
   * Create transport based on configuration
   */
  private createTransport(): IMCPTransport {
    switch (this.config.transport) {
      case 'stdio':
        return new StdioTransport({
          serverName: this.config.name,
          command: this.config.command!,
          args: this.config.args,
          env: this.config.env,
          cwd: this.config.cwd,
          timeout: this.config.timeout ?? this.defaultTimeout,
          sandboxed: this.config.sandboxed,
        });

      case 'sse':
        return new SSETransport({
          serverName: this.config.name,
          url: this.config.url!,
          headers: this.config.headers,
          timeout: this.config.timeout ?? this.defaultTimeout,
        });

      case 'websocket':
        return new WebSocketTransport({
          serverName: this.config.name,
          url: this.config.url!,
          headers: this.config.headers,
          timeout: this.config.timeout ?? this.defaultTimeout,
        });

      default:
        throw new Error(`Unknown transport: ${this.config.transport}`);
    }
  }

  /**
   * Perform MCP initialization handshake
   */
  private async initialize(): Promise<void> {
    const result = await this.sendRequest<{
      protocolVersion: string;
      capabilities: ServerCapabilities;
      serverInfo?: { name: string; version: string };
    }>('initialize', {
      protocolVersion: MCP_PROTOCOL_VERSION,
      capabilities: {
        roots: { listChanged: true },
        sampling: {},
      },
      clientInfo: CLIENT_INFO,
    });

    this.capabilities = result.capabilities;

    // Send initialized notification
    await this.sendNotification('notifications/initialized');
  }

  /**
   * Discover server capabilities (tools, resources, prompts)
   */
  private async discoverCapabilities(): Promise<void> {
    // Discover tools if supported
    if (this.capabilities.tools) {
      try {
        const result = await this.sendRequest<{
          tools: Array<{
            name: string;
            description?: string;
            inputSchema: JSONSchema;
          }>;
        }>('tools/list');

        this.tools = result.tools.map((t) => ({
          name: t.name,
          description: t.description ?? '',
          inputSchema: t.inputSchema,
          serverName: this.config.name,
        }));
      } catch (error) {
        // Tools discovery failed — server declared capability but list request failed
        console.warn(`[MCPConnection:${this.config.name}] tools/list failed:`, error instanceof Error ? error.message : String(error));
        this.tools = [];
      }
    }

    // Discover resources if supported
    if (this.capabilities.resources) {
      try {
        const result = await this.sendRequest<{
          resources: Array<{
            uri: string;
            name: string;
            description?: string;
            mimeType?: string;
          }>;
        }>('resources/list');

        this.resources = result.resources.map((r) => ({
          uri: r.uri,
          name: r.name,
          description: r.description,
          mimeType: r.mimeType,
          serverName: this.config.name,
        }));
      } catch (error) {
        // Resources discovery failed — server declared capability but list request failed
        console.warn(`[MCPConnection:${this.config.name}] resources/list failed:`, error instanceof Error ? error.message : String(error));
        this.resources = [];
      }
    }

    // Discover prompts if supported
    if (this.capabilities.prompts) {
      try {
        const result = await this.sendRequest<{
          prompts: Array<{
            name: string;
            description?: string;
            arguments?: Array<{
              name: string;
              description?: string;
              required?: boolean;
            }>;
          }>;
        }>('prompts/list');

        this.prompts = result.prompts.map((p) => ({
          name: p.name,
          description: p.description,
          arguments: p.arguments,
          serverName: this.config.name,
        }));
      } catch (error) {
        // Prompts discovery failed — server declared capability but list request failed
        console.warn(`[MCPConnection:${this.config.name}] prompts/list failed:`, error instanceof Error ? error.message : String(error));
        this.prompts = [];
      }
    }
  }

  /**
   * Handle incoming message from transport
   */
  private handleMessage(message: TransportMessage): void {
    if (isJSONRPCResponse(message)) {
      this.handleResponse(message);
    } else if ('method' in message) {
      this.handleNotification(message);
    }
  }

  /**
   * Handle response message
   */
  private handleResponse(response: JSONRPCResponse): void {
    if (response.id === null) {
      return; // Invalid response
    }

    const pending = this.pendingRequests.get(response.id);
    if (!pending) {
      return; // Unknown request
    }

    this.pendingRequests.delete(response.id);

    if (isErrorResponse(response)) {
      const error = response.error!;
      pending.reject(
        new MCPProtocolError(
          this.config.name,
          error.code,
          error.message,
          { data: error.data }
        )
      );
    } else {
      pending.resolve(response.result);
    }
  }

  /**
   * Handle notification from server
   */
  private handleNotification(notification: JSONRPCNotification): void {
    switch (notification.method) {
      case 'notifications/tools/list_changed':
        // Re-discover tools
        void this.discoverTools();
        break;

      case 'notifications/resources/list_changed':
        // Re-discover resources
        void this.discoverResources();
        break;

      case 'notifications/prompts/list_changed':
        // Re-discover prompts
        void this.discoverPrompts();
        break;

      // Handle other notifications as needed
    }
  }

  /**
   * Re-discover tools
   */
  private async discoverTools(): Promise<void> {
    try {
      const result = await this.sendRequest<{
        tools: Array<{
          name: string;
          description?: string;
          inputSchema: JSONSchema;
        }>;
      }>('tools/list');

      this.tools = result.tools.map((t) => ({
        name: t.name,
        description: t.description ?? '',
        inputSchema: t.inputSchema,
        serverName: this.config.name,
      }));

      this.onToolsChanged?.(this.tools);
    } catch {
      // Ignore errors during refresh
    }
  }

  /**
   * Re-discover resources
   */
  private async discoverResources(): Promise<void> {
    try {
      const result = await this.sendRequest<{
        resources: Array<{
          uri: string;
          name: string;
          description?: string;
          mimeType?: string;
        }>;
      }>('resources/list');

      this.resources = result.resources.map((r) => ({
        uri: r.uri,
        name: r.name,
        description: r.description,
        mimeType: r.mimeType,
        serverName: this.config.name,
      }));

      this.onResourcesChanged?.(this.resources);
    } catch {
      // Ignore errors during refresh
    }
  }

  /**
   * Re-discover prompts
   */
  private async discoverPrompts(): Promise<void> {
    try {
      const result = await this.sendRequest<{
        prompts: Array<{
          name: string;
          description?: string;
          arguments?: Array<{
            name: string;
            description?: string;
            required?: boolean;
          }>;
        }>;
      }>('prompts/list');

      this.prompts = result.prompts.map((p) => ({
        name: p.name,
        description: p.description,
        arguments: p.arguments,
        serverName: this.config.name,
      }));
    } catch {
      // Ignore errors during refresh
    }
  }

  /**
   * Handle transport error
   */
  private handleTransportError(error: Error): void {
    this.lastError = error;

    if (this.status === 'connected') {
      this.setStatus('error');
      this.attemptReconnect();
    }
  }

  /**
   * Handle transport close
   */
  private handleTransportClose(): void {
    if (this.status === 'connected' || this.status === 'connecting') {
      this.setStatus('disconnected');
      this.attemptReconnect();
    }
  }

  /**
   * Attempt to reconnect
   */
  private attemptReconnect(): void {
    if (!this.config.reconnectOnError) {
      return;
    }

    const maxAttempts = this.config.maxReconnectAttempts ?? 5;
    if (this.reconnectAttempts >= maxAttempts) {
      this.setStatus('error');
      return;
    }

    this.setStatus('reconnecting');
    this.reconnectAttempts += 1;

    // Exponential backoff
    const delay = Math.min(1000 * Math.pow(2, this.reconnectAttempts - 1), 30000);

    this.reconnectTimeout = setTimeout(async () => {
      try {
        await this.connect();
      } catch (error) {
        // connect() failed — schedule another attempt so reconnection never stalls.
        console.warn(
          `[MCPConnection:${this.config.name}] Reconnect attempt ${this.reconnectAttempts} failed:`,
          error instanceof Error ? error.message : String(error),
        );
        this.attemptReconnect();
      }
    }, delay);
  }

  /**
   * Set status and notify handler
   */
  private setStatus(status: ServerStatus): void {
    this.status = status;
    this.onStatusChange?.(status);
  }

  /**
   * Record request metrics
   */
  private recordRequest(success: boolean, latencyMs: number): void {
    this.metrics.totalRequests += 1;

    if (success) {
      this.metrics.successfulRequests += 1;
    } else {
      this.metrics.failedRequests += 1;
    }

    this.latencySum += latencyMs;
    this.metrics.averageLatencyMs = this.latencySum / this.metrics.totalRequests;
    this.metrics.lastRequestTime = new Date();
  }
}
