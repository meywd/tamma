/**
 * Tests for stub vector-store providers.
 *
 * Pinecone, Qdrant, and Weaviate are stubs in this Tamma version. Their
 * constructors must throw {@link ProviderNotImplementedError} so misconfigured
 * deployments fail fast at boot rather than at first query.
 *
 * The error message must:
 *   - identify the implementation as a "stub"
 *   - name a concrete fallback (chromadb / pgvector)
 *
 * The factory test (factory.test.ts) covers that
 * `VectorStoreFactory.create()` bubbles the constructor error to its caller.
 */

import { describe, it, expect } from 'vitest';
import { PineconeVectorStore } from '../providers/pinecone.js';
import { QdrantVectorStore } from '../providers/qdrant.js';
import { WeaviateVectorStore } from '../providers/weaviate.js';
import { ProviderNotImplementedError } from '../errors.js';
import type { VectorStoreConfig } from '../interfaces.js';

const validPineconeConfig: VectorStoreConfig = {
  provider: 'pinecone',
  dimensions: 1536,
  distanceMetric: 'cosine',
  pinecone: {
    apiKey: 'test-api-key',
    environment: 'us-east-1',
    indexName: 'test-index',
  },
};

const validQdrantConfig: VectorStoreConfig = {
  provider: 'qdrant',
  dimensions: 1536,
  distanceMetric: 'cosine',
  qdrant: {
    url: 'http://localhost:6333',
  },
};

const validWeaviateConfig: VectorStoreConfig = {
  provider: 'weaviate',
  dimensions: 1536,
  distanceMetric: 'cosine',
  weaviate: {
    scheme: 'http',
    host: 'localhost:8080',
  },
};

/**
 * Pull the human-readable message out of a thrown stub error. The constructor
 * passes the helpful fallback message via `error.context.message` (rather than
 * concatenating it onto the default ProviderNotImplementedError message),
 * because the wrapper class fixes its own message format.
 */
function captureStubError(fn: () => unknown): ProviderNotImplementedError {
  try {
    fn();
  } catch (err) {
    expect(err).toBeInstanceOf(ProviderNotImplementedError);
    return err as ProviderNotImplementedError;
  }
  throw new Error('Expected stub constructor to throw, but it did not');
}

describe('PineconeVectorStore (stub)', () => {
  it('throws ProviderNotImplementedError at construction', () => {
    expect(() => new PineconeVectorStore(validPineconeConfig)).toThrow(
      ProviderNotImplementedError,
    );
  });

  it('error identifies the stub status and the chromadb/pgvector fallback', () => {
    const err = captureStubError(() => new PineconeVectorStore(validPineconeConfig));
    const fallbackMessage = String(err.context['message'] ?? '');

    expect(err.provider).toBe('pinecone');
    expect(fallbackMessage.toLowerCase()).toContain('stub');
    expect(fallbackMessage).toMatch(/chromadb/i);
    expect(fallbackMessage).toMatch(/pgvector/i);
  });
});

describe('QdrantVectorStore (stub)', () => {
  it('throws ProviderNotImplementedError at construction', () => {
    expect(() => new QdrantVectorStore(validQdrantConfig)).toThrow(
      ProviderNotImplementedError,
    );
  });

  it('error identifies the stub status and the chromadb/pgvector fallback', () => {
    const err = captureStubError(() => new QdrantVectorStore(validQdrantConfig));
    const fallbackMessage = String(err.context['message'] ?? '');

    expect(err.provider).toBe('qdrant');
    expect(fallbackMessage.toLowerCase()).toContain('stub');
    expect(fallbackMessage).toMatch(/chromadb/i);
    expect(fallbackMessage).toMatch(/pgvector/i);
  });
});

describe('WeaviateVectorStore (stub)', () => {
  it('throws ProviderNotImplementedError at construction', () => {
    expect(() => new WeaviateVectorStore(validWeaviateConfig)).toThrow(
      ProviderNotImplementedError,
    );
  });

  it('error identifies the stub status and the chromadb/pgvector fallback', () => {
    const err = captureStubError(() => new WeaviateVectorStore(validWeaviateConfig));
    const fallbackMessage = String(err.context['message'] ?? '');

    expect(err.provider).toBe('weaviate');
    expect(fallbackMessage.toLowerCase()).toContain('stub');
    expect(fallbackMessage).toMatch(/chromadb/i);
    expect(fallbackMessage).toMatch(/pgvector/i);
  });
});
