# Task 2: Google Gemini Provider Implementation

## Overview

This task implements the Google Gemini provider integration, enabling access to Google's advanced AI models including Gemini Pro, Gemini Pro Vision, and Gemini Ultra through the unified provider interface.

## Acceptance Criteria

### 2.1: Integrate Google Generative AI SDK

- [ ] Install and configure `@google/generative-ai` SDK
- [ ] Implement Google AI API client initialization
- [ ] Add support for API key authentication
- [ ] Implement proper error handling for SDK operations
- [ ] Add SDK version compatibility checks

### 2.2: Implement Gemini Pro model support

- [ ] Add support for gemini-pro text generation model
- [ ] Implement chat completion with conversation history
- [ ] Add support for system instructions and prompts
- [ ] Implement temperature and top-p parameter controls
- [ ] Add response formatting and validation

### 2.3: Add Gemini Ultra model access when available

- [ ] Implement gemini-1.5-pro model support
- [ ] Add support for gemini-1.5-flash when available
- [ ] Implement model capability detection and selection
- [ ] Add fallback mechanisms for model unavailability
- [ ] Support model-specific parameter tuning

### 2.4: Implement function calling support

- [ ] Add function/tool calling capabilities
- [ ] Implement function definition and registration
- [ ] Add function argument parsing and validation
- [ ] Implement function execution and result handling
- [ ] Add function calling error handling

### 2.5: Add streaming and token counting

- [ ] Implement real-time streaming for text generation
- [ ] Add accurate token counting for requests and responses
- [ ] Implement streaming backpressure handling
- [ ] Add streaming cancellation and timeout
- [ ] Support chunked response aggregation

## Technical Implementation

### Provider Architecture

```typescript
// src/providers/implementations/google-gemini-provider.ts
export class GoogleGeminiProvider implements IAIProvider {
  private readonly client: GoogleGenerativeAI;
  private readonly config: GoogleGeminiConfig;
  private readonly modelManager: GeminiModelManager;
  private readonly rateLimiter: GeminiRateLimiter;
  private readonly logger: Logger;

  constructor(config: GoogleGeminiConfig) {
    this.config = this.validateConfig(config);
    this.client = new GoogleGenerativeAI(this.config.apiKey);
    this.modelManager = new GeminiModelManager(this.client);
    this.rateLimiter = new GeminiRateLimiter(this.config.rateLimits);
    this.logger = new Logger({ service: 'google-gemini-provider' });
  }

  async initialize(): Promise<void> {
    await this.validateApiKey();
    await this.modelManager.initialize();
    await this.rateLimiter.initialize();
    this.logger.info('Google Gemini provider initialized');
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    await this.rateLimiter.acquire();

    try {
      const model = await this.modelManager.getModel(request.model);

      if (request.tools && request.tools.length > 0) {
        return this.handleFunctionCalling(model, request);
      } else {
        return this.handleTextGeneration(model, request);
      }
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
        multimodal: true,
        toolUse: true,
        systemInstructions: true,
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
    this.logger.info('Google Gemini provider disposed');
  }
}
```

### Model Manager Implementation

