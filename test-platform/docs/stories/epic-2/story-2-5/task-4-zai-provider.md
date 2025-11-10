# Task 4: z.ai Provider Integration

## Overview

This task implements the z.ai provider integration, enabling access to z.ai's specialized code generation models through the unified provider interface. z.ai focuses on providing high-performance code models optimized for various programming languages and development scenarios.

## Acceptance Criteria

### 4.1: Implement z.ai API client

- [ ] Create z.ai API client with proper authentication
- [ ] Implement REST API integration for model access
- [ ] Add support for WebSocket streaming connections
- [ ] Implement proper error handling and retry logic
- [ ] Add API versioning and compatibility handling

### 4.2: Add support for specialized code models

- [ ] Implement support for z-code-coder model
- [ ] Add support for z-code-chat model
- [ ] Implement support for z-code-analyzer model
- [ ] Add support for language-specific models
- [ ] Implement model capability detection and metadata

### 4.3: Implement streaming responses

- [ ] Add real-time streaming for code generation
- [ ] Implement WebSocket-based streaming
- [ ] Add backpressure handling for large responses
- [ ] Implement streaming cancellation and timeout
- [ ] Add streaming error recovery mechanisms

### 4.4: Add model capability detection

- [ ] Implement automatic capability detection
- [ ] Add language support detection
- [ ] Implement performance benchmarking integration
- [ ] Add model specialization detection
- [ ] Create capability comparison and reporting

### 4.5: Create configuration and authentication

- [ ] Implement API key authentication
- [ ] Add support for team/organization authentication
- [ ] Implement configuration validation
- [ ] Add support for multiple z.ai instances
- [ ] Implement secure credential storage

## Technical Implementation

### Provider Architecture

```typescript
// src/providers/implementations/zai-provider.ts
export class ZAIProvider implements IAIProvider {
  private readonly client: ZAIClient;
  private readonly config: ZAIConfig;
  private readonly modelManager: ZAIModelManager;
  private readonly rateLimiter: ZAIRateLimiter;
  private readonly logger: Logger;

  constructor(config: ZAIConfig) {
    this.config = this.validateConfig(config);
    this.client = new ZAIClient(this.config);
    this.modelManager = new ZAIModelManager(this.client);
    this.rateLimiter = new ZAIRateLimiter(this.config.rateLimits);
    this.logger = new Logger({ service: 'zai-provider' });
  }

  async initialize(): Promise<void> {
    await this.client.authenticate();
    await this.modelManager.discoverModels();
    await this.rateLimiter.initialize();
    this.logger.info('z.ai provider initialized');
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    await this.rateLimiter.acquire();

    try {
      const model = await this.modelManager.getModel(request.model);

      return this.handleCodeGeneration(model, request);
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
        functionCalling: false,
        multimodal: false,
        toolUse: false,
        codeCompletion: true,
        codeAnalysis: true,
        languageDetection: true,
        performanceOptimization: true,
      },
      limits: {
        maxTokens: 8192,
        maxRequestsPerMinute: this.config.rateLimits.requestsPerMinute,
        maxTokensPerMinute: this.config.rateLimits.tokensPerMinute,
      },
    };
  }

  async dispose(): Promise<void> {
    await this.rateLimiter.dispose();
    await this.client.dispose();
    this.logger.info('z.ai provider disposed');
  }
}
```

### z.ai API Client

