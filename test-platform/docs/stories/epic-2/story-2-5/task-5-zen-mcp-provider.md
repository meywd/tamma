# Task 5: Zen MCP Provider Implementation

## Overview

This task implements the Zen MCP (Model Context Protocol) provider integration, enabling access to MCP-compliant models through the unified provider interface. MCP is a standardized protocol for AI model communication that provides consistent context management, tool use, and model capabilities.

## Acceptance Criteria

### 5.1: Implement Model Context Protocol client

- [ ] Create MCP client with proper protocol implementation
- [ ] Add support for MCP protocol versioning
- [ ] Implement JSON-RPC 2.0 communication
- [ ] Add support for both HTTP and WebSocket transports
- [ ] Implement proper error handling and protocol validation

### 5.2: Add MCP-compliant model access

- [ ] Implement MCP model discovery and enumeration
- [ ] Add support for MCP model capabilities negotiation
- [ ] Implement MCP context management and persistence
- [ ] Add support for MCP tool/function calling
- [ ] Implement MCP streaming and batch processing

### 5.3: Implement context management

- [ ] Create context storage and retrieval system
- [ ] Implement context versioning and history
- [ ] Add context sharing between model instances
- [ ] Implement context compression and optimization
- [ ] Add context privacy and security controls

### 5.4: Add streaming and function calling

- [ ] Implement MCP streaming protocol support
- [ ] Add real-time context updates during streaming
- [ ] Implement MCP function/tool calling protocol
- [ ] Add tool result streaming and aggregation
- [ ] Implement concurrent tool execution management

### 5.5: Create MCP-specific configuration

- [ ] Implement MCP server configuration management
- [ ] Add support for multiple MCP server instances
- [ ] Implement MCP protocol version negotiation
- [ ] Add MCP security and authentication configuration
- [ ] Create MCP capability and feature configuration

## Technical Implementation

### Provider Architecture

```typescript
// src/providers/implementations/zen-mcp-provider.ts
export class ZenMCPProvider implements IAIProvider {
  private readonly client: MCPClient;
  private readonly config: ZenMCPConfig;
  private readonly modelManager: MCPModelManager;
  private readonly contextManager: MCPContextManager;
  private readonly rateLimiter: MCPRateLimiter;
  private readonly logger: Logger;

  constructor(config: ZenMCPConfig) {
    this.config = this.validateConfig(config);
    this.client = new MCPClient(this.config);
    this.modelManager = new MCPModelManager(this.client);
    this.contextManager = new MCPContextManager(this.config.context);
    this.rateLimiter = new MCPRateLimiter(this.config.rateLimits);
    this.logger = new Logger({ service: 'zen-mcp-provider' });
  }

  async initialize(): Promise<void> {
    await this.client.connect();
    await this.modelManager.discoverModels();
    await this.contextManager.initialize();
    await this.rateLimiter.initialize();
    this.logger.info('Zen MCP provider initialized');
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    await this.rateLimiter.acquire();

    try {
      const model = await this.modelManager.getModel(request.model);
      const context = await this.contextManager.getOrCreateContext(request);

      return this.handleMCPRequest(model, request, context);
    } catch (error) {
      this.handleProviderError(error);
      throw error;
    }
  }

  async getCapabilities(): Promise<ProviderCapabilities> {
    return {
      models: await this.modelManager.getAvailableModels(),
      features: {
        streaming: true,
        functionCalling: true,
        multimodal: false,
        toolUse: true,
        contextManagement: true,
        toolStreaming: true,
        concurrentTools: true,
      },
      limits: {
        maxTokens: 16384,
        maxRequestsPerMinute: this.config.rateLimits.requestsPerMinute,
        maxTokensPerMinute: this.config.rateLimits.tokensPerMinute,
      },
    };
  }

  async dispose(): Promise<void> {
    await this.rateLimiter.dispose();
    await this.contextManager.dispose();
    await this.client.disconnect();
    this.logger.info('Zen MCP provider disposed');
  }
}
```

### MCP Client Implementation

