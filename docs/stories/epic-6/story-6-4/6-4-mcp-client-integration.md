# Story 6-4: MCP Client Integration

## User Story

As a **Tamma agent**, I need to connect to MCP (Model Context Protocol) servers so that I can access external tools, data sources, and services through a standardized interface.

## Description

Implement an MCP client that connects to multiple MCP servers (GitHub, Slack, Jira, databases, filesystems, custom services), discovers available tools and resources, and makes them accessible to agents during task execution.

## Acceptance Criteria

### AC1: MCP Client Core
- [ ] Implement MCP client following official specification
- [ ] Support stdio transport (subprocess servers)
- [ ] Support SSE transport (HTTP servers)
- [ ] Handle connection lifecycle (connect, reconnect, disconnect)
- [ ] Support multiple concurrent server connections

### AC2: Server Discovery
- [ ] Load server configurations from config file
- [ ] Auto-start configured stdio servers
- [ ] Connect to remote SSE servers
- [ ] List available tools from each server
- [ ] List available resources from each server
- [ ] Cache server capabilities

### AC3: Tool Invocation
- [ ] Call tools with typed arguments
- [ ] Handle tool responses (success, error)
- [ ] Support streaming tool responses
- [ ] Timeout handling per tool
- [ ] Retry logic for transient failures

### AC4: Resource Access
- [ ] Read resources by URI
- [ ] Subscribe to resource changes (if supported)
- [ ] Cache resource contents (configurable TTL)
- [ ] Handle resource pagination

### AC5: Built-in Server Support
- [ ] GitHub MCP server integration
- [ ] Filesystem MCP server integration
- [ ] PostgreSQL MCP server integration
- [ ] Slack MCP server integration (optional)
- [ ] Custom server template

### AC6: Security
- [ ] Validate server configurations
- [ ] Sandbox server processes (stdio)
- [ ] Rate limiting per server
- [ ] Audit logging of tool invocations
- [ ] Secret management for server credentials

### AC7: Monitoring
- [ ] Track server health status
- [ ] Log tool invocation metrics
- [ ] Alert on server failures
- [ ] Dashboard for MCP server status

## Technical Design

### MCP Client Architecture

```typescript
interface IMCPClient {
  // Lifecycle
  initialize(config: MCPClientConfig): Promise<void>;
  dispose(): Promise<void>;

  // Server management
  connectServer(name: string): Promise<void>;
  disconnectServer(name: string): Promise<void>;
  getServerStatus(name: string): ServerStatus;
  listServers(): ServerInfo[];

  // Tool operations
  listTools(serverName?: string): Tool[];
  invokeTool(serverName: string, toolName: string, args: unknown): Promise<ToolResult>;

  // Resource operations
  listResources(serverName?: string): Resource[];
  readResource(serverName: string, uri: string): Promise<ResourceContent>;
  subscribeResource(serverName: string, uri: string, callback: ResourceCallback): Unsubscribe;

  // Events
  on(event: 'server:connected' | 'server:disconnected' | 'server:error', handler: EventHandler): void;
}

interface MCPClientConfig {
  servers: MCPServerConfig[];
  defaultTimeout: number;
  retryAttempts: number;
  retryDelayMs: number;
}

interface MCPServerConfig {
  name: string;
  transport: 'stdio' | 'sse';

  // For stdio transport
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  cwd?: string;

  // For SSE transport
  url?: string;
  headers?: Record<string, string>;

  // Common
  timeout?: number;
  enabled?: boolean;
}

interface Tool {
  name: string;
  description: string;
  inputSchema: JSONSchema;
  serverName: string;
}

interface ToolResult {
  success: boolean;
  content: unknown;
  error?: string;
  isStreaming?: boolean;
}

interface Resource {
  uri: string;
  name: string;
  description?: string;
  mimeType?: string;
  serverName: string;
}
```

### MCP Client Implementation

```typescript
class MCPClient implements IMCPClient {
  private servers: Map<string, MCPServerConnection> = new Map();
  private toolRegistry: Map<string, Tool> = new Map();
  private resourceRegistry: Map<string, Resource> = new Map();

  async initialize(config: MCPClientConfig): Promise<void> {
    for (const serverConfig of config.servers) {
      if (serverConfig.enabled !== false) {
        await this.connectServer(serverConfig.name);
      }
    }
  }

  async connectServer(name: string): Promise<void> {
    const config = this.getServerConfig(name);
    const connection = await this.createConnection(config);

    // Discover capabilities
    const capabilities = await connection.initialize();

    // Register tools
    if (capabilities.tools) {
      const tools = await connection.listTools();
      for (const tool of tools) {
        this.toolRegistry.set(`${name}:${tool.name}`, { ...tool, serverName: name });
      }
    }

    // Register resources
    if (capabilities.resources) {
      const resources = await connection.listResources();
      for (const resource of resources) {
        this.resourceRegistry.set(`${name}:${resource.uri}`, { ...resource, serverName: name });
      }
    }

    this.servers.set(name, connection);
    this.emit('server:connected', { name });
  }

  private async createConnection(config: MCPServerConfig): Promise<MCPServerConnection> {
    if (config.transport === 'stdio') {
      return new StdioMCPConnection(config);
    } else {
      return new SSEMCPConnection(config);
    }
  }

  async invokeTool(serverName: string, toolName: string, args: unknown): Promise<ToolResult> {
    const server = this.servers.get(serverName);
    if (!server) {
      throw new Error(`Server not connected: ${serverName}`);
    }

    const tool = this.toolRegistry.get(`${serverName}:${toolName}`);
    if (!tool) {
      throw new Error(`Tool not found: ${serverName}:${toolName}`);
    }

    // Validate arguments against schema
    this.validateArgs(args, tool.inputSchema);

    // Log invocation
    this.logToolInvocation(serverName, toolName, args);

    try {
      const result = await server.callTool(toolName, args);
      return { success: true, content: result };
    } catch (error) {
      return {
        success: false,
        content: null,
        error: error instanceof Error ? error.message : String(error),
      };
    }
  }
}
```

