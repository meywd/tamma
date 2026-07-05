/**
 * RAG Pipeline Cache
 *
 * Implements query result caching and embedding caching for the RAG pipeline.
 */

import type { RAGQuery, RAGResult, CachingConfig } from './types.js';
import { calculateHash } from '../indexer/metadata/hash-calculator.js';
import { monotonicNow } from '@tamma/shared';

/**
 * Cache entry with timestamp for TTL management
 */
interface CacheEntry<T> {
  value: T;
  timestamp: number;
  accessCount: number;
}

/**
 * Cache statistics
 */
export interface RAGCacheStats {
  /** Number of entries in query cache */
  queryCount: number;
  /** Number of entries in embedding cache */
  embeddingCount: number;
  /** Cache hit rate (0-1) */
  hitRate: number;
  /** Total hits */
  hits: number;
  /** Total misses */
  misses: number;
}

/**
 * RAG Cache implementation using LRU strategy with TTL
 */
export class RAGCache {
  private queryCache: Map<string, CacheEntry<RAGResult>>;
  private embeddingCache: Map<string, CacheEntry<number[]>>;
  private config: CachingConfig;
  private hits = 0;
  private misses = 0;

  constructor(config: CachingConfig) {
    this.config = config;
    this.queryCache = new Map();
    this.embeddingCache = new Map();
  }

  /**
   * Generate cache key for a RAG query
   */
  private generateQueryKey(query: RAGQuery): string {
    const normalized = {
      text: query.text.toLowerCase().trim(),
      sources: query.sources?.sort() ?? [],
      maxTokens: query.maxTokens ?? 0,
      topK: query.topK ?? 0,
      context: query.context ?? {},
    };
    return calculateHash(JSON.stringify(normalized));
  }

  /**
   * Generate cache key for embedding text
   */
  private generateEmbeddingKey(text: string): string {
    return calculateHash(text.toLowerCase().trim());
  }

  /**
   * Check if entry is expired based on TTL
   */
  private isExpired(entry: CacheEntry<unknown>): boolean {
    const now = Date.now();
    const ttlMs = this.config.ttlSeconds * 1000;
    return now - entry.timestamp > ttlMs;
  }

  /**
   * Evict oldest entries if cache is full
   */
  private evictIfNeeded<T>(cache: Map<string, CacheEntry<T>>): void {
    if (cache.size < this.config.maxEntries) {
      return;
    }

    // Find and remove least recently used entry
    let oldestKey: string | null = null;
    let oldestTime = Infinity;

    for (const [key, entry] of cache) {
      if (entry.timestamp < oldestTime) {
        oldestTime = entry.timestamp;
        oldestKey = key;
      }
    }

    if (oldestKey) {
      cache.delete(oldestKey);
    }
  }

  /**
   * Clean up expired entries
   */
  private cleanExpired<T>(cache: Map<string, CacheEntry<T>>): void {
    for (const [key, entry] of cache) {
      if (this.isExpired(entry)) {
        cache.delete(key);
      }
    }
  }

  // === Query Cache ===

  /**
   * Get cached query result
   */
  getCachedResult(query: RAGQuery): RAGResult | null {
    if (!this.config.enabled) {
      this.misses++;
      return null;
    }

    const key = this.generateQueryKey(query);
    const entry = this.queryCache.get(key);

    if (!entry) {
      this.misses++;
      return null;
    }

    if (this.isExpired(entry)) {
      this.queryCache.delete(key);
      this.misses++;
      return null;
    }

    // Update access count and timestamp for LRU
    entry.accessCount++;
    entry.timestamp = monotonicNow();
    this.hits++;

    // Return a copy with cacheHit flag set
    return {
      ...entry.value,
      cacheHit: true,
    };
  }

  /**
   * Cache a query result
   */
  cacheResult(query: RAGQuery, result: RAGResult): void {
    if (!this.config.enabled) {
      return;
    }

    this.evictIfNeeded(this.queryCache);

    const key = this.generateQueryKey(query);
    this.queryCache.set(key, {
      value: { ...result, cacheHit: false },
      timestamp: monotonicNow(),
      accessCount: 1,
    });
  }

  // === Embedding Cache ===

  /**
   * Get cached embedding
   */
  getCachedEmbedding(text: string): number[] | null {
    if (!this.config.enabled) {
      return null;
    }

    const key = this.generateEmbeddingKey(text);
    const entry = this.embeddingCache.get(key);

    if (!entry) {
      return null;
    }

    if (this.isExpired(entry)) {
      this.embeddingCache.delete(key);
      return null;
    }

    // Update access count and timestamp for LRU
    entry.accessCount++;
    entry.timestamp = monotonicNow();

    return entry.value;
  }

  /**
   * Cache an embedding
   */
  cacheEmbedding(text: string, embedding: number[]): void {
    if (!this.config.enabled) {
      return;
    }

    this.evictIfNeeded(this.embeddingCache);

    const key = this.generateEmbeddingKey(text);
    this.embeddingCache.set(key, {
      value: embedding,
      timestamp: monotonicNow(),
      accessCount: 1,
    });
  }

  // === Cache Management ===

  /**
   * Invalidate cache entries matching a pattern
   * @param pattern - Optional pattern to match (invalidates all if not provided)
   */
  invalidate(pattern?: string): void {
    if (!pattern) {
      this.queryCache.clear();
      this.embeddingCache.clear();
      return;
    }

    // Pattern-based invalidation for query cache
    for (const [key] of this.queryCache) {
      if (key.includes(pattern)) {
        this.queryCache.delete(key);
      }
    }
  }

  /**
   * Clean expired entries from all caches
   */
  cleanup(): void {
    this.cleanExpired(this.queryCache);
    this.cleanExpired(this.embeddingCache);
  }

  /**
   * Get cache statistics
   */
  getStats(): RAGCacheStats {
    return {
      queryCount: this.queryCache.size,
      embeddingCount: this.embeddingCache.size,
      hitRate: this.hits + this.misses > 0 ? this.hits / (this.hits + this.misses) : 0,
      hits: this.hits,
      misses: this.misses,
    };
  }

  /**
   * Reset statistics
   */
  resetStats(): void {
    this.hits = 0;
    this.misses = 0;
  }

  /**
   * Clear all caches
   */
  clear(): void {
    this.queryCache.clear();
    this.embeddingCache.clear();
    this.resetStats();
  }

  /**
   * Update configuration
   */
  updateConfig(config: Partial<CachingConfig>): void {
    this.config = { ...this.config, ...config };
  }
}

/**
 * No-op cache implementation for when caching is disabled
 */
export class NoOpRAGCache extends RAGCache {
  constructor() {
    super({ enabled: false, ttlSeconds: 0, maxEntries: 0 });
  }

  override getCachedResult(): RAGResult | null {
    return null;
  }

  override cacheResult(): void {
    // No-op
  }

  override getCachedEmbedding(): number[] | null {
    return null;
  }

  override cacheEmbedding(): void {
    // No-op
  }

  override getStats(): RAGCacheStats {
    return {
      queryCount: 0,
      embeddingCount: 0,
      hitRate: 0,
      hits: 0,
      misses: 0,
    };
  }
}

/**
 * Create a cache instance based on configuration
 */
export function createRAGCache(config: CachingConfig): RAGCache {
  if (!config.enabled) {
    return new NoOpRAGCache();
  }
  return new RAGCache(config);
}
