/**
 * Tests for the environment-driven composition root (`env-composition.ts`).
 *
 * Covers three layers without needing a live ChromaDB/pgvector:
 *   1. Pure config resolution + the REAL `createVectorStore` factory picking the
 *      right provider class from env (exercises the env → provider path).
 *   2. Embedder construction from env (mock/openai/graceful-absent).
 *   3. End-to-end: a configured (in-memory) store bridged through the real
 *      `wrapVectorStore` adapter makes `/kb/vector-db/*` return REAL data, while
 *      an unconfigured deployment degrades to `not_configured` without crashing.
 */

import { describe, expect, it } from 'vitest';

import { buildServer } from '../server.js';
import {
  resolveVectorStoreConfig,
  hasVectorStoreEnv,
  createEmbedderFromEnv,
  buildIntelligenceBundleFromEnv,
  createVectorStoreFromEnv,
  createRagPipelineFromEnv,
  wrapVectorStore,
} from '../env-composition.js';
import type { IntelligenceServicesBundle, IRagPipeline } from '../types.js';

// ---------------------------------------------------------------------------
// 1. Config resolution (pure) + real provider selection
// ---------------------------------------------------------------------------

describe('resolveVectorStoreConfig', () => {
  it('selects chromadb from CHROMADB_URL', () => {
    const cfg = resolveVectorStoreConfig({ CHROMADB_URL: 'http://chromadb:8000' }, 1536);
    expect(cfg?.provider).toBe('chromadb');
    expect(cfg?.chromadb?.persistPath).toBe('http://chromadb:8000');
    expect(cfg?.dimensions).toBe(1536);
  });

  it('selects chromadb from CHROMADB_TEST_URL (CI fallback)', () => {
    const cfg = resolveVectorStoreConfig({ CHROMADB_TEST_URL: 'http://localhost:8000' });
    expect(cfg?.provider).toBe('chromadb');
  });

  it('selects chromadb from host + port', () => {
    const cfg = resolveVectorStoreConfig({ CHROMADB_HOST: 'chromadb', CHROMADB_PORT: '9000' });
    expect(cfg?.provider).toBe('chromadb');
    expect(cfg?.chromadb?.host).toBe('chromadb');
    expect(cfg?.chromadb?.port).toBe(9000);
  });

  it('selects pgvector from a Postgres connection string', () => {
    const cfg = resolveVectorStoreConfig({ PGVECTOR_URL: 'postgres://u:p@h:5432/db' });
    expect(cfg?.provider).toBe('pgvector');
    expect(cfg?.pgvector?.connectionString).toContain('postgres://');
  });

  it('honours explicit KB_VECTOR_STORE=pgvector even when chroma env present', () => {
    const cfg = resolveVectorStoreConfig({
      KB_VECTOR_STORE: 'pgvector',
      VECTOR_DB_URL: 'postgres://x',
      CHROMADB_URL: 'http://c:8000',
    });
    expect(cfg?.provider).toBe('pgvector');
  });

  it('returns undefined / hasVectorStoreEnv=false when nothing is configured', () => {
    expect(resolveVectorStoreConfig({})).toBeUndefined();
    expect(hasVectorStoreEnv({})).toBe(false);
    expect(hasVectorStoreEnv({ CHROMADB_URL: 'http://c:8000' })).toBe(true);
  });
});

describe('createVectorStore factory — real provider selection from env', () => {
  it('builds a ChromaDBVectorStore from chromadb config (constructor only, no connection)', async () => {
    const { createVectorStore } = await import('@tamma/intelligence/vector-store');
    const cfg = resolveVectorStoreConfig({ CHROMADB_URL: 'http://localhost:8000' }, 1536);
    expect(cfg).toBeDefined();
    const store = createVectorStore(cfg!);
    expect(store.constructor.name).toBe('ChromaDBVectorStore');
  });

  it('builds a PgVectorStore from pgvector config (constructor only, no connection)', async () => {
    const { createVectorStore } = await import('@tamma/intelligence/vector-store');
    const cfg = resolveVectorStoreConfig({ PGVECTOR_URL: 'postgres://u:p@h:5432/db' }, 1536);
    expect(cfg).toBeDefined();
    const store = createVectorStore(cfg!);
    expect(store.constructor.name).toBe('PgVectorStore');
  });
});

// ---------------------------------------------------------------------------
// 2. Embedder from env
// ---------------------------------------------------------------------------

