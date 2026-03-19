/**
 * MCP Management Service
 *
 * Manages MCP (Model Context Protocol) server lifecycle,
 * tool discovery, invocation, and log viewing.
 *
 * Delegates to a real IMCPClientService implementation when available;
 * otherwise returns empty state.
 */

import type {
  MCPServerInfo,
  MCPTool,
  MCPToolInvokeRequest,
  MCPToolInvokeResult,
  MCPServerLog,
} from '@tamma/shared';
import type { IMCPClientService } from './types.js';

const MAX_LOGS_PER_SERVER = 1000;

export class MCPManagementService {
  private readonly client: IMCPClientService | null;
  private logs: Map<string, MCPServerLog[]> = new Map();

  constructor(client?: IMCPClientService) {
    this.client = client ?? null;
  }

  /** Append a log entry, trimming oldest entries when the cap is exceeded. */
  private appendLog(name: string, entry: MCPServerLog): void {
    const logList = this.logs.get(name) ?? [];
    logList.push(entry);
    if (logList.length > MAX_LOGS_PER_SERVER) {
      logList.splice(0, logList.length - MAX_LOGS_PER_SERVER);
    }
    this.logs.set(name, logList);
  }

  /** Convert IMCPClientService server info to the API MCPServerInfo shape */
  private toMCPServerInfo(info: { name: string; status: string; transport: string; url?: string }): MCPServerInfo {
    const result: MCPServerInfo = {
      name: info.name,
      status: info.status === 'connecting' || info.status === 'reconnecting'
        ? 'starting'
        : info.status === 'error'
          ? 'error'
          : info.status as 'connected' | 'disconnected',
      transport: (info.transport === 'websocket' ? 'sse' : info.transport) as 'stdio' | 'sse',
      toolCount: 0,
      resourceCount: 0,
      config: {
        name: info.name,
        transport: (info.transport === 'websocket' ? 'sse' : info.transport) as 'stdio' | 'sse',
        enabled: true,
      },
    };
    return result;
  }

  async listServers(): Promise<MCPServerInfo[]> {
    if (!this.client) {
      return [];
    }

    const servers = this.client.listServers();
    return servers.map((s) => this.toMCPServerInfo(s));
  }

  async getServerStatus(name: string): Promise<MCPServerInfo> {
    if (!this.client) {
      throw new Error(`MCP server not found: ${name}`);
    }

    const servers = this.client.listServers();
    const info = servers.find((s) => s.name === name);
    if (!info) {
      throw new Error(`MCP server not found: ${name}`);
    }

    return this.toMCPServerInfo(info);
  }

  async startServer(name: string): Promise<void> {
    if (!this.client) {
      throw new Error(`MCP server not found: ${name}`);
    }

    await this.client.connectServer(name);

    this.appendLog(name, {
      timestamp: new Date().toISOString(),
      level: 'info',
      message: 'Server started successfully',
    });
  }

  async stopServer(name: string): Promise<void> {
    if (!this.client) {
      throw new Error(`MCP server not found: ${name}`);
    }

    await this.client.disconnectServer(name);

    this.appendLog(name, {
      timestamp: new Date().toISOString(),
      level: 'info',
      message: 'Server stopped',
    });
  }

  async restartServer(name: string): Promise<void> {
    await this.stopServer(name);
    await this.startServer(name);
  }

  async listTools(serverName?: string): Promise<MCPTool[]> {
    if (!this.client) {
      throw new Error('MCP client is not configured');
    }

    const clientTools = await this.client.listTools(serverName ?? '');
    return clientTools.map((t) => ({
      name: t.name,
      description: t.description ?? '',
      inputSchema: (t.inputSchema as Record<string, unknown>) ?? {},
      serverName: serverName ?? '',
    }));
  }

  async invokeTool(request: MCPToolInvokeRequest): Promise<MCPToolInvokeResult> {
    if (!this.client) {
      throw new Error('MCP client is not configured');
    }

    const startTime = Date.now();

    try {
      const result = await this.client.invokeTool(
        request.serverName,
        request.toolName,
        request.arguments,
      );

      const durationMs = Date.now() - startTime;

      const invokeResult: MCPToolInvokeResult = {
        success: result.success,
        content: result.content,
        durationMs,
      };
      if (result.error) {
        invokeResult.error = result.error;
      }
      return invokeResult;
    } catch (error) {
      return {
        success: false,
        content: null,
        error: error instanceof Error ? error.message : String(error),
        durationMs: Date.now() - startTime,
      };
    }
  }

  async getServerLogs(name: string, limit = 100): Promise<MCPServerLog[]> {
    // Try to get logs from client if supported
    if (this.client?.getServerLogs) {
      const clientLogs = this.client.getServerLogs(name, limit);
      if (clientLogs.length > 0) {
        return clientLogs;
      }
    }

    const logList = this.logs.get(name) ?? [];
    return logList.slice(-limit);
  }
}
