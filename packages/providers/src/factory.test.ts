/**
 * Test suite for Provider Factory
 * Following TDD approach: Red → Green → Refactor
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { ProviderFactory } from './factory.js';
import type { IAIProvider, ProviderConfig } from './types.js';

describe('ProviderFactory', () => {
  let factory: ProviderFactory;
  let mockConfig: ProviderConfig;

  beforeEach(() => {
    factory = new ProviderFactory();
    mockConfig = {
      apiKey: 'test-api-key',
      model: 'test-model',
    };
  });

  describe('getSupportedTypes', () => {
    it('should return supported provider types', () => {
      const types = factory.getSupportedTypes();

      expect(types).toContain('anthropic-claude');
      expect(types).toContain('openai-gpt');
      expect(types).toContain('github-copilot');
      expect(types).toContain('google-gemini');
      expect(types).toContain('local-llm');
    });

    it('should return array of strings', () => {
      const types = factory.getSupportedTypes();

      expect(Array.isArray(types)).toBe(true);
      expect(types.length).toBeGreaterThan(0);
      expect(types.every((type: string) => typeof type === 'string')).toBe(true);
    });
  });

  describe('createProvider', () => {
    it('should throw error for anthropic-claude provider (not implemented yet)', async () => {
      await expect(factory.createProvider('anthropic-claude', mockConfig)).rejects.toThrow(
        'Anthropic Claude provider not yet implemented'
      );
    });

    it('should throw error for openai-gpt provider (not implemented yet)', async () => {
      await expect(factory.createProvider('openai-gpt', mockConfig)).rejects.toThrow(
        'OpenAI GPT provider not yet implemented'
      );
    });

    it('should throw error for unsupported provider type', async () => {
      await expect(factory.createProvider('unsupported-provider', mockConfig)).rejects.toThrow(
        'Unsupported provider type: unsupported-provider'
      );
    });

    it('should throw error for empty provider type', async () => {
      await expect(factory.createProvider('', mockConfig)).rejects.toThrow(
        'Provider type cannot be empty'
      );
    });

    it('should throw error for null config', async () => {
      await expect(factory.createProvider('anthropic-claude', null as any)).rejects.toThrow(
        'Provider config cannot be null or undefined'
      );
    });

    it('should throw error for missing API key', async () => {
      const invalidConfig = { model: 'test-model' } as ProviderConfig;

      await expect(factory.createProvider('anthropic-claude', invalidConfig)).rejects.toThrow(
        'API key is required'
      );
    });
  });

  describe('registerProviderCreator', () => {
    it('should allow registering custom provider creators', async () => {
      const mockProvider: IAIProvider = {
        initialize: async () => {},
        sendMessage: async () => ({}) as AsyncIterable<any>,
        sendMessageSync: async () => ({
          id: 'test',
          content: 'test',
          model: 'test',
          usage: { inputTokens: 1, outputTokens: 1, totalTokens: 2 },
          finishReason: 'stop',
        }),
        getCapabilities: () => ({
          supportsStreaming: true,
          supportsImages: false,
          supportsTools: true,
          maxInputTokens: 1000,
          maxOutputTokens: 1000,
          supportedModels: [],
          features: {},
        }),
        getModels: async () => [],
        dispose: async () => {},
      };

      factory.registerProviderCreator('custom-provider', async (config: ProviderConfig) => {
        expect(config.apiKey).toBe('test-api-key');
        return mockProvider;
      });

      const provider = await factory.createProvider('custom-provider', mockConfig);
      expect(provider).toBe(mockProvider);
    });

    it('should throw error when registering duplicate creator', () => {
      expect(() => {
        factory.registerProviderCreator('anthropic-claude', async () => ({}) as IAIProvider);
      }).toThrow('Provider creator for "anthropic-claude" is already registered');
    });
  });
});