```typescript
// src/providers/implementations/zai-client.ts
export class ZAIClient {
  private readonly config: ZAIConfig;
  private readonly httpClient: HttpClient;
  private readonly websocketClient: WebSocketClient;
  private readonly authManager: ZAIAuthManager;
  private readonly logger: Logger;

  constructor(config: ZAIConfig) {
    this.config = config;
    this.httpClient = new HttpClient({
      baseURL: config.apiEndpoint,
      timeout: config.timeout,
      headers: {
        'User-Agent': 'Tamma-Benchmarking/1.0',
        'Content-Type': 'application/json',
        'X-API-Version': config.apiVersion || 'v1',
      },
    });
    this.websocketClient = new WebSocketClient({
      url: config.wsEndpoint || config.apiEndpoint.replace('http', 'ws'),
      protocols: ['zai-v1'],
    });
    this.authManager = new ZAIAuthManager(config.auth);
    this.logger = new Logger({ service: 'zai-client' });
  }

  async authenticate(): Promise<void> {
    const token = await this.authManager.getAuthToken();
    this.httpClient.setDefaultHeader('Authorization', `Bearer ${token}`);
    this.websocketClient.setDefaultHeader('Authorization', `Bearer ${token}`);

    // Validate authentication
    try {
      await this.get('/auth/validate');
      this.logger.info('z.ai authentication successful');
    } catch (error) {
      throw new ZAIError('AUTHENTICATION_FAILED', 'Authentication validation failed', error);
    }
  }

  async getModels(): Promise<ZAIModel[]> {
    try {
      const response = await this.get('/models');
      return response.data.map(this.transformModelData);
    } catch (error) {
      throw new ZAIError('MODEL_DISCOVERY_FAILED', 'Failed to discover models', error);
    }
  }

  async generateCode(
    modelId: string,
    request: CodeGenerationRequest
  ): Promise<AsyncIterable<CodeGenerationChunk>> {
    if (request.stream && this.config.features.websocketStreaming) {
      return this.generateCodeWebSocket(modelId, request);
    } else {
      return this.generateCodeHTTP(modelId, request);
    }
  }

  async analyzeCode(modelId: string, request: CodeAnalysisRequest): Promise<CodeAnalysisResult> {
    const payload = {
      model: modelId,
      code: request.code,
      language: request.language,
      analysis_type: request.analysisType,
      options: request.options,
    };

    try {
      const response = await this.post('/analyze', payload);
      return this.transformAnalysisResult(response.data);
    } catch (error) {
      throw new ZAIError('CODE_ANALYSIS_FAILED', 'Code analysis failed', error);
    }
  }

  async detectLanguage(code: string): Promise<LanguageDetectionResult> {
    const payload = {
      code,
      options: {
        include_confidence: true,
        top_k: 3,
      },
    };

    try {
      const response = await this.post('/detect-language', payload);
      return response.data;
    } catch (error) {
      throw new ZAIError('LANGUAGE_DETECTION_FAILED', 'Language detection failed', error);
    }
  }

  async getModelCapabilities(modelId: string): Promise<ModelCapabilities> {
    try {
      const response = await this.get(`/models/${modelId}/capabilities`);
      return this.transformCapabilities(response.data);
    } catch (error) {
      throw new ZAIError('CAPABILITIES_FAILED', `Failed to get capabilities for ${modelId}`, error);
    }
  }

  async getUsageStats(): Promise<ZAIUsageStats> {
    try {
      const response = await this.get('/usage/stats');
      return response.data;
    } catch (error) {
      throw new ZAIError('USAGE_STATS_FAILED', 'Failed to get usage statistics', error);
    }
  }

  private async generateCodeHTTP(
    modelId: string,
    request: CodeGenerationRequest
  ): Promise<AsyncIterable<CodeGenerationChunk>> {
    const payload = {
      model: modelId,
      prompt: request.prompt,
      max_tokens: request.maxTokens,
      temperature: request.temperature,
      top_p: request.topP,
      stop: request.stopSequences,
      stream: true,
      language: request.language,
      context: request.context,
      ...request.parameters,
    };

    try {
      const response = await this.post('/generate', payload, {
        responseType: 'stream',
      });

      return this.streamCodeGenerationResponse(response.data);
    } catch (error) {
      throw new ZAIError('CODE_GENERATION_FAILED', 'Code generation failed', error);
    }
  }

  private async generateCodeWebSocket(
    modelId: string,
    request: CodeGenerationRequest
  ): Promise<AsyncIterable<CodeGenerationChunk>> {
    const payload = {
      model: modelId,
      prompt: request.prompt,
      max_tokens: request.maxTokens,
      temperature: request.temperature,
      top_p: request.topP,
      stop: request.stopSequences,
      language: request.language,
      context: request.context,
      ...request.parameters,
    };

    try {
      const websocket = await this.websocketClient.connect('/generate');
      await websocket.send(JSON.stringify(payload));

      return this.streamWebSocketResponse(websocket);
    } catch (error) {
      throw new ZAIError('WEBSOCKET_FAILED', 'WebSocket connection failed', error);
    }
  }

  private async get(endpoint: string, params?: any): Promise<any> {
    return this.httpClient.get(endpoint, params);
  }

  private async post(endpoint: string, data: any, options?: any): Promise<any> {
    return this.httpClient.post(endpoint, data, options);
  }

  private transformModelData(data: any): ZAIModel {
    return {
      id: data.id,
      name: data.name,
      provider: 'zai',
      version: data.version,
      capabilities: {
        maxTokens: data.max_tokens,
        streaming: data.streaming,
        functionCalling: data.function_calling || false,
        multimodal: data.multimodal || false,
        codeCompletion: true,
        codeAnalysis: data.code_analysis || false,
        languageDetection: data.language_detection || false,
        performanceOptimization: data.performance_optimization || false,
        languages: data.supported_languages || [],
        contextWindow: data.context_window,
        specializations: data.specializations || [],
      },
      metadata: {
        description: data.description,
        category: data.category || 'code-generation',
        architecture: data.architecture,
        trainingData: data.training_data,
        fineTuned: data.fine_tuned || false,
        baseModel: data.base_model,
        pricing: data.pricing,
        tags: data.tags || [],
        performance: data.performance_metrics,
        benchmarks: data.benchmarks,
      },
    };
  }

  private transformAnalysisResult(data: any): CodeAnalysisResult {
    return {
      summary: data.summary,
      issues: data.issues || [],
      suggestions: data.suggestions || [],
      metrics: data.metrics || {},
      complexity: data.complexity,
      security: data.security || {},
      performance: data.performance || {},
      maintainability: data.maintainability || {},
    };
  }

  private transformCapabilities(data: any): ModelCapabilities {
    return {
      languages: data.languages || [],
      frameworks: data.frameworks || [],
      analysisTypes: data.analysis_types || [],
      codeGeneration: {
        supported: data.code_generation?.supported || false,
        maxTokens: data.code_generation?.max_tokens,
        languages: data.code_generation?.languages || [],
      },
      codeAnalysis: {
        supported: data.code_analysis?.supported || false,
        types: data.code_analysis?.types || [],
        depth: data.code_analysis?.depth || 'basic',
      },
      optimization: {
        supported: data.optimization?.supported || false,
        techniques: data.optimization?.techniques || [],
      },
    };
  }

  private async *streamCodeGenerationResponse(stream: any): AsyncIterable<CodeGenerationChunk> {
    const reader = stream.getReader();
    const decoder = new TextDecoder();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        const chunk = decoder.decode(value, { stream: true });
        const lines = chunk.split('\n').filter((line) => line.trim());

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const data = line.slice(6);
            if (data === '[DONE]') continue;

            try {
              const parsed = JSON.parse(data);
              yield {
                code: parsed.choices?.[0]?.text || '',
                done: parsed.choices?.[0]?.finish_reason === 'stop',
                metadata: {
                  model: parsed.model,
                  usage: parsed.usage,
                  language: parsed.language,
                  confidence: parsed.confidence,
                  timestamp: new Date().toISOString(),
                },
              };
            } catch (e) {
              this.logger.warn('Failed to parse streaming response:', e);
            }
          }
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  private async *streamWebSocketResponse(websocket: WebSocket): AsyncIterable<CodeGenerationChunk> {
    try {
      while (websocket.readyState === WebSocket.OPEN) {
        const message = await websocket.receive();

        if (message.type === 'close') {
          break;
        }

        if (message.type === 'error') {
          throw new ZAIError('WEBSOCKET_ERROR', message.data);
        }

        if (message.type === 'message') {
          try {
            const data = JSON.parse(message.data);

            if (data.type === 'chunk') {
              yield {
                code: data.content || '',
                done: data.done || false,
                metadata: {
                  model: data.model,
                  usage: data.usage,
                  language: data.language,
                  confidence: data.confidence,
                  timestamp: new Date().toISOString(),
                },
              };
            } else if (data.type === 'error') {
              throw new ZAIError('GENERATION_ERROR', data.message);
            }
          } catch (e) {
            this.logger.warn('Failed to parse WebSocket message:', e);
          }
        }
      }
    } finally {
      websocket.close();
    }
  }

  async dispose(): Promise<void> {
    await this.httpClient.dispose();
    await this.websocketClient.dispose();
  }
}
```

