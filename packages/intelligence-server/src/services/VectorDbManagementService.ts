/**
 * Vector DB Management Service
 *
 * Wraps a real vector store to expose the 6 C# /kb/vector-db/* endpoints:
 * status, search, upsert, delete, collections, stats.
 */

import type { IVectorStoreAdapter } from '../types.js';

export interface VectorSearchRequest {
  collection: string;
  query: string;
  topK?: number;
}

export interface VectorSearchResult {
  id: string;
  score: number;
  content: string;
  metadata: Record<string, unknown>;
}

export interface VectorUpsertRequest {
  collection: string;
  documents: Array<{
    id: string;
    embedding: number[];
    content?: string;
    metadata?: Record<string, unknown>;
  }>;
}

export interface VectorDeleteRequest {
  collection: string;
  ids: string[];
}

export interface VectorStatusResponse {
  status: 'ready' | 'not_configured';
  collections?: number;
}

export interface VectorStatsResponse {
  totalVectors: number;
  dimensions: number;
  totalBytes: number;
}

export interface CollectionInfo {
  name: string;
  vectorCount: number;
  dimensions: number;
  storageBytes: number;
}

export class VectorDbManagementService {
  private readonly store: IVectorStoreAdapter | null;

  constructor(store?: IVectorStoreAdapter) {
    this.store = store ?? null;
  }

  async getStatus(): Promise<VectorStatusResponse> {
    if (!this.store) {
      return { status: 'not_configured' };
    }
    try {
      const names = await this.store.listCollections();
      return { status: 'ready', collections: names.length };
    } catch {
      return { status: 'not_configured' };
    }
  }

  async search(req: VectorSearchRequest): Promise<{ results: VectorSearchResult[] }> {
    if (!this.store) {
      return { results: [] };
    }
    const limit = req.topK ?? 10;
    const raw = this.store.hybridSearch
      ? await this.store.hybridSearch(req.collection, { text: req.query, limit })
      : await this.store.search(req.collection, { text: req.query, limit });
    const results: VectorSearchResult[] = raw.map((r) => ({
      id: r.id,
      score: r.score,
      content: r.content,
      metadata: r.metadata ?? {},
    }));
    return { results };
  }

  async upsert(req: VectorUpsertRequest): Promise<{ message: string; count: number }> {
    if (!this.store) {
      return { message: 'Vectors upserted (stub — no store configured)', count: 0 };
    }
    await this.store.upsert(req.collection, req.documents);
    return { message: 'Vectors upserted', count: req.documents.length };
  }

  async delete(req: VectorDeleteRequest): Promise<{ message: string }> {
    if (!this.store) {
      return { message: 'Vectors deleted (stub — no store configured)' };
    }
    await this.store.delete(req.collection, req.ids);
    return { message: 'Vectors deleted' };
  }

  async listCollections(): Promise<CollectionInfo[]> {
    if (!this.store) {
      return [];
    }
    const names = await this.store.listCollections();
    const out: CollectionInfo[] = [];
    for (const name of names) {
      try {
        if (this.store.getCollectionStats) {
          const s = await this.store.getCollectionStats(name);
          out.push({
            name,
            vectorCount: s.vectorCount,
            dimensions: s.dimensions,
            storageBytes: s.storageBytes,
          });
        } else {
          out.push({ name, vectorCount: 0, dimensions: 0, storageBytes: 0 });
        }
      } catch {
        out.push({ name, vectorCount: 0, dimensions: 0, storageBytes: 0 });
      }
    }
    return out;
  }

  async getStats(): Promise<VectorStatsResponse> {
    if (!this.store) {
      return { totalVectors: 0, dimensions: 0, totalBytes: 0 };
    }
    const names = await this.store.listCollections();
    let totalVectors = 0;
    let totalBytes = 0;
    let dimensions = 0;
    for (const name of names) {
      try {
        if (this.store.getCollectionStats) {
          const s = await this.store.getCollectionStats(name);
          totalVectors += s.vectorCount;
          totalBytes += s.storageBytes;
          if (dimensions === 0) dimensions = s.dimensions;
        }
      } catch {
        // Skip collections whose stats cannot be read.
      }
    }
    return { totalVectors, dimensions, totalBytes };
  }
}
