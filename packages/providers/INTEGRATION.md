# AI Provider Integration Guide

This guide explains how to integrate new AI providers into the Tamma platform using the provider abstraction layer.

## Overview

The Tamma platform supports multiple AI providers through a unified interface. This allows:

- Easy switching between providers
- Provider-specific optimizations
- Graceful fallbacks
- Consistent API across all providers

## Provider Interface

All providers must implement the `IAIProvider` interface:

```typescript
interface IAIProvider {
  initialize(config: ProviderConfig): Promise<void>;
  sendMessage(
    request: MessageRequest,
    options?: StreamOptions
  ): Promise<AsyncIterable<MessageChunk>>;
  sendMessageSync(request: MessageRequest): Promise<MessageResponse>;
  getCapabilities(): ProviderCapabilities;
  getModels(): Promise<ModelInfo[]>;
  dispose(): Promise<void>;
}
```

## Step-by-Step Integration

### 1. Create Provider Class

Create a new file in `src/providers/` directory:

```typescript
// src/providers/my-provider.ts
import type {
  IAIProvider,
  ProviderConfig,
  MessageRequest,
  MessageResponse,
  MessageChunk,
  ProviderCapabilities,
  ModelInfo,
  StreamOptions,
} from '../types.js';

export class MyProvider implements IAIProvider {
  private config: ProviderConfig;
  private initialized = false;

  async initialize(config: ProviderConfig): Promise<void> {
    this.config = config;
    // Initialize provider-specific client
    this.initialized = true;
  }

  async sendMessage(
    request: MessageRequest,
    options?: StreamOptions
  ): Promise<AsyncIterable<MessageChunk>> {
    this.ensureInitialized();
    // Implement streaming message sending
    throw new Error('Not implemented');
  }

  async sendMessageSync(request: MessageRequest): Promise<MessageResponse> {
    this.ensureInitialized();
    // Implement synchronous message sending
    throw new Error('Not implemented');
  }

  getCapabilities(): ProviderCapabilities {
    return {
      supportsStreaming: true,
      supportsImages: false,
      supportsTools: true,
      maxInputTokens: 100000,
      maxOutputTokens: 4096,
      supportedModels: [],
      features: {},
    };
  }

  async getModels(): Promise<ModelInfo[]> {
    // Return available models
    return [];
  }

  async dispose(): Promise<void> {
    // Clean up resources
    this.initialized = false;
  }

  private ensureInitialized(): void {
    if (!this.initialized) {
      throw new Error('Provider not initialized');
    }
  }
}
```

### 2. Register Provider Type

Add your provider type to `types.ts`:

```typescript
export const PROVIDER_TYPES = {
  // ... existing types
  MY_PROVIDER: 'my-provider',
} as const;

export type ProviderType = (typeof PROVIDER_TYPES)[keyof typeof PROVIDER_TYPES];
```

### 3. Register Provider Creator

Update `factory.ts` to register your provider:

```typescript
private registerBuiltinCreators(): void {
  // ... existing creators

  this.creators.set(PROVIDER_TYPES.MY_PROVIDER, async (config) => {
    const { MyProvider } = await import('./providers/my-provider.js');
    const provider = new MyProvider();
    await provider.initialize(config);
    return provider;
  });
}
```

### 4. Add Tests

Create comprehensive tests for your provider:

```typescript
// src/providers/my-provider.test.ts
import { describe, it, expect, beforeEach } from 'vitest';
import { MyProvider } from './my-provider.js';

describe('MyProvider', () => {
  let provider: MyProvider;
  let config: ProviderConfig;

  beforeEach(() => {
    provider = new MyProvider();
    config = {
      apiKey: 'test-key',
      model: 'test-model',
    };
  });

  describe('initialize', () => {
    it('should initialize with valid config', async () => {
      await expect(provider.initialize(config)).resolves.toBeUndefined();
    });

    it('should throw error with invalid config', async () => {
      await expect(provider.initialize({} as ProviderConfig)).rejects.toThrow();
    });
  });

  // ... more tests for all methods
});
```