```typescript
// src/providers/implementations/mcp-client.ts
export class MCPClient {
  private readonly config: ZenMCPConfig;
  private readonly transport: MCPTransport;
  private readonly protocol: MCPProtocol;
  private readonly logger: Logger;
  private connectionState: ConnectionState = 'disconnected';

  constructor(config: ZenMCPConfig) {
    this.config = config;
    this.transport = this.createTransport();
    this.protocol = new MCPProtocol(this.transport);
    this.logger = new Logger({ service: 'mcp-client' });
  }

  async connect(): Promise<void> {
    try {
      await this.transport.connect();
      await this.protocol.initialize();
      await this.negotiateProtocol();
      this.connectionState = 'connected';
      this.logger.info('MCP client connected successfully');
    } catch (error) {
      this.connectionState = 'error';
      throw new MCPError('CONNECTION_FAILED', 'Failed to connect to MCP server', error);
    }
  }

  async disconnect(): Promise<void> {
    try {
      if (this.connectionState === 'connected') {
        await this.protocol.close();
        await this.transport.disconnect();
      }
      this.connectionState = 'disconnected';
      this.logger.info('MCP client disconnected');
    } catch (error) {
      this.logger.error('Error during disconnect:', error);
    }
  }

  async listModels(): Promise<MCPModel[]> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('models/list', {});
      return response.models.map(this.transformModelData);
    } catch (error) {
      throw new MCPError('MODEL_LIST_FAILED', 'Failed to list models', error);
    }
  }

  async getModelInfo(modelId: string): Promise<MCPModelInfo> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('models/get', { id: modelId });
      return this.transformModelInfo(response.model);
    } catch (error) {
      throw new MCPError('MODEL_INFO_FAILED', `Failed to get model info for ${modelId}`, error);
    }
  }

  async createCompletion(
    request: MCPCompletionRequest
  ): Promise<AsyncIterable<MCPCompletionChunk>> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('completion/create', request, {
        stream: true,
      });

      return this.streamCompletionResponse(response);
    } catch (error) {
      throw new MCPError('COMPLETION_FAILED', 'Completion request failed', error);
    }
  }

  async createChatCompletion(
    request: MCPChatCompletionRequest
  ): Promise<AsyncIterable<MCPChatCompletionChunk>> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('chat/completion/create', request, {
        stream: true,
      });

      return this.streamChatCompletionResponse(response);
    } catch (error) {
      throw new MCPError('CHAT_COMPLETION_FAILED', 'Chat completion request failed', error);
    }
  }

  async callTool(request: MCPToolCallRequest): Promise<AsyncIterable<MCPToolResultChunk>> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('tools/call', request, {
        stream: true,
      });

      return this.streamToolResultResponse(response);
    } catch (error) {
      throw new MCPError('TOOL_CALL_FAILED', 'Tool call failed', error);
    }
  }

  async createContext(context: MCPContextData): Promise<MCPContext> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('context/create', context);
      return response.context;
    } catch (error) {
      throw new MCPError('CONTEXT_CREATE_FAILED', 'Failed to create context', error);
    }
  }

  async getContext(contextId: string): Promise<MCPContext> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('context/get', { id: contextId });
      return response.context;
    } catch (error) {
      throw new MCPError('CONTEXT_GET_FAILED', `Failed to get context ${contextId}`, error);
    }
  }

  async updateContext(contextId: string, updates: MCPContextUpdate): Promise<MCPContext> {
    this.ensureConnected();

    try {
      const response = await this.protocol.sendRequest('context/update', {
        id: contextId,
        ...updates,
      });
      return response.context;
    } catch (error) {
      throw new MCPError('CONTEXT_UPDATE_FAILED', `Failed to update context ${contextId}`, error);
    }
  }

  async deleteContext(contextId: string): Promise<void> {
    this.ensureConnected();

    try {
      await this.protocol.sendRequest('context/delete', { id: contextId });
    } catch (error) {
      throw new MCPError('CONTEXT_DELETE_FAILED', `Failed to delete context ${contextId}`, error);
    }
  }

  private createTransport(): MCPTransport {
    switch (this.config.transport.type) {
      case 'http':
        return new MCPHttpTransport(this.config.transport);
      case 'websocket':
        return new MCPWebSocketTransport(this.config.transport);
      default:
        throw new Error(`Unsupported transport type: ${this.config.transport.type}`);
    }
  }

  private async negotiateProtocol(): Promise<void> {
    try {
      const response = await this.protocol.sendRequest('protocol/negotiate', {
        clientVersion: this.config.protocolVersion || '1.0.0',
        supportedVersions: ['1.0.0', '0.9.0'],
        capabilities: {
          streaming: true,
          tools: true,
          context: true,
          batch: true,
        },
      });

      if (!response.compatible) {
        throw new Error(
          `Protocol version mismatch. Server: ${response.serverVersion}, Client: ${this.config.protocolVersion}`
        );
      }

      this.logger.info(`Protocol negotiated: ${response.agreedVersion}`);
    } catch (error) {
      throw new MCPError('PROTOCOL_NEGOTIATION_FAILED', 'Protocol negotiation failed', error);
    }
  }

  private ensureConnected(): void {
    if (this.connectionState !== 'connected') {
      throw new MCPError('NOT_CONNECTED', 'MCP client is not connected');
    }
  }

  private transformModelData(data: any): MCPModel {
    return {
      id: data.id,
      name: data.name,
      provider: 'zen-mcp',
      version: data.version,
      capabilities: {
        maxTokens: data.max_tokens,
        streaming: data.streaming,
        functionCalling: data.function_calling || false,
        multimodal: data.multimodal || false,
        toolUse: data.tools || false,
        contextManagement: data.context || false,
        toolStreaming: data.tool_streaming || false,
        concurrentTools: data.concurrent_tools || false,
        supportedTools: data.supported_tools || [],
        contextWindow: data.context_window,
      },
      metadata: {
        description: data.description,
        category: data.category || 'general',
        architecture: data.architecture,
        mcpVersion: data.mcp_version,
        serverInfo: data.server_info,
        features: data.features || [],
        pricing: data.pricing,
        tags: data.tags || [],
      },
    };
  }

  private transformModelInfo(data: any): MCPModelInfo {
    return {
      ...this.transformModelData(data),
      details: {
        parameters: data.parameters,
        layers: data.layers,
        attentionHeads: data.attention_heads,
        hiddenSize: data.hidden_size,
        vocabularySize: data.vocabulary_size,
        mcpCapabilities: data.mcp_capabilities,
        supportedProtocols: data.supported_protocols,
        toolDefinitions: data.tool_definitions || [],
      },
      benchmarks: data.benchmarks || {},
      usage: data.usage_stats || {},
    };
  }

  private async *streamCompletionResponse(response: any): AsyncIterable<MCPCompletionChunk> {
    if (response.stream) {
      for await (const chunk of response.stream) {
        yield {
          text: chunk.content || '',
          done: chunk.done || false,
          metadata: {
            model: chunk.model,
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
          },
        };
      }
    } else {
      // Handle non-streaming response
      yield {
        text: response.content || '',
        done: true,
        metadata: {
          model: response.model,
          usage: response.usage,
          timestamp: new Date().toISOString(),
        },
      };
    }
  }

  private async *streamChatCompletionResponse(
    response: any
  ): AsyncIterable<MCPChatCompletionChunk> {
    if (response.stream) {
      for await (const chunk of response.stream) {
        yield {
          content: chunk.content || '',
          done: chunk.done || false,
          metadata: {
            model: chunk.model,
            usage: chunk.usage,
            timestamp: new Date().toISOString(),
          },
        };
      }
    } else {
      // Handle non-streaming response
      yield {
        content: response.content || '',
        done: true,
        metadata: {
          model: response.model,
          usage: response.usage,
          timestamp: new Date().toISOString(),
        },
      };
    }
  }

  private async *streamToolResultResponse(response: any): AsyncIterable<MCPToolResultChunk> {
    if (response.stream) {
      for await (const chunk of response.stream) {
        yield {
          toolName: chunk.tool_name,
          result: chunk.result || '',
          done: chunk.done || false,
          metadata: {
            model: chunk.model,
            toolCallId: chunk.tool_call_id,
            timestamp: new Date().toISOString(),
          },
        };
      }
    } else {
      // Handle non-streaming response
      yield {
        toolName: response.tool_name,
        result: response.result || '',
        done: true,
        metadata: {
          model: response.model,
          toolCallId: response.tool_call_id,
          timestamp: new Date().toISOString(),
        },
      };
    }
  }
}
```