```typescript
// src/providers/implementations/gemini-model-manager.ts
export class GeminiModelManager {
  private readonly client: GoogleGenerativeAI;
  private readonly models: Map<string, GenerativeModel>;
  private readonly logger: Logger;

  constructor(client: GoogleGenerativeAI) {
    this.client = client;
    this.models = new Map();
    this.logger = new Logger({ service: 'gemini-model-manager' });
  }

  async initialize(): Promise<void> {
    // Initialize available models
    const modelConfigs = await this.getModelConfigs();

    for (const config of modelConfigs) {
      try {
        const model = this.client.getGenerativeModel({
          model: config.name,
          generationConfig: config.defaultConfig,
        });

        this.models.set(config.id, model);
        this.logger.info(`Initialized model: ${config.id}`);
      } catch (error) {
        this.logger.warn(`Failed to initialize model ${config.id}:`, error);
      }
    }
  }

  async getModel(modelId: string): Promise<GenerativeModel> {
    const model = this.models.get(modelId);
    if (!model) {
      throw new Error(`Model not found: ${modelId}`);
    }
    return model;
  }

  async getAvailableModels(): Promise<ModelInfo[]> {
    return [
      {
        id: 'gemini-pro',
        name: 'Gemini Pro',
        provider: 'google-gemini',
        version: '1.0',
        capabilities: {
          maxTokens: 30720,
          streaming: true,
          functionCalling: true,
          multimodal: false,
          toolUse: true,
          systemInstructions: true,
          contextWindow: 32768,
        },
        metadata: {
          description: "Google's advanced language model for text generation",
          category: 'text-generation',
          pricing: { currency: 'USD', perToken: 0.00025 },
          tags: ['text', 'conversation', 'function-calling'],
        },
      },
      {
        id: 'gemini-pro-vision',
        name: 'Gemini Pro Vision',
        provider: 'google-gemini',
        version: '1.0',
        capabilities: {
          maxTokens: 16384,
          streaming: true,
          functionCalling: true,
          multimodal: true,
          toolUse: true,
          systemInstructions: true,
          contextWindow: 16384,
          supportedFormats: ['image/jpeg', 'image/png', 'image/webp'],
        },
        metadata: {
          description: 'Multimodal model supporting text and images',
          category: 'multimodal',
          pricing: { currency: 'USD', perToken: 0.0003 },
          tags: ['multimodal', 'vision', 'image-analysis'],
        },
      },
      {
        id: 'gemini-1.5-pro',
        name: 'Gemini 1.5 Pro',
        provider: 'google-gemini',
        version: '1.5',
        capabilities: {
          maxTokens: 2097152,
          streaming: true,
          functionCalling: true,
          multimodal: true,
          toolUse: true,
          systemInstructions: true,
          contextWindow: 2097152,
          supportedFormats: ['image/jpeg', 'image/png', 'image/webp', 'video/mp4', 'audio/wav'],
        },
        metadata: {
          description: "Google's most advanced model with 2M token context",
          category: 'multimodal',
          pricing: { currency: 'USD', perToken: 0.0035 },
          tags: ['advanced', 'large-context', 'multimodal'],
        },
      },
      {
        id: 'gemini-1.5-flash',
        name: 'Gemini 1.5 Flash',
        provider: 'google-gemini',
        version: '1.5',
        capabilities: {
          maxTokens: 1048576,
          streaming: true,
          functionCalling: true,
          multimodal: true,
          toolUse: true,
          systemInstructions: true,
          contextWindow: 1048576,
          supportedFormats: ['image/jpeg', 'image/png', 'image/webp'],
        },
        metadata: {
          description: 'Fast and efficient model for quick responses',
          category: 'multimodal',
          pricing: { currency: 'USD', perToken: 0.00015 },
          tags: ['fast', 'efficient', 'multimodal'],
        },
      },
    ];
  }

  async handleTextGeneration(
    model: GenerativeModel,
    request: MessageRequest
  ): Promise<AsyncIterable<MessageChunk>> {
    const prompt = this.formatPrompt(request);
    const generationConfig = this.buildGenerationConfig(request);

    const result = await model.generateContentStream({
      contents: prompt,
      generationConfig,
      systemInstruction: request.systemInstruction,
    });

    return this.streamResponse(result, request.model);
  }

  async handleFunctionCalling(
    model: GenerativeModel,
    request: MessageRequest
  ): Promise<AsyncIterable<MessageChunk>> {
    const tools = this.convertTools(request.tools);
    const prompt = this.formatPrompt(request);
    const generationConfig = this.buildGenerationConfig(request);

    const result = await model.generateContentStream({
      contents: prompt,
      tools,
      generationConfig,
      systemInstruction: request.systemInstruction,
    });

    return this.streamFunctionCallResponse(result, request.model, request.tools);
  }

  private formatPrompt(request: MessageRequest): Content[] {
    return request.messages.map((msg) => ({
      role: msg.role === 'assistant' ? 'model' : 'user',
      parts: [{ text: msg.content }],
    }));
  }

  private buildGenerationConfig(request: MessageRequest): GenerationConfig {
    return {
      temperature: request.temperature,
      topP: request.topP,
      topK: request.topK,
      maxOutputTokens: request.maxTokens,
      stopSequences: request.stopSequences,
      candidateCount: 1,
    };
  }

  private convertTools(tools: Tool[]): Tool[] {
    return tools.map((tool) => ({
      functionDeclaration: {
        name: tool.name,
        description: tool.description,
        parameters: tool.parameters,
      },
    }));
  }

  private async *streamResponse(
    result: GenerateContentStreamResult,
    modelId: string
  ): AsyncIterable<MessageChunk> {
    let fullContent = '';

    for await (const chunk of result.stream) {
      const text = chunk.text();
      fullContent += text;

      yield {
        content: text,
        done: false,
        metadata: {
          model: modelId,
          timestamp: new Date().toISOString(),
          usage: {
            promptTokens: chunk.usageMetadata?.promptTokenCount || 0,
            completionTokens: chunk.usageMetadata?.candidatesTokenCount || 0,
            totalTokens: chunk.usageMetadata?.totalTokenCount || 0,
          },
        },
      };
    }

    // Final chunk with done flag
    yield {
      content: '',
      done: true,
      metadata: {
        model: modelId,
        timestamp: new Date().toISOString(),
        finishReason: 'stop',
      },
    };
  }

  private async *streamFunctionCallResponse(
    result: GenerateContentStreamResult,
    modelId: string,
    tools: Tool[]
  ): AsyncIterable<MessageChunk> {
    let functionCalls: FunctionCall[] = [];

    for await (const chunk of result.stream) {
      const response = chunk.response;

      if (response.functionCalls && response.functionCalls.length > 0) {
        functionCalls = response.functionCalls;

        for (const call of response.functionCalls) {
          yield {
            content: '',
            done: false,
            metadata: {
              model: modelId,
              timestamp: new Date().toISOString(),
              functionCall: {
                name: call.name,
                arguments: call.args,
              },
            },
          };
        }
      } else if (response.text) {
        yield {
          content: response.text(),
          done: false,
          metadata: {
            model: modelId,
            timestamp: new Date().toISOString(),
          },
        };
      }
    }

    // Execute function calls if any
    if (functionCalls.length > 0) {
      for (const call of functionCalls) {
        const tool = tools.find((t) => t.name === call.name);
        if (tool && tool.handler) {
          try {
            const result = await tool.handler(call.args);

            yield {
              content: JSON.stringify(result),
              done: false,
              metadata: {
                model: modelId,
                timestamp: new Date().toISOString(),
                functionResult: {
                  name: call.name,
                  result,
                },
              },
            };
          } catch (error) {
            yield {
              content: `Error executing function ${call.name}: ${error.message}`,
              done: false,
              metadata: {
                model: modelId,
                timestamp: new Date().toISOString(),
                error: {
                  function: call.name,
                  message: error.message,
                },
              },
            };
          }
        }
      }
    }

    // Final chunk
    yield {
      content: '',
      done: true,
      metadata: {
        model: modelId,
        timestamp: new Date().toISOString(),
        finishReason: 'stop',
      },
    };
  }
}
```

