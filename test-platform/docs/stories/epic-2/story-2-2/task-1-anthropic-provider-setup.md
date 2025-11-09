# Task 1: Anthropic Provider Setup

**Story**: 2.2 - Anthropic Claude Provider Implementation  
**Status**: ready-for-dev  
**Priority**: High

## Overview

Implement the foundational Anthropic Claude provider class that integrates with the Anthropic SDK, handles authentication, and provides model discovery capabilities. This task establishes the core provider implementation that all other features will build upon.

## Detailed Implementation Plan

### Subtask 1.1: Create AnthropicClaudeProvider Class

**File**: `src/providers/anthropic/anthropic-claude-provider.ts`

```typescript
import { Anthropic } from '@anthropic-ai/sdk';
import type {
  IAIProvider,
  ProviderConfig,
  ProviderCapabilities,
  MessageRequest,
  ModelInfo,
} from '@tamma/shared/contracts';
import { logger } from '@tamma/observability';
import { AnthropicProviderConfig, AnthropicModelConfig } from '../types/anthropic-types';
import { CLAUDE_MODELS } from '../models/claude-models';
import { AnthropicError } from '../errors/anthropic-error';

export class AnthropicClaudeProvider implements IAIProvider {
  private readonly client: Anthropic;
  private readonly config: AnthropicProviderConfig;
  private readonly models: Map<string, AnthropicModelConfig>;
  private isInitialized = false;

  constructor(config: AnthropicProviderConfig) {
    this.config = config;
    this.models = new Map();

    // Initialize Anthropic client
    this.client = new Anthropic({
      apiKey: config.apiKey,
      baseURL: config.baseURL,
      timeout: config.timeout || 30000,
      maxRetries: 0, // We handle retries ourselves
    });

    // Register available models
    this.registerModels();
  }

  async initialize(): Promise<void> {
    try {
      logger.info('Initializing Anthropic Claude provider', {
        provider: 'anthropic-claude',
        baseURL: this.config.baseURL,
      });

      // Test authentication with a minimal API call
      await this.validateAuthentication();

      this.isInitialized = true;
      logger.info('Anthropic Claude provider initialized successfully');
    } catch (error) {
      logger.error('Failed to initialize Anthropic Claude provider', {
        error: error.message,
        provider: 'anthropic-claude',
      });
      throw new AnthropicError('PROVIDER_INIT_FAILED', 'Failed to initialize Anthropic provider', {
        cause: error,
      });
    }
  }

  private async validateAuthentication(): Promise<void> {
    try {
      // Use messages API with minimal content to validate auth
      await this.client.messages.create({
        model: CLAUDE_MODELS['claude-3-5-haiku'].id,
        max_tokens: 1,
        messages: [{ role: 'user', content: 'test' }],
      });
    } catch (error) {
      if (error.status === 401) {
        throw new AnthropicError('AUTHENTICATION_FAILED', 'Invalid Anthropic API key', {
          status: error.status,
        });
      }
      throw error;
    }
  }

  private registerModels(): void {
    Object.values(CLAUDE_MODELS).forEach((model) => {
      this.models.set(model.id, model);
    });
  }

  getCapabilities(): ProviderCapabilities {
    return {
      supportedModels: Array.from(this.models.keys()),
      maxTokens: Math.max(...Array.from(this.models.values()).map((m) => m.maxTokens)),
      supportsStreaming: true,
      supportsFunctionCalling: true,
      supportsToolUse: true,
      supportsSystemMessages: true,
      supportsMultimodal: false, // Claude doesn't support images yet
      contextWindowSizes: Object.fromEntries(
        Array.from(this.models.entries()).map(([id, config]) => [id, config.maxTokens])
      ),
    };
  }

  getAvailableModels(): ModelInfo[] {
    return Array.from(this.models.values()).map((model) => ({
      id: model.id,
      name: model.name,
      description: model.description,
      maxTokens: model.maxTokens,
      inputCostPer1K: model.pricing.input,
      outputCostPer1K: model.pricing.output,
      capabilities: model.capabilities,
    }));
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<any>> {
    if (!this.isInitialized) {
      throw new AnthropicError(
        'PROVIDER_NOT_INITIALIZED',
        'Provider must be initialized before sending messages'
      );
    }

    const modelConfig = this.models.get(request.model);
    if (!modelConfig) {
      throw new AnthropicError('MODEL_NOT_SUPPORTED', `Model ${request.model} is not supported`, {
        model: request.model,
        availableModels: Array.from(this.models.keys()),
      });
    }

    // Implementation will be completed in Task 2 (Streaming Implementation)
    throw new Error('Streaming implementation to be completed in Task 2');
  }

  async dispose(): Promise<void> {
    this.isInitialized = false;
    logger.info('Anthropic Claude provider disposed');
  }
}
```

### Subtask 1.2: Configure Anthropic SDK Integration

**File**: `src/providers/anthropic/config/anthropic-config.ts`