### MCP Protocol Implementation

```typescript
// src/providers/implementations/mcp-protocol.ts
export class MCPProtocol {
  private readonly transport: MCPTransport;
  private readonly requestId: number;
  private readonly pendingRequests: Map<number, PendingRequest>;
  private readonly logger: Logger;

  constructor(transport: MCPTransport) {
    this.transport = transport;
    this.requestId = 0;
    this.pendingRequests = new Map();
    this.logger = new Logger({ service: 'mcp-protocol' });
  }

  async initialize(): Promise<void> {
    // Set up message handlers
    this.transport.onMessage(this.handleMessage.bind(this));
    this.transport.onError(this.handleError.bind(this));
    this.transport.onClose(this.handleClose.bind(this));
  }

  async sendRequest(method: string, params: any, options: RequestOptions = {}): Promise<any> {
    const id = this.getNextRequestId();
    const request: MCPRequest = {
      jsonrpc: '2.0',
      id,
      method,
      params,
    };

    const pendingRequest: PendingRequest = {
      id,
      method,
      resolve: null as any,
      reject: null as any,
      timeout: setTimeout(() => {
        this.pendingRequests.delete(id);
        pendingRequest.reject?.(new MCPError('TIMEOUT', `Request timeout: ${method}`));
      }, options.timeout || 30000),
    };

    this.pendingRequests.set(id, pendingRequest);

    return new Promise((resolve, reject) => {
      pendingRequest.resolve = resolve;
      pendingRequest.reject = reject;

      this.transport.send(request).catch(reject);
    });
  }

  async sendNotification(method: string, params: any): Promise<void> {
    const notification: MCPNotification = {
      jsonrpc: '2.0',
      method,
      params,
    };

    await this.transport.send(notification);
  }

  async close(): Promise<void> {
    // Clear all pending requests
    for (const [id, request] of this.pendingRequests) {
      clearTimeout(request.timeout);
      request.reject?.(new MCPError('CONNECTION_CLOSED', 'Connection closed'));
    }
    this.pendingRequests.clear();
  }

  private handleMessage(message: any): void {
    try {
      if (this.isResponse(message)) {
        this.handleResponse(message);
      } else if (this.isNotification(message)) {
        this.handleNotification(message);
      } else {
        this.logger.warn('Unknown message type:', message);
      }
    } catch (error) {
      this.logger.error('Error handling message:', error);
    }
  }

  private handleResponse(message: MCPResponse): void {
    const pendingRequest = this.pendingRequests.get(message.id);
    if (!pendingRequest) {
      this.logger.warn('Received response for unknown request:', message.id);
      return;
    }

    clearTimeout(pendingRequest.timeout);
    this.pendingRequests.delete(message.id);

    if (message.error) {
      pendingRequest.reject?.(
        new MCPError(
          message.error.code || 'UNKNOWN_ERROR',
          message.error.message || 'Unknown error',
          message.error.data
        )
      );
    } else {
      pendingRequest.resolve?.(message.result);
    }
  }

  private handleNotification(message: MCPNotification): void {
    switch (message.method) {
      case 'context/updated':
        this.handleContextUpdated(message.params);
        break;
      case 'tool/progress':
        this.handleToolProgress(message.params);
        break;
      case 'server/status':
        this.handleServerStatus(message.params);
        break;
      default:
        this.logger.debug('Unhandled notification:', message.method);
    }
  }

  private handleError(error: Error): void {
    this.logger.error('Transport error:', error);

    // Reject all pending requests
    for (const [id, request] of this.pendingRequests) {
      clearTimeout(request.timeout);
      request.reject?.(new MCPError('TRANSPORT_ERROR', 'Transport error', error));
    }
    this.pendingRequests.clear();
  }

  private handleClose(): void {
    this.logger.info('Transport closed');

    // Reject all pending requests
    for (const [id, request] of this.pendingRequests) {
      clearTimeout(request.timeout);
      request.reject?.(new MCPError('CONNECTION_CLOSED', 'Connection closed'));
    }
    this.pendingRequests.clear();
  }

  private handleContextUpdated(params: any): void {
    // Emit context update event
    this.emit('contextUpdated', params);
  }

  private handleToolProgress(params: any): void {
    // Emit tool progress event
    this.emit('toolProgress', params);
  }

  private handleServerStatus(params: any): void {
    // Emit server status event
    this.emit('serverStatus', params);
  }

  private isResponse(message: any): message is MCPResponse {
    return message.jsonrpc === '2.0' && typeof message.id === 'number';
  }

  private isNotification(message: any): message is MCPNotification {
    return message.jsonrpc === '2.0' && typeof message.id === 'undefined';
  }

  private getNextRequestId(): number {
    return ++this.requestId;
  }

  private emit(event: string, data: any): void {
    // Simple event emission - could be enhanced with EventEmitter
    this.logger.debug(`Event: ${event}`, data);
  }
}
```

