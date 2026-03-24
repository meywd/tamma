/**
 * Integration Tests for ChromaDB Vector Store
 *
 * These tests require a running ChromaDB instance.
 * Skip these tests if ChromaDB is not available.
 *
 * To run: pnpm test:integration
 */

import { describe, it, expect, beforeAll, afterAll, beforeEach, type TaskContext } from 'vitest';
import { ChromaDBVectorStore } from '../../providers/chromadb.js';
import { createChromaDBStore } from '../../factory.js';
import type { VectorDocument, SearchQuery, HybridSearchQuery } from '../../interfaces.js';
import { mkdtempSync, rmSync } from 'fs';
import { tmpdir } from 'os';
import { join } from 'path';

// Skip these tests in CI or when ChromaDB is not available
const SKIP_INTEGRATION = process.env['SKIP_INTEGRATION_TESTS'] === 'true';
const CHROMADB_URL = process.env['CHROMADB_TEST_URL'] ?? '';

describe.skipIf(SKIP_INTEGRATION || !CHROMADB_URL)('ChromaDB Integration Tests', () => {
  let store: ChromaDBVectorStore;
  let tempDir: string;
  const testCollectionName = 'test-collection';
  const dimensions = 128; // Smaller dimensions for faster tests

  beforeAll(async (ctx: TaskContext) => {
    // Create temp directory for ChromaDB data
    tempDir = mkdtempSync(join(tmpdir(), 'chroma-test-'));

    store = createChromaDBStore(CHROMADB_URL, dimensions) as ChromaDBVectorStore;

    try {
      await store.initialize();
    } catch (error) {
      console.warn('ChromaDB not available, skipping integration tests');
      ctx.skip();
    }
  });

  afterAll(async () => {
    if (store) {
      await store.dispose();
    }

    // Clean up temp directory
    if (tempDir) {
      try {
        rmSync(tempDir, { recursive: true, force: true });
      } catch {
        // Ignore cleanup errors
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
      await store.createCollection('list-test-1');
      await store.createCollection('list-test-2');

      const collections = await store.listCollections();

      expect(collections).toContain('list-test-1');
      expect(collections).toContain('list-test-2');

      // Cleanup
      await store.deleteCollection('list-test-1');
      await store.deleteCollection('list-test-2');
    });

    it('should get collection stats', async () => {
      await store.createCollection(testCollectionName);

      const stats = await store.getCollectionStats(testCollectionName);

      expect(stats.name).toBe(testCollectionName);
      expect(stats.documentCount).toBe(0);
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
        scoreThreshold: 0.99, // Very high threshold
      };

      const results = await store.search(testCollectionName, query);

      // Only the most similar document should pass
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

    it('should include metadata when requested', async () => {
      const query: SearchQuery = {
        embedding: normalizeVector([...Array(dimensions).fill(1)]),
        topK: 1,
        includeMetadata: true,
      };

      const results = await store.search(testCollectionName, query);

      expect(results[0]?.metadata).toBeDefined();
      expect(results[0]?.metadata?.['language']).toBe('typescript');
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

  describe('Health Check', () => {
    it('should return healthy status', async () => {
      const health = await store.healthCheck();

      expect(health.healthy).toBe(true);
      expect(health.provider).toBe('chromadb');
      expect(health.latencyMs).toBeGreaterThanOrEqual(0);
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