describe('createEmbedderFromEnv', () => {
  it('returns undefined for the openai default when no key is present (graceful)', async () => {
    expect(await createEmbedderFromEnv({})).toBeUndefined();
    expect(await createEmbedderFromEnv({ EMBEDDING_PROVIDER: 'openai' })).toBeUndefined();
  });

  it('builds a working embedder for EMBEDDING_PROVIDER=mock (no key, no network)', async () => {
    const svc = await createEmbedderFromEnv({ EMBEDDING_PROVIDER: 'mock' });
    expect(svc).toBeDefined();
    const vec = await svc!.embed('hello world');
    expect(Array.isArray(vec)).toBe(true);
    expect(vec.length).toBeGreaterThan(0);
  });

  it('builds an OpenAI embedder when a key is present (init makes no network call)', async () => {
    const svc = await createEmbedderFromEnv({
      OPENAI_API_KEY: 'sk-test-not-real',
      EMBEDDING_MODEL: 'text-embedding-3-small',
    });
    expect(svc).toBeDefined();
    expect(svc!.getDimensions()).toBe(1536);
  });
});

// ---------------------------------------------------------------------------
// 3a. Graceful degrade — no store env
// ---------------------------------------------------------------------------

describe('buildIntelligenceBundleFromEnv — graceful degrade when unconfigured', () => {
  it('returns an empty bundle and undefined factories with no vector-store env', async () => {
    expect(await buildIntelligenceBundleFromEnv({})).toEqual({});
    expect(await createVectorStoreFromEnv({})).toBeUndefined();
    expect(await createRagPipelineFromEnv({})).toBeUndefined();
  });

  it('server boots with the empty bundle and reports not_configured (no crash)', async () => {
    const bundle = await buildIntelligenceBundleFromEnv({});
    const app = await buildServer({ services: bundle });
    await app.ready();

    const status = await app.inject({ method: 'GET', url: '/kb/vector-db/status' });
    expect(status.json().status).toBe('not_configured');

    const stats = await app.inject({ method: 'GET', url: '/kb/vector-db/stats' });
    expect(stats.json().totalVectors).toBe(0);

    const health = await app.inject({ method: 'GET', url: '/health' });
    expect(health.json()).toEqual({ status: 'ok' });

    await app.close();
  });
});

// ---------------------------------------------------------------------------
// 3b. Configured store — REAL data through the real wrapVectorStore bridge
// ---------------------------------------------------------------------------

interface StoredDoc {
  id: string;
  embedding: number[];
  content: string;
  metadata: Record<string, unknown>;
}

/**
 * Minimal in-memory IVectorStore covering exactly the methods `wrapVectorStore`
 * touches. Stands in for a real ChromaDB/pgvector when none is reachable, while
 * still exercising the real adapter bridge + sidecar services end-to-end.
 */
function makeInMemoryStore() {
  const collections = new Map<string, Map<string, StoredDoc>>();
  const ensure = (name: string) => {
    let c = collections.get(name);
    if (!c) {
      c = new Map();
      collections.set(name, c);
    }
    return c;
  };
  return {
    listCollections: async () => [...collections.keys()],
    createCollection: async (name: string) => {
      ensure(name);
    },
    deleteCollection: async (name: string) => {
      collections.delete(name);
    },
    upsert: async (collection: string, docs: StoredDoc[]) => {
      const c = ensure(collection);
      for (const d of docs) c.set(d.id, d);
    },
    delete: async (collection: string, ids: string[]) => {
      const c = collections.get(collection);
      if (c) for (const id of ids) c.delete(id);
    },
    search: async (
      collection: string,
      query: { embedding: number[]; topK: number },
    ) => {
      const c = collections.get(collection);
      if (!c) return [];
      return [...c.values()]
        .slice(0, query.topK)
        .map((d) => ({ id: d.id, score: 1, content: d.content, metadata: d.metadata }));
    },
    hybridSearch: async (
      collection: string,
      query: { text: string; embedding: number[]; topK: number },
    ) => {
      const c = collections.get(collection);
      if (!c) return [];
      return [...c.values()]
        .slice(0, query.topK)
        .map((d) => ({ id: d.id, score: 1, content: d.content, metadata: d.metadata }));
    },
    getCollectionStats: async (name: string) => {
      const c = collections.get(name);
      const count = c?.size ?? 0;
      const first = c ? [...c.values()][0] : undefined;
      return {
        name,
        documentCount: count,
        dimensions: first ? first.embedding.length : 1536,
        indexSize: count * 16,
      };
    },
  };
}

