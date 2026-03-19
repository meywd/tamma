/**
 * MCP Management Service
 *
 * Manages MCP (Model Context Protocol) server lifecycle,
 * tool discovery, invocation, and log viewing.
 *
 * Delegates to the real MCPClient from @tamma/mcp-client
 * when available; otherwise returns empty state.
 */

import type {
  MCPServerInfo,
  MCPTool,
  MCPToolInvokeRequest,
  MCPToolInvokeResult,
  MCPServerLog,
} from '@tamma/shared';
import type { MCPClient } from '@tamma/mcp-client';
import type {
  ServerInfo as MCPClientServerInfo,
  MCPTool as MCPClientTool,
} from '@tamma/mcp-client';

const MAX_LOGS_PER_SERVER = 1000;

export class MCPManagementService {
  private readonly client: MCPClient | null;
  private logs: Map<string, MCPServerLog[]> = new Map();

  constructor(client?: MCPClient) {
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

  /** Convert MCPClient ServerInfo to the API MCPServerInfo shape */
  private toMCPServerInfo(info: MCPClientServerInfo): MCPServerInfo {
    const result: MCPServerInfo = {
      name: info.name,
      status: info.status === 'connecting' || info.status === 'reconnecting'
        ? 'starting'
        : info.status === 'error'
          ? 'error'
          : info.status as 'connected' | 'disconnected',
      transport: info.transport === 'websocket' ? 'sse' : info.transport,
      toolCount: info.toolCount,
      resourceCount: info.resourceCount,
      config: {
        name: info.name,
        transport: info.transport === 'websocket' ? 'sse' : info.transport,
        enabled: true,
      },
    };
    if (info.lastConnected) {
      result.lastConnected = info.lastConnected.toISOString();
    }
    if (info.lastError) {
      result.error = info.lastError.message;
    }
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

    const info = this.client.getServerInfo(name);
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
      return [];
    }

    const clientTools: MCPClientTool[] = this.client.listTools(serverName);
    return clientTools.map((t) => ({
      name: t.name,
      description: t.description,
      inputSchema: t.inputSchema as Record<string, unknown>,
      serverName: t.serverName,
    }));
  }

  async invokeTool(request: MCPToolInvokeRequest): Promise<MCPToolInvokeResult> {
    if (!this.client) {
      return {
        success: false,
        content: null,
        error: `MCP server not found: ${request.serverName}`,
        durationMs: 0,
      };
    }

    const startTime = Date.now();

    try {
      const result = await this.client.invokeTool(
        request.serverName,
        request.toolName,
        request.arguments,
      );

      const durationMs = result.metadata?.latencyMs ?? (Date.now() - startTime);

      // Flatten text content to a simple value
      let content: unknown = null;
      if (result.content.length > 0) {
        const first = result.content[0];
        if (first && first.type === 'text') {
          content = { message: first.text };
        } else {
          content = result.content;
        }
      }

      const invokeResult: MCPToolInvokeResult = {
        success: result.success,
        content,
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
    if (this.client) {
      const info = this.client.getServerInfo(name);
      if (!info) {
        throw new Error(`MCP server not found: ${name}`);
      }
    }

    const logList = this.logs.get(name) ?? [];
    return logList.slice(-limit);
  }
}
