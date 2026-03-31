---
title: "Story 6-4: MCP Client Integration - Implementation Plan"
sidebar:
  order: 60
---

**Epic**: Epic 6 - Context & Knowledge Management
**Status**: Ready for Development
**Priority**: P2
**Last Updated**: 2025-02-05

## Overview

This implementation plan details how to build an MCP (Model Context Protocol) client that connects to multiple MCP servers (GitHub, Slack, Jira, databases, filesystems, custom services), discovers available tools and resources, and makes them accessible to Tamma agents during task execution.

## Package Location

**Package**: `packages/mcp-client`
**Package Name**: `@tamma/mcp-client`

The MCP client will be a standalone package in the monorepo, following the established pattern of other packages like `@tamma/providers`, `@tamma/platforms`, etc.

```
packages/mcp-client/
├── src/
│   ├── index.ts                    # Public exports
│   ├── types.ts                    # Type definitions
│   ├── errors.ts                   # MCP-specific error classes
│   ├── client.ts                   # Main MCPClient implementation
│   ├── registry.ts                 # Tool and resource registry
│   ├── transports/
│   │   ├── index.ts                # Transport exports
│   │   ├── base.ts                 # Base transport interface
│   │   ├── stdio.ts                # Stdio transport (subprocess)
│   │   ├── sse.ts                  # SSE transport (HTTP)
│   │   └── websocket.ts            # WebSocket transport (future)
│   ├── connections/
│   │   ├── index.ts                # Connection exports
│   │   ├── connection.ts           # Server connection manager
│   │   ├── pool.ts                 # Connection pool
│   │   └── health.ts               # Health check utilities
│   ├── cache/
│   │   ├── index.ts                # Cache exports
│   │   ├── capability-cache.ts     # Server capability cache
│   │   └── resource-cache.ts       # Resource content cache
│   ├── security/
│   │   ├── index.ts                # Security exports
│   │   ├── validator.ts            # Configuration validator
│   │   ├── sandbox.ts              # Process sandboxing
│   │   └── rate-limiter.ts         # Rate limiting per server
│   └── utils/
│       ├── index.ts                # Utility exports
│       ├── json-rpc.ts             # JSON-RPC 2.0 utilities
│       ├── retry.ts                # Retry with backoff
│       └── schema-validator.ts     # JSON Schema validation
├── __tests__/
│   ├── unit/
│   │   ├── client.test.ts
│   │   ├── transports/
│   │   │   ├── stdio.test.ts
│   │   │   └── sse.test.ts
│   │   ├── registry.test.ts
│   │   └── cache.test.ts
│   ├── integration/
│   │   ├── stdio-server.integration.test.ts
│   │   ├── sse-server.integration.test.ts
│   │   └── multi-server.integration.test.ts
│   ├── e2e/
│   │   └── github-mcp.e2e.test.ts
│   └── mocks/
│       ├── mock-mcp-server.ts
│       └── fixtures.ts
├── package.json
├── tsconfig.json
└── README.md
```

## Files to Create/Modify

### New Files

| File | Description |
|------|-------------|
| `packages/mcp-client/package.json` | Package configuration |
| `packages/mcp-client/tsconfig.json` | TypeScript configuration |
| `packages/mcp-client/src/index.ts` | Public API exports |
| `packages/mcp-client/src/types.ts` | All type definitions |
| `packages/mcp-client/src/errors.ts` | Custom error classes |
| `packages/mcp-client/src/client.ts` | Main `MCPClient` class |
| `packages/mcp-client/src/registry.ts` | `ToolRegistry` and `ResourceRegistry` |
| `packages/mcp-client/src/transports/*.ts` | Transport implementations |
| `packages/mcp-client/src/connections/*.ts` | Connection management |
| `packages/mcp-client/src/cache/*.ts` | Caching layer |
| `packages/mcp-client/src/security/*.ts` | Security utilities |
| `packages/mcp-client/src/utils/*.ts` | Helper utilities |

### Files to Modify

| File | Changes |
|------|---------|
| `package.json` (root) | Add `@tamma/mcp-client` to workspaces |
| `packages/providers/src/agent-types.ts` | Add MCP tool support to agent configs |
| `packages/intelligence/src/index.ts` | Integrate MCP client for context enrichment |

## Interfaces and Types

### Core Types