### Model Manager Implementation

````typescript
// src/providers/implementations/zai-model-manager.ts
export class ZAIModelManager {
  private readonly client: ZAIClient;
  private readonly models: Map<string, ZAIModel>;
  private readonly capabilitiesCache: Map<string, ModelCapabilities>;
  private readonly logger: Logger;

  constructor(client: ZAIClient) {
    this.client = client;
    this.models = new Map();
    this.capabilitiesCache = new Map();
    this.logger = new Logger({ service: 'zai-model-manager' });
  }

  async discoverModels(): Promise<void> {
    try {
      const models = await this.client.getModels();

      for (const model of models) {
        this.models.set(model.id, model);
        this.logger.info(`Discovered model: ${model.id}`);
      }

      this.logger.info(`Discovered ${models.length} z.ai models`);
    } catch (error) {
      this.logger.error('Failed to discover models:', error);
      throw error;
    }
  }

  async getModel(modelId: string): Promise<ZAIModel> {
    const model = this.models.get(modelId);
    if (!model) {
      throw new Error(`Model not found: ${modelId}`);
    }
    return model;
  }

  async getModelCapabilities(modelId: string): Promise<ModelCapabilities> {
    // Check cache first
    const cached = this.capabilitiesCache.get(modelId);
    if (cached && this.isCacheValid(cached)) {
      return cached;
    }

    // Fetch from API
    const capabilities = await this.client.getModelCapabilities(modelId);
    this.capabilitiesCache.set(modelId, capabilities);

    return capabilities;
  }