### Context Manager Implementation

```typescript
// src/providers/implementations/mcp-context-manager.ts
export class MCPContextManager {
  private readonly config: MCPContextConfig;
  private readonly contexts: Map<string, MCPContext>;
  private readonly storage: ContextStorage;
  private readonly logger: Logger;

  constructor(config: MCPContextConfig) {
    this.config = config;
    this.contexts = new Map();
    this.storage = new ContextStorage(config.storage);
    this.logger = new Logger({ service: 'mcp-context-manager' });
  }

  async initialize(): Promise<void> {
    await this.storage.initialize();
    await this.loadPersistedContexts();
    this.logger.info('MCP context manager initialized');
  }

  async getOrCreateContext(request: MessageRequest): Promise<MCPContext> {
    const contextId = this.generateContextId(request);

    // Check if context already exists
    let context = this.contexts.get(contextId);
    if (context) {
      return this.updateContext(context, request);
    }

    // Create new context
    context = await this.createContext(request, contextId);
    this.contexts.set(contextId, context);

    return context;
  }

  async getContext(contextId: string): Promise<MCPContext | null> {
    let context = this.contexts.get(contextId);

    if (!context) {
      // Try to load from storage
      context = await this.storage.load(contextId);
      if (context) {
        this.contexts.set(contextId, context);
      }
    }

    return context || null;
  }

  async updateContext(contextId: string, updates: MCPContextUpdate): Promise<MCPContext> {
    const context = await this.getContext(contextId);
    if (!context) {
      throw new Error(`Context not found: ${contextId}`);
    }

    const updatedContext = this.applyContextUpdates(context, updates);
    this.contexts.set(contextId, updatedContext);

    // Persist if needed
    if (this.config.persistence.enabled) {
      await this.storage.save(updatedContext);
    }

    return updatedContext;
  }

  async deleteContext(contextId: string): Promise<void> {
    this.contexts.delete(contextId);

    if (this.config.persistence.enabled) {
      await this.storage.delete(contextId);
    }
  }

  async cleanup(): Promise<void> {
    const now = Date.now();
    const maxAge = this.config.retention.maxAge * 1000; // Convert to milliseconds

    for (const [id, context] of this.contexts) {
      const age = now - new Date(context.lastUpdated).getTime();

      if (age > maxAge) {
        await this.deleteContext(id);
        this.logger.debug(`Cleaned up old context: ${id}`);
      }
    }
  }

  async dispose(): Promise<void> {
    // Save all contexts if persistence is enabled
    if (this.config.persistence.enabled) {
      for (const context of this.contexts.values()) {
        await this.storage.save(context);
      }
    }

    this.contexts.clear();
    await this.storage.dispose();
  }

  private generateContextId(request: MessageRequest): string {
    // Generate context ID based on request characteristics
    const hash = crypto.createHash('sha256');
    hash.update(
      JSON.stringify({
        model: request.model,
        systemInstruction: request.systemInstruction,
        userHash: this.hashUserMessages(request.messages),
      })
    );

    return hash.digest('hex').substring(0, 16);
  }

  private hashUserMessages(messages: Message[]): string {
    const userMessages = messages.filter((msg) => msg.role === 'user');
    const hash = crypto.createHash('sha256');
    hash.update(JSON.stringify(userMessages));
    return hash.digest('hex');
  }

  private async createContext(request: MessageRequest, contextId: string): Promise<MCPContext> {
    const context: MCPContext = {
      id: contextId,
      model: request.model,
      systemInstruction: request.systemInstruction,
      messages: [],
      metadata: {
        createdAt: new Date().toISOString(),
        lastUpdated: new Date().toISOString(),
        version: 1,
        tags: this.generateContextTags(request),
      },
      storage: {
        compressed: false,
        encrypted: this.config.security.encryption,
      },
    };

    // Add initial messages
    for (const message of request.messages) {
      await this.addMessageToContext(context, message);
    }

    return context;
  }

  private updateContext(context: MCPContext, request: MessageRequest): Promise<MCPContext> {
    // Add new messages to existing context
    const existingMessageHashes = new Set(context.messages.map((msg) => this.hashMessage(msg)));

    for (const message of request.messages) {
      const messageHash = this.hashMessage(message);
      if (!existingMessageHashes.has(messageHash)) {
        this.addMessageToContext(context, message);
      }
    }

    // Update metadata
    context.metadata.lastUpdated = new Date().toISOString();
    context.metadata.version += 1;

    return context;
  }

  private async addMessageToContext(context: MCPContext, message: Message): Promise<void> {
    const contextMessage: MCPContextMessage = {
      ...message,
      id: crypto.randomUUID(),
      timestamp: new Date().toISOString(),
      tokens: this.estimateTokens(message.content),
    };

    context.messages.push(contextMessage);

    // Check if context needs compression
    if (this.shouldCompressContext(context)) {
      await this.compressContext(context);
    }
  }

  private hashMessage(message: Message): string {
    const hash = crypto.createHash('sha256');
    hash.update(JSON.stringify(message));
    return hash.digest('hex');
  }

  private estimateTokens(content: string): number {
    // Simple token estimation - could be enhanced with proper tokenizer
    return Math.ceil(content.length / 4);
  }

  private shouldCompressContext(context: MCPContext): boolean {
    const totalTokens = context.messages.reduce((sum, msg) => sum + msg.tokens, 0);
    return totalTokens > this.config.compression.threshold;
  }

  private async compressContext(context: MCPContext): Promise<void> {
    if (context.storage.compressed) {
      return; // Already compressed
    }

    // Simple compression - remove old messages while preserving recent ones
    const maxMessages = this.config.compression.maxMessages;
    if (context.messages.length > maxMessages) {
      const keepCount = Math.floor(maxMessages * 0.8); // Keep 80% of max
      context.messages = context.messages.slice(-keepCount);
      context.storage.compressed = true;
      context.metadata.compressedAt = new Date().toISOString();
    }
  }

  private applyContextUpdates(context: MCPContext, updates: MCPContextUpdate): MCPContext {
    const updatedContext = { ...context };

    if (updates.messages) {
      updatedContext.messages = [...context.messages, ...updates.messages];
    }

    if (updates.metadata) {
      updatedContext.metadata = { ...context.metadata, ...updates.metadata };
    }

    if (updates.storage) {
      updatedContext.storage = { ...context.storage, ...updates.storage };
    }

    updatedContext.metadata.lastUpdated = new Date().toISOString();
    updatedContext.metadata.version += 1;

    return updatedContext;
  }

  private generateContextTags(request: MessageRequest): string[] {
    const tags: string[] = [];

    // Add model-specific tags
    tags.push(`model:${request.model}`);

    // Add content-based tags
    const content = request.messages
      .map((msg) => msg.content)
      .join(' ')
      .toLowerCase();

    if (content.includes('code') || content.includes('function')) {
      tags.push('code');
    }

    if (content.includes('debug') || content.includes('error')) {
      tags.push('debugging');
    }

    if (content.includes('test') || content.includes('spec')) {
      tags.push('testing');
    }

    return tags;
  }

  private async loadPersistedContexts(): Promise<void> {
    if (!this.config.persistence.enabled) {
      return;
    }

    try {
      const contexts = await this.storage.loadAll();
      for (const context of contexts) {
        this.contexts.set(context.id, context);
      }

      this.logger.info(`Loaded ${contexts.length} persisted contexts`);
    } catch (error) {
      this.logger.error('Failed to load persisted contexts:', error);
    }
  }
}
```