```typescript
// packages/mcp-client/src/types.ts

/**
 * MCP Client configuration
 */
export interface MCPClientConfig {
  servers: MCPServerConfig[];
  defaultTimeout: number;        // Default: 30000ms
  retryAttempts: number;         // Default: 3
  retryDelayMs: number;          // Default: 1000ms
  enableCaching: boolean;        // Default: true
  cacheTTLMs: number;            // Default: 300000ms (5 min)
  logLevel: 'debug' | 'info' | 'warn' | 'error';
}

/**
 * MCP Server configuration
 */
export interface MCPServerConfig {
  name: string;
  transport: 'stdio' | 'sse' | 'websocket';

  // Stdio transport options
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  cwd?: string;

  // SSE/WebSocket transport options
  url?: string;
  headers?: Record<string, string>;

  // Common options
  timeout?: number;
  enabled?: boolean;
  autoConnect?: boolean;        // Default: true
  reconnectOnError?: boolean;   // Default: true
  maxReconnectAttempts?: number; // Default: 5

  // Rate limiting
  rateLimitRpm?: number;        // Requests per minute

  // Security
  sandboxed?: boolean;          // Default: true for stdio
}

/**
 * Server connection status
 */
export type ServerStatus =
  | 'disconnected'
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'error';

/**
 * Server information
 */
export interface ServerInfo {
  name: string;
  transport: 'stdio' | 'sse' | 'websocket';
  status: ServerStatus;
  capabilities: ServerCapabilities;
  toolCount: number;
  resourceCount: number;
  lastConnected?: Date;
  lastError?: Error;
  metrics: ServerMetrics;
}

/**
 * Server capabilities as returned by initialize
 */
export interface ServerCapabilities {
  tools?: { listChanged?: boolean };
  resources?: { subscribe?: boolean; listChanged?: boolean };
  prompts?: { listChanged?: boolean };
  logging?: Record<string, unknown>;
  experimental?: Record<string, unknown>;
}

/**
 * Server metrics
 */
export interface ServerMetrics {
  totalRequests: number;
  successfulRequests: number;
  failedRequests: number;
  averageLatencyMs: number;
  lastRequestTime?: Date;
}

/**
 * MCP Tool definition
 */
export interface MCPTool {
  name: string;
  description: string;
  inputSchema: JSONSchema;
  serverName: string;
}

/**
 * Tool invocation result
 */
export interface ToolResult {
  success: boolean;
  content: ToolResultContent[];
  error?: string;
  isStreaming?: boolean;
  metadata?: {
    latencyMs: number;
    serverName: string;
    toolName: string;
  };
}

export type ToolResultContent =
  | { type: 'text'; text: string }
  | { type: 'image'; data: string; mimeType: string }
  | { type: 'resource'; resource: ResourceReference };

/**
 * MCP Resource definition
 */
export interface MCPResource {
  uri: string;
  name: string;
  description?: string;
  mimeType?: string;
  serverName: string;
}

/**
 * Resource content
 */
export interface ResourceContent {
  uri: string;
  mimeType?: string;
  text?: string;
  blob?: Uint8Array;
}

/**
 * Resource reference (for tool results)
 */
export interface ResourceReference {
  uri: string;
  mimeType?: string;
  text?: string;
}

/**
 * JSON Schema type for tool input schemas
 */
export interface JSONSchema {
  type?: string;
  properties?: Record<string, JSONSchema>;
  required?: string[];
  items?: JSONSchema;
  enum?: unknown[];
  description?: string;
  default?: unknown;
  [key: string]: unknown;
}

/**
 * Resource subscription callback
 */
export type ResourceCallback = (content: ResourceContent) => void | Promise<void>;

/**
 * Unsubscribe function
 */
export type Unsubscribe = () => void;

/**
 * MCP Client event types
 */
export type MCPClientEvent =
  | 'server:connected'
  | 'server:disconnected'
  | 'server:error'
  | 'server:reconnecting'
  | 'tool:invoked'
  | 'tool:completed'
  | 'resource:updated';

/**
 * Event handler type
 */
export type EventHandler<T = unknown> = (data: T) => void | Promise<void>;
```

### Main Interface