### 5. Update Documentation

Add your provider to this integration guide with specific configuration examples.

## Provider Configuration

All providers accept a standard `ProviderConfig`:

```typescript
interface ProviderConfig {
  apiKey: string; // Required: API key or token
  baseUrl?: string; // Optional: Custom API base URL
  timeout?: number; // Optional: Request timeout in ms
  maxRetries?: number; // Optional: Maximum retry attempts
  retryDelay?: number; // Optional: Delay between retries
  model?: string; // Optional: Default model
  metadata?: Record<string, unknown>; // Optional: Provider-specific metadata
}
```

## Error Handling

Providers should throw `ProviderError` for known error conditions:

```typescript
import { PROVIDER_ERROR_CODES } from '../types.js';

// Example: Rate limit error
throw {
  name: 'ProviderError',
  code: PROVIDER_ERROR_CODES.RATE_LIMIT_EXCEEDED,
  message: 'Rate limit exceeded',
  retryable: true,
  retryAfter: 60,
  severity: 'medium',
  context: { requestId: 'req-123' },
} as ProviderError;
```

## Streaming Implementation

For streaming responses, implement an async iterable:

```typescript
async function* streamResponse(response: Response): AsyncIterable<MessageChunk> {
  const reader = response.body?.getReader();
  if (!reader) throw new Error('No response body');

  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split('\n');
    buffer = lines.pop() || '';

    for (const line of lines) {
      if (line.startsWith('data: ')) {
        const data = line.slice(6);
        if (data === '[DONE]') return;

        try {
          const parsed = JSON.parse(data);
          yield this.formatChunk(parsed);
        } catch (e) {
          // Skip invalid JSON
        }
      }
    }
  }
}
```

## Testing Your Provider

### Unit Tests

- Test all public methods
- Test error conditions
- Test configuration validation
- Mock external API calls

### Integration Tests

- Test with real API (using test credentials)
- Test streaming responses
- Test rate limiting behavior
- Test network failures

### Test Credentials

Use environment variables for test credentials:

```bash
export MY_PROVIDER_API_KEY_TEST="test-key-here"
export MY_PROVIDER_BASE_URL_TEST="https://api.test.com"
```

## Example Provider Implementations

### Anthropic Claude (Reference Implementation)

See `src/providers/anthropic-claude.ts` (to be implemented in Story 1-2) for a complete reference implementation.

### OpenAI GPT

See `src/providers/openai-gpt.ts` (to be implemented in Story 1-10) for another example.

## Best Practices

1. **Always validate configuration** in `initialize()`
2. **Use structured logging** for debugging
3. **Implement proper error handling** with retry logic
4. **Support both streaming and sync** responses
5. **Provide accurate capabilities** information
6. **Handle rate limits gracefully**
7. **Use TypeScript strict mode**
8. **Write comprehensive tests**
9. **Document provider-specific features**
10. **Follow naming conventions**

## Provider-Specific Features

Some providers have unique features. Document these in the `features` object of `ProviderCapabilities`:

```typescript
getCapabilities(): ProviderCapabilities {
  return {
    // ... standard capabilities
    features: {
      parallelToolUse: true,      // Can run tools in parallel
      promptCaching: true,         // Supports prompt caching
      thinkingMode: false,         // Supports thinking mode
      imageAnalysis: true,          // Can analyze images
      codeExecution: false,         // Can execute code
      webSearch: false,            // Can search the web
      // Add provider-specific features here
    }
  };
}
```

## Contributing

When adding a new provider:

1. Create a feature branch from `main`
2. Implement the provider following this guide
3. Add comprehensive tests
4. Update this integration guide
5. Submit a pull request with:
   - Provider implementation
   - Tests
   - Documentation updates
   - Example usage

## Support

For questions about provider integration:

1. Check existing provider implementations
2. Review this integration guide
3. Check the story documentation in `docs/stories/`
4. Create an issue on GitHub with specific questions

---

This guide will be updated as new providers are added and patterns evolve.
