/**
 * Vector DB Management Service
 *
 * Manages vector database collections, search testing, and metrics.
 *
 * Delegates to the real IVectorStore from @tamma/intelligence
 * when available; otherwise returns empty state.
 */

import type {
  CollectionInfo,
  CollectionStatsInfo,
  VectorSearchRequest,
  VectorSearchResult,
  StorageUsage,
} from '@tamma/shared';
import type { IVectorStore } from '@tamma/intelligence/vector-store';

export class VectorDBManagementService {
  private readonly store: IVectorStore | null;
  private queryCounters: Map<string, number> = new Map();

  constructor(store?: IVectorStore) {
    this.store = store ?? null;
  }

  async listCollections(): Promise<CollectionInfo[]> {
    if (!this.store) {
      return [];
    }

    const names = await this.store.listCollections();
    const collections: CollectionInfo[] = [];

    for (const name of names) {
      try {
        const stats = await this.store.getCollectionStats(name);
        collections.push({
          name: stats.name,
          vectorCount: stats.documentCount,
          dimensions: stats.dimensions,
          storageBytes: stats.indexSize ?? 0,
          createdAt: stats.createdAt ? stats.createdAt.toISOString() : new Date().toISOString(),
          lastModified: stats.updatedAt ? stats.updatedAt.toISOString() : new Date().toISOString(),
        });
      } catch {
        // If stats fail for a collection, include it with zeroed stats
        collections.push({
          name,
          vectorCount: 0,
          dimensions: 0,
          storageBytes: 0,
          createdAt: new Date().toISOString(),
          lastModified: new Date().toISOString(),
        });
      }
    }

    return collections;
  }

  async getCollectionStats(name: string): Promise<CollectionStatsInfo> {
    if (!this.store) {
      throw new Error(`Collection not found: ${name}`);
    }

    const exists = await this.store.collectionExists(name);
    if (!exists) {
      throw new Error(`Collection not found: ${name}`);
    }

    const stats = await this.store.getCollectionStats(name);
    const totalQueries = this.queryCounters.get(name) ?? 0;

    return {
      name: stats.name,
      vectorCount: stats.documentCount,
      dimensions: stats.dimensions,
      storageBytes: stats.indexSize ?? 0,
      queryMetrics: {
        totalQueries,
        avgLatencyMs: 0,
        p95LatencyMs: 0,
        p99LatencyMs: 0,
        queriesPerMinute: 0,
      },
    };
  }

  async createCollection(name: string, dimensions = 1536): Promise<void> {
    if (!this.store) {
      throw new Error('No vector store configured');
    }

    const exists = await this.store.collectionExists(name);
    if (exists) {
      throw new Error(`Collection already exists: ${name}`);
    }

    await this.store.createCollection(name, { dimensions });
    this.queryCounters.set(name, 0);
  }

  async deleteCollection(name: string): Promise<void> {
    if (!this.store) {
      throw new Error(`Collection not found: ${name}`);
    }

    const exists = await this.store.collectionExists(name);
    if (!exists) {
      throw new Error(`Collection not found: ${name}`);
    }

    await this.store.deleteCollection(name);
    this.queryCounters.delete(name);
  }

  async search(request: VectorSearchRequest): Promise<VectorSearchResult[]> {
    if (!this.store) {
      throw new Error(`Collection not found: ${request.collection}`);
    }

    const exists = await this.store.collectionExists(request.collection);
    if (!exists) {
      throw new Error(`Collection not found: ${request.collection}`);
    }

    // Track query count
    const current = this.queryCounters.get(request.collection) ?? 0;
    this.queryCounters.set(request.collection, current + 1);

    // The IVectorStore.search() requires an embedding vector, but VectorSearchRequest
    // from the API provides a text query. We do a hybrid search if available, or
    // fall back to returning empty results if we cannot generate an embedding.
    // For now, use hybridSearch which accepts a text query.
    try {
      const hybridQuery: import('@tamma/intelligence/vector-store').HybridSearchQuery = {
        embedding: [], // Empty embedding; hybrid search can use text alone
        text: request.query,
        topK: request.topK,
        includeContent: true,
        includeMetadata: true,
      };
      if (request.scoreThreshold !== undefined) {
        hybridQuery.scoreThreshold = request.scoreThreshold;
      }

      const results = await this.store.hybridSearch(request.collection, hybridQuery);

      return results.map((r) => ({
        id: r.id,
        score: r.score,
        content: r.content ?? '',
        metadata: (r.metadata as Record<string, unknown>) ?? {},
      }));
    } catch {
      // If hybrid search is not supported or fails, return empty results
      return [];
    }
  }

  async getStorageUsage(): Promise<StorageUsage> {
    if (!this.store) {
      return { totalBytes: 0, byCollection: {} };
    }

    const names = await this.store.listCollections();
    const byCollection: Record<string, number> = {};
    let totalBytes = 0;

    for (const name of names) {
      try {
        const stats = await this.store.getCollectionStats(name);
        const bytes = stats.indexSize ?? 0;
        byCollection[name] = bytes;
        totalBytes += bytes;
      } catch {
        byCollection[name] = 0;
      }
    }

    return { totalBytes, byCollection };
  }
}