```typescript
// packages/mcp-client/src/types.ts (continued)

/**
 * MCP Client Interface
 */
export interface IMCPClient {
  // Lifecycle
  initialize(config: MCPClientConfig): Promise<void>;
  dispose(): Promise<void>;

  // Server management
  connectServer(name: string): Promise<void>;
  disconnectServer(name: string): Promise<void>;
  getServerStatus(name: string): ServerStatus;
  getServerInfo(name: string): ServerInfo | undefined;
  listServers(): ServerInfo[];

  // Tool operations
  listTools(serverName?: string): MCPTool[];
  getToolSchema(serverName: string, toolName: string): JSONSchema | undefined;
  invokeTool(
    serverName: string,
    toolName: string,
    args: Record<string, unknown>,
    options?: ToolInvocationOptions
  ): Promise<ToolResult>;

  // Resource operations
  listResources(serverName?: string): MCPResource[];
  readResource(
    serverName: string,
    uri: string,
    options?: ResourceReadOptions
  ): Promise<ResourceContent>;
  subscribeResource(
    serverName: string,
    uri: string,
    callback: ResourceCallback
  ): Unsubscribe;

  // Events
  on<T = unknown>(event: MCPClientEvent, handler: EventHandler<T>): Unsubscribe;

  // Health check
  healthCheck(): Promise<HealthCheckResult>;
}

export interface ToolInvocationOptions {
  timeout?: number;
  signal?: AbortSignal;
  stream?: boolean;
}

export interface ResourceReadOptions {
  timeout?: number;
  useCache?: boolean;
  signal?: AbortSignal;
}

export interface HealthCheckResult {
  healthy: boolean;
  servers: Record<string, {
    status: ServerStatus;
    latencyMs?: number;
    error?: string;
  }>;
}
```

### Transport Interface

```typescript
// packages/mcp-client/src/transports/base.ts

export interface IMCPTransport {
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  send(message: JSONRPCRequest): Promise<void>;
  onMessage(handler: (message: JSONRPCResponse) => void): void;
  onError(handler: (error: Error) => void): void;
  onClose(handler: () => void): void;
  isConnected(): boolean;
}

export interface JSONRPCRequest {
  jsonrpc: '2.0';
  id: number | string;
  method: string;
  params?: Record<string, unknown>;
}

export interface JSONRPCResponse {
  jsonrpc: '2.0';
  id: number | string | null;
  result?: unknown;
  error?: {
    code: number;
    message: string;
    data?: unknown;
  };
}

export interface JSONRPCNotification {
  jsonrpc: '2.0';
  method: string;
  params?: Record<string, unknown>;
}
```

### Error Types

```typescript
// packages/mcp-client/src/errors.ts

export class MCPError extends Error {
  constructor(
    message: string,
    public readonly code: string,
    public readonly serverName?: string,
    public readonly cause?: Error
  ) {
    super(message);
    this.name = 'MCPError';
  }
}

export class MCPConnectionError extends MCPError {
  constructor(serverName: string, message: string, cause?: Error) {
    super(message, 'MCP_CONNECTION_ERROR', serverName, cause);
    this.name = 'MCPConnectionError';
  }
}

export class MCPTimeoutError extends MCPError {
  constructor(serverName: string, operation: string, timeoutMs: number) {
    super(
      `Operation '${operation}' timed out after ${timeoutMs}ms`,
      'MCP_TIMEOUT',
      serverName
    );
    this.name = 'MCPTimeoutError';
  }
}

export class MCPToolError extends MCPError {
  constructor(
    serverName: string,
    toolName: string,
    message: string,
    cause?: Error
  ) {
    super(
      `Tool '${toolName}' failed: ${message}`,
      'MCP_TOOL_ERROR',
      serverName,
      cause
    );
    this.name = 'MCPToolError';
  }
}

export class MCPResourceError extends MCPError {
  constructor(
    serverName: string,
    uri: string,
    message: string,
    cause?: Error
  ) {
    super(
      `Resource '${uri}' error: ${message}`,
      'MCP_RESOURCE_ERROR',
      serverName,
      cause
    );
    this.name = 'MCPResourceError';
  }
}

export class MCPValidationError extends MCPError {
  constructor(message: string, details?: Record<string, unknown>) {
    super(message, 'MCP_VALIDATION_ERROR');
    this.name = 'MCPValidationError';
    if (details) {
      (this as any).details = details;
    }
  }
}

export class MCPRateLimitError extends MCPError {
  constructor(
    serverName: string,
    retryAfterMs?: number
  ) {
    super(
      `Rate limit exceeded for server '${serverName}'`,
      'MCP_RATE_LIMIT',
      serverName
    );
    this.name = 'MCPRateLimitError';
    (this as any).retryAfterMs = retryAfterMs;
  }
}
```

