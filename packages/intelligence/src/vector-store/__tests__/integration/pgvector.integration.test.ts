/**
 * Integration Tests for PostgreSQL pgvector Vector Store
 *
 * These tests require a running PostgreSQL instance with pgvector extension.
 * Skip these tests if PostgreSQL is not available.
 *
 * To run: pnpm test:integration
 *
 * Required environment variables:
 * - PGVECTOR_TEST_CONNECTION_STRING: PostgreSQL connection string
 */

import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import { PgVectorStore } from '../../providers/pgvector.js';
import { createPgVectorStore } from '../../factory.js';
import type { VectorDocument, SearchQuery } from '../../interfaces.js';

// Skip when explicitly disabled or when pgvector connection string is not provided.
// The plain INTEGRATION_TEST_PG flag only guarantees a vanilla Postgres — not pgvector.
// To run pgvector tests, set PGVECTOR_TEST_CONNECTION_STRING to a Postgres with pgvector.
const SKIP_INTEGRATION = process.env['SKIP_INTEGRATION_TESTS'] === 'true';
const PGVECTOR_CONNECTION = process.env['PGVECTOR_TEST_CONNECTION_STRING'] ?? '';

function buildConnectionString(): string {
  if (PGVECTOR_CONNECTION) {
    return PGVECTOR_CONNECTION;
  }
  const host = process.env['PG_TEST_HOST'] ?? 'localhost';
  const port = process.env['PG_TEST_PORT'] ?? '5432';
  const user = process.env['PG_TEST_USER'] ?? 'postgres';
  const password = process.env['PG_TEST_PASSWORD'] ?? '';
  const db = process.env['PG_TEST_DB'] ?? 'tamma_test';
  const auth = password ? `${user}:${password}` : user;
  return `postgresql://${auth}@${host}:${port}/${db}`;
}

const CONNECTION_STRING = buildConnectionString();