const fakeRag: IRagPipeline = {
  retrieve: async () => ({
    queryId: 'rag-1',
    retrievedChunks: [
      { content: 'chunk', source: 'vectorDb', score: 0.9, metadata: { file: 'a.ts' } },
    ],
    cacheHit: false,
    latencyMs: 12,
  }),
  getCacheStats: () => ({ hits: 3, misses: 1, size: 4 }),
  getFeedbackOverview: () => ({ totalFeedback: 0, averageRelevance: 0 }),
  configure: () => undefined,
};

describe('configured store — endpoints return REAL data via wrapVectorStore', () => {
  async function makeConfiguredApp() {
    const store = makeInMemoryStore();
    const embedder = await createEmbedderFromEnv({ EMBEDDING_PROVIDER: 'mock' });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const vectorStore = wrapVectorStore(store as any, embedder);
    const bundle: IntelligenceServicesBundle = { vectorStore, ragPipeline: fakeRag };
    const app = await buildServer({ services: bundle });
    await app.ready();
    return app;
  }

  it('reports ready and reflects real counts after an upsert op', async () => {
    const app = await makeConfiguredApp();

    const status = await app.inject({ method: 'GET', url: '/kb/vector-db/status' });
    expect(status.json().status).toBe('ready');

    const upsert = await app.inject({
      method: 'POST',
      url: '/kb/vector-db/upsert',
      payload: {
        collection: 'codebase',
        documents: [
          { id: 'd1', embedding: [0.1, 0.2, 0.3], content: 'alpha', metadata: { path: 'a.ts' } },
          { id: 'd2', embedding: [0.4, 0.5, 0.6], content: 'beta', metadata: { path: 'b.ts' } },
          { id: 'd3', embedding: [0.7, 0.8, 0.9], content: 'gamma', metadata: { path: 'c.ts' } },
        ],
      },
    });
    expect(upsert.statusCode).toBe(200);
    expect(upsert.json().count).toBe(3);

    const stats = await app.inject({ method: 'GET', url: '/kb/vector-db/stats' });
    expect(stats.json().totalVectors).toBe(3);
    expect(stats.json().dimensions).toBe(3);

    const collections = await app.inject({ method: 'GET', url: '/kb/vector-db/collections' });
    expect(collections.json()).toHaveLength(1);
    expect(collections.json()[0].name).toBe('codebase');
    expect(collections.json()[0].vectorCount).toBe(3);

    await app.close();
  });

  it('text search returns real hits (embedding computed by the wired embedder)', async () => {
    const app = await makeConfiguredApp();
    await app.inject({
      method: 'POST',
      url: '/kb/vector-db/upsert',
      payload: {
        collection: 'codebase',
        documents: [{ id: 'd1', embedding: [0.1, 0.2, 0.3], content: 'hello', metadata: {} }],
      },
    });

    const search = await app.inject({
      method: 'POST',
      url: '/kb/vector-db/search',
      payload: { collection: 'codebase', query: 'hello', topK: 5 },
    });
    expect(search.statusCode).toBe(200);
    expect(search.json().results.length).toBeGreaterThanOrEqual(1);
    expect(search.json().results[0].content).toBe('hello');

    await app.close();
  });

  it('rag/metrics is non-stub when a pipeline is wired', async () => {
    const app = await makeConfiguredApp();
    await app.inject({ method: 'POST', url: '/kb/rag/query', payload: { query: 'x' } });
    const metrics = await app.inject({ method: 'GET', url: '/kb/rag/metrics' });
    expect(metrics.statusCode).toBe(200);
    expect(metrics.json().queries).toBeGreaterThanOrEqual(1);
    expect(metrics.json().cacheHitRate).toBeCloseTo(0.75, 2);
    await app.close();
  });

  it('search surfaces a clear error when no embedder is configured', async () => {
    const store = makeInMemoryStore();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const vectorStore = wrapVectorStore(store as any, undefined);
    const app = await buildServer({ services: { vectorStore } });
    await app.ready();

    // Non-embedding ops still work without an embedder…
    const status = await app.inject({ method: 'GET', url: '/kb/vector-db/status' });
    expect(status.json().status).toBe('ready');

    // …but text search (which needs an embedding) rejects clearly.
    const search = await app.inject({
      method: 'POST',
      url: '/kb/vector-db/search',
      payload: { collection: 'codebase', query: 'hello' },
    });
    expect(search.statusCode).toBeGreaterThanOrEqual(500);

    await app.close();
  });
});