## Implementation Phases

### Phase 1: Core Infrastructure (Week 1)

**Goal**: Basic MCP client with stdio transport

#### Tasks

1. **Task 1.1: Project Setup**
   - Create package structure
   - Configure TypeScript, ESLint, Vitest
   - Set up package.json with dependencies
   - Create base type definitions

2. **Task 1.2: JSON-RPC Utilities**
   - Implement JSON-RPC 2.0 message serialization
   - Create request ID generation
   - Implement response parsing and validation
   - Add notification handling

3. **Task 1.3: Stdio Transport**
   - Implement subprocess spawning
   - Create message framing (newline-delimited JSON)
   - Handle stdin/stdout communication
   - Implement process lifecycle management

4. **Task 1.4: Basic MCPClient**
   - Implement initialize/dispose lifecycle
   - Create server connection management
   - Implement MCP initialization handshake
   - Add basic error handling

**Deliverables**:
- Working stdio transport
- MCPClient can connect to a single stdio server
- MCP initialization handshake works

### Phase 2: Tool & Resource Operations (Week 2)

**Goal**: Complete tool invocation and resource access

#### Tasks

1. **Task 2.1: Tool Registry**
   - Implement tool discovery (tools/list)
   - Create tool schema storage
   - Add tool lookup by name
   - Implement tool filtering

2. **Task 2.2: Tool Invocation**
   - Implement tools/call with argument validation
   - Handle tool responses (text, image, resource)
   - Add streaming tool response support
   - Implement timeout handling

3. **Task 2.3: Resource Registry**
   - Implement resource discovery (resources/list)
   - Create resource URI storage
   - Add resource lookup by URI

4. **Task 2.4: Resource Operations**
   - Implement resources/read
   - Add resource content caching
   - Implement resource subscriptions
   - Handle pagination for large resources

**Deliverables**:
- Tool invocation working end-to-end
- Resource reading with caching
- Argument validation using JSON Schema

### Phase 3: SSE Transport & Multi-Server (Week 3)

**Goal**: HTTP/SSE support and concurrent server connections

#### Tasks

1. **Task 3.1: SSE Transport**
   - Implement EventSource connection
   - Handle session management
   - Implement HTTP POST for requests
   - Add reconnection logic

2. **Task 3.2: Multi-Server Support**
   - Implement connection pool
   - Add concurrent server initialization
   - Create namespaced tool/resource registry
   - Handle server isolation

3. **Task 3.3: Connection Management**
   - Implement automatic reconnection
   - Add connection health monitoring
   - Create server status tracking
   - Implement graceful shutdown

4. **Task 3.4: Event System**
   - Implement event emitter
   - Add typed event handlers
   - Create event logging
   - Implement event filtering

**Deliverables**:
- SSE transport working
- Multiple servers can be connected simultaneously
- Automatic reconnection on failures

### Phase 4: Security & Reliability (Week 4)

**Goal**: Production-ready security and resilience

#### Tasks

1. **Task 4.1: Configuration Validation**
   - Validate server configs on load
   - Sanitize environment variables
   - Check command allowlists (stdio)
   - Validate URLs (SSE)

2. **Task 4.2: Process Sandboxing**
   - Implement resource limits for subprocesses
   - Add process isolation
   - Implement kill timeout
   - Add output size limits

3. **Task 4.3: Rate Limiting**
   - Implement per-server rate limiting
   - Add request queuing
   - Create backpressure handling
   - Log rate limit events

4. **Task 4.4: Retry & Resilience**
   - Implement exponential backoff
   - Add jitter to retries
   - Create circuit breaker pattern
   - Handle transient failures

5. **Task 4.5: Audit Logging**
   - Log all tool invocations
   - Track resource access
   - Record server connections
   - Create audit trail format

**Deliverables**:
- Secure configuration handling
- Rate limiting working
- Comprehensive audit logging

### Phase 5: Built-in Server Support (Week 5)

**Goal**: Pre-configured support for common MCP servers

#### Tasks