### Configuration Schema

```typescript
// src/providers/configs/zen-mcp-config.schema.ts
export const ZenMCPConfigSchema = {
  type: 'object',
  required: ['transport', 'rateLimits'],
  properties: {
    transport: {
      type: 'object',
      required: ['type'],
      oneOf: [
        {
          title: 'HTTP Transport',
          properties: {
            type: { const: 'http' },
            url: {
              type: 'string',
              format: 'uri',
              description: 'MCP server HTTP URL',
            },
            headers: {
              type: 'object',
              description: 'Additional HTTP headers',
            },
            timeout: {
              type: 'integer',
              default: 30000,
              description: 'HTTP request timeout in milliseconds',
            },
          },
        },
        {
          title: 'WebSocket Transport',
          properties: {
            type: { const: 'websocket' },
            url: {
              type: 'string',
              format: 'uri',
              description: 'MCP server WebSocket URL',
            },
            protocols: {
              type: 'array',
              items: { type: 'string' },
              default: ['mcp-v1'],
              description: 'WebSocket subprotocols',
            },
            pingInterval: {
              type: 'integer',
              default: 30000,
              description: 'WebSocket ping interval in milliseconds',
            },
            reconnect: {
              type: 'object',
              properties: {
                enabled: { type: 'boolean', default: true },
                maxAttempts: { type: 'integer', default: 5 },
                delay: { type: 'integer', default: 1000 },
              },
            },
          },
        },
      ],
    },
    protocolVersion: {
      type: 'string',
      default: '1.0.0',
      description: 'MCP protocol version',
    },
    authentication: {
      type: 'object',
      properties: {
        type: {
          type: 'string',
          enum: ['none', 'token', 'certificate', 'oauth'],
          default: 'none',
        },
        token: {
          type: 'string',
          description: 'Authentication token',
        },
        certificate: {
          type: 'object',
          properties: {
            cert: { type: 'string' },
            key: { type: 'string' },
            ca: { type: 'string' },
          },
        },
        oauth: {
          type: 'object',
          properties: {
            clientId: { type: 'string' },
            clientSecret: { type: 'string' },
            tokenUrl: { type: 'string' },
          },
        },
      },
    },
    rateLimits: {
      type: 'object',
      required: ['requestsPerMinute', 'tokensPerMinute'],
      properties: {
        requestsPerMinute: {
          type: 'integer',
          minimum: 1,
          maximum: 1000,
          default: 60,
          description: 'Maximum requests per minute',
        },
        tokensPerMinute: {
          type: 'integer',
          minimum: 1000,
          maximum: 1000000,
          default: 60000,
          description: 'Maximum tokens per minute',
        },
        concurrentRequests: {
          type: 'integer',
          minimum: 1,
          maximum: 20,
          default: 5,
          description: 'Maximum concurrent requests',
        },
      },
    },
    context: {
      type: 'object',
      properties: {
        persistence: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            storage: {
              type: 'string',
              enum: ['memory', 'file', 'database'],
              default: 'memory',
            },
            path: {
              type: 'string',
              description: 'Storage path for file-based persistence',
            },
          },
        },
        compression: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            threshold: {
              type: 'integer',
              default: 10000,
              description: 'Token threshold for compression',
            },
            maxMessages: {
              type: 'integer',
              default: 100,
              description: 'Maximum messages after compression',
            },
          },
        },
        retention: {
          type: 'object',
          properties: {
            maxAge: {
              type: 'integer',
              default: 86400,
              description: 'Maximum age in seconds',
            },
            maxContexts: {
              type: 'integer',
              default: 1000,
              description: 'Maximum number of contexts',
            },
          },
        },
        security: {
          type: 'object',
          properties: {
            encryption: { type: 'boolean', default: false },
            encryptionKey: { type: 'string' },
            accessControl: { type: 'boolean', default: false },
          },
        },
      },
    },
    features: {
      type: 'object',
      properties: {
        streaming: {
          type: 'boolean',
          default: true,
          description: 'Enable streaming responses',
        },
        tools: {
          type: 'boolean',
          default: true,
          description: 'Enable tool/function calling',
        },
        batch: {
          type: 'boolean',
          default: true,
          description: 'Enable batch processing',
        },
        compression: {
          type: 'boolean',
          default: true,
          description: 'Enable response compression',
        },
      },
    },
    timeout: {
      type: 'integer',
      minimum: 5000,
      maximum: 300000,
      default: 30000,
      description: 'Default request timeout in milliseconds',
    },
  },
};
```

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/zen-mcp-provider.test.ts
describe('ZenMCPProvider', () => {
  let provider: ZenMCPProvider;
  let mockClient: jest.Mocked<MCPClient>;

  beforeEach(() => {
    mockClient = new MCPClient({
      transport: { type: 'http', url: 'http://localhost:8080' },
    } as any) as jest.Mocked<MCPClient>;

    provider = new ZenMCPProvider({
      transport: { type: 'http', url: 'http://localhost:8080' },
      rateLimits: {
        requestsPerMinute: 60,
        tokensPerMinute: 60000,
      },
    });
  });

  describe('MCP protocol', () => {
    it('should negotiate protocol version', async () => {
      mockClient.connect.mockResolvedValue();

      await provider.initialize();

      expect(mockClient.connect).toHaveBeenCalled();
    });
  });

  describe('context management', () => {
    it('should create and manage contexts', async () => {
      const request: MessageRequest = {
        model: 'test-model',
        messages: [{ role: 'user', content: 'Hello' }],
      };

      const mockContext = { id: 'test-context', model: 'test-model' };
      mockClient.createContext.mockResolvedValue(mockContext);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThan(0);
    });
  });

  describe('tool calling', () => {
    it('should handle MCP tool calls', async () => {
      const request: MessageRequest = {
        model: 'test-model',
        messages: [{ role: 'user', content: 'Call a tool' }],
        tools: [
          {
            name: 'test_tool',
            description: 'Test tool',
            parameters: { type: 'object' },
          },
        ],
      };

      const mockToolStream = createMockToolStream();
      mockClient.callTool.mockResolvedValue(mockToolStream);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.some((c) => c.metadata.toolCall)).toBe(true);
    });
  });
});
```

### Integration Tests

```typescript
// tests/providers/zen-mcp-integration.test.ts
describe('Zen MCP Integration', () => {
  let provider: ZenMCPProvider;

  beforeAll(async () => {
    provider = new ZenMCPProvider({
      transport: {
        type: 'websocket',
        url: process.env.ZEN_MCP_WS_URL_TEST || 'ws://localhost:8080',
      },
      rateLimits: {
        requestsPerMinute: 30,
        tokensPerMinute: 30000,
      },
    });

    await provider.initialize();
  });

  it('should handle MCP chat completions', async () => {
    const request: MessageRequest = {
      model: 'zen-mcp-model',
      messages: [
        {
          role: 'user',
          content: 'Explain the Model Context Protocol',
        },
      ],
      maxTokens: 200,
    };

    const response = await collectStream(provider.sendMessage(request));

    expect(response.content).toContain('protocol');
    expect(response.metadata.usage.totalTokens).toBeGreaterThan(0);
  });

  it('should manage contexts across requests', async () => {
    const request1: MessageRequest = {
      model: 'zen-mcp-model',
      messages: [{ role: 'user', content: 'My name is Alice' }],
    };

    const request2: MessageRequest = {
      model: 'zen-mcp-model',
      messages: [
        { role: 'user', content: 'My name is Alice' },
        { role: 'assistant', content: 'Hello Alice!' },
        { role: 'user', content: 'What is my name?' },
      ],
    };

    const response1 = await collectStream(provider.sendMessage(request1));
    const response2 = await collectStream(provider.sendMessage(request2));

    expect(response2.content).toContain('Alice');
  });
});
```

## Success Metrics

### Performance Metrics

- MCP connection time: < 2s
- Protocol negotiation time: < 500ms
- Context creation time: < 100ms
- Message processing latency: < 200ms
- Tool execution latency: < 1s

### Reliability Metrics

- MCP connection success rate: 99%
- Protocol compliance: 100%
- Context persistence accuracy: 99.9%
- Error handling coverage: 100%
- Retry success rate: 95%

### Integration Metrics

- Model discovery accuracy: 100%
- Context management efficiency: 95%+
- Tool calling success rate: 90%+
- Test coverage: 90%+

## Dependencies

### External Dependencies

- `ws`: WebSocket client for MCP transport
- `axios`: HTTP client for MCP transport
- `json-rpc-2.0`: JSON-RPC 2.0 protocol implementation

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/errors`: Error handling utilities
- `@tamma/core/logging`: Logging utilities
- `@tamma/core/config`: Configuration management

