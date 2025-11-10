# Task 3: OpenCode Provider Implementation

## Overview

This task implements the OpenCode provider integration, enabling access to specialized open-source code models through the unified provider interface. OpenCode focuses on providing access to various open-source fine-tuned models optimized for code generation and understanding.

## Acceptance Criteria

### 3.1: Implement OpenCode API integration

- [ ] Create OpenCode API client with proper authentication
- [ ] Implement REST API integration for model access
- [ ] Add support for custom endpoints and configurations
- [ ] Implement proper error handling for API failures
- [ ] Add API versioning and compatibility handling

### 3.2: Add support for open-source code models

- [ ] Implement support for CodeLlama models
- [ ] Add support for StarCoder models
- [ ] Implement support for WizardCoder models
- [ ] Add support for custom fine-tuned models
- [ ] Implement model capability detection and metadata

### 3.3: Implement model discovery and metadata

- [ ] Create automatic model discovery system
- [ ] Implement model metadata extraction
- [ ] Add model capability detection
- [ ] Implement model versioning and updates
- [ ] Add model performance metrics collection

### 3.4: Add authentication and configuration

- [ ] Implement API key authentication
- [ ] Add support for OAuth authentication
- [ ] Implement configuration validation
- [ ] Add support for multiple OpenCode instances
- [ ] Implement secure credential storage

### 3.5: Create error handling for API limits

- [ ] Implement rate limiting detection
- [ ] Add quota management and tracking
- [ ] Implement exponential backoff for retries
- [ ] Add graceful degradation for API limits
- [ ] Implement usage monitoring and alerting

## Technical Implementation

### Provider Architecture

```typescript
// src/providers/implementations/opencode-provider.ts
export class OpenCodeProvider implements IAIProvider {
  private readonly client: OpenCodeClient;
  private readonly config: OpenCodeConfig;
  private readonly modelManager: OpenCodeModelManager;
  private readonly rateLimiter: OpenCodeRateLimiter;
  private readonly logger: Logger;

  constructor(config: OpenCodeConfig) {
    this.config = this.validateConfig(config);
    this.client = new OpenCodeClient(this.config);
    this.modelManager = new OpenCodeModelManager(this.client);
    this.rateLimiter = new OpenCodeRateLimiter(this.config.rateLimits);
    this.logger = new Logger({ service: 'opencode-provider' });
  }

  async initialize(): Promise<void> {
    await this.client.authenticate();
    await this.modelManager.discoverModels();
    await this.rateLimiter.initialize();
    this.logger.info('OpenCode provider initialized');
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
      },
      limits: {
        maxTokens: 4096,
        maxRequestsPerMinute: this.config.rateLimits.requestsPerMinute,
        maxTokensPerMinute: this.config.rateLimits.tokensPerMinute,
      },
    };
  }

  async dispose(): Promise<void> {
    await this.rateLimiter.dispose();
    await this.client.dispose();
    this.logger.info('OpenCode provider disposed');
  }
}
```

### OpenCode API Client