  async getAvailableModels(): Promise<ModelInfo[]> {
    return Array.from(this.models.values()).map((model) => ({
      id: model.id,
      name: model.name,
      provider: model.provider,
      version: model.version,
      capabilities: model.capabilities,
      metadata: model.metadata,
    }));
  }

  async handleCodeGeneration(
    model: ZAIModel,
    request: MessageRequest
  ): Promise<AsyncIterable<MessageChunk>> {
    const codeRequest = await this.buildCodeGenerationRequest(model, request);
    const stream = await this.client.generateCode(model.id, codeRequest);

    return this.transformCodeStream(stream, model.id);
  }

  private async buildCodeGenerationRequest(
    model: ZAIModel,
    request: MessageRequest
  ): Promise<CodeGenerationRequest> {
    const prompt = this.extractPrompt(request);
    const language = await this.detectLanguage(prompt);

    return {
      prompt,
      maxTokens: request.maxTokens || model.capabilities.maxTokens,
      temperature: request.temperature,
      topP: request.topP,
      stopSequences: request.stopSequences,
      stream: true,
      language,
      context: this.buildContext(request),
      parameters: this.extractModelParameters(model, request),
    };
  }

  private extractPrompt(request: MessageRequest): string {
    const lastMessage = request.messages[request.messages.length - 1];

    // Extract code from code blocks if present
    const codeMatch = lastMessage.content.match(/```(\w+)?\n([\s\S]*?)\n```/);
    if (codeMatch) {
      return codeMatch[2];
    }

    return lastMessage.content;
  }