```typescript
import type { ProviderConfig } from '@tamma/shared/contracts';

export interface AnthropicProviderConfig extends ProviderConfig {
  readonly apiKey: string;
  readonly baseURL?: string;
  readonly timeout?: number;
  readonly defaultModel?: string;
  readonly retryConfig?: {
    maxAttempts: number;
    baseDelay: number;
    maxDelay: number;
  };
  readonly rateLimitConfig?: {
    requestsPerMinute: number;
    tokensPerMinute: number;
  };
}

export const DEFAULT_ANTHROPIC_CONFIG: Partial<AnthropicProviderConfig> = {
  baseURL: 'https://api.anthropic.com',
  timeout: 30000,
  defaultModel: 'claude-3-5-sonnet-20241022',
  retryConfig: {
    maxAttempts: 3,
    baseDelay: 1000,
    maxDelay: 10000,
  },
  rateLimitConfig: {
    requestsPerMinute: 50,
    tokensPerMinute: 40000,
  },
};
```

### Subtask 1.3: Implement Model Discovery

**File**: `src/providers/anthropic/models/claude-models.ts`

```typescript
import type { AnthropicModelConfig } from '../types/anthropic-types';

export const CLAUDE_MODELS: Record<string, AnthropicModelConfig> = {
  'claude-3-5-sonnet-20241022': {
    id: 'claude-3-5-sonnet-20241022',
    name: 'Claude 3.5 Sonnet (October 2024)',
    description: 'Most powerful model for complex tasks, updated October 2024',
    maxTokens: 200000,
    pricing: {
      input: 3.0, // $3.00 per 1M input tokens
      output: 15.0, // $15.00 per 1M output tokens
    },
    capabilities: ['text-generation', 'function-calling', 'tool-use', 'long-context', 'analysis'],
    contextWindow: 200000,
    releaseDate: '2024-10-22',
  },

  'claude-3-5-haiku-20241022': {
    id: 'claude-3-5-haiku-20241022',
    name: 'Claude 3.5 Haiku (October 2024)',
    description: 'Fast and efficient model for everyday tasks, updated October 2024',
    maxTokens: 200000,
    pricing: {
      input: 0.25, // $0.25 per 1M input tokens
      output: 1.25, // $1.25 per 1M output tokens
    },
    capabilities: ['text-generation', 'function-calling', 'tool-use', 'long-context', 'speed'],
    contextWindow: 200000,
    releaseDate: '2024-10-22',
  },

  'claude-3-opus-20240229': {
    id: 'claude-3-opus-20240229',
    name: 'Claude 3 Opus (February 2024)',
    description: 'Most capable model for highly complex tasks',
    maxTokens: 200000,
    pricing: {
      input: 15.0, // $15.00 per 1M input tokens
      output: 75.0, // $75.00 per 1M output tokens
    },
    capabilities: [
      'text-generation',
      'function-calling',
      'tool-use',
      'long-context',
      'analysis',
      'reasoning',
    ],
    contextWindow: 200000,
    releaseDate: '2024-02-29',
  },
};

// Model aliases for convenience
export const MODEL_ALIASES: Record<string, string> = {
  'claude-3-5-sonnet': 'claude-3-5-sonnet-20241022',
  'claude-3-5-haiku': 'claude-3-5-haiku-20241022',
  'claude-3-opus': 'claude-3-opus-20240229',
  sonnet: 'claude-3-5-sonnet-20241022',
  haiku: 'claude-3-5-haiku-20241022',
  opus: 'claude-3-opus-20240229',
};

export function resolveModelAlias(model: string): string {
  return MODEL_ALIASES[model] || model;
}

export function isValidClaudeModel(model: string): boolean {
  return model in CLAUDE_MODELS || model in MODEL_ALIASES;
}
```

### Subtask 1.4: Add Model-Specific Configuration

**File**: `src/providers/anthropic/types/anthropic-types.ts`

```typescript
import type { ProviderCapabilities, ModelInfo, PricingInfo } from '@tamma/shared/contracts';

export interface AnthropicModelConfig {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly maxTokens: number;
  readonly pricing: PricingInfo;
  readonly capabilities: string[];
  readonly contextWindow: number;
  readonly releaseDate: string;
}

export interface AnthropicMessageRequest {
  readonly model: string;
  readonly messages: AnthropicMessage[];
  readonly maxTokens?: number;
  readonly temperature?: number;
  readonly topP?: number;
  readonly stopSequences?: string[];
  readonly system?: string;
  readonly tools?: AnthropicTool[];
  readonly stream?: boolean;
}

export interface AnthropicMessage {
  readonly role: 'user' | 'assistant';
  readonly content: string | AnthropicContentBlock[];
}

export interface AnthropicContentBlock {
  readonly type: 'text' | 'tool_use' | 'tool_result';
  readonly text?: string;
  readonly id?: string;
  readonly name?: string;
  readonly input?: Record<string, unknown>;
  readonly content?: string | AnthropicContentBlock[];
  readonly tool_use_id?: string;
  readonly is_error?: boolean;
}

export interface AnthropicTool {
  readonly name: string;
  readonly description: string;
  readonly input_schema: {
    readonly type: 'object';
    readonly properties: Record<string, unknown>;
    readonly required: string[];
  };
}

export interface AnthropicUsage {
  readonly input_tokens: number;
  readonly output_tokens: number;
}

export interface AnthropicResponse {
  readonly id: string;
  readonly type: 'message';
  readonly role: 'assistant';
  readonly content: AnthropicContentBlock[];
  readonly model: string;
  readonly stop_reason: 'end_turn' | 'max_tokens' | 'stop_sequence' | 'tool_use';
  readonly stop_sequence?: string;
  readonly usage: AnthropicUsage;
}
```