```typescript
// src/providers/implementations/opencode-client.ts
export class OpenCodeClient {
  private readonly config: OpenCodeConfig;
  private readonly httpClient: HttpClient;
  private readonly authManager: OpenCodeAuthManager;
  private readonly logger: Logger;

  constructor(config: OpenCodeConfig) {
    this.config = config;
    this.httpClient = new HttpClient({
      baseURL: config.apiEndpoint,
      timeout: config.timeout,
      headers: {
        'User-Agent': 'Tamma-Benchmarking/1.0',
        'Content-Type': 'application/json',
      },
    });
    this.authManager = new OpenCodeAuthManager(config.auth);
    this.logger = new Logger({ service: 'opencode-client' });
  }

  async authenticate(): Promise<void> {
    const token = await this.authManager.getAuthToken();
    this.httpClient.setDefaultHeader('Authorization', `Bearer ${token}`);

    // Validate authentication
    try {
      await this.get('/auth/validate');
      this.logger.info('OpenCode authentication successful');
    } catch (error) {
      throw new OpenCodeError('AUTHENTICATION_FAILED', 'Authentication validation failed', error);
    }
  }

  async getModels(): Promise<OpenCodeModel[]> {
    try {
      const response = await this.get('/models');
      return response.data.map(this.transformModelData);
    } catch (error) {
      throw new OpenCodeError('MODEL_DISCOVERY_FAILED', 'Failed to discover models', error);
    }
  }

  async generateCompletion(
    modelId: string,
    request: CompletionRequest
  ): Promise<AsyncIterable<CompletionChunk>> {
    const payload = {
      model: modelId,
      prompt: request.prompt,
      max_tokens: request.maxTokens,
      temperature: request.temperature,
      top_p: request.topP,
      stop: request.stopSequences,
      stream: true,
      ...request.parameters,
    };

    try {
      const response = await this.post('/completions', payload, {
        responseType: 'stream',
      });

      return this.streamCompletionResponse(response.data);
    } catch (error) {
      throw new OpenCodeError('COMPLETION_FAILED', 'Code completion failed', error);
    }
  }

  async generateChatCompletion(
    modelId: string,
    request: ChatCompletionRequest
  ): Promise<AsyncIterable<ChatCompletionChunk>> {
    const payload = {
      model: modelId,
      messages: request.messages.map((msg) => ({
        role: msg.role,
        content: msg.content,
      })),
      max_tokens: request.maxTokens,
      temperature: request.temperature,
      top_p: request.topP,
      stop: request.stopSequences,
      stream: true,
      ...request.parameters,
    };

    try {
      const response = await this.post('/chat/completions', payload, {
        responseType: 'stream',
      });

      return this.streamChatCompletionResponse(response.data);
    } catch (error) {
      throw new OpenCodeError('CHAT_COMPLETION_FAILED', 'Chat completion failed', error);
    }
  }

  async getModelInfo(modelId: string): Promise<OpenCodeModelInfo> {
    try {
      const response = await this.get(`/models/${modelId}`);
      return this.transformModelInfo(response.data);
    } catch (error) {
      throw new OpenCodeError(
        'MODEL_INFO_FAILED',
        `Failed to get model info for ${modelId}`,
        error
      );
    }
  }

  async getUsageStats(): Promise<OpenCodeUsageStats> {
    try {
      const response = await this.get('/usage/stats');
      return response.data;
    } catch (error) {
      throw new OpenCodeError('USAGE_STATS_FAILED', 'Failed to get usage statistics', error);
    }
  }

  private async get(endpoint: string, params?: any): Promise<any> {
    return this.httpClient.get(endpoint, params);
  }

  private async post(endpoint: string, data: any, options?: any): Promise<any> {
    return this.httpClient.post(endpoint, data, options);
  }

  private transformModelData(data: any): OpenCodeModel {
    return {
      id: data.id,
      name: data.name,
      provider: 'opencode',
      version: data.version,
      capabilities: {
        maxTokens: data.max_tokens,
        streaming: data.streaming,
        functionCalling: data.function_calling || false,
        multimodal: data.multimodal || false,
        codeCompletion: true,
        codeAnalysis: data.code_analysis || false,
        languages: data.supported_languages || [],
        contextWindow: data.context_window,
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
      },
    };
  }

  private transformModelInfo(data: any): OpenCodeModelInfo {
    return {
      ...this.transformModelData(data),
      details: {
        parameters: data.parameters,
        layers: data.layers,
        attentionHeads: data.attention_heads,
        hiddenSize: data.hidden_size,
        vocabularySize: data.vocabulary_size,
        trainingSteps: data.training_steps,
        trainingDataSize: data.training_data_size,
        license: data.license,
        repository: data.repository,
        paper: data.paper,
      },
      benchmarks: data.benchmarks || {},
      usage: data.usage_stats || {},
    };
  }

  private async *streamCompletionResponse(stream: any): AsyncIterable<CompletionChunk> {
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
                text: parsed.choices?.[0]?.text || '',
                done: parsed.choices?.[0]?.finish_reason === 'stop',
                metadata: {
                  model: parsed.model,
                  usage: parsed.usage,
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

  private async *streamChatCompletionResponse(stream: any): AsyncIterable<ChatCompletionChunk> {
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
                content: parsed.choices?.[0]?.delta?.content || '',
                done: parsed.choices?.[0]?.finish_reason === 'stop',
                metadata: {
                  model: parsed.model,
                  usage: parsed.usage,
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

  async dispose(): Promise<void> {
    await this.httpClient.dispose();
  }
}
```

### Model Manager Implementation

````typescript
// src/providers/implementations/opencode-model-manager.ts
export class OpenCodeModelManager {
  private readonly client: OpenCodeClient;
  private readonly models: Map<string, OpenCodeModel>;
  private readonly modelCache: Map<string, OpenCodeModelInfo>;
  private readonly logger: Logger;

  constructor(client: OpenCodeClient) {
    this.client = client;
    this.models = new Map();
    this.modelCache = new Map();
    this.logger = new Logger({ service: 'opencode-model-manager' });
  }

