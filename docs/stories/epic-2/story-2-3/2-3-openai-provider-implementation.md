# Story 2.3: OpenAI Provider Implementation

**Epic**: Epic 2 - Autonomous Development Workflow  
**Category**: MVP-Critical (AI Provider)  
**Status**: Draft  
**Priority**: High

## User Story

As a **developer**, I want to **implement OpenAI as an additional AI provider**, so that **Tamma can leverage OpenAI's models for autonomous development tasks**.

## Acceptance Criteria

### AC1: OpenAI Provider Implementation

- [ ] Complete implementation of IAIProvider interface for OpenAI
- [ ] Support for GPT-4, GPT-3.5-turbo, and o1 models
- [ ] Streaming response support for real-time feedback
- [ ] Proper error handling for rate limits and API errors
- [ ] Token usage tracking and cost management

### AC2: Model Configuration

- [ ] Configurable model selection and parameters
- [ ] Support for different model capabilities (text, code, reasoning)
- [ ] Temperature, max tokens, and other parameter controls
- [ ] Model-specific optimization and tuning
- [ ] Fallback model configuration

### AC3: Integration Features

- [ ] Seamless integration with existing provider selection logic
- [ ] Context window management for large inputs
- [ ] Function calling and tool use support
- [ ] Batch processing capabilities
- [ ] Response caching and optimization

### AC4: Performance and Reliability

- [ ] Retry logic with exponential backoff
- [ ] Circuit breaker pattern for API failures
- [ ] Performance monitoring and metrics
- [ ] Cost tracking and budget management
- [ ] Comprehensive error handling and logging

## Technical Context

### Architecture Integration

- **Provider Package**: `packages/providers/src/openai/`
- **OpenAI SDK**: Integration with official OpenAI SDK
- **Configuration**: Integration with provider configuration system
- **Monitoring**: Integration with metrics and logging

### OpenAI Provider Implementation