### Rate Limiting Implementation

```typescript
// src/providers/implementations/gemini-rate-limiter.ts
export class GeminiRateLimiter {
  private readonly config: GeminiRateLimitConfig;
  private readonly tokenBucket: TokenBucket;
  private readonly requestQueue: RequestQueue;
  private readonly logger: Logger;

  constructor(config: GeminiRateLimitConfig) {
    this.config = config;
    this.tokenBucket = new TokenBucket({
      capacity: config.tokensPerMinute,
      refillRate: config.tokensPerMinute / 60,
    });
    this.requestQueue = new RequestQueue({
      maxConcurrent: config.concurrentRequests,
      maxQueue: config.maxQueueSize,
    });
    this.logger = new Logger({ service: 'gemini-rate-limiter' });
  }

  async initialize(): Promise<void> {
    // Initialize rate limiting state
    await this.tokenBucket.initialize();
    this.logger.info('Gemini rate limiter initialized');
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
  }

  async release(): Promise<void> {
    await this.requestQueue.dequeue();
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
// src/providers/configs/google-gemini-config.schema.ts
export const GoogleGeminiConfigSchema = {
  type: 'object',
  required: ['apiKey', 'rateLimits'],
  properties: {
    apiKey: {
      type: 'string',
      minLength: 39,
      description: 'Google AI API key',
    },
    rateLimits: {
      type: 'object',
      required: ['requestsPerMinute', 'tokensPerMinute'],
      properties: {
        requestsPerMinute: {
          type: 'integer',
          minimum: 1,
          maximum: 60,
          default: 60,
          description: 'Maximum requests per minute',
        },
        tokensPerMinute: {
          type: 'integer',
          minimum: 1000,
          maximum: 32000,
          default: 32000,
          description: 'Maximum tokens per minute',
        },
        concurrentRequests: {
          type: 'integer',
          minimum: 1,
          maximum: 10,
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
          enum: ['gemini-pro', 'gemini-pro-vision', 'gemini-1.5-pro', 'gemini-1.5-flash'],
          default: 'gemini-pro',
          description: 'Default model to use',
        },
        enabledModels: {
          type: 'array',
          items: {
            type: 'string',
            enum: ['gemini-pro', 'gemini-pro-vision', 'gemini-1.5-pro', 'gemini-1.5-flash'],
          },
          default: ['gemini-pro', 'gemini-pro-vision'],
          description: 'List of enabled models',
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
        functionCalling: {
          type: 'boolean',
          default: true,
          description: 'Enable function calling',
        },
        multimodal: {
          type: 'boolean',
          default: true,
          description: 'Enable multimodal capabilities',
        },
        systemInstructions: {
          type: 'boolean',
          default: true,
          description: 'Enable system instructions',
        },
      },
    },
    safety: {
      type: 'object',
      properties: {
        enableSafetySettings: {
          type: 'boolean',
          default: true,
          description: 'Enable Google safety filters',
        },
        harassmentThreshold: {
          type: 'string',
          enum: ['BLOCK_NONE', 'BLOCK_ONLY_HIGH', 'BLOCK_MEDIUM_AND_ABOVE', 'BLOCK_LOW_AND_ABOVE'],
          default: 'BLOCK_MEDIUM_AND_ABOVE',
        },
        hateSpeechThreshold: {
          type: 'string',
          enum: ['BLOCK_NONE', 'BLOCK_ONLY_HIGH', 'BLOCK_MEDIUM_AND_ABOVE', 'BLOCK_LOW_AND_ABOVE'],
          default: 'BLOCK_MEDIUM_AND_ABOVE',
        },
        sexuallyExplicitThreshold: {
          type: 'string',
          enum: ['BLOCK_NONE', 'BLOCK_ONLY_HIGH', 'BLOCK_MEDIUM_AND_ABOVE', 'BLOCK_LOW_AND_ABOVE'],
          default: 'BLOCK_MEDIUM_AND_ABOVE',
        },
        dangerousContentThreshold: {
          type: 'string',
          enum: ['BLOCK_NONE', 'BLOCK_ONLY_HIGH', 'BLOCK_MEDIUM_AND_ABOVE', 'BLOCK_LOW_AND_ABOVE'],
          default: 'BLOCK_MEDIUM_AND_ABOVE',
        },
      },
    },
  },
};
```

