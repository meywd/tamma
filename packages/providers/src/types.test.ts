/**
 * Test suite for AI Provider interfaces and types
 * Following TDD approach: Red → Green → Refactor
 */

import { describe, it, expect } from 'vitest';
import type {
  IAIProvider,
  MessageRequest,
  MessageResponse,
  MessageChunk,
  ProviderCapabilities,
  ProviderError,
  ProviderConfig,
  StreamOptions,
  ModelInfo,
  TokenUsage,
} from './types.js';

describe('AI Provider Interfaces', () => {
  describe('IAIProvider', () => {
    it('should define required methods', () => {
      // This test ensures the interface has all required methods
      // We'll create a mock implementation to test the interface contract

      const mockProvider: IAIProvider = {
        initialize: async () => {},
        sendMessage: async () => ({}) as AsyncIterable<MessageChunk>,
        sendMessageSync: async () => ({}) as MessageResponse,
        getCapabilities: () => ({}) as ProviderCapabilities,
        getModels: async () => [],
        dispose: async () => {},
      };

      expect(mockProvider).toBeDefined();
      expect(typeof mockProvider.initialize).toBe('function');
      expect(typeof mockProvider.sendMessage).toBe('function');
      expect(typeof mockProvider.sendMessageSync).toBe('function');
      expect(typeof mockProvider.getCapabilities).toBe('function');
      expect(typeof mockProvider.getModels).toBe('function');
      expect(typeof mockProvider.dispose).toBe('function');
    });

    it('should require async initialization', async () => {
      const mockProvider: IAIProvider = {
        initialize: async () => {
          // Initialization logic would go here
        },
        sendMessage: async () => ({}) as AsyncIterable<MessageChunk>,
        sendMessageSync: async () => ({}) as MessageResponse,
        getCapabilities: () => ({}) as ProviderCapabilities,
        getModels: async () => [],
        dispose: async () => {},
      };

      await expect(mockProvider.initialize({} as ProviderConfig)).resolves.toBeUndefined();
    });
  });

  describe('MessageRequest', () => {
    it('should accept required fields', () => {
      const request: MessageRequest = {
        messages: [
          {
            role: 'user',
            content: 'test message',
          },
        ],
      };

      expect(request.messages).toHaveLength(1);
      expect(request.messages[0].role).toBe('user');
      expect(request.messages[0].content).toBe('test message');
    });

    it('should accept optional fields', () => {
      const request: MessageRequest = {
        messages: [
          {
            role: 'user',
            content: 'test message',
          },
        ],
        model: 'claude-3-sonnet',
        maxTokens: 1000,
        temperature: 0.7,
        stream: true,
        metadata: {
          traceId: 'trace-123',
          userId: 'user-456',
        },
      };

      expect(request.model).toBe('claude-3-sonnet');
      expect(request.maxTokens).toBe(1000);
      expect(request.temperature).toBe(0.7);
      expect(request.stream).toBe(true);
      expect(request.metadata?.traceId).toBe('trace-123');
    });
  });

  describe('MessageResponse', () => {
    it('should contain response data', () => {
      const response: MessageResponse = {
        id: 'resp-123',
        content: 'Generated response',
        model: 'claude-3-sonnet',
        usage: {
          inputTokens: 10,
          outputTokens: 20,
          totalTokens: 30,
        },
        finishReason: 'stop',
        metadata: {
          traceId: 'trace-123',
        },
      };

      expect(response.id).toBe('resp-123');
      expect(response.content).toBe('Generated response');
      expect(response.model).toBe('claude-3-sonnet');
      expect(response.usage.inputTokens).toBe(10);
      expect(response.usage.outputTokens).toBe(20);
      expect(response.usage.totalTokens).toBe(30);
      expect(response.finishReason).toBe('stop');
    });
  });

  describe('MessageChunk', () => {
    it('should support streaming responses', () => {
      const chunk: MessageChunk = {
        id: 'chunk-123',
        content: 'Partial response',
        delta: ' response',
        model: 'claude-3-sonnet',
        usage: {
          inputTokens: 10,
          outputTokens: 5,
          totalTokens: 15,
        },
        finishReason: null,
        metadata: {
          traceId: 'trace-123',
        },
      };

      expect(chunk.id).toBe('chunk-123');
      expect(chunk.content).toBe('Partial response');
      expect(chunk.delta).toBe(' response');
      expect(chunk.finishReason).toBeNull();
    });
  });

  describe('ProviderCapabilities', () => {
    it('should define provider features', () => {
      const capabilities: ProviderCapabilities = {
        supportsStreaming: true,
        supportsImages: false,
        supportsTools: true,
        maxInputTokens: 100000,
        maxOutputTokens: 4096,
        supportedModels: [
          {
            id: 'claude-3-sonnet',
            name: 'Claude 3 Sonnet',
            maxTokens: 100000,
            supportsStreaming: true,
            supportsImages: false,
            supportsTools: true,
          },
        ],
        features: {
          parallelToolUse: true,
          promptCaching: true,
          thinkingMode: false,
        },
      };

      expect(capabilities.supportsStreaming).toBe(true);
      expect(capabilities.supportsImages).toBe(false);
      expect(capabilities.supportsTools).toBe(true);
      expect(capabilities.maxInputTokens).toBe(100000);
      expect(capabilities.maxOutputTokens).toBe(4096);
      expect(capabilities.supportedModels).toHaveLength(1);
      expect(capabilities.features.parallelToolUse).toBe(true);
    });
  });

  describe('ProviderError', () => {
    it('should handle error types', () => {
      const error: ProviderError = {
        name: 'ProviderError',
        code: 'RATE_LIMIT_EXCEEDED',
        message: 'Rate limit exceeded',
        retryable: true,
        retryAfter: 60,
        severity: 'medium',
        context: {
          requestId: 'req-123',
          provider: 'anthropic',
        },
      };

      expect(error.code).toBe('RATE_LIMIT_EXCEEDED');
      expect(error.message).toBe('Rate limit exceeded');
      expect(error.retryable).toBe(true);
      expect(error.retryAfter).toBe(60);
      expect(error.context?.provider).toBe('anthropic');
      expect(error.severity).toBe('medium');
    });
  });

  describe('ProviderConfig', () => {
    it('should accept configuration parameters', () => {
      const config: ProviderConfig = {
        apiKey: 'sk-test-key',
        baseUrl: 'https://api.anthropic.com',
        timeout: 30000,
        maxRetries: 3,
        retryDelay: 1000,
        model: 'claude-3-sonnet',
        metadata: {
          environment: 'test',
        },
      };

      expect(config.apiKey).toBe('sk-test-key');
      expect(config.baseUrl).toBe('https://api.anthropic.com');
      expect(config.timeout).toBe(30000);
      expect(config.maxRetries).toBe(3);
      expect(config.retryDelay).toBe(1000);
      expect(config.model).toBe('claude-3-sonnet');
    });
  });

  describe('StreamOptions', () => {
    it('should configure streaming behavior', () => {
      const options: StreamOptions = {
        onChunk: async (chunk: MessageChunk) => {
          expect(chunk).toBeDefined();
        },
        onError: async (error: ProviderError) => {
          expect(error).toBeDefined();
        },
        onComplete: async () => {
          // Completion handler
        },
        timeout: 60000,
      };

      expect(typeof options.onChunk).toBe('function');
      expect(typeof options.onError).toBe('function');
      expect(typeof options.onComplete).toBe('function');
      expect(options.timeout).toBe(60000);
    });
  });

  describe('ModelInfo', () => {
    it('should contain model information', () => {
      const model: ModelInfo = {
        id: 'claude-3-sonnet',
        name: 'Claude 3 Sonnet',
        description: 'Balanced model for most tasks',
        maxTokens: 100000,
        supportsStreaming: true,
        supportsImages: false,
        supportsTools: true,
        pricing: {
          inputTokens: 3.0,
          outputTokens: 15.0,
          currency: 'USD',
          unit: 'perMillion',
        },
      };

      expect(model.id).toBe('claude-3-sonnet');
      expect(model.name).toBe('Claude 3 Sonnet');
      expect(model.maxTokens).toBe(100000);
      expect(model.supportsStreaming).toBe(true);
      expect(model.pricing?.inputTokens).toBe(3.0);
      expect(model.pricing?.currency).toBe('USD');
    });
  });

  describe('TokenUsage', () => {
    it('should track token usage', () => {
      const usage: TokenUsage = {
        inputTokens: 100,
        outputTokens: 200,
        totalTokens: 300,
        cacheReadTokens: 50,
        cacheWriteTokens: 25,
      };

      expect(usage.inputTokens).toBe(100);
      expect(usage.outputTokens).toBe(200);
      expect(usage.totalTokens).toBe(300);
      expect(usage.cacheReadTokens).toBe(50);
      expect(usage.cacheWriteTokens).toBe(25);
    });
  });
});
