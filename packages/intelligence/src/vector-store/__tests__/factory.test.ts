/**
 * Tests for Vector Store Factory
 */

import { describe, it, expect } from 'vitest';
import {
  VectorStoreFactory,
  vectorStoreFactory,
  createVectorStore,
  createChromaDBStore,
  createPgVectorStore,
} from '../factory.js';
import { ChromaDBVectorStore } from '../providers/chromadb.js';
import { PgVectorStore } from '../providers/pgvector.js';
// Note: stub provider classes (Pinecone/Qdrant/Weaviate) are no longer
// imported here — their constructors throw ProviderNotImplementedError, so the
// factory tests below assert on the bubbled error rather than instance-of.
import {
  ProviderNotSupportedError,
  ProviderNotImplementedError,
  InvalidConfigError,
} from '../errors.js';
import type { VectorStoreConfig } from '../interfaces.js';

describe('VectorStoreFactory', () => {
  let factory: VectorStoreFactory;

  beforeEach(() => {
    factory = new VectorStoreFactory();
  });

  describe('create', () => {
    it('should create ChromaDB store', () => {
      const config: VectorStoreConfig = {
        provider: 'chromadb',
        dimensions: 1536,
        distanceMetric: 'cosine',
        chromadb: {
          persistPath: './test-data',
        },
      };

      const store = factory.create(config);
      expect(store).toBeInstanceOf(ChromaDBVectorStore);
    });

    it('should create pgvector store', () => {
      const config: VectorStoreConfig = {
        provider: 'pgvector',
        dimensions: 1536,
        distanceMetric: 'cosine',
        pgvector: {
          connectionString: 'postgresql://localhost:5432/test',
        },
      };

      const store = factory.create(config);
      expect(store).toBeInstanceOf(PgVectorStore);
    });

    it('should fail fast for Pinecone stub at construction (factory bubbles error)', () => {
      const config: VectorStoreConfig = {
        provider: 'pinecone',
        dimensions: 1536,
        distanceMetric: 'cosine',
        pinecone: {
          apiKey: 'test-api-key',
          environment: 'us-east-1',
          indexName: 'test-index',
        },
      };

      expect(() => factory.create(config)).toThrow(ProviderNotImplementedError);
    });

    it('should fail fast for Qdrant stub at construction (factory bubbles error)', () => {
      const config: VectorStoreConfig = {
        provider: 'qdrant',
        dimensions: 1536,
        distanceMetric: 'cosine',
        qdrant: {
          url: 'http://localhost:6333',
        },
      };

      expect(() => factory.create(config)).toThrow(ProviderNotImplementedError);
    });

    it('should fail fast for Weaviate stub at construction (factory bubbles error)', () => {
      const config: VectorStoreConfig = {
        provider: 'weaviate',
        dimensions: 1536,
        distanceMetric: 'cosine',
        weaviate: {
          scheme: 'http',
          host: 'localhost:8080',
        },
      };

      expect(() => factory.create(config)).toThrow(ProviderNotImplementedError);
    });

    it('should throw for unsupported provider', () => {
      const config = {
        provider: 'milvus' as 'chromadb',
        dimensions: 1536,
        distanceMetric: 'cosine' as const,
      };

      expect(() => factory.create(config)).toThrow(ProviderNotSupportedError);
    });

    it('should apply default dimensions', () => {
      const config: VectorStoreConfig = {
        provider: 'chromadb',
        dimensions: 0, // Will be overridden? No, we validate
        distanceMetric: 'cosine',
        chromadb: {
          persistPath: './test-data',
        },
      };

      // Should throw for invalid dimensions
      expect(() => factory.create(config)).toThrow(InvalidConfigError);
    });

    it('should throw for invalid dimensions', () => {
      const config: VectorStoreConfig = {
        provider: 'chromadb',
        dimensions: -1,
        distanceMetric: 'cosine',
        chromadb: {
          persistPath: './test-data',
        },
      };

      expect(() => factory.create(config)).toThrow(InvalidConfigError);
    });

    it('should throw for non-integer dimensions', () => {
      const config: VectorStoreConfig = {
        provider: 'chromadb',
        dimensions: 1536.5,
        distanceMetric: 'cosine',
        chromadb: {
          persistPath: './test-data',
        },
      };

      expect(() => factory.create(config)).toThrow(InvalidConfigError);
    });
  });

  describe('getSupportedProviders', () => {
    it('should return all supported providers', () => {
      const providers = factory.getSupportedProviders();

      expect(providers).toContain('chromadb');
      expect(providers).toContain('pgvector');
      expect(providers).toContain('pinecone');
      expect(providers).toContain('qdrant');
      expect(providers).toContain('weaviate');
      expect(providers.length).toBe(5);
    });
  });

  describe('getImplementedProviders', () => {
    it('should return fully implemented providers', () => {
      const providers = factory.getImplementedProviders();

      expect(providers).toContain('chromadb');
      expect(providers).toContain('pgvector');
      expect(providers).not.toContain('pinecone');
      expect(providers).not.toContain('qdrant');
      expect(providers).not.toContain('weaviate');
    });
  });

  describe('isProviderSupported', () => {
    it('should return true for supported providers', () => {
      expect(factory.isProviderSupported('chromadb')).toBe(true);
      expect(factory.isProviderSupported('pgvector')).toBe(true);
      expect(factory.isProviderSupported('pinecone')).toBe(true);
    });

    it('should return false for unsupported providers', () => {
      expect(factory.isProviderSupported('milvus')).toBe(false);
      expect(factory.isProviderSupported('elasticsearch')).toBe(false);
    });
  });

  describe('isProviderImplemented', () => {
    it('should return true for implemented providers', () => {
      expect(factory.isProviderImplemented('chromadb')).toBe(true);
      expect(factory.isProviderImplemented('pgvector')).toBe(true);
    });

    it('should return false for stub providers', () => {
      expect(factory.isProviderImplemented('pinecone')).toBe(false);
      expect(factory.isProviderImplemented('qdrant')).toBe(false);
      expect(factory.isProviderImplemented('weaviate')).toBe(false);
    });
  });
});

describe('vectorStoreFactory singleton', () => {
  it('should be an instance of VectorStoreFactory', () => {
    expect(vectorStoreFactory).toBeInstanceOf(VectorStoreFactory);
  });

  it('should work the same as a new instance', () => {
    const providers = vectorStoreFactory.getSupportedProviders();
    expect(providers.length).toBe(5);
  });
});

describe('createVectorStore', () => {
  it('should create store using singleton factory', () => {
    const store = createVectorStore({
      provider: 'chromadb',
      dimensions: 1536,
      distanceMetric: 'cosine',
      chromadb: {
        persistPath: './test-data',
      },
    });

    expect(store).toBeInstanceOf(ChromaDBVectorStore);
  });
});

describe('createChromaDBStore', () => {
  it('should create ChromaDB store with minimal config', () => {
    const store = createChromaDBStore('./test-data');
    expect(store).toBeInstanceOf(ChromaDBVectorStore);
  });

  it('should accept custom dimensions', () => {
    const store = createChromaDBStore('./test-data', 768);
    expect(store).toBeInstanceOf(ChromaDBVectorStore);
  });
});

describe('createPgVectorStore', () => {
  it('should create pgvector store with minimal config', () => {
    const store = createPgVectorStore('postgresql://localhost:5432/test');
    expect(store).toBeInstanceOf(PgVectorStore);
  });

  it('should accept custom dimensions', () => {
    const store = createPgVectorStore('postgresql://localhost:5432/test', 768);
    expect(store).toBeInstanceOf(PgVectorStore);
  });
});