  async discoverModels(): Promise<void> {
    try {
      const models = await this.client.getModels();

      for (const model of models) {
        this.models.set(model.id, model);
        this.logger.info(`Discovered model: ${model.id}`);
      }

      this.logger.info(`Discovered ${models.length} OpenCode models`);
    } catch (error) {
      this.logger.error('Failed to discover models:', error);
      throw error;
    }
  }

  async getModel(modelId: string): Promise<OpenCodeModel> {
    const model = this.models.get(modelId);
    if (!model) {
      throw new Error(`Model not found: ${modelId}`);
    }
    return model;
  }

  async getModelInfo(modelId: string): Promise<OpenCodeModelInfo> {
    // Check cache first
    const cached = this.modelCache.get(modelId);
    if (cached && this.isCacheValid(cached)) {
      return cached;
    }

    // Fetch from API
    const modelInfo = await this.client.getModelInfo(modelId);
    this.modelCache.set(modelId, modelInfo);

    return modelInfo;
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
    model: OpenCodeModel,
    request: MessageRequest
  ): Promise<AsyncIterable<MessageChunk>> {
    const isChatRequest = request.messages.length > 1 || request.messages[0].role === 'system';

    if (isChatRequest) {
      return this.handleChatCompletion(model, request);
    } else {
      return this.handleCompletion(model, request);
    }
  }

  private async handleChatCompletion(
    model: OpenCodeModel,
    request: MessageRequest
  ): Promise<AsyncIterable<MessageChunk>> {
    const chatRequest: ChatCompletionRequest = {
      messages: request.messages,
      maxTokens: request.maxTokens || model.capabilities.maxTokens,
      temperature: request.temperature,
      topP: request.topP,
      stopSequences: request.stopSequences,
      parameters: this.extractModelParameters(model, request),
    };

    const stream = await this.client.generateChatCompletion(model.id, chatRequest);

    return this.transformChatStream(stream, model.id);
  }

  private async handleCompletion(
    model: OpenCodeModel,
    request: MessageRequest
  ): Promise<AsyncIterable<MessageChunk>> {
    const prompt = this.extractPrompt(request);
    const completionRequest: CompletionRequest = {
      prompt,
      maxTokens: request.maxTokens || model.capabilities.maxTokens,
      temperature: request.temperature,
      topP: request.topP,
      stopSequences: request.stopSequences,
      parameters: this.extractModelParameters(model, request),
    };

    const stream = await this.client.generateCompletion(model.id, completionRequest);

    return this.transformCompletionStream(stream, model.id);
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

  private extractModelParameters(model: OpenCodeModel, request: MessageRequest): any {
    const params: any = {};

    // Model-specific parameters
    if (model.metadata.architecture?.includes('llama')) {
      params.repeat_penalty = request.repeatPenalty || 1.1;
      params.top_k = request.topK || 40;
    }

    if (model.metadata.architecture?.includes('starcoder')) {
      params.alpha_frequency = request.alphaFrequency || 0.0;
      params.alpha_presence = request.alphaPresence || 0.0;
      params.alpha_decay = request.alphaDecay || 0.99;
    }

    return params;
  }

  private async *transformChatStream(
    stream: AsyncIterable<ChatCompletionChunk>,
    modelId: string
  ): AsyncIterable<MessageChunk> {
    for await (const chunk of stream) {
      yield {
        content: chunk.content,
        done: chunk.done,
        metadata: {
          model: modelId,
          ...chunk.metadata,
        },
      };
    }
  }

  private async *transformCompletionStream(
    stream: AsyncIterable<CompletionChunk>,
    modelId: string
  ): AsyncIterable<MessageChunk> {
    for await (const chunk of stream) {
      yield {
        content: chunk.text,
        done: chunk.done,
        metadata: {
          model: modelId,
          ...chunk.metadata,
        },
      };
    }
  }

  private isCacheValid(cached: OpenCodeModelInfo): boolean {
    const cacheAge = Date.now() - new Date(cached.lastUpdated).getTime();
    return cacheAge < 5 * 60 * 1000; // 5 minutes
  }
}
````

### Authentication Manager

```typescript
// src/providers/implementations/opencode-auth.ts
export class OpenCodeAuthManager {
  private readonly config: OpenCodeAuthConfig;
  private readonly secureStore: SecureStore;
  private tokenCache: TokenInfo | null = null;

  constructor(config: OpenCodeAuthConfig) {
    this.config = config;
    this.secureStore = new SecureStore('opencode');
  }

  async getAuthToken(): Promise<string> {
    switch (this.config.type) {
      case 'api-key':
        return this.getApiKeyToken();
      case 'oauth':
        return this.getOAuthToken();
      case 'jwt':
        return this.getJWTToken();
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

  private async getJWTToken(): Promise<string> {
    if (!this.config.jwt.privateKey) {
      throw new Error('Private key is required for JWT authentication');
    }

    const payload = {
      sub: this.config.jwt.subject,
      aud: this.config.jwt.audience,
      exp: Math.floor(Date.now() / 1000) + (this.config.jwt.expiresIn || 3600),
      iat: Math.floor(Date.now() / 1000),
    };

    return jwt.sign(payload, this.config.jwt.privateKey, {
      algorithm: this.config.jwt.algorithm || 'RS256',
    });
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

### Rate Limiting Implementation

```typescript
// src/providers/implementations/opencode-rate-limiter.ts
export class OpenCodeRateLimiter {
  private readonly config: OpenCodeRateLimitConfig;
  private readonly tokenBucket: TokenBucket;
  private readonly requestQueue: RequestQueue;
  private readonly usageTracker: UsageTracker;
  private readonly logger: Logger;

