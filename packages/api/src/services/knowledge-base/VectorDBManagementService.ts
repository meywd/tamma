/**
 * Vector DB Management Service
 *
 * Manages vector database collections, search testing, and metrics.
 *
 * Delegates to a real IVectorStoreService implementation when available;
 * otherwise returns empty state.
 */

import type {
  CollectionInfo,
  CollectionStatsInfo,
  VectorSearchRequest,
  VectorSearchResult,
  StorageUsage,
} from '@tamma/shared';
import type { IVectorStoreService } from './types.js';

export class VectorDBManagementService {
  private readonly store: IVectorStoreService | null;
  private queryCounters: Map<string, number> = new Map();

  constructor(store?: IVectorStoreService) {
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
        if (this.store.getCollectionStats) {
          const stats = await this.store.getCollectionStats(name);
          collections.push({
            name,
            vectorCount: stats.vectorCount,
            dimensions: stats.dimensions,
            storageBytes: stats.storageBytes,
            createdAt: new Date().toISOString(),
            lastModified: new Date().toISOString(),
          });
        } else {
          collections.push({
            name,
            vectorCount: 0,
            dimensions: 0,
            storageBytes: 0,
            createdAt: new Date().toISOString(),
            lastModified: new Date().toISOString(),
          });
        }
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
    if (!this.store || !this.store.getCollectionStats) {
      throw new Error(`Collection not found: ${name}`);
    }

    const stats = await this.store.getCollectionStats(name);
    const totalQueries = this.queryCounters.get(name) ?? 0;

    return {
      name,
      vectorCount: stats.vectorCount,
      dimensions: stats.dimensions,
      storageBytes: stats.storageBytes,
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

    await this.store.createCollection(name, { dimensions });
    this.queryCounters.set(name, 0);
  }

  async deleteCollection(name: string): Promise<void> {
    if (!this.store) {
      throw new Error(`Collection not found: ${name}`);
    }

    await this.store.deleteCollection(name);
    this.queryCounters.delete(name);
  }

  async search(request: VectorSearchRequest): Promise<VectorSearchResult[]> {
    if (!this.store) {
      throw new Error(`Collection not found: ${request.collection}`);
    }

    // Track query count
    const current = this.queryCounters.get(request.collection) ?? 0;
    this.queryCounters.set(request.collection, current + 1);

    try {
      // Prefer hybridSearch if available (accepts text query), fall back to search
      if (this.store.hybridSearch) {
        const results = await this.store.hybridSearch(request.collection, {
          text: request.query,
          limit: request.topK,
        });

        return results.map((r) => ({
          id: r.id,
          score: r.score,
          content: r.content,
          metadata: r.metadata ?? {},
        }));
      }

      const results = await this.store.search(request.collection, {
        text: request.query,
        limit: request.topK,
      });

      return results.map((r) => ({
        id: r.id,
        score: r.score,
        content: r.content,
        metadata: r.metadata ?? {},
      }));
    } catch {
      // If search fails, return empty results
      return [];
    }
  }

  async getStorageUsage(): Promise<StorageUsage> {
    if (!this.store) {
      return { totalBytes: 0, byCollection: {} };
    }

    // Use dedicated getStorageUsage if available
    if (this.store.getStorageUsage) {
      const usage = await this.store.getStorageUsage();
      return { totalBytes: usage.totalBytes, byCollection: {} };
    }

    // Fall back to aggregating from collection stats
    if (!this.store.getCollectionStats) {
      return { totalBytes: 0, byCollection: {} };
    }

    const names = await this.store.listCollections();
    const byCollection: Record<string, number> = {};
    let totalBytes = 0;

    for (const name of names) {
      try {
        const stats = await this.store.getCollectionStats(name);
        byCollection[name] = stats.storageBytes;
        totalBytes += stats.storageBytes;
      } catch {
        byCollection[name] = 0;
      }
    }

    return { totalBytes, byCollection };
  }
}