### Error Handling

```typescript
// src/providers/implementations/gemini-error-handler.ts
export class GeminiErrorHandler {
  private readonly logger: Logger;

  constructor(logger: Logger) {
    this.logger = logger;
  }

  handleError(error: any): TammaError {
    if (error.status === 400) {
      return new TammaError(
        'GEMINI_BAD_REQUEST',
        'Invalid request parameters or content',
        { details: error.message },
        false,
        'medium'
      );
    }

    if (error.status === 401) {
      return new TammaError(
        'GEMINI_AUTHENTICATION_FAILED',
        'Invalid API key or authentication failed',
        { originalError: error.message },
        true,
        'high'
      );
    }

    if (error.status === 403) {
      if (error.message?.includes('quota')) {
        return new TammaError(
          'GEMINI_QUOTA_EXCEEDED',
          'API quota exceeded. Please check your usage and billing.',
          { quotaType: this.extractQuotaType(error.message) },
          true,
          'high'
        );
      }

      return new TammaError(
        'GEMINI_ACCESS_DENIED',
        'Access denied. Check your API key permissions.',
        { originalError: error.message },
        false,
        'high'
      );
    }

    if (error.status === 429) {
      return new TammaError(
        'GEMINI_RATE_LIMITED',
        'Too many requests. Please try again later.',
        { retryAfter: error.headers?.['retry-after'] },
        true,
        'medium'
      );
    }

    if (error.status === 500) {
      return new TammaError(
        'GEMINI_SERVER_ERROR',
        'Google AI server error. Please try again later.',
        { originalError: error.message },
        true,
        'high'
      );
    }

    if (error.status === 503) {
      return new TammaError(
        'GEMINI_SERVICE_UNAVAILABLE',
        'Google AI service temporarily unavailable.',
        { originalError: error.message },
        true,
        'high'
      );
    }

    // Handle specific Gemini SDK errors
    if (error.message?.includes('Content blocked')) {
      return new TammaError(
        'GEMINI_CONTENT_BLOCKED',
        'Content was blocked by safety filters.',
        { category: this.extractSafetyCategory(error.message) },
        false,
        'medium'
      );
    }

    if (error.message?.includes('Model not found')) {
      return new TammaError(
        'GEMINI_MODEL_NOT_FOUND',
        'Requested model is not available.',
        { model: error.message.match(/model\s+(\w+)/)?.[1] },
        false,
        'medium'
      );
    }

    return new TammaError(
      'GEMINI_UNKNOWN_ERROR',
      `Unknown Gemini error: ${error.message}`,
      { originalError: error },
      false,
      'medium'
    );
  }

  private extractQuotaType(message: string): string {
    if (message.includes('token')) return 'tokens';
    if (message.includes('request')) return 'requests';
    return 'unknown';
  }

  private extractSafetyCategory(message: string): string {
    if (message.includes('harassment')) return 'harassment';
    if (message.includes('hate')) return 'hate_speech';
    if (message.includes('sexually')) return 'sexually_explicit';
    if (message.includes('dangerous')) return 'dangerous_content';
    return 'unknown';
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/google-gemini-provider.test.ts
describe('GoogleGeminiProvider', () => {
  let provider: GoogleGeminiProvider;
  let mockClient: jest.Mocked<GoogleGenerativeAI>;

  beforeEach(() => {
    mockClient = new GoogleGenerativeAI('test-key') as jest.Mocked<GoogleGenerativeAI>;
    provider = new GoogleGeminiProvider({
      apiKey: 'test-key',
      rateLimits: {
        requestsPerMinute: 60,
        tokensPerMinute: 32000,
      },
    });
  });

  describe('model initialization', () => {
    it('should initialize available models', async () => {
      await provider.initialize();

      const capabilities = await provider.getCapabilities();
      expect(capabilities.models).toHaveLength(4);
      expect(capabilities.models[0].id).toBe('gemini-pro');
    });
  });

  describe('text generation', () => {
    it('should handle text generation requests', async () => {
      const request: MessageRequest = {
        model: 'gemini-pro',
        messages: [{ role: 'user', content: 'Hello, world!' }],
        maxTokens: 100,
      };

      const mockStream = createMockGeminiStream();
      jest.spyOn(mockClient, 'getGenerativeModel').mockReturnValue({
        generateContentStream: jest.fn().mockResolvedValue(mockStream),
      } as any);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.length).toBeGreaterThan(0);
      expect(chunks[chunks.length - 1].done).toBe(true);
    });
  });

  describe('function calling', () => {
    it('should handle function calling requests', async () => {
      const request: MessageRequest = {
        model: 'gemini-pro',
        messages: [{ role: 'user', content: 'What is the weather?' }],
        tools: [
          {
            name: 'get_weather',
            description: 'Get weather information',
            parameters: {
              type: 'object',
              properties: {
                location: { type: 'string' },
              },
            },
            handler: async (args) => ({ temperature: 72, condition: 'sunny' }),
          },
        ],
      };

      const mockStream = createMockFunctionCallStream();
      jest.spyOn(mockClient, 'getGenerativeModel').mockReturnValue({
        generateContentStream: jest.fn().mockResolvedValue(mockStream),
      } as any);

      const chunks = [];
      for await (const chunk of provider.sendMessage(request)) {
        chunks.push(chunk);
      }

      expect(chunks.some((c) => c.metadata.functionCall)).toBe(true);
      expect(chunks.some((c) => c.metadata.functionResult)).toBe(true);
    });
  });
});
```

