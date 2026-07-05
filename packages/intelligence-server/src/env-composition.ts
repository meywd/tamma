/**
 * Environment-driven composition root for the intelligence sidecar.
 *
 * Turns process env into a live {@link IntelligenceServicesBundle}: it constructs
 * a real `@tamma/intelligence` vector store (ChromaDB or pgvector) + an
 * OpenAI-style embedder + a RAG pipeline, adapted to the narrow sidecar
 * interfaces through {@link adaptVectorStore} / {@link adaptRagPipeline}.
 *
 * Degrades gracefully: when NO vector-store env is configured the factories
 * return `undefined` / an empty bundle, so an unconfigured deployment still
 * boots with the pre-existing `not_configured` stub behaviour. A configured
 * store makes the `/kb/vector-db/*` (and, with an embedder, `/kb/rag/*`)
 * endpoints return real data.
 *
 * Env vars consumed:
 *   Vector store (pick one; ChromaDB wins if both present):
 *     - CHROMADB_URL              e.g. http://chromadb:8000  (preferred)
 *     - CHROMADB_HOST / CHROMADB_PORT
 *     - CHROMADB_TEST_URL         (CI fallback)
 *     - PGVECTOR_URL / VECTOR_DB_URL / KB_PGVECTOR_CONNECTION_STRING  (Postgres conn string)
 *     - KB_VECTOR_STORE / VECTOR_STORE_PROVIDER = 'chromadb' | 'pgvector'  (explicit override)
 *   Embedder (needed for text search + RAG; absent => those ops surface a clear error):
 *     - OPENAI_API_KEY / EMBEDDING_API_KEY
 *     - EMBEDDING_PROVIDER = 'openai' | 'cohere' | 'ollama' | 'local' | 'mock'  (default 'openai')
 *     - EMBEDDING_MODEL           (default 'text-embedding-3-small')
 *     - EMBEDDING_BASE_URL / OPENAI_BASE_URL
 *     - EMBEDDING_DIMENSIONS      (informational; overridden by the provider's real dims)
 *   RAG:
 *     - KB_RAG_COLLECTION / KB_INDEX_COLLECTION  (default 'codebase')
 */

import {
  createVectorStore,
  type IVectorStore,
  type VectorStoreConfig,
  type ChromaDBConfig,
  type CollectionOptions,
  type VectorMetadata,
} from '@tamma/intelligence/vector-store';
// Import EmbeddingService from the NARROW `/embedding` subpath — NOT the full
// `/indexer` barrel. The barrel transitively value-imports `typescript` (a
// devDependency of @tamma/intelligence, pruned in the `--prod` runtime image)
// via the TypeScript chunker, which would fail ESM link-time resolution and
// crash-loop `node dist/server.js` even with no vector-store env. The sidecar
// never uses the chunker, so this subpath keeps the prod import graph clean.
import {
  EmbeddingService,
  type EmbeddingProviderType,
  type EmbeddingProviderConfig,
} from '@tamma/intelligence/embedding';
import {
  createRAGPipeline,
  type RAGPipeline,
  type RAGQuery,
  type RAGConfig,
  type RAGSourceType,
} from '@tamma/intelligence/rag';

import { adaptVectorStore, adaptRagPipeline } from './adapters.js';
import type {
  IVectorStoreAdapter,
  IRagPipeline,
  IntelligenceServicesBundle,
} from './types.js';

type Env = Record<string, string | undefined>;

/** The exact `real` shape {@link adaptVectorStore} expects to bridge from. */
type RealVectorStore = Parameters<typeof adaptVectorStore>[0];
/** The exact `real` shape {@link adaptRagPipeline} expects to bridge from. */
type RealRagPipeline = Parameters<typeof adaptRagPipeline>[0];

const DEFAULT_EMBEDDING_MODEL = 'text-embedding-3-small';
const DEFAULT_EMBEDDING_DIMENSIONS = 1536;
const DEFAULT_RAG_COLLECTION = 'codebase';

/**
 * Message thrown lazily when a text-search / RAG operation needs an embedder
 * that was never configured (store env present but embedding key absent). This
 * is the "clear failure if the key is absent" contract: non-embedding vector
 * ops (status, stats, collections, upsert, delete) still work; text search and
 * RAG surface this explicit error instead of silently returning empty results.
 */
