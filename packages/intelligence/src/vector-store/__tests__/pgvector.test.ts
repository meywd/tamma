/**
 * Tests for PostgreSQL pgvector Vector Store
 *
 * These are unit tests that mock the pg client.
 * Integration tests with a real PostgreSQL instance are in pgvector.integration.test.ts
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { PgVectorStore } from '../providers/pgvector.js';
import {
  InvalidConfigError,
  NotInitializedError,
  CollectionExistsError,
  CollectionNotFoundError,
} from '../errors.js';
import type { VectorStoreConfig, VectorDocument, SearchQuery } from '../interfaces.js';

// Create mock query function
const mockQuery = vi.fn();
const mockConnect = vi.fn();
const mockRelease = vi.fn();
const mockEnd = vi.fn();

// Mock pg module
vi.mock('pg', () => {
  const mockClient = {
    query: mockQuery,
    release: mockRelease,
  };

  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  const MockPool = vi.fn().mockImplementation(function () {
    return {
      connect: mockConnect.mockResolvedValue(mockClient),
      query: mockQuery,
      end: mockEnd,
      totalCount: 5,
      idleCount: 3,
      waitingCount: 0,
    };
  });

  return {
    default: {
      Pool: MockPool,
    },
  };
});

describe('PgVectorStore', () => {
  let store: PgVectorStore;
  let config: VectorStoreConfig;

  beforeEach(() => {
    vi.clearAllMocks();

    // Reset mock implementations
    mockQuery.mockReset();
    mockConnect.mockReset();

    // Setup default mock responses
    mockQuery.mockImplementation((sql: string) => {
      if (sql.includes('CREATE EXTENSION')) {
        return Promise.resolve({ rows: [] });
      }
      if (sql.includes('vector_dims')) {
        return Promise.resolve({ rows: [{ vector_dims: 3 }] });
      }
      if (sql.includes('current_database')) {
        return Promise.resolve({
          rows: [
            {
              database: 'test',
              schema: 'public',
              version: 'PostgreSQL 15.0',
              vector_version: '0.5.1',
            },
          ],
        });
      }
      if (sql.includes('information_schema.tables') && sql.includes('EXISTS')) {
        return Promise.resolve({ rows: [{ exists: false }] });
      }
      if (sql.includes('information_schema.tables') && sql.includes('LIKE')) {
        return Promise.resolve({
          rows: [{ table_name: 'vector_collection1' }, { table_name: 'vector_collection2' }],
        });
      }
      if (sql.includes('count(*)')) {
        return Promise.resolve({ rows: [{ count: '100' }] });
      }
      if (sql.includes('pg_total_relation_size')) {
        return Promise.resolve({ rows: [{ count: '100', size: '1048576' }] });
      }
      return Promise.resolve({ rows: [] });
    });

    const mockClient = {
      query: mockQuery,
      release: mockRelease,
    };
    mockConnect.mockResolvedValue(mockClient);

    config = {
      provider: 'pgvector',
      dimensions: 3, // Small for testing
      distanceMetric: 'cosine',
      pgvector: {
        connectionString: 'postgresql://localhost:5432/test',
        poolSize: 5,
        schema: 'public',
      },
    };

    store = new PgVectorStore(config);
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
      expect(store).toBeInstanceOf(PgVectorStore);
    });

    it('should throw for missing pgvector config', () => {
      const invalidConfig: VectorStoreConfig = {
        provider: 'pgvector',
        dimensions: 1536,
        distanceMetric: 'cosine',
      };

      expect(() => new PgVectorStore(invalidConfig)).toThrow(InvalidConfigError);
    });
  });

  describe('initialize', () => {
    it('should initialize and connect to PostgreSQL', async () => {
      await store.initialize();
      const health = await store.healthCheck();

      expect(health.healthy).toBe(true);
      expect(health.provider).toBe('pgvector');
    });

    it('should create pgvector extension if not exists', async () => {
      await store.initialize();

      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('CREATE EXTENSION'));
    });

    it('should verify pgvector is working', async () => {
      await store.initialize();

      expect(mockQuery).toHaveBeenCalledWith(
        expect.stringContaining('vector_dims'),
        expect.any(Array),
      );
    });
  });

  describe('createCollection', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should create a new collection table', async () => {
      // Setup: collection doesn't exist
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: false }] });
        }
        if (sql.includes('BEGIN') || sql.includes('COMMIT')) {
          return Promise.resolve({ rows: [] });
        }
        if (sql.includes('CREATE TABLE')) {
          return Promise.resolve({ rows: [] });
        }
        if (sql.includes('CREATE INDEX')) {
          return Promise.resolve({ rows: [] });
        }
        if (sql.includes('INSERT INTO') && sql.includes('metadata')) {
          return Promise.resolve({ rows: [] });
        }
        return Promise.resolve({ rows: [] });
      });

      await store.createCollection('new-collection');

      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('CREATE TABLE'));
      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('CREATE INDEX'));
    });

    it('should throw if collection already exists', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: true }] });
        }
        return Promise.resolve({ rows: [] });
      });

      await expect(store.createCollection('existing-collection')).rejects.toThrow(
        CollectionExistsError,
      );
    });

    it('should create HNSW index by default', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: false }] });
        }
        return Promise.resolve({ rows: [] });
      });

      await store.createCollection('test-collection');

      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('USING hnsw'));
    });
  });

  describe('deleteCollection', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should delete an existing collection', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: true }] });
        }
        if (sql.includes('DROP TABLE')) {
          return Promise.resolve({ rows: [] });
        }
        return Promise.resolve({ rows: [] });
      });

      await store.deleteCollection('test-collection');

      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('DROP TABLE'));
    });

    it('should throw if collection does not exist', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: false }] });
        }
        return Promise.resolve({ rows: [] });
      });

      await expect(store.deleteCollection('nonexistent')).rejects.toThrow(CollectionNotFoundError);
    });
  });

  describe('listCollections', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should return list of collection names', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('information_schema.tables') && sql.includes('LIKE')) {
          return Promise.resolve({
            rows: [{ table_name: 'vector_collection1' }, { table_name: 'vector_collection2' }],
          });
        }
        return Promise.resolve({ rows: [] });
      });

      const collections = await store.listCollections();

      expect(collections).toContain('collection1');
      expect(collections).toContain('collection2');
    });
  });

  describe('upsert', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should upsert documents', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: true }] });
        }
        if (sql.includes('BEGIN') || sql.includes('COMMIT')) {
          return Promise.resolve({ rows: [] });
        }
        if (sql.includes('INSERT INTO')) {
          return Promise.resolve({ rows: [] });
        }
        return Promise.resolve({ rows: [] });
      });

      const documents: VectorDocument[] = [
        {
          id: 'doc-1',
          embedding: [0.1, 0.2, 0.3],
          content: 'test content',
          metadata: { language: 'typescript' },
        },
      ];

      await store.upsert('test-collection', documents);

      expect(mockQuery).toHaveBeenCalledWith(
        expect.stringContaining('INSERT INTO'),
        expect.any(Array),
      );
    });

    it('should use ON CONFLICT for upsert', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: true }] });
        }
        return Promise.resolve({ rows: [] });
      });

      const documents: VectorDocument[] = [
        {
          id: 'doc-1',
          embedding: [0.1, 0.2, 0.3],
          content: 'test content',
          metadata: {},
        },
      ];

      await store.upsert('test-collection', documents);

      expect(mockQuery).toHaveBeenCalledWith(
        expect.stringContaining('ON CONFLICT'),
        expect.any(Array),
      );
    });
  });

  describe('search', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should perform similarity search', async () => {
      mockQuery.mockImplementation((sql: string, _params?: unknown[]) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: true }] });
        }
        if (sql.includes('ORDER BY embedding')) {
          return Promise.resolve({
            rows: [
              { id: 'doc-1', distance: 0.1, content: 'content 1', metadata: {} },
              { id: 'doc-2', distance: 0.3, content: 'content 2', metadata: {} },
            ],
          });
        }
        return Promise.resolve({ rows: [] });
      });

      const query: SearchQuery = {
        embedding: [0.1, 0.2, 0.3],
        topK: 10,
      };

      const results = await store.search('test-collection', query);

      expect(results.length).toBe(2);
      expect(results[0]?.id).toBe('doc-1');
    });

    it('should use correct distance operator for cosine', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('EXISTS')) {
          return Promise.resolve({ rows: [{ exists: true }] });
        }
        if (sql.includes('<=>')) {
          return Promise.resolve({ rows: [] });
        }
        return Promise.resolve({ rows: [] });
      });

      const query: SearchQuery = {
        embedding: [0.1, 0.2, 0.3],
        topK: 10,
      };

      await store.search('test-collection', query);

      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('<=>'), expect.any(Array));
    });
  });

  describe('get', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should retrieve documents by ID', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('SELECT id, embedding')) {
          return Promise.resolve({
            rows: [
              { id: 'doc-1', embedding: '[0.1,0.2,0.3]', content: 'content', metadata: {} },
            ],
          });
        }
        return Promise.resolve({ rows: [] });
      });

      const docs = await store.get('test-collection', ['doc-1']);

      expect(docs.length).toBe(1);
      expect(docs[0]?.id).toBe('doc-1');
    });
  });

  describe('count', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should return document count', async () => {
      mockQuery.mockImplementation((sql: string) => {
        if (sql.includes('count(*)')) {
          return Promise.resolve({ rows: [{ count: '42' }] });
        }
        return Promise.resolve({ rows: [] });
      });

      const count = await store.count('test-collection');

      expect(count).toBe(42);
    });
  });

  describe('healthCheck', () => {
    it('should return healthy status when connected', async () => {
      await store.initialize();
      const health = await store.healthCheck();

      expect(health.healthy).toBe(true);
      expect(health.provider).toBe('pgvector');
      expect(health.details?.vectorExtensionVersion).toBeDefined();
    });
  });

  describe('optimize', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should reindex and analyze table', async () => {
      await store.optimize('test-collection');

      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('REINDEX TABLE'));
      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('ANALYZE'));
    });
  });

  describe('vacuum', () => {
    beforeEach(async () => {
      await store.initialize();
    });

    it('should vacuum the table', async () => {
      await store.vacuum('test-collection');

      expect(mockQuery).toHaveBeenCalledWith(expect.stringContaining('VACUUM ANALYZE'));
    });
  });

  describe('dispose', () => {
    it('should close the connection pool', async () => {
      await store.initialize();
      await store.dispose();

      expect(mockEnd).toHaveBeenCalled();
    });
  });
});