### Integration Tests

```typescript
// tests/providers/google-gemini-integration.test.ts
describe('Google Gemini Integration', () => {
  let provider: GoogleGeminiProvider;

  beforeAll(async () => {
    if (!process.env.GOOGLE_AI_API_KEY_TEST) {
      throw new Error('GOOGLE_AI_API_KEY_TEST environment variable required');
    }

    provider = new GoogleGeminiProvider({
      apiKey: process.env.GOOGLE_AI_API_KEY_TEST,
      rateLimits: {
        requestsPerMinute: 30,
        tokensPerMinute: 16000,
      },
    });

    await provider.initialize();
  });

  it('should generate text with Gemini Pro', async () => {
    const request: MessageRequest = {
      model: 'gemini-pro',
      messages: [
        {
          role: 'user',
          content: 'Explain quantum computing in simple terms.',
        },
      ],
      maxTokens: 200,
    };

    const response = await collectStream(provider.sendMessage(request));

    expect(response.content).toContain('quantum');
    expect(response.metadata.usage.totalTokens).toBeGreaterThan(0);
  });

  it('should handle function calling', async () => {
    const request: MessageRequest = {
      model: 'gemini-pro',
      messages: [
        {
          role: 'user',
          content: 'What is the weather in New York?',
        },
      ],
      tools: [
        {
          name: 'get_weather',
          description: 'Get current weather for a location',
          parameters: {
            type: 'object',
            properties: {
              location: { type: 'string' },
            },
            required: ['location'],
          },
          handler: async (args) => ({
            location: args.location,
            temperature: 22,
            condition: 'partly cloudy',
          }),
        },
      ],
    };

    const chunks = [];
    for await (const chunk of provider.sendMessage(request)) {
      chunks.push(chunk);
    }

    const functionCall = chunks.find((c) => c.metadata.functionCall);
    const functionResult = chunks.find((c) => c.metadata.functionResult);

    expect(functionCall).toBeDefined();
    expect(functionResult).toBeDefined();
    expect(functionResult.metadata.functionResult.location).toBe('New York');
  });
});
```

