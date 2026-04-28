/**
 * @tamma/mcp-client
 * Connection pool for managing multiple server connections
 */

import type { MCPServerConfig, ServerInfo, ServerStatus } from '../types.js';
import { ServerConnection } from './connection.js';
import { MCPServerNotFoundError } from '../errors.js';

/**
 * Connection pool options
 */
export interface ConnectionPoolOptions {
  /** Default timeout for operations (ms) */
  defaultTimeout?: number;
  /** Maximum concurrent connection attempts */
  maxConcurrentConnections?: number;
}

/**
 * Default pool options
 */
const DEFAULT_OPTIONS: Required<ConnectionPoolOptions> = {
  defaultTimeout: 30000,
  maxConcurrentConnections: 10,
};

/**
 * Connection pool for managing multiple MCP server connections
 */
export class ConnectionPool {
  private readonly connections = new Map<string, ServerConnection>();
  private readonly options: Required<ConnectionPoolOptions>;

  // Event handlers
  private onServerStatusChange?: (serverName: string, status: ServerStatus) => void;

  constructor(options: ConnectionPoolOptions = {}) {
    this.options = { ...DEFAULT_OPTIONS, ...options };
  }

  /**
   * Add a server configuration to the pool
   * Does not connect automatically
   */
  addServer(config: MCPServerConfig): void {
    if (this.connections.has(config.name)) {
      throw new Error(`Server '${config.name}' already exists in pool`);
    }

    const connection = new ServerConnection(config, this.options.defaultTimeout);

    // Set up event handlers
    connection.setOnStatusChange((status) => {
      this.onServerStatusChange?.(config.name, status);
    });

    this.connections.set(config.name, connection);
  }

  /**
   * Remove a server from the pool
   * Disconnects if connected
   */
  async removeServer(name: string): Promise<void> {
    const connection = this.connections.get(name);
    if (!connection) {
      return;
    }

    await connection.disconnect();
    this.connections.delete(name);
  }

  /**
   * Get a connection by name
   */
  getConnection(name: string): ServerConnection | undefined {
    return this.connections.get(name);
  }

  /**
   * Get a connection by name, throwing if not found
   */
  requireConnection(name: string): ServerConnection {
    const connection = this.connections.get(name);
    if (!connection) {
      throw new MCPServerNotFoundError(name);
    }
    return connection;
  }

  /**
   * Connect to a specific server
   */
  async connect(name: string): Promise<void> {
    const connection = this.requireConnection(name);
    await connection.connect();
  }

  /**
   * Disconnect from a specific server
   */
  async disconnect(name: string): Promise<void> {
    const connection = this.connections.get(name);
    if (connection) {
      await connection.disconnect();
    }
  }

  /**
   * Connect to all servers
   * Connects in parallel with concurrency limit
   */
  async connectAll(): Promise<Map<string, Error | null>> {
    const results = new Map<string, Error | null>();
    const servers = Array.from(this.connections.entries());

    // Process in batches
    for (let i = 0; i < servers.length; i += this.options.maxConcurrentConnections) {
      const batch = servers.slice(i, i + this.options.maxConcurrentConnections);

      await Promise.all(
        batch.map(async ([name, connection]) => {
          try {
            await connection.connect();
            results.set(name, null);
          } catch (error) {
            results.set(name, error instanceof Error ? error : new Error(String(error)));
          }
        })
      );
    }

    return results;
  }

  /**
   * Disconnect from all servers
   */
  async disconnectAll(): Promise<void> {
    await Promise.all(
      Array.from(this.connections.values()).map(async (connection) =>
        connection.disconnect()
      )
    );
  }

  /**
   * Get all server names
   */
  getServerNames(): string[] {
    return Array.from(this.connections.keys());
  }

  /**
   * Get server info for all connections
   */
  getServerInfos(): ServerInfo[] {
    return Array.from(this.connections.entries()).map(([name, connection]) => ({
      name,
      transport: connection.config.transport,
      status: connection.getStatus(),
      capabilities: connection.getCapabilities(),
      toolCount: connection.getTools().length,
      resourceCount: connection.getResources().length,
      promptCount: connection.getPrompts().length,
      lastConnected: connection.getLastConnected(),
      lastError: connection.getLastError(),
      metrics: connection.getMetrics(),
    }));
  }

  /**
   * Get server info for a specific server
   */
  getServerInfo(name: string): ServerInfo | undefined {
    const connection = this.connections.get(name);
    if (!connection) {
      return undefined;
    }

    return {
      name,
      transport: connection.config.transport,
      status: connection.getStatus(),
      capabilities: connection.getCapabilities(),
      toolCount: connection.getTools().length,
      resourceCount: connection.getResources().length,
      promptCount: connection.getPrompts().length,
      lastConnected: connection.getLastConnected(),
      lastError: connection.getLastError(),
      metrics: connection.getMetrics(),
    };
  }

  /**
   * Get status of a server
   */
  getStatus(name: string): ServerStatus {
    const connection = this.connections.get(name);
    return connection?.getStatus() ?? 'disconnected';
  }

  /**
   * Check if a server is connected
   */
  isConnected(name: string): boolean {
    return this.getStatus(name) === 'connected';
  }

  /**
   * Get all connected servers
   */
  getConnectedServers(): string[] {
    return Array.from(this.connections.entries())
      .filter(([_, connection]) => connection.getStatus() === 'connected')
      .map(([name]) => name);
  }

  /**
   * Get count of servers
   */
  getServerCount(): number {
    return this.connections.size;
  }

  /**
   * Get count of connected servers
   */
  getConnectedCount(): number {
    return this.getConnectedServers().length;
  }

  /**
   * Set status change handler
   */
  setOnServerStatusChange(
    handler: (serverName: string, status: ServerStatus) => void
  ): void {
    this.onServerStatusChange = handler;
  }

  /**
   * Clear the pool (disconnects all servers)
   */
  async clear(): Promise<void> {
    await this.disconnectAll();
    this.connections.clear();
  }
}