  constructor(config: OpenCodeRateLimitConfig) {
    this.config = config;
    this.tokenBucket = new TokenBucket({
      capacity: config.tokensPerMinute,
      refillRate: config.tokensPerMinute / 60,
    });
    this.requestQueue = new RequestQueue({
      maxConcurrent: config.concurrentRequests,
      maxQueue: config.maxQueueSize,
    });
    this.usageTracker = new UsageTracker();
    this.logger = new Logger({ service: 'opencode-rate-limiter' });
  }

  async initialize(): Promise<void> {
    await this.tokenBucket.initialize();
    await this.usageTracker.initialize();
    this.logger.info('OpenCode rate limiter initialized');
  }

  async acquire(): Promise<void> {
    // Check token availability
    const tokensAvailable = await this.tokenBucket.consume(1);
    if (!tokensAvailable) {
      const waitTime = this.calculateWaitTime();
      this.logger.warn(`Rate limit reached, waiting ${waitTime}ms`);
      await this.sleep(waitTime);
      return this.acquire();
    }

    // Check request queue capacity
    await this.requestQueue.enqueue();

    // Track usage
    await this.usageTracker.recordRequest();
  }

  async release(): Promise<void> {
    await this.requestQueue.dequeue();
  }

  async getUsageStats(): Promise<UsageStats> {
    return this.usageTracker.getStats();
  }