```typescript
class OpenAIProvider implements IAIProvider {
  private client: OpenAI;
  private config: OpenAIConfig;
  private metrics: ProviderMetrics;

  constructor(config: OpenAIConfig) {
    this.config = config;
    this.client = new OpenAI({
      apiKey: config.apiKey,
      baseURL: config.baseUrl,
      timeout: config.timeout || 30000,
    });
    this.metrics = new ProviderMetrics('openai');
  }

  async initialize(): Promise<void> {
    try {
      // Test API connection
      await this.client.models.list();

      // Load model configurations
      await this.loadModelConfigs();

      // Initialize metrics
      this.metrics.initialize();

      console.log('OpenAI provider initialized successfully');
    } catch (error) {
      throw new Error(`Failed to initialize OpenAI provider: ${error.message}`);
    }
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    const startTime = Date.now();

    try {
      const openaiRequest = this.convertToOpenAIRequest(request);

      const stream = await this.client.chat.completions.create({
        ...openaiRequest,
        stream: true,
      });

      this.metrics.incrementCounter('requests_total', { model: request.model });

      return this.createAsyncIterable(stream, startTime, request);
    } catch (error) {
      this.metrics.incrementCounter('errors_total', {
        error_type: this.classifyError(error),
        model: request.model,
      });
      throw this.handleOpenAIError(error);
    }
  }

  async getCapabilities(): Promise<ProviderCapabilities> {
    return {
      supportedModels: [
        {
          id: 'gpt-4',
          name: 'GPT-4',
          maxTokens: 8192,
          supportsStreaming: true,
          supportsFunctionCalling: true,
          costPerToken: 0.00003,
          capabilities: ['text', 'code', 'reasoning'],
        },
        {
          id: 'gpt-3.5-turbo',
          name: 'GPT-3.5 Turbo',
          maxTokens: 4096,
          supportsStreaming: true,
          supportsFunctionCalling: true,
          costPerToken: 0.000002,
          capabilities: ['text', 'code'],
        },
        {
          id: 'o1-preview',
          name: 'OpenAI o1 Preview',
          maxTokens: 32768,
          supportsStreaming: false,
          supportsFunctionCalling: false,
          costPerToken: 0.000015,
          capabilities: ['reasoning', 'text', 'code'],
        },
      ],
      features: {
        streaming: true,
        functionCalling: true,
        imageGeneration: false,
        codeExecution: false,
        webSearch: false,
      },
      limits: {
        requestsPerMinute: 3500,
        tokensPerMinute: 90000,
        requestsPerDay: 10000,
      },
    };
  }

  async dispose(): Promise<void> {
    this.metrics.dispose();
    // Clean up resources
  }

  private convertToOpenAIRequest(request: MessageRequest): any {
    return {
      model: request.model || this.config.defaultModel,
      messages: request.messages.map((msg) => ({
        role: msg.role,
        content: msg.content,
        name: msg.name,
      })),
      max_tokens: request.maxTokens,
      temperature: request.temperature,
      top_p: request.topP,
      frequency_penalty: request.frequencyPenalty,
      presence_penalty: request.presencePenalty,
      functions: request.functions,
      function_call: request.functionCall,
      stream: true,
    };
  }

  private async *createAsyncIterable(
    stream: any,
    startTime: number,
    request: MessageRequest
  ): AsyncIterable<MessageChunk> {
    let fullContent = '';
    let tokenCount = 0;

    try {
      for await (const chunk of stream) {
        const delta = chunk.choices[0]?.delta;

        if (delta?.content) {
          fullContent += delta.content;
          tokenCount++;

          yield {
            content: delta.content,
            role: 'assistant',
            timestamp: new Date(),
            metadata: {
              model: request.model,
              tokensSoFar: tokenCount,
              finishReason: null,
            },
          };
        }

        if (chunk.choices[0]?.finish_reason) {
          const finishReason = chunk.choices[0].finish_reason;
          const duration = Date.now() - startTime;

          // Record metrics
          this.metrics.recordHistogram('request_duration', duration, { model: request.model });
          this.metrics.recordHistogram('tokens_used', tokenCount, { model: request.model });
          this.metrics.incrementCounter('requests_completed', {
            model: request.model,
            finish_reason: finishReason,
          });

          yield {
            content: '',
            role: 'assistant',
            timestamp: new Date(),
            metadata: {
              model: request.model,
              tokensSoFar: tokenCount,
              finishReason,
              duration,
            },
            done: true,
          };

          break;
        }
      }
    } catch (error) {
      this.metrics.incrementCounter('stream_errors_total', {
        error_type: this.classifyError(error),
        model: request.model,
      });
      throw error;
    }
  }

  private handleOpenAIError(error: any): Error {
    if (error.status === 429) {
      return new RateLimitError(
        `OpenAI rate limit exceeded: ${error.message}`,
        error.headers?.['retry-after']
      );
    }

    if (error.status === 401) {
      return new AuthenticationError(`OpenAI authentication failed: ${error.message}`);
    }

    if (error.status === 400) {
      return new ValidationError(`OpenAI validation error: ${error.message}`);
    }

    if (error.status >= 500) {
      return new ProviderError(`OpenAI server error: ${error.message}`, true);
    }

    return new ProviderError(`OpenAI error: ${error.message}`, false);
  }

  private classifyError(error: any): string {
    if (error.status === 429) return 'rate_limit';
    if (error.status === 401) return 'authentication';
    if (error.status === 400) return 'validation';
    if (error.status >= 500) return 'server_error';
    if (error.code === 'ECONNRESET') return 'connection_reset';
    if (error.code === 'ETIMEDOUT') return 'timeout';
    return 'unknown';
  }
}
```

### Configuration Schema

```typescript
interface OpenAIConfig {
  apiKey: string;
  baseUrl?: string;
  defaultModel: string;
  timeout?: number;
  retryPolicy: {
    maxAttempts: number;
    baseDelay: number;
    maxDelay: number;
  };
  rateLimiting: {
    enabled: boolean;
    requestsPerMinute: number;
    tokensPerMinute: number;
  };
  costManagement: {
    budgetLimit?: number;
    alertThreshold: number;
    trackingEnabled: boolean;
  };
}
```

### Model-Specific Optimizations

```typescript
class ModelOptimizer {
  private optimizations: Map<string, ModelOptimization> = new Map([
    [
      'gpt-4',
      {
        temperature: 0.1,
        maxTokens: 4096,
        systemPrompt: 'You are a helpful AI assistant specialized in software development.',
        reasoning: true,
      },
    ],
    [
      'gpt-3.5-turbo',
      {
        temperature: 0.3,
        maxTokens: 2048,
        systemPrompt: 'You are a helpful AI assistant for coding tasks.',
        reasoning: false,
      },
    ],
    [
      'o1-preview',
      {
        temperature: 1, // Fixed temperature for o1 models
        maxTokens: 32768,
        systemPrompt: 'You are a reasoning AI assistant. Think step by step.',
        reasoning: true,
        streaming: false, // o1 models don't support streaming
      },
    ],
  ]);

  optimizeForTask(model: string, taskType: string): ModelOptimization {
    const baseOptimization =
      this.optimizations.get(model) || this.optimizations.get('gpt-3.5-turbo')!;

    // Task-specific adjustments
    switch (taskType) {
      case 'code_generation':
        return {
          ...baseOptimization,
          temperature: baseOptimization.temperature * 0.8, // Lower temperature for code
          maxTokens: Math.min(baseOptimization.maxTokens, 2048),
        };
      case 'reasoning':
        return {
          ...baseOptimization,
          temperature: baseOptimization.temperature * 1.2, // Higher temperature for reasoning
          maxTokens: baseOptimization.maxTokens,
        };
      case 'analysis':
        return {
          ...baseOptimization,
          temperature: baseOptimization.temperature * 0.5, // Lower temperature for analysis
          maxTokens: Math.min(baseOptimization.maxTokens, 1024),
        };
      default:
        return baseOptimization;
    }
  }
}

interface ModelOptimization {
  temperature: number;
  maxTokens: number;
  systemPrompt: string;
  reasoning: boolean;
  streaming?: boolean;
}
```

