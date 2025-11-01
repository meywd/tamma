/**
 * Test suite for Provider Registry
 * Following TDD approach: Red → Green → Refactor
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { ProviderRegistry } from './registry.js';
import type { IAIProvider } from './types.js';

describe('ProviderRegistry', () => {
  let registry: ProviderRegistry;
  let mockProvider: IAIProvider;

  beforeEach(() => {
    registry = new ProviderRegistry();
    mockProvider = {
      initialize: async () => {},
      sendMessage: async () => ({}) as AsyncIterable<any>,
      sendMessageSync: async () => ({
        id: 'test-response',
        content: 'Test response',
        model: 'test-model',
        usage: { inputTokens: 10, outputTokens: 20, totalTokens: 30 },
        finishReason: 'stop',
      }),
      getCapabilities: () => ({
        supportsStreaming: true,
        supportsImages: false,
        supportsTools: true,
        maxInputTokens: 100000,
        maxOutputTokens: 4096,
        supportedModels: [],
        features: {},
      }),
      getModels: async () => [],
      dispose: async () => {},
    };
  });

  describe('register', () => {
    it('should register a provider', () => {
      registry.register('test-provider', mockProvider);

      expect(registry.hasProvider('test-provider')).toBe(true);
      expect(registry.getProvider('test-provider')).toBe(mockProvider);
    });

    it('should throw error when registering duplicate provider', () => {
      registry.register('test-provider', mockProvider);

      expect(() => {
        registry.register('test-provider', mockProvider);
      }).toThrow('Provider "test-provider" is already registered');
    });

    it('should throw error when provider name is empty', () => {
      expect(() => {
        registry.register('', mockProvider);
      }).toThrow('Provider name cannot be empty');
    });

    it('should throw error when provider is null', () => {
      expect(() => {
        registry.register('test-provider', null as any);
      }).toThrow('Provider cannot be null or undefined');
    });
  });

  describe('getProvider', () => {
    it('should return registered provider', () => {
      registry.register('test-provider', mockProvider);

      const provider = registry.getProvider('test-provider');
      expect(provider).toBe(mockProvider);
    });

    it('should return undefined for non-existent provider', () => {
      const provider = registry.getProvider('non-existent');
      expect(provider).toBeUndefined();
    });
  });

  describe('getProviders', () => {
    it('should return all registered providers', () => {
      registry.register('provider1', mockProvider);
      registry.register('provider2', mockProvider);

      const providers = registry.getProviders();
      expect(providers.size).toBe(2);
      expect(providers.has('provider1')).toBe(true);
      expect(providers.has('provider2')).toBe(true);
    });

    it('should return empty map when no providers registered', () => {
      const providers = registry.getProviders();
      expect(providers.size).toBe(0);
    });
  });

  describe('unregister', () => {
    it('should unregister a provider', () => {
      registry.register('test-provider', mockProvider);
      expect(registry.hasProvider('test-provider')).toBe(true);

      registry.unregister('test-provider');
      expect(registry.hasProvider('test-provider')).toBe(false);
    });

    it('should not throw when unregistering non-existent provider', () => {
      expect(() => {
        registry.unregister('non-existent');
      }).not.toThrow();
    });
  });

  describe('hasProvider', () => {
    it('should return true for registered provider', () => {
      registry.register('test-provider', mockProvider);
      expect(registry.hasProvider('test-provider')).toBe(true);
    });

    it('should return false for non-existent provider', () => {
      expect(registry.hasProvider('non-existent')).toBe(false);
    });
  });

  describe('clear', () => {
    it('should clear all registered providers', () => {
      registry.register('provider1', mockProvider);
      registry.register('provider2', mockProvider);
      expect(registry.getProviders().size).toBe(2);

      registry.clear();
      expect(registry.getProviders().size).toBe(0);
    });
  });
});
