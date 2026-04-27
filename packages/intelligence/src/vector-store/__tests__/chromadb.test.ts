/**
 * Tests for ChromaDB Vector Store
 *
 * These are unit tests that mock the ChromaDB client.
 * Integration tests with a real ChromaDB instance are in chromadb.integration.test.ts
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { ChromaDBVectorStore } from '../providers/chromadb.js';
import {
  InvalidConfigError,
  NotInitializedError,
  CollectionExistsError,
  CollectionNotFoundError,
  VectorStoreError,
} from '../errors.js';
import type { VectorStoreConfig, VectorDocument, SearchQuery } from '../interfaces.js';

// Mock chromadb module
vi.mock('chromadb', () => {
  const mockCollection = {
    name: 'test-collection',
    metadata: { 'hnsw:space': 'cosine', dimensions: 1536 },
    count: vi.fn().mockResolvedValue(100),
    upsert: vi.fn().mockResolvedValue(undefined),
    delete: vi.fn().mockResolvedValue(undefined),
    get: vi.fn().mockResolvedValue({
      ids: ['doc-1', 'doc-2'],
      embeddings: [[0.1, 0.2, 0.3], [0.4, 0.5, 0.6]],
      metadatas: [{ language: 'typescript' }, { language: 'javascript' }],
      documents: ['content 1', 'content 2'],
    }),
    query: vi.fn().mockResolvedValue({
      ids: [['doc-1', 'doc-2']],
      distances: [[0.1, 0.3]],
      metadatas: [[{ language: 'typescript' }, { language: 'javascript' }]],
      documents: [['content 1', 'content 2']],
      embeddings: [[[0.1, 0.2, 0.3], [0.4, 0.5, 0.6]]],
    }),
  };

  const mockClient = {
    heartbeat: vi.fn().mockResolvedValue(1234567890),
    version: vi.fn().mockResolvedValue('1.0.0'),
    createCollection: vi.fn().mockResolvedValue(mockCollection),
    deleteCollection: vi.fn().mockResolvedValue(undefined),
    getCollection: vi.fn().mockResolvedValue(mockCollection),
    getOrCreateCollection: vi.fn().mockResolvedValue(mockCollection),
    listCollections: vi.fn().mockResolvedValue([
      { name: 'collection1' },
      { name: 'collection2' },
    ]),
  };

  return {
    // Vitest 4: constructor mocks must use `function` keyword, not arrow
    ChromaClient: vi.fn().mockImplementation(function () {
      return mockClient;
    }),
  };
});

describe('ChromaDBVectorStore', () => {
  let store: ChromaDBVectorStore;
  let config: VectorStoreConfig;

  beforeEach(() => {
    vi.clearAllMocks();

    config = {
      provider: 'chromadb',
      dimensions: 3, // Small for testing
      distanceMetric: 'cosine',
      chromadb: {
        persistPath: './test-data',
        anonymizedTelemetry: false,
      },
    };

    store = new ChromaDBVectorStore(config);
  });

  afterEach(async () => {
    if (store) {
      try {
        await store.dispose();
      } catch {
        // Ignore
      }
    }
  });

  describe('constructor', () => {
    it('should create store with valid config', () => {
      expect(store).toBeInstanceOf(ChromaDBVectorStore);
    });

    it('should throw for missing chromadb config', () => {
      const invalidConfig: VectorStoreConfig = {
        provider: 'chromadb',
        dimensions: 1536,
        distanceMetric: 'cosine',
      };

      expect(() => new ChromaDBVectorStore(invalidConfig)).toThrow(InvalidConfigError);
    });
  });

  describe('initialize', () => {
    it('should initialize and connect to ChromaDB', async () => {
      await store.initialize();
      const health = await store.healthCheck();

      expect(health.healthy).toBe(true);
      expect(health.provider).toBe('chromadb');
      expect(health.details?.heartbeat).toBeDefined();
    });

    it('should handle initialization errors', async () => {
      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      MockClient.mockImplementationOnce(() => ({
        heartbeat: vi.fn().mockRejectedValue(new Error('Connection refused')),
      }));

      const failingStore = new ChromaDBVectorStore(config);
      await expect(failingStore.initialize()).rejects.toThrow('Connection refused');
    });

    it('should warn when already initialized', async () => {
      await store.initialize();
      // Second initialization should not throw
      await expect(store.initialize()).resolves.not.toThrow();
    });
  });

  describe('createCollection', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should create a new collection', async () => {
      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      const mockClient = MockClient.mock.results[0]?.value;

      // Mock listCollections to return empty (collection doesn't exist)
      mockClient.listCollections.mockResolvedValueOnce([]);

      await store.createCollection('new-collection');

      expect(mockClient.createCollection).toHaveBeenCalledWith(
        expect.objectContaining({
          name: 'new-collection',
        }),
      );
    });

    it('should throw if collection already exists', async () => {
      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      const mockClient = MockClient.mock.results[0]?.value;

      // Mock listCollections to return the collection
      mockClient.listCollections.mockResolvedValueOnce([{ name: 'existing-collection' }]);

      await expect(store.createCollection('existing-collection')).rejects.toThrow(
        CollectionExistsError,
      );
    });

    it('should throw when not initialized', async () => {
      const uninitializedStore = new ChromaDBVectorStore(config);
      await expect(uninitializedStore.createCollection('test')).rejects.toThrow(
        NotInitializedError,
      );
    });
  });

  describe('deleteCollection', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should delete an existing collection', async () => {
      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      const mockClient = MockClient.mock.results[0]?.value;

      await store.deleteCollection('test-collection');

      expect(mockClient.deleteCollection).toHaveBeenCalledWith({ name: 'test-collection' });
    });

    it('should throw if collection does not exist', async () => {
      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      const mockClient = MockClient.mock.results[0]?.value;

      mockClient.deleteCollection.mockRejectedValueOnce(new Error('Collection does not exist'));

      await expect(store.deleteCollection('nonexistent')).rejects.toThrow(CollectionNotFoundError);
    });
  });

  describe('listCollections', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should return list of collection names', async () => {
      const collections = await store.listCollections();

      expect(collections).toContain('collection1');
      expect(collections).toContain('collection2');
      expect(collections.length).toBe(2);
    });
  });

  describe('upsert', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should upsert documents', async () => {
      const documents: VectorDocument[] = [
        {
          id: 'doc-1',
          embedding: [0.1, 0.2, 0.3],
          content: 'test content',
          metadata: { language: 'typescript' },
        },
      ];

      await store.upsert('test-collection', documents);

      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      const mockClient = MockClient.mock.results[0]?.value;
      const mockCollection = await mockClient.getCollection({ name: 'test-collection' });

      expect(mockCollection.upsert).toHaveBeenCalled();
    });

    it('should handle empty document array', async () => {
      await expect(store.upsert('test-collection', [])).resolves.not.toThrow();
    });

    it('should validate embedding dimensions', async () => {
      const documents: VectorDocument[] = [
        {
          id: 'doc-1',
          embedding: [0.1, 0.2], // Wrong dimensions (2 instead of 3)
          content: 'test',
          metadata: {},
        },
      ];

      await expect(store.upsert('test-collection', documents)).rejects.toThrow(
        'Invalid embedding dimensions',
      );
    });
  });

  describe('search', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should perform similarity search', async () => {
      const query: SearchQuery = {
        embedding: [0.1, 0.2, 0.3],
        topK: 10,
      };

      const results = await store.search('test-collection', query);

      expect(results.length).toBe(2);
      expect(results[0]?.id).toBe('doc-1');
      expect(results[0]?.score).toBeGreaterThan(0);
    });

    it('should filter by score threshold', async () => {
      const query: SearchQuery = {
        embedding: [0.1, 0.2, 0.3],
        topK: 10,
        scoreThreshold: 0.9, // High threshold
      };

      const results = await store.search('test-collection', query);

      // All results should be above threshold (or filtered out)
      for (const result of results) {
        expect(result.score).toBeGreaterThanOrEqual(query.scoreThreshold as number);
      }
    });

    it('should include metadata when requested', async () => {
      const query: SearchQuery = {
        embedding: [0.1, 0.2, 0.3],
        topK: 10,
        includeMetadata: true,
      };

      const results = await store.search('test-collection', query);

      expect(results[0]?.metadata).toBeDefined();
    });

    it('should validate query dimensions', async () => {
      const query: SearchQuery = {
        embedding: [0.1, 0.2], // Wrong dimensions
        topK: 10,
      };

      await expect(store.search('test-collection', query)).rejects.toThrow(
        'Invalid embedding dimensions',
      );
    });
  });

  describe('get', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should retrieve documents by ID', async () => {
      const docs = await store.get('test-collection', ['doc-1', 'doc-2']);

      expect(docs.length).toBe(2);
      expect(docs[0]?.id).toBe('doc-1');
      expect(docs[0]?.embedding).toBeDefined();
      expect(docs[0]?.content).toBeDefined();
    });

    it('should handle empty ID array', async () => {
      const docs = await store.get('test-collection', []);
      expect(docs).toEqual([]);
    });
  });

  describe('delete', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should delete documents by ID', async () => {
      await store.delete('test-collection', ['doc-1', 'doc-2']);

      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      const mockClient = MockClient.mock.results[0]?.value;
      const mockCollection = await mockClient.getCollection({ name: 'test-collection' });

      expect(mockCollection.delete).toHaveBeenCalled();
    });

    it('should handle empty ID array', async () => {
      await expect(store.delete('test-collection', [])).resolves.not.toThrow();
    });
  });

  describe('healthCheck', () => {
    it('should return healthy status when connected', async () => {
      await store.initialize();
      const health = await store.healthCheck();

      expect(health.healthy).toBe(true);
      expect(health.provider).toBe('chromadb');
      expect(health.latencyMs).toBeGreaterThanOrEqual(0);
    });

    it('should return unhealthy status on error', async () => {
      await store.initialize();

      const chromadb = await import('chromadb');
      const MockClient = chromadb.ChromaClient as unknown as ReturnType<typeof vi.fn>;
      const mockClient = MockClient.mock.results[0]?.value;

      mockClient.heartbeat.mockRejectedValueOnce(new Error('Connection lost'));

      const health = await store.healthCheck();

      expect(health.healthy).toBe(false);
      expect(health.error).toContain('Connection lost');
    });
  });

  describe('dispose', () => {
    it('should clean up resources', async () => {
      await store.initialize();
      await store.dispose();

      // Should throw when trying to use after dispose
      await expect(store.listCollections()).rejects.toThrow(NotInitializedError);
    });

    it('should be safe to call multiple times', async () => {
      await store.initialize();
      await store.dispose();
      await expect(store.dispose()).resolves.not.toThrow();
    });
  });
});