## Implementation Details

### Phase 1: Basic Provider Implementation

1. **Core Provider Implementation**
   - Implement IAIProvider interface
   - Add OpenAI SDK integration
   - Implement basic message handling
   - Add error handling and logging

2. **Model Support**
   - Add support for GPT-4 and GPT-3.5-turbo
   - Implement model configuration
   - Add parameter validation
   - Test basic functionality

### Phase 2: Advanced Features

1. **Streaming and Optimization**
   - Implement streaming response support
   - Add model-specific optimizations
   - Implement token usage tracking
   - Add performance monitoring

2. **Reliability Features**
   - Add retry logic and circuit breakers
   - Implement rate limiting
   - Add cost tracking
   - Add comprehensive error handling

### Phase 3: Integration and Testing

1. **Provider Integration**
   - Integrate with provider selection logic
   - Add configuration management
   - Implement fallback mechanisms
   - Add monitoring and metrics

2. **Testing and Validation**
   - Comprehensive unit testing
   - Integration testing with real API
   - Performance testing and optimization
   - Security and compliance testing

## Dependencies

### Internal Dependencies

- **Story 1.1**: AI Provider Interface (interface definition)
- **Story 1.2**: Claude Provider Implementation (reference implementation)
- **Story 1.3**: Provider Configuration Management (configuration integration)
- **Story 2.12**: Intelligent Provider Selection (provider selection logic)

### External Dependencies

- **OpenAI SDK**: Official OpenAI Node.js SDK
- **Configuration System**: Provider configuration management
- **Metrics System**: Performance monitoring
- **Logging System**: Error logging and debugging

## Testing Strategy

### Unit Tests

- Provider implementation logic
- Message conversion and handling
- Error classification and handling
- Model optimization logic

### Integration Tests

- End-to-end API integration
- Streaming response handling
- Error recovery and retry logic
- Configuration validation

### Performance Tests

- Response time and throughput
- Memory usage and efficiency
- Concurrent request handling
- Rate limiting behavior

## Success Metrics

### Performance Targets

- **Response Time**: < 2 seconds for GPT-3.5, < 5 seconds for GPT-4
- **Streaming Latency**: < 100ms first token, < 50ms subsequent tokens
- **Error Rate**: < 1% for successful requests
- **Retry Success**: 90%+ successful retry recovery

### Reliability Targets

- **API Availability**: 99.9% uptime
- **Rate Limit Handling**: 100% graceful handling
- **Cost Accuracy**: 99% accurate cost tracking
- **Model Performance**: 95%+ successful task completion

## Risks and Mitigations

### Technical Risks

- **API Changes**: Monitor API updates and adapt quickly
- **Rate Limits**: Implement intelligent rate limiting and queuing
- **Cost Overruns**: Add budget controls and monitoring
- **Performance Issues**: Optimize requests and use caching

### Operational Risks

- **Key Compromise**: Use secure key management
- **Service Outages**: Implement fallback providers
- **Quality Degradation**: Monitor response quality
- **Compliance Issues**: Follow data handling guidelines

## Rollout Plan

### Phase 1: Basic Implementation (Week 1)

- Implement core provider functionality
- Add basic model support
- Test with development environment
- Validate error handling

### Phase 2: Advanced Features (Week 2)

- Add streaming support
- Implement optimizations
- Add monitoring and metrics
- Test with staging environment

### Phase 3: Production Release (Week 3)

- Complete integration testing
- Add cost management
- Deploy to production
- Monitor and optimize

## Definition of Done

- [ ] All acceptance criteria met and verified
- [ ] Unit tests with 95%+ coverage
- [ ] Integration tests passing
- [ ] Performance benchmarks met
- [ ] Security review completed
- [ ] Documentation updated
- [ ] Production deployment successful

## Context XML Generation

This story will generate the following context XML upon completion:

- `2-3-openai-provider-implementation.context.xml` - Complete technical implementation context

---

**Last Updated**: 2025-11-09  
**Next Review**: 2025-11-16  
**Story Owner**: TBD  
**Reviewers**: TBD
