/**
 * MCP Management Service
 *
 * Wraps a real MCP client to expose the 8 C# /kb/mcp/* endpoints:
 * list servers, get server, start, stop, get/update config, list tools, invoke tool.
 */

import type { IMcpClient } from '../types.js';

export interface McpServerInfo {
  name: string;
  status: 'connected' | 'disconnected' | 'starting' | 'error';
  transport: 'stdio' | 'sse';
  toolCount: number;
  resourceCount: number;
  url?: string;
}

export interface McpToolInfo {
  name: string;
  description: string;
  inputSchema: Record<string, unknown>;
  serverName: string;
}

export interface McpInvokeRequest {
  serverName: string;
  toolName: string;
  arguments?: Record<string, unknown>;
}

export interface McpInvokeResponse {
  success: boolean;
  content: unknown;
  error?: string;
  durationMs: number;
}

export interface McpConfigResponse {
  servers: Array<{
    name: string;
    transport: 'stdio' | 'sse';
    enabled: boolean;
    url?: string;
  }>;
}

export class McpManagementService {
  private readonly client: IMcpClient | null;
  private configuredServers: McpConfigResponse['servers'] = [];

  constructor(client?: IMcpClient) {
    this.client = client ?? null;
  }

  private toServerInfo(info: {
    name: string;
    status: string;
    transport: string;
    url?: string;
  }): McpServerInfo {
    const status: McpServerInfo['status'] =
      info.status === 'connecting' || info.status === 'reconnecting'
        ? 'starting'
        : info.status === 'error'
          ? 'error'
          : info.status === 'connected'
            ? 'connected'
            : 'disconnected';
    const transport: 'stdio' | 'sse' =
      info.transport === 'websocket' ? 'sse' : info.transport === 'stdio' ? 'stdio' : 'sse';
    const out: McpServerInfo = {
      name: info.name,
      status,
      transport,
      toolCount: 0,
      resourceCount: 0,
    };
    if (info.url !== undefined) out.url = info.url;
    return out;
  }

  async listServers(): Promise<McpServerInfo[]> {
    if (!this.client) return [];
    return this.client.listServers().map((s) => this.toServerInfo(s));
  }

  async getServer(id: string): Promise<McpServerInfo> {
    if (!this.client) {
      return {
        name: id,
        status: 'disconnected',
        transport: 'stdio',
        toolCount: 0,
        resourceCount: 0,
      };
    }
    const found = this.client.listServers().find((s) => s.name === id);
    if (!found) {
      return {
        name: id,
        status: 'disconnected',
        transport: 'stdio',
        toolCount: 0,
        resourceCount: 0,
      };
    }
    return this.toServerInfo(found);
  }

  async startServer(id: string): Promise<{ message: string }> {
    if (!this.client) {
      return { message: `MCP server ${id} start requested (stub)` };
    }
    await this.client.connectServer(id);
    return { message: `MCP server ${id} started` };
  }

  async stopServer(id: string): Promise<{ message: string }> {
    if (!this.client) {
      return { message: `MCP server ${id} stop requested (stub)` };
    }
    await this.client.disconnectServer(id);
    return { message: `MCP server ${id} stopped` };
  }

  async getConfig(): Promise<McpConfigResponse> {
    if (!this.client) {
      return { servers: this.configuredServers };
    }
    const servers = this.client.listServers().map((s) => {
      const transport: 'stdio' | 'sse' =
        s.transport === 'websocket' ? 'sse' : s.transport === 'stdio' ? 'stdio' : 'sse';
      const row: McpConfigResponse['servers'][number] = {
        name: s.name,
        transport,
        enabled: true,
      };
      if (s.url !== undefined) row.url = s.url;
      return row;
    });
    return { servers };
  }

  async updateConfig(
    patch: { servers?: McpConfigResponse['servers'] },
  ): Promise<McpConfigResponse & { message: string }> {
    if (patch.servers) this.configuredServers = patch.servers;
    return { servers: this.configuredServers, message: 'MCP config updated' };
  }

  async listTools(serverName?: string): Promise<McpToolInfo[]> {
    if (!this.client) return [];
    const tools = await this.client.listTools(serverName ?? '');
    return tools.map((t) => ({
      name: t.name,
      description: t.description ?? '',
      inputSchema: t.inputSchema ?? {},
      serverName: serverName ?? '',
    }));
  }

  async invokeTool(req: McpInvokeRequest): Promise<McpInvokeResponse> {
    if (!this.client) {
      return {
        success: false,
        content: null,
        error: 'MCP client not configured',
        durationMs: 0,
      };
    }
    const start = Date.now();
    try {
      const result = await this.client.invokeTool(req.serverName, req.toolName, req.arguments);
      const res: McpInvokeResponse = {
        success: result.success,
        content: result.content,
        durationMs: Date.now() - start,
      };
      if (result.error) res.error = result.error;
      return res;
    } catch (err) {
      return {
        success: false,
        content: null,
        error: err instanceof Error ? err.message : String(err),
        durationMs: Date.now() - start,
      };
    }
  }
}