### Stdio Transport

```typescript
class StdioMCPConnection implements MCPServerConnection {
  private process: ChildProcess;
  private messageId = 0;
  private pendingRequests: Map<number, PendingRequest> = new Map();

  async initialize(): Promise<ServerCapabilities> {
    // Spawn server process
    this.process = spawn(this.config.command!, this.config.args ?? [], {
      env: { ...process.env, ...this.config.env },
      cwd: this.config.cwd,
      stdio: ['pipe', 'pipe', 'pipe'],
    });

    // Set up message handling
    this.process.stdout!.on('data', (data) => this.handleMessage(data));
    this.process.stderr!.on('data', (data) => this.handleError(data));

    // Send initialize request
    return this.sendRequest('initialize', {
      protocolVersion: '2024-11-05',
      capabilities: {},
      clientInfo: { name: 'tamma', version: '1.0.0' },
    });
  }

  async callTool(name: string, args: unknown): Promise<unknown> {
    return this.sendRequest('tools/call', { name, arguments: args });
  }

  private sendRequest(method: string, params: unknown): Promise<unknown> {
    const id = ++this.messageId;
    const message = JSON.stringify({ jsonrpc: '2.0', id, method, params });

    return new Promise((resolve, reject) => {
      this.pendingRequests.set(id, { resolve, reject });
      this.process.stdin!.write(message + '\n');
    });
  }
}
```

### SSE Transport

```typescript
class SSEMCPConnection implements MCPServerConnection {
  private eventSource: EventSource;
  private sessionId: string;

  async initialize(): Promise<ServerCapabilities> {
    // Connect to SSE endpoint
    this.eventSource = new EventSource(this.config.url!, {
      headers: this.config.headers,
    });

    return new Promise((resolve, reject) => {
      this.eventSource.onmessage = (event) => {
        const message = JSON.parse(event.data);
        if (message.type === 'session') {
          this.sessionId = message.sessionId;
          resolve(message.capabilities);
        }
      };
      this.eventSource.onerror = reject;
    });
  }

  async callTool(name: string, args: unknown): Promise<unknown> {
    const response = await fetch(`${this.config.url}/tools/call`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Session-ID': this.sessionId,
        ...this.config.headers,
      },
      body: JSON.stringify({ name, arguments: args }),
    });

    return response.json();
  }
}
```

## Dependencies

- MCP specification (https://modelcontextprotocol.io)
- JSON-RPC implementation
- EventSource for SSE
- Child process management

## Testing Strategy

### Unit Tests
- Connection lifecycle management
- Message serialization/deserialization
- Tool registry operations
- Error handling

### Integration Tests
- Stdio server connection (mock server)
- SSE server connection (mock server)
- Tool invocation end-to-end
- Resource reading

### E2E Tests
- Real GitHub MCP server
- Real filesystem MCP server
- Multiple concurrent servers

## Configuration

```yaml
mcp:
  servers:
    - name: github
      transport: stdio
      command: npx
      args: ["@modelcontextprotocol/server-github"]
      env:
        GITHUB_TOKEN: ${GITHUB_TOKEN}
      enabled: true

    - name: filesystem
      transport: stdio
      command: npx
      args: ["@modelcontextprotocol/server-filesystem", "/allowed/path"]
      enabled: true

    - name: postgres
      transport: stdio
      command: npx
      args: ["@modelcontextprotocol/server-postgres"]
      env:
        DATABASE_URL: ${DATABASE_URL}
      enabled: true

    - name: slack
      transport: sse
      url: http://localhost:3001/mcp
      headers:
        Authorization: "Bearer ${SLACK_TOKEN}"
      enabled: false

    - name: custom
      transport: stdio
      command: python
      args: ["./scripts/custom_mcp_server.py"]
      enabled: false

  settings:
    default_timeout_ms: 30000
    retry_attempts: 3
    retry_delay_ms: 1000
```

## Success Metrics

- Server connection success rate > 99%
- Tool invocation latency p95 < 500ms
- Zero security incidents
- All configured servers healthy

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