export const EMBEDDER_MISSING_MESSAGE =
  'KB embedding provider is not configured: set OPENAI_API_KEY (or EMBEDDING_API_KEY) ' +
  'and optionally EMBEDDING_MODEL to enable vector text search and RAG retrieval.';

// ---------------------------------------------------------------------------
// Config resolution (pure — no network)
// ---------------------------------------------------------------------------

function resolveChromaEndpoint(env: Env): string | undefined {
  return env['CHROMADB_URL'] ?? env['CHROMADB_TEST_URL'];
}

function resolvePgConnectionString(env: Env): string | undefined {
  return (
    env['KB_PGVECTOR_CONNECTION_STRING'] ??
    env['PGVECTOR_URL'] ??
    env['VECTOR_DB_URL']
  );
}

/** Cheap predicate: is ANY vector-store backend configured in env? */
export function hasVectorStoreEnv(env: Env = process.env): boolean {
  return resolveVectorStoreConfig(env, DEFAULT_EMBEDDING_DIMENSIONS) !== undefined;
}

function resolveDimensions(env: Env): number {
  const raw = env['EMBEDDING_DIMENSIONS'];
  if (raw) {
    const parsed = Number.parseInt(raw, 10);
    if (Number.isInteger(parsed) && parsed > 0) {
      return parsed;
    }
  }
  return DEFAULT_EMBEDDING_DIMENSIONS;
}

/**
 * Resolve a {@link VectorStoreConfig} from env, or `undefined` when no backend
 * is configured. ChromaDB is preferred (it is what docker-compose/CI provide);
 * pgvector is used when a Postgres connection string is supplied. An explicit
 * `KB_VECTOR_STORE` / `VECTOR_STORE_PROVIDER` overrides the inference.
 */
export function resolveVectorStoreConfig(
  env: Env = process.env,
  dimensions: number = DEFAULT_EMBEDDING_DIMENSIONS,
): VectorStoreConfig | undefined {
  const explicit = (env['KB_VECTOR_STORE'] ?? env['VECTOR_STORE_PROVIDER'])?.toLowerCase();

  const chromaEndpoint = resolveChromaEndpoint(env);
  const chromaHost = env['CHROMADB_HOST'];
  const hasChroma = Boolean(chromaEndpoint || chromaHost);

  const pgConn = resolvePgConnectionString(env);
  const hasPg = Boolean(pgConn);

  let provider: 'chromadb' | 'pgvector' | undefined;
  if (explicit === 'chromadb' || explicit === 'pgvector') {
    provider = explicit;
  } else if (hasChroma) {
    provider = 'chromadb';
  } else if (hasPg) {
    provider = 'pgvector';
  }

  if (provider === 'chromadb') {
    if (!hasChroma) return undefined;
    // chromadb v3's ChromaDBVectorStore accepts a full URL / host:port string
    // via `persistPath`, and parses host/port/ssl from it. When only a bare
    // host is supplied we set host+port explicitly (client mode).
    const chromadb: ChromaDBConfig = {
      persistPath: chromaEndpoint ?? '',
      anonymizedTelemetry: false,
    };
    if (!chromaEndpoint && chromaHost) {
      const port = env['CHROMADB_PORT'] ? Number.parseInt(env['CHROMADB_PORT'], 10) : 8000;
      chromadb.host = chromaHost;
      chromadb.port = port;
      chromadb.persistPath = `${chromaHost}:${port}`;
    }
    return { provider: 'chromadb', dimensions, distanceMetric: 'cosine', chromadb };
  }

  if (provider === 'pgvector') {
    if (!pgConn) return undefined;
    return {
      provider: 'pgvector',
      dimensions,
      distanceMetric: 'cosine',
      pgvector: { connectionString: pgConn },
    };
  }

  return undefined;
}

// ---------------------------------------------------------------------------
// Embedder
// ---------------------------------------------------------------------------

/**
 * Build + initialize an {@link EmbeddingService} from env, or `undefined` when
 * a cloud provider is selected but no API key is present. `ollama`/`local`/
 * `mock` providers need no key. Initialization is network-free for OpenAI (it
 * only stores config), so this is cheap to call.
 */