## Success Metrics

### Performance Metrics

- Model initialization time: < 2s
- Text generation latency: < 3s
- Function calling latency: < 4s
- Streaming latency: < 200ms per chunk
- Token counting accuracy: 100%

### Reliability Metrics

- API request success rate: 99.5%
- Error handling coverage: 100%
- Retry success rate: 95%
- Rate limiting accuracy: 99%+

### Integration Metrics

- Model discovery accuracy: 100%
- Function calling success rate: 95%
- Safety filter effectiveness: 99%
- Test coverage: 95%+

## Dependencies

### External Dependencies

- `@google/generative-ai`: Google Generative AI SDK
- `@google-cloud/aiplatform`: Optional for Vertex AI integration

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/errors`: Error handling utilities
- `@tamma/core/logging`: Logging utilities
- `@tamma/core/config`: Configuration management

## Security Considerations

### API Key Management

- Encrypt API keys at rest
- Use secure key storage (OS keychain)
- Implement key rotation
- Never log API keys

### Content Safety

- Enable Google safety filters by default
- Configure appropriate safety thresholds
- Log safety filter activations
- Handle blocked content gracefully

### Rate Limiting

- Respect Google AI rate limits
- Implement token bucket algorithm
- Monitor usage patterns
- Alert on unusual activity

## Deliverables

1. **Google Gemini Provider** (`src/providers/implementations/google-gemini-provider.ts`)
2. **Model Manager** (`src/providers/implementations/gemini-model-manager.ts`)
3. **Rate Limiter** (`src/providers/implementations/gemini-rate-limiter.ts`)
4. **Error Handler** (`src/providers/implementations/gemini-error-handler.ts`)
5. **Configuration Schema** (`src/providers/configs/google-gemini-config.schema.ts`)
6. **Unit Tests** (`tests/providers/google-gemini-provider.test.ts`)
7. **Integration Tests** (`tests/providers/google-gemini-integration.test.ts`)
8. **Documentation** (`docs/providers/google-gemini.md`)

This implementation provides comprehensive Google Gemini integration with support for text generation, function calling, multimodal capabilities, and robust error handling while maintaining compatibility with the unified provider interface.
