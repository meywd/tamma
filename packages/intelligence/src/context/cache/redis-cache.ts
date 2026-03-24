/**
 * Redis Cache Implementation
 *
 * Provides distributed caching for the Context Aggregator using Redis.
 * Falls back gracefully when Redis is unavailable.
 *
 * @module @tamma/intelligence/context/cache
 */

import type { IContextCache, ContextResponse, CacheStats, CachingConfig } from '../types.js';

/**
 * Minimal Redis client interface so callers can inject any
 * ioredis-compatible instance without a hard dependency on ioredis.
 */
export interface IRedisClient {
  get(key: string): Promise<string | null>;
  set(key: string, value: string, exMode?: string, exValue?: number): Promise<string | null>;
  setex(key: string, seconds: number, value: string): Promise<string>;
  del(...keys: string[]): Promise<number>;
  keys(pattern: string): Promise<string[]>;
  ping(): Promise<string>;
  quit(): Promise<string>;
}

/**
 * Redis-backed context cache for distributed deployments.
 *
 * Implements the IContextCache interface, serialising ContextResponse
 * objects to JSON and storing them with a configurable TTL.
 */
export class RedisCache implements IContextCache {
  private client: IRedisClient;
  private prefix: string;
  private defaultTtlSeconds: number;
  private maxEntries: number;
  private hits = 0;
  private misses = 0;

  constructor(client: IRedisClient, config: CachingConfig, prefix = 'tamma:ctx:') {
    this.client = client;
    this.prefix = prefix;
    this.defaultTtlSeconds = config.ttlSeconds;
    this.maxEntries = config.maxEntries;
  }

  async get(key: string): Promise<ContextResponse | null> {
    try {
      const raw = await this.client.get(this.prefix + key);
      if (!raw) {
        this.misses++;
        return null;
      }
      this.hits++;
      return JSON.parse(raw) as ContextResponse;
    } catch {
      this.misses++;
      return null;
    }
  }

  async set(key: string, value: ContextResponse, ttl?: number): Promise<void> {
    try {
      // Enforce maxEntries: count existing keys under this prefix and skip write if at capacity.
      // This is a best-effort check; Redis does not provide atomic conditional-insert.
      if (this.maxEntries > 0) {
        const keys = await this.client.keys(this.prefix + '*');
        if (keys.length >= this.maxEntries && !keys.includes(this.prefix + key)) {
          // At capacity and this is a new key — skip the write.
          return;
        }
      }
      const ttlSeconds = ttl ?? this.defaultTtlSeconds;
      const serialized = JSON.stringify(value);
      await this.client.setex(this.prefix + key, ttlSeconds, serialized);
    } catch {
      // Silently fail -- cache write failure should not break retrieval
    }
  }

  async delete(key: string): Promise<void> {
    try {
      await this.client.del(this.prefix + key);
    } catch {
      // best-effort
    }
  }

  async clear(pattern?: string): Promise<void> {
    try {
      const searchPattern = pattern
        ? this.prefix + pattern
        : this.prefix + '*';
      const keys = await this.client.keys(searchPattern);
      if (keys.length > 0) {
        await this.client.del(...keys);
      }
    } catch {
      // best-effort
    }
  }

  getStats(): CacheStats {
    const total = this.hits + this.misses;
    return {
      hits: this.hits,
      misses: this.misses,
      size: -1, // Exact size not tracked cheaply in Redis; use INFO or DBSIZE externally
      maxSize: this.maxEntries,
      hitRate: total === 0 ? 0 : this.hits / total,
    };
  }

  async healthCheck(): Promise<boolean> {
    try {
      const result = await this.client.ping();
      return result === 'PONG';
    } catch {
      return false;
    }
  }
}

export function createRedisCache(
  client: IRedisClient,
  config: CachingConfig,
  prefix?: string
): RedisCache {
  return new RedisCache(client, config, prefix);
}