export async function createEmbedderFromEnv(
  env: Env = process.env,
): Promise<EmbeddingService | undefined> {
  const provider = (env['EMBEDDING_PROVIDER'] ?? 'openai').toLowerCase() as EmbeddingProviderType;
  const apiKey = env['EMBEDDING_API_KEY'] ?? env['OPENAI_API_KEY'];
  const needsKey = provider === 'openai' || provider === 'cohere';
  if (needsKey && !apiKey) {
    return undefined;
  }

  const providerConfig: EmbeddingProviderConfig = {
    model: env['EMBEDDING_MODEL'] ?? DEFAULT_EMBEDDING_MODEL,
  };
  if (apiKey) providerConfig.apiKey = apiKey;
  const baseUrl = env['EMBEDDING_BASE_URL'] ?? env['OPENAI_BASE_URL'];
  if (baseUrl) providerConfig.baseUrl = baseUrl;

  const service = new EmbeddingService({ provider, providerConfig });
  await service.initialize();
  return service;
}

// ---------------------------------------------------------------------------
// Vector store adapter
// ---------------------------------------------------------------------------

/**
 * Bridge a concrete `@tamma/intelligence` {@link IVectorStore} to the narrow
 * {@link IVectorStoreAdapter} the sidecar services consume, mapping the small
 * shape differences (VectorDocument defaults, CollectionStats field names) and
 * wiring the text→embedding function.
 */
export function wrapVectorStore(
  store: IVectorStore,
  embeddingService?: EmbeddingService,
): IVectorStoreAdapter {
  const embedText: (text: string) => Promise<number[]> = embeddingService
    ? (text) => embeddingService.embed(text)
    : () => Promise.reject(new Error(EMBEDDER_MISSING_MESSAGE));

  const real: RealVectorStore = {
    listCollections: () => store.listCollections(),
    createCollection: (name, opts) =>
      store.createCollection(name, opts as CollectionOptions | undefined),
    deleteCollection: (name) => store.deleteCollection(name),
    upsert: (collection, docs) =>
      store.upsert(
        collection,
        docs.map((d) => ({
          id: d.id,
          embedding: d.embedding,
          content: d.content ?? '',
          metadata: (d.metadata ?? {}) as VectorMetadata,
        })),
      ),
    delete: (collection, ids) => store.delete(collection, ids),
    search: async (collection, query) => {
      const results = await store.search(collection, {
        embedding: query.embedding,
        topK: query.topK,
        includeContent: query.includeContent ?? true,
        includeMetadata: query.includeMetadata ?? true,
      });
      return results.map((r) => ({
        id: r.id,
        score: r.score,
        content: r.content ?? '',
        metadata: r.metadata ?? {},
      }));
    },
    hybridSearch: async (collection, query) => {
      const results = await store.hybridSearch(collection, {
        text: query.text,
        embedding: query.embedding,
        topK: query.topK,
        includeContent: query.includeContent ?? true,
        includeMetadata: query.includeMetadata ?? true,
      });
      return results.map((r) => ({
        id: r.id,
        score: r.score,
        content: r.content ?? '',
        metadata: r.metadata ?? {},
      }));
    },
    getCollectionStats: async (name) => {
      const stats = await store.getCollectionStats(name);
      return {
        vectorCount: stats.documentCount,
        dimensions: stats.dimensions,
        storageBytes: stats.indexSize ?? 0,
      };
    },
  };

  return adaptVectorStore(real, embedText);
}

// ---------------------------------------------------------------------------
// RAG pipeline adapter
// ---------------------------------------------------------------------------

function wrapRagPipeline(pipeline: RAGPipeline): RealRagPipeline {
  return {
    retrieve: async (query, options) => {
      const ragQuery: RAGQuery = { text: query.text };
      if (query.maxResults !== undefined) ragQuery.topK = query.maxResults;
      if (query.sources) ragQuery.sources = query.sources as RAGSourceType[];
      const maxTokens = options?.['maxTokens'];
      if (typeof maxTokens === 'number') ragQuery.maxTokens = maxTokens;

      const result = await pipeline.retrieve(ragQuery);
      return {
        queryId: result.queryId,
        retrievedChunks: result.retrievedChunks.map((chunk) => ({
          content: chunk.content,
          source: String(chunk.source),
          score: chunk.score,
          metadata: chunk.metadata as unknown as Record<string, unknown>,
        })),
        cacheHit: result.cacheHit,
        latencyMs: result.latencyMs,
      };
    },
    getCacheStats: () => {
      const stats = pipeline.getCacheStats();
      return { hits: stats.hits, misses: stats.misses, size: stats.hits + stats.misses };
    },
    getFeedbackOverview: () => {
      const overview = pipeline.getFeedbackOverview();
      return {
        totalFeedback: overview.totalQueries,
        averageRelevance: overview.avgHelpfulRate,
      };
    },
    configure: (config) => {
      void pipeline.configure(config as Partial<RAGConfig>);
    },
  };
}