  private async detectLanguage(code: string): Promise<string> {
    try {
      const result = await this.client.detectLanguage(code);
      return result.language || 'auto';
    } catch (error) {
      this.logger.warn('Language detection failed:', error);
      return 'auto';
    }
  }

  private buildContext(request: MessageRequest): any {
    const context: any = {
      conversation_history: request.messages.slice(0, -1).map((msg) => ({
        role: msg.role,
        content: msg.content,
      })),
    };

    // Add file context if available
    const fileContext = this.extractFileContext(request);
    if (fileContext) {
      context.file_context = fileContext;
    }

    return context;
  }

  private extractFileContext(request: MessageRequest): any {
    const lastMessage = request.messages[request.messages.length - 1];
    const fileMatch = lastMessage.content.match(/file:\s*(\S+)/i);

    if (fileMatch) {
      return {
        filename: fileMatch[1],
        language: this.getLanguageFromFilename(fileMatch[1]),
      };
    }

    return null;
  }

  private getLanguageFromFilename(filename: string): string {
    const ext = filename.split('.').pop()?.toLowerCase();
    const languageMap: Record<string, string> = {
      js: 'javascript',
      ts: 'typescript',
      py: 'python',
      java: 'java',
      cpp: 'cpp',
      c: 'c',
      cs: 'csharp',
      go: 'go',
      rs: 'rust',
      php: 'php',
      rb: 'ruby',
      swift: 'swift',
      kt: 'kotlin',
      scala: 'scala',
      r: 'r',
      sql: 'sql',
      html: 'html',
      css: 'css',
      json: 'json',
      xml: 'xml',
      yaml: 'yaml',
      yml: 'yaml',
    };

    return languageMap[ext || ''] || 'text';
  }

  private extractModelParameters(model: ZAIModel, request: MessageRequest): any {
    const params: any = {};

    // Model-specific parameters
    if (model.capabilities.specializations?.includes('performance')) {
      params.optimization_level = request.optimizationLevel || 'balanced';
    }

    if (model.capabilities.specializations?.includes('security')) {
      params.security_scan = request.securityScan || false;
    }

    if (model.capabilities.specializations?.includes('enterprise')) {
      params.enterprise_mode = request.enterpriseMode || false;
    }

    return params;
  }

  private async *transformCodeStream(
    stream: AsyncIterable<CodeGenerationChunk>,
    modelId: string
  ): AsyncIterable<MessageChunk> {
    for await (const chunk of stream) {
      yield {
        content: chunk.code,
        done: chunk.done,
        metadata: {
          model: modelId,
          language: chunk.metadata.language,
          confidence: chunk.metadata.confidence,
          ...chunk.metadata,
        },
      };
    }
  }

  private isCacheValid(capabilities: ModelCapabilities): boolean {
    // Simple cache validation - could be enhanced with timestamps
    return true;
  }
}
````

### Authentication Manager

