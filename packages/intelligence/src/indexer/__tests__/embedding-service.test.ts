/**
 * Tests for Embedding Service
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { EmbeddingService, type EmbeddingServiceConfig } from '../embedding/embedding-service.js';
import { MockEmbeddingProvider } from '../embedding/mock-embedding-provider.js';
import type { CodeChunk } from '../types.js';

describe('EmbeddingService', () => {
  let service: EmbeddingService;

  const mockConfig: EmbeddingServiceConfig = {
    provider: 'mock',
    providerConfig: {
      model: 'test-model',
    },
    enableCache: true,
    cacheTtlMs: 60000,
    maxCacheEntries: 100,
    batchSize: 10,
    rateLimitPerMin: 1000,
  };

  beforeEach(async () => {
    service = new EmbeddingService(mockConfig);
    await service.initialize();
  });

  afterEach(async () => {
    await service.dispose();
  });

  describe('initialize', () => {
    it('should initialize successfully', async () => {
      const newService = new EmbeddingService(mockConfig);
      await expect(newService.initialize()).resolves.toBeUndefined();
      await newService.dispose();
    });

    it('should allow multiple initialize calls', async () => {
      await expect(service.initialize()).resolves.toBeUndefined();
    });
  });

  describe('getDimensions', () => {
    it('should return embedding dimensions', () => {
      const dimensions = service.getDimensions();
      expect(dimensions).toBe(1536); // Mock default
    });
  });

  describe('embed', () => {
    it('should generate embedding for text', async () => {
      const embedding = await service.embed('hello world');

      expect(embedding).toBeInstanceOf(Array);
      expect(embedding.length).toBe(1536);
    });

    it('should return consistent embeddings for same text', async () => {
      const embedding1 = await service.embed('test content');
      const embedding2 = await service.embed('test content');

      expect(embedding1).toEqual(embedding2);
    });

    it('should return different embeddings for different text', async () => {
      const embedding1 = await service.embed('content A');
      const embedding2 = await service.embed('content B');

      expect(embedding1).not.toEqual(embedding2);
    });

    it('should use cache for repeated requests', async () => {
      // First call
      await service.embed('cached text');

      // Get cache stats
      const stats1 = service.getCacheStats();
      expect(stats1.size).toBe(1);

      // Second call should use cache
      await service.embed('cached text');

      // Cache size should still be 1
      const stats2 = service.getCacheStats();
      expect(stats2.size).toBe(1);
    });
  });

  describe('embedBatch', () => {
    it('should generate embeddings for multiple texts', async () => {
      const texts = ['text 1', 'text 2', 'text 3'];
      const embeddings = await service.embedBatch(texts);

      expect(embeddings).toHaveLength(3);
      expect(embeddings[0].length).toBe(1536);
    });

    it('should handle empty array', async () => {
      const embeddings = await service.embedBatch([]);
      expect(embeddings).toEqual([]);
    });

    it('should use cache for duplicate texts in batch', async () => {
      // Pre-cache one text
      await service.embed('duplicate');

      const texts = ['duplicate', 'new text', 'another'];
      const embeddings = await service.embedBatch(texts);

      expect(embeddings).toHaveLength(3);
    });

    it('should split large batches', async () => {
      // Create more texts than batch size (10)
      const texts = Array(25).fill('').map((_, i) => `text ${i}`);
      const embeddings = await service.embedBatch(texts);

      expect(embeddings).toHaveLength(25);
    });
  });

  describe('embedChunks', () => {
    const createMockChunk = (content: string, index: number): CodeChunk => ({
      id: `chunk-${index}`,
      fileId: 'file-123',
      filePath: 'test.ts',
      language: 'typescript',
      chunkType: 'function',
      name: `function${index}`,
      content,
      startLine: 1,
      endLine: 3,
      imports: [],
      exports: [],
      tokenCount: 10,
      hash: 'abc123',
    });

    it('should embed code chunks', async () => {
      const chunks: CodeChunk[] = [
        createMockChunk('function a() {}', 0),
        createMockChunk('function b() {}', 1),
      ];

      const indexedChunks = await service.embedChunks(chunks);

      expect(indexedChunks).toHaveLength(2);
      expect(indexedChunks[0].embedding).toBeDefined();
      expect(indexedChunks[0].embedding.length).toBe(1536);
      expect(indexedChunks[0].indexedAt).toBeInstanceOf(Date);
    });

    it('should preserve chunk properties', async () => {
      const chunk = createMockChunk('function test() {}', 0);
      const [indexedChunk] = await service.embedChunks([chunk]);

      expect(indexedChunk.id).toBe(chunk.id);
      expect(indexedChunk.fileId).toBe(chunk.fileId);
      expect(indexedChunk.filePath).toBe(chunk.filePath);
      expect(indexedChunk.language).toBe(chunk.language);
      expect(indexedChunk.chunkType).toBe(chunk.chunkType);
      expect(indexedChunk.content).toBe(chunk.content);
    });

    it('should handle empty chunk array', async () => {
      const indexedChunks = await service.embedChunks([]);
      expect(indexedChunks).toEqual([]);
    });
  });

  describe('estimateCost', () => {
    it('should return 0 for mock provider', async () => {
      const chunks: CodeChunk[] = [
        {
          id: 'chunk-1',
          fileId: 'file-123',
          filePath: 'test.ts',
          language: 'typescript',
          chunkType: 'function',
          name: 'test',
          content: 'function test() {}',
          startLine: 1,
          endLine: 1,
          imports: [],
          exports: [],
          tokenCount: 100,
          hash: 'abc',
        },
      ];

      const cost = service.estimateCost(chunks);
      expect(cost).toBe(0); // Mock provider has 0 cost
    });
  });

  describe('cache management', () => {
    it('should track cache statistics', async () => {
      await service.embed('text 1');
      await service.embed('text 2');
      await service.embed('text 3');

      const stats = service.getCacheStats();
      expect(stats.size).toBe(3);
      expect(stats.maxSize).toBe(100);
    });

    it('should clear cache', async () => {
      await service.embed('text 1');
      await service.embed('text 2');

      service.clearCache();

      const stats = service.getCacheStats();
      expect(stats.size).toBe(0);
    });

    it('should respect cache disabled option', async () => {
      const noCacheService = new EmbeddingService({
        ...mockConfig,
        enableCache: false,
      });
      await noCacheService.initialize();

      await noCacheService.embed('text');
      const stats = noCacheService.getCacheStats();
      expect(stats.size).toBe(0);

      await noCacheService.dispose();
    });

    it('should evict old entries when cache is full', async () => {
      const smallCacheService = new EmbeddingService({
        ...mockConfig,
        maxCacheEntries: 3,
      });
      await smallCacheService.initialize();

      await smallCacheService.embed('text 1');
      await smallCacheService.embed('text 2');
      await smallCacheService.embed('text 3');
      await smallCacheService.embed('text 4'); // Should evict oldest

      const stats = smallCacheService.getCacheStats();
      expect(stats.size).toBe(3);

      await smallCacheService.dispose();
    });
  });

  describe('dispose', () => {
    it('should clean up resources', async () => {
      await service.embed('test');
      await service.dispose();

      // Should throw when trying to use after dispose
      await expect(service.embed('test')).rejects.toThrow();
    });
  });
});

describe('MockEmbeddingProvider', () => {
  let provider: MockEmbeddingProvider;

  beforeEach(async () => {
    provider = new MockEmbeddingProvider();
    await provider.initialize({ model: 'test' });
  });

  describe('configure', () => {
    it('should allow custom dimensions', async () => {
      provider.configure({ dimensions: 768 });
      expect(provider.dimensions).toBe(768);

      const embedding = await provider.embed('test');
      expect(embedding.length).toBe(768);
    });

    it('should allow custom delay', async () => {
      provider.configure({ embedDelay: 50 });

      const start = Date.now();
      await provider.embed('test');
      const duration = Date.now() - start;

      // Use 40ms threshold (not 50) to account for timer imprecision
      expect(duration).toBeGreaterThanOrEqual(40);
    });

    it('should allow failure simulation', async () => {
      provider.configure({ failRate: 1.0 }); // Always fail

      await expect(provider.embed('test')).rejects.toThrow('Mock embedding failure');
    });
  });

  describe('embed', () => {
    it('should return normalized vectors', async () => {
      const embedding = await provider.embed('test');

      // Calculate magnitude
      const magnitude = Math.sqrt(
        embedding.reduce((sum, v) => sum + v * v, 0),
      );

      // Should be approximately 1.0 (normalized)
      expect(magnitude).toBeCloseTo(1.0, 5);
    });

    it('should return deterministic embeddings', async () => {
      const embedding1 = await provider.embed('same content');
      const embedding2 = await provider.embed('same content');

      expect(embedding1).toEqual(embedding2);
    });
  });

  describe('estimateCost', () => {
    it('should always return 0', () => {
      expect(provider.estimateCost(1000)).toBe(0);
      expect(provider.estimateCost(1000000)).toBe(0);
    });
  });
});