  private calculateWaitTime(): number {
    const refillRate = this.config.tokensPerMinute / 60;
    const tokensNeeded = 1;
    return (tokensNeeded / refillRate) * 1000; // Convert to milliseconds
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}
```

### Configuration Schema

```typescript
// src/providers/configs/opencode-config.schema.ts
export const OpenCodeConfigSchema = {
  type: 'object',
  required: ['auth', 'apiEndpoint', 'rateLimits'],
  properties: {
    apiEndpoint: {
      type: 'string',
      format: 'uri',
      description: 'OpenCode API endpoint URL',
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
              description: 'OpenCode API key',
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
        {
          title: 'JWT Authentication',
          properties: {
            type: { const: 'jwt' },
            privateKey: {
              type: 'string',
              description: 'JWT private key (PEM format)',
            },
            subject: {
              type: 'string',
              description: 'JWT subject',
            },
            audience: {
              type: 'string',
              description: 'JWT audience',
            },
            algorithm: {
              type: 'string',
              enum: ['RS256', 'RS384', 'RS512', 'ES256', 'ES384', 'ES512'],
              default: 'RS256',
            },
            expiresIn: {
              type: 'integer',
              default: 3600,
              description: 'JWT expiration time in seconds',
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
          description: 'Default model to use',
        },
        enabledModels: {
          type: 'array',
          items: { type: 'string' },
          description: 'List of enabled model IDs',
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
        codeCompletion: {
          type: 'boolean',
          default: true,
          description: 'Enable code completion mode',
        },
        codeAnalysis: {
          type: 'boolean',
          default: true,
          description: 'Enable code analysis features',
        },
        customParameters: {
          type: 'boolean',
          default: true,
          description: 'Allow custom model parameters',
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
// tests/providers/opencode-provider.test.ts
describe('OpenCodeProvider', () => {
  let provider: OpenCodeProvider;
  let mockClient: jest.Mocked<OpenCodeClient>;

  beforeEach(() => {
    mockClient = new OpenCodeClient({
      apiEndpoint: 'https://api.opencode.com',
      auth: { type: 'api-key', apiKey: 'test-key' },
    } as any) as jest.Mocked<OpenCodeClient>;

    provider = new OpenCodeProvider({
      apiEndpoint: 'https://api.opencode.com',
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
        { id: 'codellama-7b', name: 'CodeLlama 7B' },
        { id: 'starcoder-15b', name: 'StarCoder 15B' },
      ];

      mockClient.getModels.mockResolvedValue(mockModels as any);

      await provider.initialize();

      const capabilities = await provider.getCapabilities();
      expect(capabilities.models).toHaveLength(2);
      expect(capabilities.models[0].id).toBe('codellama-7b');
    });
  });

  describe('code generation', () => {
    it('should handle code completion requests', async () => {
      const request: MessageRequest = {
        model: 'codellama-7b',
        messages: [{ role: 'user', content: 'function hello() {' }],
        maxTokens: 100,
      };

      const mockStream = createMockCompletionStream();
      mockClient.generateCompletion.mockResolvedValue(mockStream);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThan(0);
      expect(chunks[chunks.length - 1].done).toBe(true);
    });
  });

  describe('authentication', () => {
    it('should authenticate with API key', async () => {
      await provider.initialize();
      expect(mockClient.authenticate).toHaveBeenCalled();
    });

    it('should handle authentication failures', async () => {
      mockClient.authenticate.mockRejectedValue(new Error('Invalid API key'));

      await expect(provider.initialize()).rejects.toThrow('AUTHENTICATION_FAILED');
    });
  });
});
```

### Integration Tests

````typescript
// tests/providers/opencode-integration.test.ts
describe('OpenCode Integration', () => {
  let provider: OpenCodeProvider;

  beforeAll(async () => {
    if (!process.env.OPENCODE_API_KEY_TEST) {
      throw new Error('OPENCODE_API_KEY_TEST environment variable required');
    }

    provider = new OpenCodeProvider({
      apiEndpoint: process.env.OPENCODE_API_ENDPOINT_TEST || 'https://api.opencode.com',
      auth: {
        type: 'api-key',
        apiKey: process.env.OPENCODE_API_KEY_TEST,
      },
      rateLimits: {
        requestsPerMinute: 30,
        tokensPerMinute: 30000,
      },
    });

    await provider.initialize();
  });

  it('should generate code with CodeLlama', async () => {
    const request: MessageRequest = {
      model: 'codellama-7b',
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
    expect(response.metadata.usage.totalTokens).toBeGreaterThan(0);
  });

  it('should handle chat conversations', async () => {
    const request: MessageRequest = {
      model: 'starcoder-15b',
      messages: [{ role: 'user', content: 'How do I implement binary search in JavaScript?' }],
      maxTokens: 300,
    };

    const response = await collectStream(provider.sendMessage(request));

    expect(response.content).toContain('function');
    expect(response.content).toContain('binary');
  });
});
````

## Success Metrics

### Performance Metrics

- Model discovery time: < 5s
- Code generation latency: < 4s
- Streaming latency: < 150ms per chunk
- Authentication time: < 1s
- Token counting accuracy: 100%

### Reliability Metrics

- API request success rate: 99%
- Authentication success rate: 99.9%
- Error handling coverage: 100%
- Retry success rate: 90%

### Integration Metrics

- Model discovery accuracy: 100%
- Code generation quality: 85%+ pass rate
- Test coverage: 90%+
- Configuration validation: 100%

## Dependencies

### External Dependencies

- `axios`: HTTP client for API requests
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
- Implement token rotation
- Never log credentials

### API Security

- Validate all API responses
- Use HTTPS for all communications
- Implement request timeouts
- Add request signing for sensitive operations

### Rate Limiting

- Respect OpenCode rate limits
- Implement token bucket algorithm
- Monitor usage patterns
- Alert on unusual activity

## Deliverables

1. **OpenCode Provider** (`src/providers/implementations/opencode-provider.ts`)
2. **API Client** (`src/providers/implementations/opencode-client.ts`)
3. **Model Manager** (`src/providers/implementations/opencode-model-manager.ts`)
4. **Auth Manager** (`src/providers/implementations/opencode-auth.ts`)
5. **Rate Limiter** (`src/providers/implementations/opencode-rate-limiter.ts`)
6. **Configuration Schema** (`src/providers/configs/opencode-config.schema.ts`)
7. **Unit Tests** (`tests/providers/opencode-provider.test.ts`)
8. **Integration Tests** (`tests/providers/opencode-integration.test.ts`)
9. **Documentation** (`docs/providers/opencode.md`)

This implementation provides comprehensive OpenCode integration with support for various open-source code models, robust authentication, and efficient rate limiting while maintaining compatibility with the unified provider interface.
