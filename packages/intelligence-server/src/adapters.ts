/**
 * Adapter factories that bridge concrete @tamma/intelligence implementations
 * to the narrow interfaces the sidecar services depend on.
 *
 * These use dynamic `import()` so the sidecar can be compiled and tested
 * WITHOUT requiring @tamma/intelligence to typecheck cleanly. Intelligence
 * has pre-existing strict-mode build errors (documented in the CONCERNS
 * section of the bridge delivery) — the sidecar deliberately depends only
 * on the runtime JS output.
 *
 * Typical wiring (called from a composition root, e.g. a deploy harness):
 *
 *   const bundle: IntelligenceServicesBundle = {
 *     vectorStore: await createVectorStoreFromEnv(),
 *     ragPipeline: await createRagPipelineFromEnv(),
 *   };
 *   await startServer({ services: bundle });
 */

import type {
  IVectorStoreAdapter,
  IRagPipeline,
} from './types.js';

/**
 * Adapt the concrete @tamma/intelligence IVectorStore interface to the
 * narrower IVectorStoreAdapter the sidecar services expect.
 *
 * The real IVectorStore.search takes a `{ embedding: number[], topK }`
 * query; this adapter exposes a text-first signature by requiring callers
 * to supply an embedding function. For a default setup, use the embedding
 * service from @tamma/intelligence/indexer.
 */
export function adaptVectorStore(
  real: {
    listCollections(): Promise<string[]>;
    createCollection(name: string, opts?: unknown): Promise<void>;
    deleteCollection(name: string): Promise<void>;
    upsert(collection: string, docs: Array<{ id: string; embedding: number[]; content?: string; metadata?: Record<string, unknown> }>): Promise<void>;
    delete(collection: string, ids: string[]): Promise<void>;
    search(
      collection: string,
      query: { embedding: number[]; topK: number; includeContent?: boolean; includeMetadata?: boolean },
    ): Promise<Array<{ id: string; score: number; content?: string; metadata?: Record<string, unknown> }>>;
    hybridSearch?(
      collection: string,
      query: { text: string; embedding: number[]; topK: number; includeContent?: boolean; includeMetadata?: boolean },
    ): Promise<Array<{ id: string; score: number; content?: string; metadata?: Record<string, unknown> }>>;
    getCollectionStats?(name: string): Promise<{ vectorCount: number; dimensions: number; storageBytes: number }>;
  },
  embedText: (text: string) => Promise<number[]>,
): IVectorStoreAdapter {
  const base: IVectorStoreAdapter = {
    listCollections: () => real.listCollections(),
    createCollection: (name, opts) => real.createCollection(name, opts),
    deleteCollection: (name) => real.deleteCollection(name),
    upsert: (collection, docs) => real.upsert(collection, docs),
    delete: (collection, ids) => real.delete(collection, ids),
    async search(collection, query) {
      const limit = query.limit ?? 10;
      const embedding = query.vector ?? (query.text ? await embedText(query.text) : []);
      const raw = await real.search(collection, {
        embedding,
        topK: limit,
        includeContent: true,
        includeMetadata: true,
      });
      return raw.map((r) => ({
        id: r.id,
        score: r.score,
        content: r.content ?? '',
        metadata: r.metadata ?? {},
      }));
    },
  };
  if (real.hybridSearch) {
    base.hybridSearch = async (collection, query) => {
      const limit = query.limit ?? 10;
      const embedding = await embedText(query.text);
      const raw = await real.hybridSearch!(collection, {
        text: query.text,
        embedding,
        topK: limit,
        includeContent: true,
        includeMetadata: true,
      });
      return raw.map((r) => ({
        id: r.id,
        score: r.score,
        content: r.content ?? '',
        metadata: r.metadata ?? {},
      }));
    };
  }
  if (real.getCollectionStats) {
    base.getCollectionStats = (name) => real.getCollectionStats!(name);
  }
  return base;
}

/**
 * Adapt the concrete @tamma/intelligence IRAGPipeline to the narrower
 * sidecar IRagPipeline interface. The shapes are already very close; this
 * is largely a pass-through kept as a seam for future changes.
 */
export function adaptRagPipeline(
  real: {
    retrieve(
      query: { text: string; maxResults?: number; sources?: string[] },
      opts?: Record<string, unknown>,
    ): Promise<{
      queryId: string;
      retrievedChunks: Array<{
        content: string;
        source: string;
        score: number;
        metadata?: Record<string, unknown>;
      }>;
      cacheHit: boolean;
      latencyMs: number;
    }>;
    getCacheStats?(): { hits: number; misses: number; size: number };
    getFeedbackOverview?(): { totalFeedback: number; averageRelevance: number };
    configure?(config: Record<string, unknown>): void;
  },
): IRagPipeline {
  const out: IRagPipeline = {
    retrieve: (q, o) => real.retrieve(q, o),
  };
  if (real.getCacheStats) out.getCacheStats = real.getCacheStats.bind(real);
  if (real.getFeedbackOverview) out.getFeedbackOverview = real.getFeedbackOverview.bind(real);
  if (real.configure) out.configure = real.configure.bind(real);
  return out;
}