1. **Task 5.1: GitHub MCP Server**
   - Document configuration
   - Create integration tests
   - Add example usage
   - Handle GitHub-specific errors

2. **Task 5.2: Filesystem MCP Server**
   - Document allowed paths
   - Add security warnings
   - Create integration tests
   - Implement path validation

3. **Task 5.3: PostgreSQL MCP Server**
   - Document connection setup
   - Add query timeout handling
   - Create integration tests
   - Handle database errors

4. **Task 5.4: Custom Server Template**
   - Create server template
   - Document MCP protocol
   - Add Python example
   - Add Node.js example

**Deliverables**:
- Working GitHub MCP integration
- Filesystem MCP with security
- PostgreSQL MCP connection
- Custom server documentation

### Phase 6: Monitoring & Dashboard (Week 6)

**Goal**: Observability and status monitoring

#### Tasks

1. **Task 6.1: Metrics Collection**
   - Track request latency
   - Count success/failure rates
   - Monitor connection status
   - Calculate throughput

2. **Task 6.2: Health Checks**
   - Implement server ping
   - Create aggregated health endpoint
   - Add degradation detection
   - Implement alerting hooks

3. **Task 6.3: Dashboard Integration**
   - Create status API endpoint
   - Add server list view
   - Implement tool browser
   - Show invocation history

4. **Task 6.4: Documentation**
   - Write API documentation
   - Create usage examples
   - Add troubleshooting guide
   - Document best practices

**Deliverables**:
- Complete metrics collection
- Health check endpoint
- Dashboard integration ready
- Comprehensive documentation

## Dependencies

### External Dependencies

```json
{
  "dependencies": {
    "zod": "^3.23.0",
    "eventemitter3": "^5.0.0",
    "p-queue": "^8.0.0",
    "p-retry": "^6.0.0"
  },
  "devDependencies": {
    "@types/node": "^22.0.0",
    "vitest": "^3.0.0",
    "typescript": "~5.7.0",
    "esbuild": "^0.24.0"
  }
}
```

### Internal Dependencies

- `@tamma/shared` - Common types and utilities
- `@tamma/observability` - Logging and metrics
- `@tamma/providers` - Integration with agent providers

### Peer Dependencies

- Node.js >= 22.0.0 (for subprocess and EventSource support)

## Testing Strategy

### Unit Tests

Coverage target: **90%**

| Component | Test Focus |
|-----------|------------|
| `client.ts` | Lifecycle, configuration, server management |
| `stdio.ts` | Spawning, messaging, error handling |
| `sse.ts` | Connection, session, HTTP requests |
| `registry.ts` | Tool/resource storage, lookup, filtering |
| `cache.ts` | TTL, invalidation, memory limits |
| `rate-limiter.ts` | Limiting, queuing, backpressure |
| `json-rpc.ts` | Serialization, validation, parsing |
| `retry.ts` | Backoff, jitter, max attempts |

### Integration Tests

| Test Suite | Description |
|------------|-------------|
| `stdio-server.integration.test.ts` | End-to-end stdio server communication |
| `sse-server.integration.test.ts` | End-to-end SSE server communication |
| `multi-server.integration.test.ts` | Multiple concurrent server connections |
| `reconnection.integration.test.ts` | Automatic reconnection scenarios |
| `rate-limiting.integration.test.ts` | Rate limit enforcement |

### E2E Tests

| Test Suite | Description |
|------------|-------------|
| `github-mcp.e2e.test.ts` | Real GitHub MCP server (requires token) |
| `filesystem-mcp.e2e.test.ts` | Real filesystem MCP server |

### Mock Server

Create a reusable mock MCP server for testing:

```typescript
// packages/mcp-client/__tests__/mocks/mock-mcp-server.ts

export class MockMCPServer {
  private tools: Map<string, MCPTool> = new Map();
  private resources: Map<string, ResourceContent> = new Map();

  addTool(tool: MCPTool): void;
  addResource(uri: string, content: ResourceContent): void;

  handleRequest(request: JSONRPCRequest): JSONRPCResponse;

  // Control methods for testing
  setLatency(ms: number): void;
  simulateError(method: string, error: Error): void;
  reset(): void;
}
```

## Configuration

### Configuration Schema