/**
 * Build + initialize a real {@link RAGPipeline} over the supplied store +
 * embedder, adapted to the sidecar {@link IRagPipeline} interface.
 */
export async function buildRagPipeline(
  store: IVectorStore,
  embeddingService: EmbeddingService,
  env: Env = process.env,
): Promise<IRagPipeline> {
  const collectionName =
    env['KB_RAG_COLLECTION'] ?? env['KB_INDEX_COLLECTION'] ?? DEFAULT_RAG_COLLECTION;
  const pipeline = createRAGPipeline();
  await pipeline.initialize({ embeddingService, vectorStore: store, collectionName });
  return adaptRagPipeline(wrapRagPipeline(pipeline));
}

// ---------------------------------------------------------------------------
// Shared resource construction
// ---------------------------------------------------------------------------

interface EnvResources {
  store: IVectorStore;
  embeddingService: EmbeddingService | undefined;
}

/**
 * Construct + initialize the raw store (and embedder, if configured) from env.
 * Returns `undefined` when no vector-store backend is configured. Throws if a
 * store IS configured but cannot be reached (callers decide whether to degrade).
 */
async function buildResourcesFromEnv(env: Env): Promise<EnvResources | undefined> {
  if (!hasVectorStoreEnv(env)) {
    return undefined;
  }
  const embeddingService = await createEmbedderFromEnv(env);
  const dimensions = embeddingService?.getDimensions() ?? resolveDimensions(env);
  const config = resolveVectorStoreConfig(env, dimensions);
  if (!config) {
    return undefined;
  }
  const store = createVectorStore(config);
  await store.initialize();
  return { store, embeddingService };
}

// ---------------------------------------------------------------------------
// Public factories (referenced by adapters.ts JSDoc / used by startServer)
// ---------------------------------------------------------------------------

/**
 * Build a real vector-store adapter from env, or `undefined` when no store is
 * configured. Opens its own store connection — prefer
 * {@link buildIntelligenceBundleFromEnv} when you also want RAG, so the store
 * is shared.
 */
export async function createVectorStoreFromEnv(
  env: Env = process.env,
): Promise<IVectorStoreAdapter | undefined> {
  const resources = await buildResourcesFromEnv(env);
  if (!resources) {
    return undefined;
  }
  return wrapVectorStore(resources.store, resources.embeddingService);
}

/**
 * Build a real RAG pipeline from env, or `undefined` when no store OR no
 * embedder is configured (RAG retrieval fundamentally requires embeddings).
 */
export async function createRagPipelineFromEnv(
  env: Env = process.env,
): Promise<IRagPipeline | undefined> {
  const resources = await buildResourcesFromEnv(env);
  if (!resources || !resources.embeddingService) {
    return undefined;
  }
  return buildRagPipeline(resources.store, resources.embeddingService, env);
}

/**
 * The composition root used by `startServer`. Builds the store + embedder ONCE
 * and shares them across the vector-store and RAG adapters. Returns an empty
 * bundle (all endpoints degrade to `not_configured` stubs) when no vector-store
 * env is configured.
 */
export async function buildIntelligenceBundleFromEnv(
  env: Env = process.env,
): Promise<IntelligenceServicesBundle> {
  const resources = await buildResourcesFromEnv(env);
  if (!resources) {
    return {};
  }
  const bundle: IntelligenceServicesBundle = {
    vectorStore: wrapVectorStore(resources.store, resources.embeddingService),
  };
  if (resources.embeddingService) {
    bundle.ragPipeline = await buildRagPipeline(
      resources.store,
      resources.embeddingService,
      env,
    );
  }
  return bundle;
}
