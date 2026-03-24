import type { IContextCache, ContextResponse, CacheStats, CachingConfig } from '../types.js';

interface CacheEntry {
  value: ContextResponse;
  expiresAt: number;
}

export class MemoryCache implements IContextCache {
  private cache = new Map<string, CacheEntry>();
  private hits = 0;
  private misses = 0;
  private maxEntries: number;
  private defaultTtlMs: number;

  constructor(config: CachingConfig) {
    this.maxEntries = config.maxEntries;
    this.defaultTtlMs = config.ttlSeconds * 1000;
  }

  async get(key: string): Promise<ContextResponse | null> {
    const entry = this.cache.get(key);
    if (!entry) {
      this.misses++;
      return null;
    }
    if (Date.now() > entry.expiresAt) {
      this.cache.delete(key);
      this.misses++;
      return null;
    }
    this.hits++;
    return entry.value;
  }

  async set(key: string, value: ContextResponse, ttl?: number): Promise<void> {
    // Evict if at capacity
    if (this.cache.size >= this.maxEntries) {
      this.evictOldest();
    }
    const ttlMs = ttl ? ttl * 1000 : this.defaultTtlMs;
    this.cache.set(key, {
      value,
      expiresAt: Date.now() + ttlMs,
    });
  }

  async delete(key: string): Promise<void> {
    this.cache.delete(key);
  }

  async clear(pattern?: string): Promise<void> {
    if (!pattern) {
      this.cache.clear();
      return;
    }
    const regex = new RegExp(pattern.replace(/\*/g, '.*'));
    for (const key of this.cache.keys()) {
      if (regex.test(key)) {
        this.cache.delete(key);
      }
    }
  }

  getStats(): CacheStats {
    const total = this.hits + this.misses;
    return {
      hits: this.hits,
      misses: this.misses,
      size: this.cache.size,
      maxSize: this.maxEntries,
      hitRate: total === 0 ? 0 : this.hits / total,
    };
  }

  async healthCheck(): Promise<boolean> {
    // Consider unhealthy only when the cache is full to capacity (no room for new entries).
    return this.maxEntries === 0 || this.cache.size < this.maxEntries;
  }

  private evictOldest(): void {
    let oldestKey = '';
    let oldestTime = Infinity;
    for (const [key, entry] of this.cache) {
      if (entry.expiresAt < oldestTime) {
        oldestKey = key;
        oldestTime = entry.expiresAt;
      }
    }
    if (oldestKey) {
      this.cache.delete(oldestKey);
    }
  }
}

export function createMemoryCache(config: CachingConfig): MemoryCache {
  return new MemoryCache(config);
}