```yaml
# tamma.yaml - MCP section
mcp:
  # Global settings
  settings:
    default_timeout_ms: 30000
    retry_attempts: 3
    retry_delay_ms: 1000
    enable_caching: true
    cache_ttl_ms: 300000
    log_level: info

  # Server definitions
  servers:
    - name: github
      transport: stdio
      command: npx
      args: ["@modelcontextprotocol/server-github"]
      env:
        GITHUB_TOKEN: ${GITHUB_TOKEN}
      enabled: true
      rate_limit_rpm: 100
      timeout_ms: 60000

    - name: filesystem
      transport: stdio
      command: npx
      args: ["@modelcontextprotocol/server-filesystem", "/workspace"]
      enabled: true
      sandboxed: true
      rate_limit_rpm: 1000

    - name: postgres
      transport: stdio
      command: npx
      args: ["@modelcontextprotocol/server-postgres"]
      env:
        DATABASE_URL: ${DATABASE_URL}
      enabled: true
      timeout_ms: 120000

    - name: slack
      transport: sse
      url: http://localhost:3001/mcp
      headers:
        Authorization: "Bearer ${SLACK_TOKEN}"
      enabled: false
      reconnect_on_error: true

    - name: custom
      transport: stdio
      command: python
      args: ["./scripts/custom_mcp_server.py"]
      enabled: false
      cwd: /workspace/custom-server
```

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `MCP_DEFAULT_TIMEOUT` | Default operation timeout | No |
| `MCP_LOG_LEVEL` | Logging verbosity | No |
| `GITHUB_TOKEN` | GitHub API token | For GitHub MCP |
| `DATABASE_URL` | PostgreSQL connection string | For PostgreSQL MCP |
| `SLACK_TOKEN` | Slack API token | For Slack MCP |

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Server connection success rate | > 99% | Successful connects / total connects |
| Tool invocation latency (p95) | < 500ms | Time from call to response |
| Resource read latency (p95) | < 200ms | Time from read to response |
| Reconnection success rate | > 95% | Successful reconnects / disconnect events |
| Cache hit rate | > 70% | Cache hits / total reads |
| Zero security incidents | 0 | Audit log analysis |
| Test coverage | > 90% | Unit + integration tests |

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| MCP server compatibility | Test with official MCP SDK servers first |
| Process hangs (stdio) | Implement kill timeout and process monitoring |
| Memory leaks | Implement proper cleanup, use WeakMap where appropriate |
| Rate limit cascades | Use circuit breaker pattern, backoff |
| Configuration secrets leak | Never log secrets, use redaction |

## Implementation Order Summary

```
Week 1: Core Infrastructure
├── Project setup and types
├── JSON-RPC utilities
├── Stdio transport
└── Basic MCPClient

Week 2: Tool & Resource Operations
├── Tool registry and discovery
├── Tool invocation with validation
├── Resource registry
└── Resource operations with caching

Week 3: SSE Transport & Multi-Server
├── SSE transport implementation
├── Multi-server connection pool
├── Connection management
└── Event system

Week 4: Security & Reliability
├── Configuration validation
├── Process sandboxing
├── Rate limiting
├── Retry and circuit breaker
└── Audit logging

Week 5: Built-in Server Support
├── GitHub MCP integration
├── Filesystem MCP integration
├── PostgreSQL MCP integration
└── Custom server template

Week 6: Monitoring & Dashboard
├── Metrics collection
├── Health checks
├── Dashboard integration
└── Documentation
```

## Related Stories

- **Story 6-1**: Codebase Indexer - May use filesystem MCP
- **Story 6-3**: RAG Pipeline - Consumes tools from MCP servers
- **Story 6-5**: Context Aggregator - Coordinates MCP with other context sources
- **Story 6-6**: Knowledge Base UI - Displays MCP server status

## References

- [MCP Specification](https://modelcontextprotocol.io/specification)
- [MCP TypeScript SDK](https://github.com/modelcontextprotocol/typescript-sdk)
- [MCP Reference Servers](https://github.com/modelcontextprotocol/servers)
- [Story 6-4: MCP Client Integration](./6-4-mcp-client-integration.md)
- [Engine Flow Architecture](../../../architecture/engine-flow.md)
- [Provider Research](../../../architecture/provider-research.md)

---

**Created**: 2025-02-05
**Author**: Tamma Development Team
**Target Start**: TBD
**Target Completion**: TBD