```typescript
// src/providers/implementations/zai-auth.ts
export class ZAIAuthManager {
  private readonly config: ZAIAuthConfig;
  private readonly secureStore: SecureStore;
  private tokenCache: TokenInfo | null = null;

  constructor(config: ZAIAuthConfig) {
    this.config = config;
    this.secureStore = new SecureStore('zai');
  }

  async getAuthToken(): Promise<string> {
    switch (this.config.type) {
      case 'api-key':
        return this.getApiKeyToken();
      case 'team':
        return this.getTeamToken();
      case 'oauth':
        return this.getOAuthToken();
      default:
        throw new Error(`Unsupported auth type: ${this.config.type}`);
    }
  }

  private async getApiKeyToken(): Promise<string> {
    if (!this.config.apiKey) {
      throw new Error('API key is required for API key authentication');
    }
    return this.config.apiKey;
  }

  private async getTeamToken(): Promise<string> {
    if (!this.config.team?.teamId || !this.config.team?.teamSecret) {
      throw new Error('Team ID and team secret are required for team authentication');
    }

    // Check cached token
    if (this.tokenCache && !this.isTokenExpired(this.tokenCache)) {
      return this.tokenCache.token;
    }

    // Generate team token
    const payload = {
      team_id: this.config.team.teamId,
      timestamp: Math.floor(Date.now() / 1000),
      exp: Math.floor(Date.now() / 1000) + 3600, // 1 hour
    };

    const token = jwt.sign(payload, this.config.team.teamSecret, {
      algorithm: 'HS256',
    });

    // Cache token
    this.tokenCache = {
      token,
      expiresAt: new Date(Date.now() + 3600 * 1000),
    };

    return token;
  }

  private async getOAuthToken(): Promise<string> {
    // Check cached token
    if (this.tokenCache && !this.isTokenExpired(this.tokenCache)) {
      return this.tokenCache.token;
    }

    // Try to refresh token
    const refreshToken = await this.secureStore.get('refresh_token');
    if (refreshToken) {
      try {
        const newToken = await this.refreshOAuthToken(refreshToken);
        await this.cacheToken(newToken);
        return newToken.token;
      } catch (error) {
        this.logger.warn('Failed to refresh OAuth token:', error);
      }
    }

    // Need to re-authenticate
    throw new Error('OAuth token expired and no refresh token available');
  }

  private async refreshOAuthToken(refreshToken: string): Promise<TokenInfo> {
    const response = await fetch(`${this.config.oauth.tokenUrl}`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
      },
      body: new URLSearchParams({
        grant_type: 'refresh_token',
        refresh_token: refreshToken,
        client_id: this.config.oauth.clientId,
        client_secret: this.config.oauth.clientSecret,
      }),
    });

    if (!response.ok) {
      throw new Error(`OAuth refresh failed: ${response.statusText}`);
    }

    const data = await response.json();

    return {
      token: data.access_token,
      refreshToken: data.refresh_token || refreshToken,
      expiresAt: new Date(Date.now() + data.expires_in * 1000),
      scope: data.scope,
    };
  }

  private async cacheToken(tokenInfo: TokenInfo): Promise<void> {
    this.tokenCache = tokenInfo;

    if (tokenInfo.refreshToken) {
      await this.secureStore.store('refresh_token', tokenInfo.refreshToken);
    }
  }

  private isTokenExpired(tokenInfo: TokenInfo): boolean {
    return Date.now() >= tokenInfo.expiresAt.getTime() - 60000; // 1 minute buffer
  }
}
```

### Configuration Schema