## Dependencies

### Internal Dependencies

- `@tamma/shared/contracts` - Core interfaces and types
- `@tamma/observability` - Logging utilities
- Story 2.1 interfaces (`IAIProvider`, `ProviderConfig`, etc.)

### External Dependencies

- `@anthropic-ai/sdk` - Official Anthropic SDK
- TypeScript 5.7+ with strict mode

## Testing Strategy

### Unit Tests

```typescript
// tests/providers/anthropic/anthropic-claude-provider.test.ts
describe('AnthropicClaudeProvider', () => {
  describe('constructor', () => {
    it('should initialize with valid config');
    it('should register all Claude models');
    it('should create Anthropic client with correct options');
  });

  describe('initialize', () => {
    it('should successfully initialize with valid API key');
    it('should throw authentication error with invalid API key');
    it('should handle network errors during initialization');
  });

  describe('getCapabilities', () => {
    it('should return correct provider capabilities');
    it('should include all supported models');
    it('should reflect correct feature support');
  });

  describe('getAvailableModels', () => {
    it('should return all available models with correct info');
    it('should include pricing information');
    it('should include model capabilities');
  });
});
```

### Integration Tests

```typescript
// tests/providers/anthropic/integration.test.ts
describe('AnthropicClaudeProvider Integration', () => {
  let provider: AnthropicClaudeProvider;

  beforeAll(async () => {
    provider = new AnthropicClaudeProvider({
      apiKey: process.env.ANTHROPIC_API_KEY_TEST!,
      timeout: 30000,
    });
    await provider.initialize();
  });

  afterAll(async () => {
    await provider.dispose();
  });

  it('should connect to real Anthropic API');
  it('should validate authentication with real API');
  it('should retrieve actual model capabilities');
});
```

## Risk Mitigation

### Technical Risks

1. **API Key Security**: Ensure keys are never logged or exposed
   - Mitigation: Use secure credential storage, redact logs
2. **SDK Compatibility**: Anthropic SDK updates might break compatibility
   - Mitigation: Pin to specific version, implement compatibility tests
3. **Model Availability**: Models might be deprecated or changed
   - Mitigation: Implement model validation, graceful fallbacks

### Operational Risks

1. **Rate Limiting**: Exceeding API rate limits
   - Mitigation: Implement rate limiting, queue management
2. **Network Connectivity**: API connectivity issues
   - Mitigation: Retry logic, circuit breaker pattern
3. **Cost Management**: Unexpected high costs
   - Mitigation: Token counting, cost alerts, usage limits

## Deliverables

1. **Core Provider Class**: `AnthropicClaudeProvider` implementing `IAIProvider`
2. **Configuration System**: Type-safe configuration with validation
3. **Model Registry**: Complete Claude model definitions with capabilities
4. **Type Definitions**: Comprehensive TypeScript interfaces
5. **Unit Tests**: Full test coverage for all components
6. **Integration Tests**: Real API connection validation
7. **Documentation**: Setup and usage instructions

## Success Criteria

- [ ] Provider implements all required `IAIProvider` methods
- [ ] Successfully authenticates with Anthropic API
- [ ] Registers all Claude 3.5 and 3 Opus models
- [ ] Provides accurate model capabilities and pricing
- [ ] Handles initialization errors gracefully
- [ ] Passes all unit and integration tests
- [ ] Comprehensive error handling with structured logging
- [ ] Secure credential management with no exposure in logs

## File Structure

```
src/providers/anthropic/
├── anthropic-claude-provider.ts     # Main provider class
├── config/
│   └── anthropic-config.ts          # Configuration types
├── models/
│   └── claude-models.ts             # Model definitions
├── types/
│   └── anthropic-types.ts           # TypeScript interfaces
└── index.ts                         # Public exports

tests/providers/anthropic/
├── anthropic-claude-provider.test.ts
├── integration.test.ts
└── fixtures/
    └── mock-responses.ts
```

This task establishes the foundation for the Anthropic Claude provider, providing the core infrastructure that subsequent tasks will build upon for streaming, error handling, and advanced features.