// Skip the entire suite if no pgvector connection string is explicitly provided
// (INTEGRATION_TEST_PG alone only guarantees vanilla Postgres without pgvector extension)
describe.skipIf(SKIP_INTEGRATION || !PGVECTOR_CONNECTION)('pgvector Integration Tests', () => {
  let store: PgVectorStore;
  let initialized = false;
  const testCollectionName = 'test_collection';
  const dimensions = 128; // Smaller dimensions for faster tests

  beforeAll(async () => {
    store = createPgVectorStore(CONNECTION_STRING, dimensions) as PgVectorStore;

    try {
      await store.initialize();
      initialized = true;
    } catch (error) {
      console.warn('PostgreSQL/pgvector not available, skipping integration tests:', (error as Error).message);
    }
  });

  afterAll(async () => {
    if (store) {
      try {
        // Clean up test collections
        const collections = await store.listCollections();
        for (const collection of collections) {
          if (collection.startsWith('test_') || collection.startsWith('list_test_')) {
            try {
              await store.deleteCollection(collection);
            } catch {
              // Ignore
            }
          }
        }
      } catch {
        // Store may not be initialized if beforeAll skipped
      }

      try {
        await store.dispose();
      } catch {
        // Ignore dispose errors on uninitialized store
      }
    }
  });

  beforeEach(async () => {
    // Clean up test collection if it exists
    if (await store.collectionExists(testCollectionName)) {
      await store.deleteCollection(testCollectionName);
    }
  });

  describe('Collection Management', () => {
    it('should create a new collection', async () => {
      await store.createCollection(testCollectionName);

      const exists = await store.collectionExists(testCollectionName);
      expect(exists).toBe(true);
    });

    it('should list collections', async () => {
      await store.createCollection('list_test_1');
      await store.createCollection('list_test_2');

      const collections = await store.listCollections();

      expect(collections).toContain('list_test_1');
      expect(collections).toContain('list_test_2');
    });

    it('should get collection stats', async () => {
      await store.createCollection(testCollectionName);

      const stats = await store.getCollectionStats(testCollectionName);

      expect(stats.name).toBe(testCollectionName);
      expect(stats.documentCount).toBe(0);
      expect(stats.dimensions).toBe(dimensions);
    });

    it('should delete a collection', async () => {
      await store.createCollection(testCollectionName);
      await store.deleteCollection(testCollectionName);

      const exists = await store.collectionExists(testCollectionName);
      expect(exists).toBe(false);
    });
  });

  describe('Document Operations', () => {
    beforeEach(async () => {
      await store.createCollection(testCollectionName);
    });

    it('should upsert documents', async () => {
      const documents = generateTestDocuments(10, dimensions);

      await store.upsert(testCollectionName, documents);

      const count = await store.count(testCollectionName);
      expect(count).toBe(10);
    });

    it('should update existing documents', async () => {
      const doc: VectorDocument = {
        id: 'doc-update-test',
        embedding: generateRandomEmbedding(dimensions),
        content: 'original content',
        metadata: { version: 1 },
      };

      await store.upsert(testCollectionName, [doc]);

      // Update the document
      const updatedDoc: VectorDocument = {
        ...doc,
        content: 'updated content',
        metadata: { version: 2 },
      };

      await store.upsert(testCollectionName, [updatedDoc]);

      // Count should still be 1
      const count = await store.count(testCollectionName);
      expect(count).toBe(1);

      // Content should be updated
      const retrieved = await store.get(testCollectionName, ['doc-update-test']);
      expect(retrieved[0]?.content).toBe('updated content');
    });

    it('should get documents by ID', async () => {
      const documents = generateTestDocuments(5, dimensions);
      await store.upsert(testCollectionName, documents);

      const retrieved = await store.get(testCollectionName, ['doc-0', 'doc-2']);

      expect(retrieved.length).toBe(2);
      expect(retrieved.map((d) => d.id).sort()).toEqual(['doc-0', 'doc-2']);
    });

    it('should delete documents by ID', async () => {
      const documents = generateTestDocuments(5, dimensions);
      await store.upsert(testCollectionName, documents);

      await store.delete(testCollectionName, ['doc-0', 'doc-1']);

      const count = await store.count(testCollectionName);
      expect(count).toBe(3);

      const retrieved = await store.get(testCollectionName, ['doc-0']);
      expect(retrieved.length).toBe(0);
    });

    it('should handle batch upsert', async () => {
      const documents = generateTestDocuments(500, dimensions);

      await store.upsert(testCollectionName, documents);

      const count = await store.count(testCollectionName);
      expect(count).toBe(500);
    });
  });

  describe('Search Operations', () => {
    beforeEach(async () => {
      await store.createCollection(testCollectionName);

      // Insert test documents with known embeddings
      const documents: VectorDocument[] = [
        {
          id: 'doc-ts-1',
          embedding: normalizeVector([...Array(dimensions).fill(1)]),
          content: 'TypeScript function implementation',
          metadata: { language: 'typescript', type: 'function' },
        },
        {
          id: 'doc-ts-2',
          embedding: normalizeVector([...Array(dimensions).fill(0.9)]),
          content: 'TypeScript class definition',
          metadata: { language: 'typescript', type: 'class' },
        },
        {
          id: 'doc-js-1',
          embedding: normalizeVector([...Array(dimensions).fill(-1)]),
          content: 'JavaScript utility function',
          metadata: { language: 'javascript', type: 'function' },
        },
      ];

      await store.upsert(testCollectionName, documents);
    });

    it('should perform similarity search', async () => {
      const query: SearchQuery = {
        embedding: normalizeVector([...Array(dimensions).fill(1)]),
        topK: 2,
      };

      const results = await store.search(testCollectionName, query);

      expect(results.length).toBe(2);
      expect(results[0]?.id).toBe('doc-ts-1'); // Most similar
      expect(results[0]?.score).toBeGreaterThan(results[1]?.score ?? 0);
    });

    it('should filter by score threshold', async () => {
      const query: SearchQuery = {
        embedding: normalizeVector([...Array(dimensions).fill(1)]),
        topK: 10,
        scoreThreshold: 0.99,
      };

      const results = await store.search(testCollectionName, query);

      for (const result of results) {
        expect(result.score).toBeGreaterThanOrEqual(0.99);
      }
    });

    it('should filter by metadata', async () => {
      const query: SearchQuery = {
        embedding: normalizeVector([...Array(dimensions).fill(1)]),
        topK: 10,
        filter: { where: { language: 'typescript' } },
      };

      const results = await store.search(testCollectionName, query);

      expect(results.length).toBe(2);
      for (const result of results) {
        expect(result.metadata?.['language']).toBe('typescript');
      }
    });

    it('should include content when requested', async () => {
      const query: SearchQuery = {
        embedding: normalizeVector([...Array(dimensions).fill(1)]),
        topK: 1,
        includeContent: true,
      };

      const results = await store.search(testCollectionName, query);

      expect(results[0]?.content).toBeDefined();
      expect(results[0]?.content).toContain('TypeScript');
    });
  });

  describe('Maintenance Operations', () => {
    beforeEach(async () => {
      await store.createCollection(testCollectionName);
      await store.upsert(testCollectionName, generateTestDocuments(100, dimensions));
    });

    it('should optimize collection', async () => {
      await expect(store.optimize(testCollectionName)).resolves.not.toThrow();
    });

    it('should vacuum collection', async () => {
      // Delete some documents first
      await store.delete(testCollectionName, ['doc-0', 'doc-1', 'doc-2']);

      await expect(store.vacuum(testCollectionName)).resolves.not.toThrow();
    });
  });

  describe('Health Check', () => {
    it('should return healthy status', async () => {
      const health = await store.healthCheck();

      expect(health.healthy).toBe(true);
      expect(health.provider).toBe('pgvector');
      expect(health.latencyMs).toBeGreaterThanOrEqual(0);
      expect(health.details?.['vectorExtensionVersion']).toBeDefined();
    });
  });
});

// Helper functions

function generateRandomEmbedding(dimensions: number): number[] {
  const embedding = Array.from({ length: dimensions }, () => Math.random() - 0.5);
  return normalizeVector(embedding);
}

function normalizeVector(vector: number[]): number[] {
  const magnitude = Math.sqrt(vector.reduce((sum, x) => sum + x * x, 0));
  if (magnitude === 0) return vector;
  return vector.map((x) => x / magnitude);
}

function generateTestDocuments(count: number, dimensions: number): VectorDocument[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `doc-${i}`,
    embedding: generateRandomEmbedding(dimensions),
    content: `Test content for document ${i}`,
    metadata: {
      filePath: `src/file-${i % 10}.ts`,
      language: i % 2 === 0 ? 'typescript' : 'javascript',
      chunkType: i % 3 === 0 ? 'function' : 'class',
      startLine: i * 10,
      endLine: i * 10 + 20,
    },
  }));
}