```typescript
// src/providers/configs/zai-config.schema.ts
export const ZAIConfigSchema = {
  type: 'object',
  required: ['auth', 'apiEndpoint', 'rateLimits'],
  properties: {
    apiEndpoint: {
      type: 'string',
      format: 'uri',
      description: 'z.ai API endpoint URL',
    },
    wsEndpoint: {
      type: 'string',
      format: 'uri',
      description: 'z.ai WebSocket endpoint URL (optional)',
    },
    apiVersion: {
      type: 'string',
      default: 'v1',
      description: 'z.ai API version',
    },
    auth: {
      type: 'object',
      required: ['type'],
      oneOf: [
        {
          title: 'API Key Authentication',
          properties: {
            type: { const: 'api-key' },
            apiKey: {
              type: 'string',
              description: 'z.ai API key',
            },
          },
        },
        {
          title: 'Team Authentication',
          properties: {
            type: { const: 'team' },
            team: {
              type: 'object',
              required: ['teamId', 'teamSecret'],
              properties: {
                teamId: {
                  type: 'string',
                  description: 'Team ID',
                },
                teamSecret: {
                  type: 'string',
                  description: 'Team secret key',
                },
              },
            },
          },
        },
        {
          title: 'OAuth Authentication',
          properties: {
            type: { const: 'oauth' },
            clientId: {
              type: 'string',
              description: 'OAuth client ID',
            },
            clientSecret: {
              type: 'string',
              description: 'OAuth client secret',
            },
            tokenUrl: {
              type: 'string',
              format: 'uri',
              description: 'OAuth token URL',
            },
            scopes: {
              type: 'array',
              items: { type: 'string' },
              description: 'OAuth scopes',
            },
          },
        },
      ],
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
        maxQueueSize: {
          type: 'integer',
          minimum: 10,
          maximum: 1000,
          default: 100,
          description: 'Maximum queue size for pending requests',
        },
      },
    },
    models: {
      type: 'object',
      properties: {
        defaultModel: {
          type: 'string',
          enum: ['z-code-coder', 'z-code-chat', 'z-code-analyzer'],
          default: 'z-code-coder',
          description: 'Default model to use',
        },
        enabledModels: {
          type: 'array',
          items: {
            type: 'string',
            enum: ['z-code-coder', 'z-code-chat', 'z-code-analyzer'],
          },
          default: ['z-code-coder', 'z-code-chat'],
          description: 'List of enabled models',
        },
        autoDiscovery: {
          type: 'boolean',
          default: true,
          description: 'Automatically discover available models',
        },
        cacheTimeout: {
          type: 'integer',
          default: 300,
          description: 'Model cache timeout in seconds',
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
        websocketStreaming: {
          type: 'boolean',
          default: true,
          description: 'Use WebSocket for streaming when available',
        },
        codeAnalysis: {
          type: 'boolean',
          default: true,
          description: 'Enable code analysis features',
        },
        languageDetection: {
          type: 'boolean',
          default: true,
          description: 'Enable automatic language detection',
        },
        performanceOptimization: {
          type: 'boolean',
          default: true,
          description: 'Enable performance optimization features',
        },
      },
    },
    timeout: {
      type: 'integer',
      minimum: 5000,
      maximum: 300000,
      default: 30000,
      description: 'Request timeout in milliseconds',
    },
  },
};
```

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/zai-provider.test.ts
describe('ZAIProvider', () => {
  let provider: ZAIProvider;
  let mockClient: jest.Mocked<ZAIClient>;

  beforeEach(() => {
    mockClient = new ZAIClient({
      apiEndpoint: 'https://api.z.ai',
      auth: { type: 'api-key', apiKey: 'test-key' },
    } as any) as jest.Mocked<ZAIClient>;

    provider = new ZAIProvider({
      apiEndpoint: 'https://api.z.ai',
      auth: { type: 'api-key', apiKey: 'test-key' },
      rateLimits: {
        requestsPerMinute: 60,
        tokensPerMinute: 60000,
      },
    });
  });

  describe('model discovery', () => {
    it('should discover available models', async () => {
      const mockModels = [
        { id: 'z-code-coder', name: 'z.ai Code Coder' },
        { id: 'z-code-chat', name: 'z.ai Code Chat' },
      ];

      mockClient.getModels.mockResolvedValue(mockModels as any);

      await provider.initialize();

      const capabilities = await provider.getCapabilities();
      expect(capabilities.models).toHaveLength(2);
      expect(capabilities.models[0].id).toBe('z-code-coder');
    });
  });

  describe('code generation', () => {
    it('should handle code generation requests', async () => {
      const request: MessageRequest = {
        model: 'z-code-coder',
        messages: [{ role: 'user', content: 'function hello() {' }],
        maxTokens: 100,
      };

      const mockStream = createMockCodeStream();
      mockClient.generateCode.mockResolvedValue(mockStream);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThan(0);
      expect(chunks[chunks.length - 1].done).toBe(true);
    });
  });

  describe('language detection', () => {
    it('should detect programming languages', async () => {
      const code = 'function hello() { console.log("Hello"); }';

      mockClient.detectLanguage.mockResolvedValue({
        language: 'javascript',
        confidence: 0.95,
        alternatives: [{ language: 'typescript', confidence: 0.8 }],
      });

      const result = await mockClient.detectLanguage(code);

      expect(result.language).toBe('javascript');
      expect(result.confidence).toBe(0.95);
    });
  });
});
```

### Integration Tests

````typescript
// tests/providers/zai-integration.test.ts
describe('ZAI Integration', () => {
  let provider: ZAIProvider;

  beforeAll(async () => {
    if (!process.env.ZAI_API_KEY_TEST) {
      throw new Error('ZAI_API_KEY_TEST environment variable required');
    }

    provider = new ZAIProvider({
      apiEndpoint: process.env.ZAI_API_ENDPOINT_TEST || 'https://api.z.ai',
      auth: {
        type: 'api-key',
        apiKey: process.env.ZAI_API_KEY_TEST,
      },
      rateLimits: {
        requestsPerMinute: 30,
        tokensPerMinute: 30000,
      },
    });

    await provider.initialize();
  });

  it('should generate code with z-code-coder', async () => {
    const request: MessageRequest = {
      model: 'z-code-coder',
      messages: [
        {
          role: 'user',
          content: '```python\ndef fibonacci(n):\n    ',
        },
      ],
      maxTokens: 200,
    };

    const response = await collectStream(provider.sendMessage(request));

    expect(response.content).toContain('def');
    expect(response.metadata.language).toBe('python');
    expect(response.metadata.usage.totalTokens).toBeGreaterThan(0);
  });

  it('should detect programming languages', async () => {
    const code = `
    class Calculator {
      add(a, b) {
        return a + b;
      }
    }
    `;

    const capabilities = await provider.getCapabilities();
    const modelManager = provider['modelManager'];
    const detection = await modelManager['client'].detectLanguage(code);

    expect(detection.language).toBe('javascript');
    expect(detection.confidence).toBeGreaterThan(0.8);
  });
});
````