## Security Considerations

### Protocol Security

- Validate all MCP protocol messages
- Implement proper error handling for malformed messages
- Use secure transports (WSS, HTTPS)
- Implement message authentication

### Context Security

- Encrypt sensitive context data
- Implement context access controls
- Add context retention policies
- Sanitize context data before storage

### Authentication Security

- Support multiple authentication methods
- Implement token rotation
- Use secure credential storage
- Add authentication timeout handling

## Deliverables

1. **Zen MCP Provider** (`src/providers/implementations/zen-mcp-provider.ts`)
2. **MCP Client** (`src/providers/implementations/mcp-client.ts`)
3. **MCP Protocol** (`src/providers/implementations/mcp-protocol.ts`)
4. **Context Manager** (`src/providers/implementations/mcp-context-manager.ts`)
5. **Transport Layer** (`src/providers/implementations/mcp-transport.ts`)
6. **Configuration Schema** (`src/providers/configs/zen-mcp-config.schema.ts`)
7. **Unit Tests** (`tests/providers/zen-mcp-provider.test.ts`)
8. **Integration Tests** (`tests/providers/zen-mcp-integration.test.ts`)
9. **Documentation** (`docs/providers/zen-mcp.md`)

This implementation provides comprehensive Zen MCP integration with full protocol compliance, context management, tool calling, and robust error handling while maintaining compatibility with the unified provider interface.