## Success Metrics

### Performance Metrics

- Model discovery time: < 3s
- Code generation latency: < 3s
- Streaming latency: < 100ms per chunk
- Language detection accuracy: 95%+
- WebSocket connection time: < 500ms

### Reliability Metrics

- API request success rate: 99%
- Authentication success rate: 99.9%
- WebSocket connection success rate: 98%
- Error handling coverage: 100%
- Retry success rate: 90%

### Integration Metrics

- Model discovery accuracy: 100%
- Language detection accuracy: 95%+
- Code generation quality: 85%+ pass rate
- Test coverage: 90%+

## Dependencies

### External Dependencies

- `axios`: HTTP client for API requests
- `ws`: WebSocket client for streaming
- `jsonwebtoken`: JWT authentication support
- `node-fetch`: Alternative HTTP client

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/errors`: Error handling utilities
- `@tamma/core/logging`: Logging utilities
- `@tamma/core/config`: Configuration management

## Security Considerations

### Authentication Security

- Encrypt API keys at rest
- Use secure token storage
- Implement token rotation for team auth
- Never log credentials

### WebSocket Security

- Use WSS for secure connections
- Validate WebSocket messages
- Implement connection timeouts
- Add rate limiting for WebSocket connections

### API Security

- Validate all API responses
- Use HTTPS for all communications
- Implement request timeouts
- Add request signing for sensitive operations

## Deliverables

1. **ZAI Provider** (`src/providers/implementations/zai-provider.ts`)
2. **API Client** (`src/providers/implementations/zai-client.ts`)
3. **Model Manager** (`src/providers/implementations/zai-model-manager.ts`)
4. **Auth Manager** (`src/providers/implementations/zai-auth.ts`)
5. **Rate Limiter** (`src/providers/implementations/zai-rate-limiter.ts`)
6. **Configuration Schema** (`src/providers/configs/zai-config.schema.ts`)
7. **Unit Tests** (`tests/providers/zai-provider.test.ts`)
8. **Integration Tests** (`tests/providers/zai-integration.test.ts`)
9. **Documentation** (`docs/providers/zai.md`)

This implementation provides comprehensive z.ai integration with support for specialized code models, WebSocket streaming, language detection, and robust authentication while maintaining compatibility with the unified provider interface.
