# Task 3: Build Model Caching System

## Overview

Implement a comprehensive model caching system with TTL support, invalidation strategies, persistent storage, and version history tracking for model metadata.

## Objectives

- Create ModelCache implementation with TTL and performance optimization
- Implement cache invalidation strategies for different scenarios
- Add persistent storage for model metadata with database integration
- Create version history tracking for model changes and rollbacks

## Implementation Steps

### Subtask 3.1: Create ModelCache Implementation with TTL

**Description**: Implement a high-performance in-memory cache with TTL support, LRU eviction, and Redis integration for distributed scenarios.

**Implementation Details**:

1. **Create Core Cache Implementation**:

```typescript
// packages/providers/src/cache/model-cache.ts
import {
  IModelCache,
  CacheSetOptions,
  CacheStats,
  CacheEntry,
  CacheEvictionEvent,
  CacheEventCallback,
  CacheEvictionCallback,
  UnsubscribeFunction,
} from '../interfaces/model-cache.interface';

import { Model, ModelFilter } from '../interfaces/model.types';
import { EventEmitter } from 'events';
import { Redis } from 'ioredis';
import { createHash } from 'crypto';

export class ModelCache extends EventEmitter implements IModelCache {
  private memoryCache: Map<string, CacheEntry<Model>> = new Map();
  private redis?: Redis;
  private config: CacheConfig;
  private cleanupTimer?: NodeJS.Timeout;
  private stats: CacheStats;
  private eventCallbacks: Map<string, Set<Function>> = new Map();

  constructor(config: Partial<CacheConfig> = {}) {
    super();
    this.config = this.mergeWithDefaults(config);
    this.stats = this.initializeStats();

    if (this.config.redis) {
      this.initializeRedis();
    }

    this.startCleanupTimer();
  }

  // Basic cache operations
  async get(modelId: string): Promise<Model | undefined> {
    const startTime = Date.now();

    try {
      // Try memory cache first
      let entry = this.memoryCache.get(modelId);

      if (entry) {
        // Check TTL
        if (this.isExpired(entry)) {
          this.memoryCache.delete(modelId);
          this.emit('cacheEviction', {
            key: modelId,
            reason: 'expired',
            entry,
            timestamp: new Date().toISOString(),
          });
        } else {
          // Update access statistics
          entry.lastAccessed = new Date().toISOString();
          entry.accessCount++;

          this.updateStats('hit', Date.now() - startTime);
          this.emit('cacheHit', modelId, entry);

          return entry.value;
        }
      }

      // Try Redis if available
      if (this.redis) {
        const redisEntry = await this.getFromRedis(modelId);
        if (redisEntry) {
          // Store in memory cache
          this.memoryCache.set(modelId, redisEntry);

          this.updateStats('hit', Date.now() - startTime);
          this.emit('cacheHit', modelId, redisEntry);

          return redisEntry.value;
        }
      }

      this.updateStats('miss', Date.now() - startTime);
      this.emit('cacheMiss', modelId);

      return undefined;
    } catch (error) {
      this.emit('cacheError', { operation: 'get', key: modelId, error });
      throw error;
    }
  }

  async set(modelId: string, model: Model, options: CacheSetOptions = {}): Promise<void> {
    const startTime = Date.now();

    try {
      const ttl = options.ttl || this.config.defaultTTL;
      const expiresAt = ttl ? new Date(Date.now() + ttl).toISOString() : undefined;

      const entry: CacheEntry<Model> = {
        key: modelId,
        value: model,
        createdAt: new Date().toISOString(),
        lastAccessed: new Date().toISOString(),
        ttl,
        expiresAt,
        accessCount: 0,
        tags: options.tags || [],
        priority: options.priority || 0,
        metadata: options.metadata || {},
      };

      // Store in memory cache
      this.memoryCache.set(modelId, entry);

      // Store in Redis if available
      if (this.redis) {
        await this.setToRedis(modelId, entry, ttl);
      }

      // Check memory usage and evict if necessary
      await this.checkMemoryUsage();

      this.updateStats('set', Date.now() - startTime);
      this.emit('cacheSet', modelId, entry);
    } catch (error) {
      this.emit('cacheError', { operation: 'set', key: modelId, error });
      throw error;
    }
  }

  async delete(modelId: string): Promise<boolean> {
    const startTime = Date.now();

    try {
      const memoryDeleted = this.memoryCache.delete(modelId);
      let redisDeleted = false;

      if (this.redis) {
        redisDeleted = await this.redis.del(this.getRedisKey(modelId));
      }

      const deleted = memoryDeleted || redisDeleted > 0;

      if (deleted) {
        this.updateStats('delete', Date.now() - startTime);
        this.emit('cacheDelete', modelId);
      }

      return deleted;
    } catch (error) {
      this.emit('cacheError', { operation: 'delete', key: modelId, error });
      throw error;
    }
  }

  async has(modelId: string): Promise<boolean> {
    const entry = this.memoryCache.get(modelId);
    if (entry && !this.isExpired(entry)) {
      return true;
    }

    if (this.redis) {
      const exists = await this.redis.exists(this.getRedisKey(modelId));
      return exists > 0;
    }

    return false;
  }

  // Batch operations
  async getMultiple(modelIds: string[]): Promise<Map<string, Model | undefined>> {
    const result = new Map<string, Model | undefined>();

    for (const modelId of modelIds) {
      try {
        const model = await this.get(modelId);
        result.set(modelId, model);
      } catch (error) {
        result.set(modelId, undefined);
        this.emit('cacheError', { operation: 'getMultiple', key: modelId, error });
      }
    }

    return result;
  }

  async setMultiple(models: Map<string, Model>, options: CacheSetOptions = {}): Promise<void> {
    const promises = Array.from(models.entries()).map(([modelId, model]) =>
      this.set(modelId, model, options).catch((error) => {
        this.emit('cacheError', { operation: 'setMultiple', key: modelId, error });
      })
    );

    await Promise.allSettled(promises);
  }

  async deleteMultiple(modelIds: string[]): Promise<number> {
    let deletedCount = 0;

    for (const modelId of modelIds) {
      try {
        const deleted = await this.delete(modelId);
        if (deleted) deletedCount++;
      } catch (error) {
        this.emit('cacheError', { operation: 'deleteMultiple', key: modelId, error });
      }
    }

    return deletedCount;
  }

  // Provider operations
  async getProviderModels(providerName: string): Promise<Model[]> {
    const providerPrefix = `provider:${providerName}:`;
    const models: Model[] = [];

    // Check memory cache
    for (const [key, entry] of this.memoryCache) {
      if (key.startsWith(providerPrefix) && !this.isExpired(entry)) {
        models.push(entry.value);
      }
    }

    // Check Redis if available
    if (this.redis) {
      try {
        const redisKeys = await this.redis.keys(`${this.getRedisKey(providerPrefix)}*`);
        if (redisKeys.length > 0) {
          const redisEntries = await this.redis.mget(redisKeys);
          for (const redisEntry of redisEntries) {
            if (redisEntry) {
              const entry = JSON.parse(redisEntry) as CacheEntry<Model>;
              if (!this.isExpired(entry)) {
                models.push(entry.value);
              }
            }
          }
        }
      } catch (error) {
        this.emit('cacheError', { operation: 'getProviderModels', key: providerName, error });
      }
    }

    return models;
  }

  async setProviderModels(
    providerName: string,
    models: Model[],
    options: CacheSetOptions = {}
  ): Promise<void> {
    const modelMap = new Map<string, Model>();

    for (const model of models) {
      const modelId = `provider:${providerName}:${model.id}`;
      modelMap.set(modelId, model);
    }

    await this.setMultiple(modelMap, options);
  }

  async invalidateProvider(providerName: string): Promise<void> {
    const providerPrefix = `provider:${providerName}:`;
    const keysToDelete: string[] = [];

    // Collect keys from memory cache
    for (const key of this.memoryCache.keys()) {
      if (key.startsWith(providerPrefix)) {
        keysToDelete.push(key);
      }
    }

    // Delete from memory cache
    for (const key of keysToDelete) {
      this.memoryCache.delete(key);
    }

    // Delete from Redis
    if (this.redis) {
      try {
        const redisKeys = await this.redis.keys(`${this.getRedisKey(providerPrefix)}*`);
        if (redisKeys.length > 0) {
          await this.redis.del(...redisKeys);
        }
      } catch (error) {
        this.emit('cacheError', { operation: 'invalidateProvider', key: providerName, error });
      }
    }

    this.emit('providerInvalidated', providerName, keysToDelete.length);
  }

  // Search and filtering
  async getAll(): Promise<Model[]> {
    const models: Model[] = [];

    for (const [key, entry] of this.memoryCache) {
      if (!this.isExpired(entry)) {
        models.push(entry.value);
      }
    }

    return models;
  }

  async search(filter: ModelFilter): Promise<Model[]> {
    const allModels = await this.getAll();
    return this.applyFilter(allModels, filter);
  }

  async getByCapability(capability: string): Promise<Model[]> {
    const allModels = await this.getAll();
    return allModels.filter(
      (model) => model.capabilities && Object.keys(model.capabilities).includes(capability)
    );
  }

  async count(filter?: ModelFilter): Promise<number> {
    const models = filter ? await this.search(filter) : await this.getAll();
    return models.length;
  }

  // TTL and expiration
  async getTTL(modelId: string): Promise<number | undefined> {
    const entry = this.memoryCache.get(modelId);
    if (entry && entry.expiresAt) {
      return new Date(entry.expiresAt).getTime() - Date.now();
    }

    if (this.redis) {
      try {
        const ttl = await this.redis.ttl(this.getRedisKey(modelId));
        return ttl > 0 ? ttl * 1000 : undefined; // Redis TTL is in seconds
      } catch (error) {
        this.emit('cacheError', { operation: 'getTTL', key: modelId, error });
      }
    }

    return undefined;
  }

  async setTTL(modelId: string, ttl: number): Promise<void> {
    const entry = this.memoryCache.get(modelId);
    if (entry) {
      entry.ttl = ttl;
      entry.expiresAt = new Date(Date.now() + ttl).toISOString();
    }

    if (this.redis) {
      try {
        await this.redis.expire(this.getRedisKey(modelId), Math.ceil(ttl / 1000));
      } catch (error) {
        this.emit('cacheError', { operation: 'setTTL', key: modelId, error });
      }
    }
  }

  async refresh(modelId: string): Promise<boolean> {
    const entry = this.memoryCache.get(modelId);
    if (entry) {
      entry.lastAccessed = new Date().toISOString();
      entry.accessCount++;

      if (this.redis) {
        try {
          await this.setToRedis(modelId, entry, entry.ttl);
        } catch (error) {
          this.emit('cacheError', { operation: 'refresh', key: modelId, error });
          return false;
        }
      }

      return true;
    }

    return false;
  }

  async refreshProvider(providerName: string): Promise<number> {
    const providerPrefix = `provider:${providerName}:`;
    let refreshedCount = 0;

    for (const [key, entry] of this.memoryCache) {
      if (key.startsWith(providerPrefix)) {
        entry.lastAccessed = new Date().toISOString();
        entry.accessCount++;
        refreshedCount++;

        if (this.redis) {
          try {
            await this.setToRedis(key, entry, entry.ttl);
          } catch (error) {
            this.emit('cacheError', { operation: 'refreshProvider', key, error });
          }
        }
      }
    }

    return refreshedCount;
  }

  // Cache management
  async clear(): Promise<void> {
    this.memoryCache.clear();

    if (this.redis) {
      try {
        const pattern = `${this.config.redis?.keyPrefix || 'model_cache'}:*`;
        const keys = await this.redis.keys(pattern);
        if (keys.length > 0) {
          await this.redis.del(...keys);
        }
      } catch (error) {
        this.emit('cacheError', { operation: 'clear', error });
      }
    }

    this.resetStats();
    this.emit('cacheCleared');
  }

  async cleanup(): Promise<number> {
    let cleanedCount = 0;
    const now = Date.now();

    // Clean memory cache
    for (const [key, entry] of this.memoryCache) {
      if (this.isExpired(entry)) {
        this.memoryCache.delete(key);
        cleanedCount++;

        this.emit('cacheEviction', {
          key,
          reason: 'expired',
          entry,
          timestamp: new Date().toISOString(),
        });
      }
    }

    // Clean Redis (handled automatically by Redis TTL)

    this.updateMemoryUsage();
    return cleanedCount;
  }

  async getStats(): Promise<CacheStats> {
    this.updateMemoryUsage();
    return { ...this.stats };
  }

  // Events
  onCacheHit(callback: CacheEventCallback): UnsubscribeFunction {
    return this.addEventListener('cacheHit', callback);
  }

  onCacheMiss(callback: CacheEventCallback): UnsubscribeFunction {
    return this.addEventListener('cacheMiss', callback);
  }

  onCacheEviction(callback: CacheEvictionCallback): UnsubscribeFunction {
    return this.addEventListener('cacheEviction', callback);
  }

  // Private helper methods
  private async initializeRedis(): Promise<void> {
    if (!this.config.redis) return;

    this.redis = new Redis(this.config.redis);

    this.redis.on('error', (error) => {
      this.emit('redisError', error);
    });

    this.redis.on('connect', () => {
      this.emit('redisConnected');
    });

    this.redis.on('disconnect', () => {
      this.emit('redisDisconnected');
    });
  }

  private async getFromRedis(modelId: string): Promise<CacheEntry<Model> | undefined> {
    if (!this.redis) return undefined;

    try {
      const data = await this.redis.get(this.getRedisKey(modelId));
      return data ? JSON.parse(data) : undefined;
    } catch (error) {
      this.emit('redisError', error);
      return undefined;
    }
  }

  private async setToRedis(modelId: string, entry: CacheEntry<Model>, ttl?: number): Promise<void> {
    if (!this.redis) return;

    try {
      const key = this.getRedisKey(modelId);
      const data = JSON.stringify(entry);
      const ttlSeconds = ttl ? Math.ceil(ttl / 1000) : this.config.redis?.defaultTTL;

      if (ttlSeconds) {
        await this.redis.setex(key, ttlSeconds, data);
      } else {
        await this.redis.set(key, data);
      }
    } catch (error) {
      this.emit('redisError', error);
      throw error;
    }
  }

  private getRedisKey(modelId: string): string {
    const prefix = this.config.redis?.keyPrefix || 'model_cache';
    return `${prefix}:${modelId}`;
  }

  private isExpired(entry: CacheEntry<Model>): boolean {
    if (!entry.expiresAt) return false;
    return new Date() >= new Date(entry.expiresAt);
  }

  private async checkMemoryUsage(): Promise<void> {
    const currentUsage = this.getMemoryUsage();
    const maxUsage = this.config.maxMemoryUsage || 100 * 1024 * 1024; // 100MB default

    if (currentUsage > maxUsage) {
      await this.evictLRU(currentUsage - maxUsage);
    }
  }

  private async evictLRU(bytesToFree: number): Promise<void> {
    const entries = Array.from(this.memoryCache.entries()).sort(
      ([, a], [, b]) => new Date(a.lastAccessed).getTime() - new Date(b.lastAccessed).getTime()
    );

    let freedBytes = 0;
    for (const [key, entry] of entries) {
      const entrySize = this.getEntrySize(entry);
      this.memoryCache.delete(key);
      freedBytes += entrySize;

      this.emit('cacheEviction', {
        key,
        reason: 'memory',
        entry,
        timestamp: new Date().toISOString(),
      });

      if (freedBytes >= bytesToFree) {
        break;
      }
    }
  }

  private getEntrySize(entry: CacheEntry<Model>): number {
    return JSON.stringify(entry).length * 2; // Rough estimate
  }

  private getMemoryUsage(): number {
    let totalSize = 0;
    for (const entry of this.memoryCache.values()) {
      totalSize += this.getEntrySize(entry);
    }
    return totalSize;
  }

  private updateMemoryUsage(): void {
    this.stats.memoryUsage = this.getMemoryUsage();
  }

  private applyFilter(models: Model[], filter: ModelFilter): Model[] {
    return models.filter((model) => {
      if (filter.provider && model.providerName !== filter.provider) {
        return false;
      }

      if (
        filter.capability &&
        (!model.capabilities || !Object.keys(model.capabilities).includes(filter.capability))
      ) {
        return false;
      }

      if (filter.name && !model.name.toLowerCase().includes(filter.name.toLowerCase())) {
        return false;
      }

      if (filter.minTokens && (!model.maxTokens || model.maxTokens < filter.minTokens)) {
        return false;
      }

      if (filter.maxTokens && (!model.maxTokens || model.maxTokens > filter.maxTokens)) {
        return false;
      }

      if (filter.tags && filter.tags.length > 0) {
        const modelTags = model.tags || [];
        const hasAllTags = filter.tags.every((tag) => modelTags.includes(tag));
        if (!hasAllTags) return false;
      }

      return true;
    });
  }

  private startCleanupTimer(): void {
    if (this.config.cleanupInterval > 0) {
      this.cleanupTimer = setInterval(async () => {
        try {
          await this.cleanup();
        } catch (error) {
          this.emit('cleanupError', error);
        }
      }, this.config.cleanupInterval);
    }
  }

  private addEventListener(event: string, callback: Function): UnsubscribeFunction {
    if (!this.eventCallbacks.has(event)) {
      this.eventCallbacks.set(event, new Set());
    }

    this.eventCallbacks.get(event)!.add(callback);

    return () => {
      const callbacks = this.eventCallbacks.get(event);
      if (callbacks) {
        callbacks.delete(callback);
        if (callbacks.size === 0) {
          this.eventCallbacks.delete(event);
        }
      }
    };
  }

  private updateStats(operation: 'hit' | 'miss' | 'set' | 'delete', duration: number): void {
    switch (operation) {
      case 'hit':
        this.stats.hitCount++;
        this.stats.totalRequests++;
        break;
      case 'miss':
        this.stats.missCount++;
        this.stats.totalRequests++;
        break;
      case 'set':
        this.stats.totalEntries = this.memoryCache.size;
        break;
      case 'delete':
        this.stats.totalEntries = this.memoryCache.size;
        break;
    }

    // Update hit rate
    this.stats.hitRate =
      this.stats.totalRequests > 0 ? this.stats.hitCount / this.stats.totalRequests : 0;
  }

  private resetStats(): void {
    this.stats = this.initializeStats();
  }

  private initializeStats(): CacheStats {
    return {
      totalEntries: 0,
      memoryUsage: 0,
      hitCount: 0,
      missCount: 0,
      totalRequests: 0,
      hitRate: 0,
      evictionCount: 0,
      lastCleanup: new Date().toISOString(),
      entriesByProvider: {},
      entriesByTag: {},
    };
  }

  private mergeWithDefaults(config: Partial<CacheConfig>): CacheConfig {
    return {
      defaultTTL: 600000, // 10 minutes
      maxMemoryUsage: 100 * 1024 * 1024, // 100MB
      cleanupInterval: 60000, // 1 minute
      redis: config.redis || undefined,
      ...config,
    };
  }

  // Cleanup
  async dispose(): Promise<void> {
    if (this.cleanupTimer) {
      clearInterval(this.cleanupTimer);
    }

    if (this.redis) {
      await this.redis.quit();
    }

    this.memoryCache.clear();
    this.eventCallbacks.clear();
    this.removeAllListeners();
  }
}

export interface CacheConfig {
  defaultTTL: number;
  maxMemoryUsage: number;
  cleanupInterval: number;
  redis?: {
    host: string;
    port: number;
    password?: string;
    db?: number;
    keyPrefix?: string;
    defaultTTL?: number;
    maxRetries?: number;
    retryDelay?: number;
  };
}
```

### Subtask 3.2: Implement Cache Invalidation Strategies

**Description**: Create sophisticated cache invalidation strategies including tag-based, time-based, and event-driven invalidation.

**Implementation Details**:

1. **Create Invalidation Strategy Manager**:

```typescript
// packages/providers/src/cache/invalidation-manager.ts
import { IModelCache, CacheEntry } from '../interfaces/model-cache.interface';
import { Model } from '../interfaces/model.types';
import { EventEmitter } from 'events';

export interface InvalidationStrategy {
  name: string;
  shouldInvalidate(entry: CacheEntry<Model>, context: InvalidationContext): boolean;
  invalidate(cache: IModelCache, entries: CacheEntry<Model>[]): Promise<void>;
}

export interface InvalidationContext {
  reason: InvalidationReason;
  timestamp: string;
  metadata?: Record<string, any>;
  providerName?: string;
  modelId?: string;
  tags?: string[];
}

export enum InvalidationReason {
  TTL_EXPIRED = 'ttl_expired',
  MEMORY_PRESSURE = 'memory_pressure',
  PROVIDER_UPDATE = 'provider_update',
  MODEL_UPDATE = 'model_update',
  TAG_INVALIDATION = 'tag_invalidation',
  MANUAL_INVALIDATION = 'manual_invalidation',
  VERSION_CHANGE = 'version_change',
  CAPABILITY_CHANGE = 'capability_change',
  CONFIGURATION_CHANGE = 'configuration_change',
}

export interface InvalidationRule {
  id: string;
  name: string;
  strategy: string;
  conditions: InvalidationCondition[];
  enabled: boolean;
  priority: number;
  actions: InvalidationAction[];
}

export interface InvalidationCondition {
  field: string;
  operator: InvalidationOperator;
  value: any;
  type: 'entry' | 'metadata' | 'context';
}

export enum InvalidationOperator {
  EQUALS = 'equals',
  NOT_EQUALS = 'not_equals',
  CONTAINS = 'contains',
  NOT_CONTAINS = 'not_contains',
  GREATER_THAN = 'greater_than',
  LESS_THAN = 'less_than',
  IN = 'in',
  NOT_IN = 'not_in',
  REGEX = 'regex',
}

export interface InvalidationAction {
  type: 'invalidate' | 'refresh' | 'extend_ttl' | 'tag' | 'notify';
  parameters: Record<string, any>;
}

export class InvalidationManager extends EventEmitter {
  private strategies: Map<string, InvalidationStrategy> = new Map();
  private rules: Map<string, InvalidationRule> = new Map();
  private invalidationHistory: InvalidationEvent[] = [];
  private isProcessing: boolean = false;

  constructor(private cache: IModelCache) {
    super();
    this.initializeDefaultStrategies();
  }

  // Strategy management
  registerStrategy(strategy: InvalidationStrategy): void {
    this.strategies.set(strategy.name, strategy);
    this.emit('strategyRegistered', strategy);
  }

  unregisterStrategy(strategyName: string): void {
    this.strategies.delete(strategyName);
    this.emit('strategyUnregistered', strategyName);
  }

  getStrategy(strategyName: string): InvalidationStrategy | undefined {
    return this.strategies.get(strategyName);
  }

  getAllStrategies(): InvalidationStrategy[] {
    return Array.from(this.strategies.values());
  }

  // Rule management
  addRule(rule: InvalidationRule): void {
    this.rules.set(rule.id, rule);
    this.emit('ruleAdded', rule);
  }

  removeRule(ruleId: string): void {
    this.rules.delete(ruleId);
    this.emit('ruleRemoved', ruleId);
  }

  updateRule(ruleId: string, updates: Partial<InvalidationRule>): void {
    const rule = this.rules.get(ruleId);
    if (rule) {
      const updatedRule = { ...rule, ...updates };
      this.rules.set(ruleId, updatedRule);
      this.emit('ruleUpdated', updatedRule);
    }
  }

  getRule(ruleId: string): InvalidationRule | undefined {
    return this.rules.get(ruleId);
  }

  getAllRules(): InvalidationRule[] {
    return Array.from(this.rules.values());
  }

  // Invalidation operations
  async invalidate(context: InvalidationContext): Promise<InvalidationResult> {
    if (this.isProcessing) {
      return {
        success: false,
        reason: 'Invalidation already in progress',
        invalidatedCount: 0,
      };
    }

    this.isProcessing = true;
    const startTime = Date.now();

    try {
      const allModels = await this.cache.getAll();
      const entries = await this.convertToEntries(allModels);
      const entriesToInvalidate: CacheEntry<Model>[] = [];

      // Apply strategies
      for (const strategy of this.strategies.values()) {
        for (const entry of entries) {
          if (strategy.shouldInvalidate(entry, context)) {
            entriesToInvalidate.push(entry);
          }
        }
      }

      // Apply rules
      for (const rule of this.rules.values()) {
        if (rule.enabled) {
          const matchingEntries = await this.applyRule(rule, entries, context);
          entriesToInvalidate.push(...matchingEntries);
        }
      }

      // Remove duplicates
      const uniqueEntries = Array.from(new Set(entriesToInvalidate));

      // Perform invalidation
      if (uniqueEntries.length > 0) {
        await this.performInvalidation(uniqueEntries, context);
      }

      const duration = Date.now() - startTime;
      const result: InvalidationResult = {
        success: true,
        invalidatedCount: uniqueEntries.length,
        duration,
        reason: context.reason,
      };

      // Record event
      this.recordInvalidationEvent(context, result);

      this.emit('invalidationCompleted', result);
      return result;
    } catch (error) {
      const result: InvalidationResult = {
        success: false,
        reason: error.message,
        invalidatedCount: 0,
        duration: Date.now() - startTime,
      };

      this.emit('invalidationFailed', result);
      return result;
    } finally {
      this.isProcessing = false;
    }
  }

  async invalidateByTags(
    tags: string[],
    reason: InvalidationReason = InvalidationReason.TAG_INVALIDATION
  ): Promise<InvalidationResult> {
    const context: InvalidationContext = {
      reason,
      timestamp: new Date().toISOString(),
      tags,
    };

    return this.invalidate(context);
  }

  async invalidateByProvider(
    providerName: string,
    reason: InvalidationReason = InvalidationReason.PROVIDER_UPDATE
  ): Promise<InvalidationResult> {
    const context: InvalidationContext = {
      reason,
      timestamp: new Date().toISOString(),
      providerName,
    };

    return this.invalidate(context);
  }

  async invalidateByModel(
    modelId: string,
    reason: InvalidationReason = InvalidationReason.MODEL_UPDATE
  ): Promise<InvalidationResult> {
    const context: InvalidationContext = {
      reason,
      timestamp: new Date().toISOString(),
      modelId,
    };

    return this.invalidate(context);
  }

  async invalidateExpired(): Promise<InvalidationResult> {
    const context: InvalidationContext = {
      reason: InvalidationReason.TTL_EXPIRED,
      timestamp: new Date().toISOString(),
    };

    return this.invalidate(context);
  }

  // History and statistics
  getInvalidationHistory(filter?: InvalidationHistoryFilter): InvalidationEvent[] {
    let history = [...this.invalidationHistory];

    if (filter) {
      if (filter.reason) {
        history = history.filter((event) => event.reason === filter.reason);
      }
      if (filter.startTime) {
        history = history.filter((event) => new Date(event.timestamp) >= filter.startTime!);
      }
      if (filter.endTime) {
        history = history.filter((event) => new Date(event.timestamp) <= filter.endTime!);
      }
      if (filter.providerName) {
        history = history.filter((event) => event.context.providerName === filter.providerName);
      }
    }

    return history.sort(
      (a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime()
    );
  }

  getInvalidationStats(): InvalidationStats {
    const history = this.invalidationHistory;
    const now = new Date();
    const last24h = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    const last7d = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);

    const last24hEvents = history.filter((event) => new Date(event.timestamp) >= last24h);
    const last7dEvents = history.filter((event) => new Date(event.timestamp) >= last7d);

    const reasonCounts = history.reduce(
      (counts, event) => {
        counts[event.reason] = (counts[event.reason] || 0) + 1;
        return counts;
      },
      {} as Record<InvalidationReason, number>
    );

    const providerCounts = history.reduce(
      (counts, event) => {
        const provider = event.context.providerName || 'unknown';
        counts[provider] = (counts[provider] || 0) + 1;
        return counts;
      },
      {} as Record<string, number>
    );

    return {
      totalInvalidations: history.length,
      last24hInvalidations: last24hEvents.length,
      last7dInvalidations: last7dEvents.length,
      averageInvalidationTime:
        history.reduce((sum, event) => sum + (event.result.duration || 0), 0) / history.length || 0,
      successRate: history.filter((event) => event.result.success).length / history.length || 0,
      invalidationsByReason: reasonCounts,
      invalidationsByProvider: providerCounts,
      mostCommonReason: this.getMostCommon(reasonCounts),
      mostActiveProvider: this.getMostCommon(providerCounts),
    };
  }

  // Private helper methods
  private initializeDefaultStrategies(): void {
    // TTL strategy
    this.registerStrategy(new TTLInvalidationStrategy());

    // Memory pressure strategy
    this.registerStrategy(new MemoryPressureStrategy());

    // Provider update strategy
    this.registerStrategy(new ProviderUpdateStrategy());

    // Tag-based strategy
    this.registerStrategy(new TagInvalidationStrategy());

    // Version change strategy
    this.registerStrategy(new VersionChangeStrategy());
  }

  private async convertToEntries(models: Model[]): Promise<CacheEntry<Model>[]> {
    const entries: CacheEntry<Model>[] = [];

    for (const model of models) {
      // This is a simplified conversion - in reality, you'd get actual cache entries
      entries.push({
        key: model.id,
        value: model,
        createdAt: model.discoveredAt || new Date().toISOString(),
        lastAccessed: new Date().toISOString(),
        ttl: undefined,
        accessCount: 0,
        tags: model.tags || [],
        priority: 0,
        metadata: {},
      });
    }

    return entries;
  }

  private async applyRule(
    rule: InvalidationRule,
    entries: CacheEntry<Model>[],
    context: InvalidationContext
  ): Promise<CacheEntry<Model>[]> {
    const matchingEntries: CacheEntry<Model>[] = [];

    for (const entry of entries) {
      if (this.evaluateConditions(rule.conditions, entry, context)) {
        matchingEntries.push(entry);
      }
    }

    return matchingEntries;
  }

  private evaluateConditions(
    conditions: InvalidationCondition[],
    entry: CacheEntry<Model>,
    context: InvalidationContext
  ): boolean {
    for (const condition of conditions) {
      if (!this.evaluateCondition(condition, entry, context)) {
        return false;
      }
    }
    return true;
  }

  private evaluateCondition(
    condition: InvalidationCondition,
    entry: CacheEntry<Model>,
    context: InvalidationContext
  ): boolean {
    let value: any;

    // Get value based on condition type
    switch (condition.type) {
      case 'entry':
        value = (entry as any)[condition.field];
        break;
      case 'metadata':
        value = entry.metadata[condition.field];
        break;
      case 'context':
        value = (context as any)[condition.field];
        break;
    }

    // Apply operator
    switch (condition.operator) {
      case InvalidationOperator.EQUALS:
        return value === condition.value;
      case InvalidationOperator.NOT_EQUALS:
        return value !== condition.value;
      case InvalidationOperator.CONTAINS:
        return typeof value === 'string' && value.includes(condition.value);
      case InvalidationOperator.NOT_CONTAINS:
        return typeof value === 'string' && !value.includes(condition.value);
      case InvalidationOperator.GREATER_THAN:
        return typeof value === 'number' && value > condition.value;
      case InvalidationOperator.LESS_THAN:
        return typeof value === 'number' && value < condition.value;
      case InvalidationOperator.IN:
        return Array.isArray(condition.value) && condition.value.includes(value);
      case InvalidationOperator.NOT_IN:
        return Array.isArray(condition.value) && !condition.value.includes(value);
      case InvalidationOperator.REGEX:
        return typeof value === 'string' && new RegExp(condition.value).test(value);
      default:
        return false;
    }
  }

  private async performInvalidation(
    entries: CacheEntry<Model>[],
    context: InvalidationContext
  ): Promise<void> {
    for (const entry of entries) {
      try {
        await this.cache.delete(entry.key);
      } catch (error) {
        this.emit('invalidationError', { entry, error });
      }
    }
  }

  private recordInvalidationEvent(context: InvalidationContext, result: InvalidationResult): void {
    const event: InvalidationEvent = {
      id: this.generateEventId(),
      timestamp: new Date().toISOString(),
      context,
      result,
    };

    this.invalidationHistory.push(event);

    // Trim history (keep last 1000 events)
    if (this.invalidationHistory.length > 1000) {
      this.invalidationHistory = this.invalidationHistory.slice(-1000);
    }
  }

  private getMostCommon(counts: Record<string, number>): string | undefined {
    const entries = Object.entries(counts);
    if (entries.length === 0) return undefined;

    return entries.reduce((max, [key, count]) => (count > max.count ? { key, count } : max), {
      key: '',
      count: 0,
    }).key;
  }

  private generateEventId(): string {
    return `inv_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }
}

// Built-in invalidation strategies
class TTLInvalidationStrategy implements InvalidationStrategy {
  name = 'ttl';

  shouldInvalidate(entry: CacheEntry<Model>, context: InvalidationContext): boolean {
    if (context.reason !== InvalidationReason.TTL_EXPIRED) {
      return false;
    }

    if (!entry.expiresAt) {
      return false;
    }

    return new Date() >= new Date(entry.expiresAt);
  }

  async invalidate(cache: IModelCache, entries: CacheEntry<Model>[]): Promise<void> {
    for (const entry of entries) {
      await cache.delete(entry.key);
    }
  }
}

class MemoryPressureStrategy implements InvalidationStrategy {
  name = 'memory_pressure';

  shouldInvalidate(entry: CacheEntry<Model>, context: InvalidationContext): boolean {
    if (context.reason !== InvalidationReason.MEMORY_PRESSURE) {
      return false;
    }

    // Prioritize entries with low access count and old last access time
    const now = new Date();
    const daysSinceLastAccess =
      (now.getTime() - new Date(entry.lastAccessed).getTime()) / (1000 * 60 * 60 * 24);

    return entry.accessCount < 2 && daysSinceLastAccess > 1;
  }

  async invalidate(cache: IModelCache, entries: CacheEntry<Model>[]): Promise<void> {
    // Sort by priority (lower priority = higher chance of eviction)
    const sortedEntries = entries.sort((a, b) => a.priority - b.priority);

    for (const entry of sortedEntries) {
      await cache.delete(entry.key);
    }
  }
}

class ProviderUpdateStrategy implements InvalidationStrategy {
  name = 'provider_update';

  shouldInvalidate(entry: CacheEntry<Model>, context: InvalidationContext): boolean {
    if (context.reason !== InvalidationReason.PROVIDER_UPDATE) {
      return false;
    }

    if (!context.providerName) {
      return false;
    }

    // Check if entry belongs to the specified provider
    return entry.key.startsWith(`provider:${context.providerName}:`);
  }

  async invalidate(cache: IModelCache, entries: CacheEntry<Model>[]): Promise<void> {
    for (const entry of entries) {
      await cache.delete(entry.key);
    }
  }
}

class TagInvalidationStrategy implements InvalidationStrategy {
  name = 'tag_invalidation';

  shouldInvalidate(entry: CacheEntry<Model>, context: InvalidationContext): boolean {
    if (context.reason !== InvalidationReason.TAG_INVALIDATION) {
      return false;
    }

    if (!context.tags || context.tags.length === 0) {
      return false;
    }

    // Check if entry has any of the specified tags
    return entry.tags.some((tag) => context.tags!.includes(tag));
  }

  async invalidate(cache: IModelCache, entries: CacheEntry<Model>[]): Promise<void> {
    for (const entry of entries) {
      await cache.delete(entry.key);
    }
  }
}

class VersionChangeStrategy implements InvalidationStrategy {
  name = 'version_change';

  shouldInvalidate(entry: CacheEntry<Model>, context: InvalidationContext): boolean {
    if (context.reason !== InvalidationReason.VERSION_CHANGE) {
      return false;
    }

    // Invalidate if model version is different from expected version
    const expectedVersion = context.metadata?.expectedVersion;
    const currentVersion = entry.value.version;

    return expectedVersion && currentVersion !== expectedVersion;
  }

  async invalidate(cache: IModelCache, entries: CacheEntry<Model>[]): Promise<void> {
    for (const entry of entries) {
      await cache.delete(entry.key);
    }
  }
}

// Supporting interfaces
export interface InvalidationResult {
  success: boolean;
  reason?: string;
  invalidatedCount: number;
  duration: number;
}

export interface InvalidationEvent {
  id: string;
  timestamp: string;
  context: InvalidationContext;
  result: InvalidationResult;
}

export interface InvalidationHistoryFilter {
  reason?: InvalidationReason;
  startTime?: Date;
  endTime?: Date;
  providerName?: string;
}

export interface InvalidationStats {
  totalInvalidations: number;
  last24hInvalidations: number;
  last7dInvalidations: number;
  averageInvalidationTime: number;
  successRate: number;
  invalidationsByReason: Record<InvalidationReason, number>;
  invalidationsByProvider: Record<string, number>;
  mostCommonReason?: string;
  mostActiveProvider?: string;
}
```

### Subtask 3.3: Add Persistent Storage for Model Metadata

**Description**: Implement persistent storage using PostgreSQL for model metadata with schema management and migration support.

**Implementation Details**:

1. **Create Persistent Storage Manager**:

```typescript
// packages/providers/src/storage/persistent-storage.ts
import { Model, ModelCapabilities } from '../interfaces/model.types';
import { Pool, PoolClient } from 'pg';
import { EventEmitter } from 'events';
import { createHash } from 'crypto';

export interface StorageConfig {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
  ssl?: boolean;
  maxConnections?: number;
  idleTimeoutMillis?: number;
  connectionTimeoutMillis?: number;
}

export interface ModelRecord {
  id: string;
  provider_name: string;
  model_name: string;
  display_name: string;
  description?: string;
  max_tokens: number;
  input_cost_per_1k: number;
  output_cost_per_1k: number;
  supports_streaming: boolean;
  supports_function_calling: boolean;
  supports_vision: boolean;
  capabilities: ModelCapabilities;
  tags: string[];
  metadata: Record<string, any>;
  version: string;
  discovered_at: string;
  updated_at: string;
  created_at: string;
  is_active: boolean;
  is_deprecated: boolean;
  deprecation_date?: string;
  alternatives: string[];
}

export interface ModelVersion {
  id: string;
  model_id: string;
  version: string;
  changes: ModelChange[];
  created_at: string;
  created_by: string;
  rollback_available: boolean;
}

export interface ModelChange {
  field: string;
  old_value: any;
  new_value: any;
  change_type: 'create' | 'update' | 'delete';
}

export class PersistentStorage extends EventEmitter {
  private pool: Pool;
  private config: StorageConfig;

  constructor(config: StorageConfig) {
    super();
    this.config = config;
    this.pool = new Pool(config);

    this.pool.on('error', (error) => {
      this.emit('storageError', error);
    });

    this.pool.on('connect', (client) => {
      this.emit('clientConnected', client);
    });

    this.pool.on('remove', (client) => {
      this.emit('clientRemoved', client);
    });
  }

  async initialize(): Promise<void> {
    const client = await this.pool.connect();

    try {
      await this.createTables(client);
      await this.createIndexes(client);
      await this.setupTriggers(client);
      this.emit('storageInitialized');
    } finally {
      client.release();
    }
  }

  // Model operations
  async saveModel(model: Model, version?: string): Promise<void> {
    const client = await this.pool.connect();

    try {
      await client.query('BEGIN');

      // Check if model exists
      const existingResult = await client.query('SELECT id, version FROM models WHERE id = $1', [
        model.id,
      ]);

      const isNew = existingResult.rows.length === 0;
      const currentVersion = existingResult.rows[0]?.version;
      const newVersion = version || this.generateVersion();

      if (isNew) {
        // Insert new model
        await this.insertModel(client, model, newVersion);
      } else {
        // Update existing model
        await this.updateModel(client, model, newVersion);

        // Create version record if version changed
        if (currentVersion !== newVersion) {
          await this.createVersionRecord(client, model.id, currentVersion, newVersion);
        }
      }

      await client.query('COMMIT');

      this.emit('modelSaved', { model, version: newVersion, isNew });
    } catch (error) {
      await client.query('ROLLBACK');
      this.emit('modelSaveError', { model, error });
      throw error;
    } finally {
      client.release();
    }
  }

  async getModel(modelId: string): Promise<Model | undefined> {
    const client = await this.pool.connect();

    try {
      const result = await client.query(`SELECT * FROM models WHERE id = $1 AND is_active = true`, [
        modelId,
      ]);

      if (result.rows.length === 0) {
        return undefined;
      }

      return this.mapRowToModel(result.rows[0]);
    } finally {
      client.release();
    }
  }

  async getModelsByProvider(providerName: string): Promise<Model[]> {
    const client = await this.pool.connect();

    try {
      const result = await client.query(
        `SELECT * FROM models WHERE provider_name = $1 AND is_active = true ORDER BY created_at DESC`,
        [providerName]
      );

      return result.rows.map((row) => this.mapRowToModel(row));
    } finally {
      client.release();
    }
  }

  async getAllModels(activeOnly: boolean = true): Promise<Model[]> {
    const client = await this.pool.connect();

    try {
      const query = activeOnly
        ? `SELECT * FROM models WHERE is_active = true ORDER BY provider_name, model_name`
        : `SELECT * FROM models ORDER BY provider_name, model_name`;

      const result = await client.query(query);

      return result.rows.map((row) => this.mapRowToModel(row));
    } finally {
      client.release();
    }
  }

  async searchModels(criteria: ModelSearchCriteria): Promise<Model[]> {
    const client = await this.pool.connect();

    try {
      let query = `SELECT * FROM models WHERE is_active = true`;
      const params: any[] = [];
      let paramIndex = 1;

      // Build WHERE clause
      if (criteria.providerName) {
        query += ` AND provider_name = $${paramIndex++}`;
        params.push(criteria.providerName);
      }

      if (criteria.capability) {
        query += ` AND capabilities->>${paramIndex++} = 'true'`;
        params.push(criteria.capability);
      }

      if (criteria.tags && criteria.tags.length > 0) {
        query += ` AND tags && $${paramIndex++}`;
        params.push(criteria.tags);
      }

      if (criteria.minTokens) {
        query += ` AND max_tokens >= $${paramIndex++}`;
        params.push(criteria.minTokens);
      }

      if (criteria.maxTokens) {
        query += ` AND max_tokens <= $${paramIndex++}`;
        params.push(criteria.maxTokens);
      }

      if (criteria.searchTerm) {
        query += ` AND (model_name ILIKE $${paramIndex++} OR display_name ILIKE $${paramIndex++})`;
        params.push(`%${criteria.searchTerm}%`, `%${criteria.searchTerm}%`);
      }

      if (criteria.includeDeprecated === false) {
        query += ` AND is_deprecated = false`;
      }

      query += ` ORDER BY provider_name, model_name`;

      if (criteria.limit) {
        query += ` LIMIT $${paramIndex++}`;
        params.push(criteria.limit);
      }

      if (criteria.offset) {
        query += ` OFFSET $${paramIndex++}`;
        params.push(criteria.offset);
      }

      const result = await client.query(query, params);

      return result.rows.map((row) => this.mapRowToModel(row));
    } finally {
      client.release();
    }
  }

  async deleteModel(modelId: string): Promise<boolean> {
    const client = await this.pool.connect();

    try {
      await client.query('BEGIN');

      // Soft delete (set is_active = false)
      const result = await client.query(
        `UPDATE models SET is_active = false, updated_at = NOW() WHERE id = $1`,
        [modelId]
      );

      await client.query('COMMIT');

      const deleted = result.rowCount > 0;

      if (deleted) {
        this.emit('modelDeleted', { modelId });
      }

      return deleted;
    } catch (error) {
      await client.query('ROLLBACK');
      this.emit('modelDeleteError', { modelId, error });
      throw error;
    } finally {
      client.release();
    }
  }

  // Version operations
  async getModelVersions(modelId: string): Promise<ModelVersion[]> {
    const client = await this.pool.connect();

    try {
      const result = await client.query(
        `SELECT * FROM model_versions WHERE model_id = $1 ORDER BY created_at DESC`,
        [modelId]
      );

      return result.rows.map((row) => ({
        id: row.id,
        modelId: row.model_id,
        version: row.version,
        changes: row.changes,
        createdAt: row.created_at,
        createdBy: row.created_by,
        rollbackAvailable: row.rollback_available,
      }));
    } finally {
      client.release();
    }
  }

  async rollbackModel(modelId: string, targetVersion: string): Promise<boolean> {
    const client = await this.pool.connect();

    try {
      await client.query('BEGIN');

      // Get version record
      const versionResult = await client.query(
        `SELECT * FROM model_versions WHERE model_id = $1 AND version = $2 AND rollback_available = true`,
        [modelId, targetVersion]
      );

      if (versionResult.rows.length === 0) {
        await client.query('ROLLBACK');
        return false;
      }

      const version = versionResult.rows[0];

      // Restore model to target version
      await this.restoreModelVersion(client, modelId, version);

      // Create new version record for the rollback
      await this.createVersionRecord(
        client,
        modelId,
        this.getCurrentVersion(modelId),
        targetVersion
      );

      await client.query('COMMIT');

      this.emit('modelRolledBack', { modelId, targetVersion });
      return true;
    } catch (error) {
      await client.query('ROLLBACK');
      this.emit('modelRollbackError', { modelId, targetVersion, error });
      throw error;
    } finally {
      client.release();
    }
  }

  // Statistics and analytics
  async getModelStats(): Promise<ModelStats> {
    const client = await this.pool.connect();

    try {
      const totalResult = await client.query(
        `SELECT COUNT(*) as count FROM models WHERE is_active = true`
      );
      const providerResult = await client.query(
        `SELECT provider_name, COUNT(*) as count FROM models WHERE is_active = true GROUP BY provider_name`
      );
      const capabilityResult = await client.query(
        `SELECT jsonb_object_keys(capabilities) as capability, COUNT(*) as count FROM models WHERE is_active = true GROUP BY capability`
      );
      const recentResult = await client.query(
        `SELECT COUNT(*) as count FROM models WHERE created_at >= NOW() - INTERVAL '7 days' AND is_active = true`
      );

      return {
        totalModels: parseInt(totalResult.rows[0].count),
        modelsByProvider: providerResult.rows.reduce(
          (acc, row) => {
            acc[row.provider_name] = parseInt(row.count);
            return acc;
          },
          {} as Record<string, number>
        ),
        modelsByCapability: capabilityResult.rows.reduce(
          (acc, row) => {
            acc[row.capability] = parseInt(row.count);
            return acc;
          },
          {} as Record<string, number>
        ),
        modelsAddedLast7Days: parseInt(recentResult.rows[0].count),
      };
    } finally {
      client.release();
    }
  }

  // Health and maintenance
  async healthCheck(): Promise<StorageHealth> {
    const client = await this.pool.connect();

    try {
      const startTime = Date.now();
      await client.query('SELECT 1');
      const responseTime = Date.now() - startTime;

      const poolStats = await this.pool.query(`
        SELECT 
          COUNT(*) as total_connections,
          COUNT(*) FILTER (WHERE state = 'active') as active_connections,
          COUNT(*) FILTER (WHERE state = 'idle') as idle_connections
        FROM pg_stat_activity
        WHERE datname = current_database()
      `);

      return {
        status: 'healthy',
        responseTime,
        totalConnections: parseInt(poolStats.rows[0].total_connections),
        activeConnections: parseInt(poolStats.rows[0].active_connections),
        idleConnections: parseInt(poolStats.rows[0].idle_connections),
        lastCheck: new Date().toISOString(),
      };
    } catch (error) {
      return {
        status: 'unhealthy',
        responseTime: 0,
        totalConnections: 0,
        activeConnections: 0,
        idleConnections: 0,
        lastCheck: new Date().toISOString(),
        error: error.message,
      };
    } finally {
      client.release();
    }
  }

  async cleanup(): Promise<CleanupResult> {
    const client = await this.pool.connect();

    try {
      await client.query('BEGIN');

      // Clean up old versions (keep last 10 per model)
      const versionCleanupResult = await client.query(`
        DELETE FROM model_versions 
        WHERE id NOT IN (
          SELECT id FROM (
            SELECT id, ROW_NUMBER() OVER (PARTITION BY model_id ORDER BY created_at DESC) as rn
            FROM model_versions
          ) t WHERE rn <= 10
        )
      `);

      // Clean up old inactive models (older than 30 days)
      const modelCleanupResult = await client.query(`
        DELETE FROM models 
        WHERE is_active = false AND updated_at < NOW() - INTERVAL '30 days'
      `);

      await client.query('COMMIT');

      const result: CleanupResult = {
        versionsDeleted: parseInt(versionCleanupResult.rowCount),
        modelsDeleted: parseInt(modelCleanupResult.rowCount),
        duration: 0,
        timestamp: new Date().toISOString(),
      };

      this.emit('cleanupCompleted', result);
      return result;
    } catch (error) {
      await client.query('ROLLBACK');
      this.emit('cleanupError', error);
      throw error;
    } finally {
      client.release();
    }
  }

  // Private helper methods
  private async createTables(client: PoolClient): Promise<void> {
    // Models table
    await client.query(`
      CREATE TABLE IF NOT EXISTS models (
        id VARCHAR(255) PRIMARY KEY,
        provider_name VARCHAR(100) NOT NULL,
        model_name VARCHAR(255) NOT NULL,
        display_name VARCHAR(255) NOT NULL,
        description TEXT,
        max_tokens INTEGER NOT NULL,
        input_cost_per_1k DECIMAL(10, 6) NOT NULL,
        output_cost_per_1k DECIMAL(10, 6) NOT NULL,
        supports_streaming BOOLEAN DEFAULT false,
        supports_function_calling BOOLEAN DEFAULT false,
        supports_vision BOOLEAN DEFAULT false,
        capabilities JSONB NOT NULL DEFAULT '{}',
        tags TEXT[] DEFAULT '{}',
        metadata JSONB NOT NULL DEFAULT '{}',
        version VARCHAR(50) NOT NULL,
        discovered_at TIMESTAMP WITH TIME ZONE NOT NULL,
        updated_at TIMESTAMP WITH TIME ZONE NOT NULL,
        created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
        is_active BOOLEAN DEFAULT true,
        is_deprecated BOOLEAN DEFAULT false,
        deprecation_date TIMESTAMP WITH TIME ZONE,
        alternatives TEXT[] DEFAULT '{}'
      )
    `);

    // Model versions table
    await client.query(`
      CREATE TABLE IF NOT EXISTS model_versions (
        id VARCHAR(255) PRIMARY KEY,
        model_id VARCHAR(255) NOT NULL REFERENCES models(id) ON DELETE CASCADE,
        version VARCHAR(50) NOT NULL,
        changes JSONB NOT NULL DEFAULT '[]',
        created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
        created_by VARCHAR(100) NOT NULL,
        rollback_available BOOLEAN DEFAULT true
      )
    `);

    // Create indexes
    await this.createIndexes(client);
  }

  private async createIndexes(client: PoolClient): Promise<void> {
    const indexes = [
      'CREATE INDEX IF NOT EXISTS idx_models_provider_name ON models(provider_name)',
      'CREATE INDEX IF NOT EXISTS idx_models_is_active ON models(is_active)',
      'CREATE INDEX IF NOT EXISTS idx_models_created_at ON models(created_at)',
      'CREATE INDEX IF NOT EXISTS idx_models_updated_at ON models(updated_at)',
      'CREATE INDEX IF NOT EXISTS idx_models_tags ON models USING GIN(tags)',
      'CREATE INDEX IF NOT EXISTS idx_models_capabilities ON models USING GIN(capabilities)',
      'CREATE INDEX IF NOT EXISTS idx_model_versions_model_id ON model_versions(model_id)',
      'CREATE INDEX IF NOT EXISTS idx_model_versions_created_at ON model_versions(created_at)',
    ];

    for (const indexSql of indexes) {
      await client.query(indexSql);
    }
  }

  private async setupTriggers(client: PoolClient): Promise<void> {
    // Update timestamp trigger
    await client.query(`
      CREATE OR REPLACE FUNCTION update_updated_at_column()
      RETURNS TRIGGER AS $$
      BEGIN
        NEW.updated_at = NOW();
        RETURN NEW;
      END;
      $$ language 'plpgsql'
    `);

    await client.query(`
      DROP TRIGGER IF EXISTS update_models_updated_at ON models;
      CREATE TRIGGER update_models_updated_at
        BEFORE UPDATE ON models
        FOR EACH ROW
        EXECUTE FUNCTION update_updated_at_column()
    `);
  }

  private async insertModel(client: PoolClient, model: Model, version: string): Promise<void> {
    await client.query(
      `
      INSERT INTO models (
        id, provider_name, model_name, display_name, description, max_tokens,
        input_cost_per_1k, output_cost_per_1k, supports_streaming,
        supports_function_calling, supports_vision, capabilities, tags,
        metadata, version, discovered_at, updated_at, is_active,
        is_deprecated, deprecation_date, alternatives
      ) VALUES (
        $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13,
        $14, $15, $16, $17, $18, $19, $20, $21
      )
    `,
      [
        model.id,
        model.providerName,
        model.name,
        model.displayName,
        model.description,
        model.maxTokens,
        model.inputCostPer1K,
        model.outputCostPer1K,
        model.supportsStreaming,
        model.supportsFunctionCalling,
        model.supportsVision,
        JSON.stringify(model.capabilities),
        model.tags,
        JSON.stringify(model.metadata),
        version,
        model.discoveredAt || new Date().toISOString(),
        new Date().toISOString(),
        true,
        model.deprecated || false,
        model.deprecationDate,
        model.alternatives || [],
      ]
    );
  }

  private async updateModel(client: PoolClient, model: Model, version: string): Promise<void> {
    const changes = await this.detectChanges(client, model.id, model);

    await client.query(
      `
      UPDATE models SET
        provider_name = $2, model_name = $3, display_name = $4, description = $5,
        max_tokens = $6, input_cost_per_1k = $7, output_cost_per_1k = $8,
        supports_streaming = $9, supports_function_calling = $10, supports_vision = $11,
        capabilities = $12, tags = $13, metadata = $14, version = $15,
        updated_at = $16, is_deprecated = $17, deprecation_date = $18,
        alternatives = $19
      WHERE id = $1
    `,
      [
        model.id,
        model.providerName,
        model.name,
        model.displayName,
        model.description,
        model.maxTokens,
        model.inputCostPer1K,
        model.outputCostPer1K,
        model.supportsStreaming,
        model.supportsFunctionCalling,
        model.supportsVision,
        JSON.stringify(model.capabilities),
        model.tags,
        JSON.stringify(model.metadata),
        version,
        new Date().toISOString(),
        model.deprecated || false,
        model.deprecationDate,
        model.alternatives || [],
      ]
    );
  }

  private async createVersionRecord(
    client: PoolClient,
    modelId: string,
    oldVersion: string,
    newVersion: string
  ): Promise<void> {
    const versionId = this.generateVersionId();

    await client.query(
      `
      INSERT INTO model_versions (id, model_id, version, changes, created_by)
      VALUES ($1, $2, $3, $4, $5)
    `,
      [
        versionId,
        modelId,
        newVersion,
        JSON.stringify([
          { field: 'version', oldValue: oldVersion, newValue: newVersion, changeType: 'update' },
        ]),
        'system',
      ]
    );
  }

  private async detectChanges(
    client: PoolClient,
    modelId: string,
    newModel: Model
  ): Promise<ModelChange[]> {
    const currentResult = await client.query(`SELECT * FROM models WHERE id = $1`, [modelId]);

    if (currentResult.rows.length === 0) {
      return [];
    }

    const current = currentResult.rows[0];
    const changes: ModelChange[] = [];

    // Compare fields and record changes
    const fieldsToCompare = [
      'model_name',
      'display_name',
      'description',
      'max_tokens',
      'input_cost_per_1k',
      'output_cost_per_1k',
      'supports_streaming',
      'supports_function_calling',
      'supports_vision',
      'capabilities',
      'tags',
      'metadata',
      'is_deprecated',
      'deprecation_date',
      'alternatives',
    ];

    for (const field of fieldsToCompare) {
      const oldValue = current[field];
      let newValue: any;

      switch (field) {
        case 'model_name':
          newValue = newModel.name;
          break;
        case 'display_name':
          newValue = newModel.displayName;
          break;
        case 'description':
          newValue = newModel.description;
          break;
        case 'max_tokens':
          newValue = newModel.maxTokens;
          break;
        case 'input_cost_per_1k':
          newValue = newModel.inputCostPer1K;
          break;
        case 'output_cost_per_1k':
          newValue = newModel.outputCostPer1K;
          break;
        case 'supports_streaming':
          newValue = newModel.supportsStreaming;
          break;
        case 'supports_function_calling':
          newValue = newModel.supportsFunctionCalling;
          break;
        case 'supports_vision':
          newValue = newModel.supportsVision;
          break;
        case 'capabilities':
          newValue = newModel.capabilities;
          break;
        case 'tags':
          newValue = newModel.tags;
          break;
        case 'metadata':
          newValue = newModel.metadata;
          break;
        case 'is_deprecated':
          newValue = newModel.deprecated || false;
          break;
        case 'deprecation_date':
          newValue = newModel.deprecationDate;
          break;
        case 'alternatives':
          newValue = newModel.alternatives || [];
          break;
      }

      if (JSON.stringify(oldValue) !== JSON.stringify(newValue)) {
        changes.push({
          field,
          oldValue,
          newValue,
          changeType: 'update',
        });
      }
    }

    return changes;
  }

  private async restoreModelVersion(
    client: PoolClient,
    modelId: string,
    version: ModelVersion
  ): Promise<void> {
    // This would need to restore the model state from the version changes
    // For simplicity, we'll just mark it as a special update
    await client.query(
      `
      UPDATE models SET
        updated_at = NOW(),
        version = $2,
        metadata = metadata || '{}' || '{"rollback_from": "' + version.version + '"}'
      WHERE id = $1
    `,
      [modelId, this.generateVersion()]
    );
  }

  private mapRowToModel(row: any): Model {
    return {
      id: row.id,
      providerName: row.provider_name,
      name: row.model_name,
      displayName: row.display_name,
      description: row.description,
      maxTokens: row.max_tokens,
      inputCostPer1K: parseFloat(row.input_cost_per_1k),
      outputCostPer1K: parseFloat(row.output_cost_per_1k),
      supportsStreaming: row.supports_streaming,
      supportsFunctionCalling: row.supports_function_calling,
      supportsVision: row.supports_vision,
      capabilities: row.capabilities,
      tags: row.tags,
      metadata: row.metadata,
      version: row.version,
      discoveredAt: row.discovered_at,
      deprecated: row.is_deprecated,
      deprecationDate: row.deprecation_date,
      alternatives: row.alternatives,
    };
  }

  private generateVersion(): string {
    return `v${Date.now()}-${Math.random().toString(36).substr(2, 6)}`;
  }

  private generateVersionId(): string {
    return `ver_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private getCurrentVersion(modelId: string): string {
    // This would typically query the current version
    return this.generateVersion();
  }

  // Cleanup
  async dispose(): Promise<void> {
    await this.pool.end();
    this.removeAllListeners();
  }
}

// Supporting interfaces
export interface ModelSearchCriteria {
  providerName?: string;
  capability?: string;
  tags?: string[];
  minTokens?: number;
  maxTokens?: number;
  searchTerm?: string;
  includeDeprecated?: boolean;
  limit?: number;
  offset?: number;
}

export interface ModelStats {
  totalModels: number;
  modelsByProvider: Record<string, number>;
  modelsByCapability: Record<string, number>;
  modelsAddedLast7Days: number;
}

export interface StorageHealth {
  status: 'healthy' | 'unhealthy';
  responseTime: number;
  totalConnections: number;
  activeConnections: number;
  idleConnections: number;
  lastCheck: string;
  error?: string;
}

export interface CleanupResult {
  versionsDeleted: number;
  modelsDeleted: number;
  duration: number;
  timestamp: string;
}
```

### Subtask 3.4: Create Version History Tracking for Model Changes

**Description**: Implement comprehensive version history tracking with change detection, rollback capabilities, and audit trails.

**Implementation Details**:

1. **Create Version History Manager**:

```typescript
// packages/providers/src/versioning/version-history-manager.ts
import { Model, ModelChange } from '../interfaces/model.types';
import { PersistentStorage, ModelVersion } from './persistent-storage';
import { EventEmitter } from 'events';

export interface VersionHistoryConfig {
  maxVersionsPerModel: number;
  retentionPeriod: number; // days
  autoCleanup: boolean;
  enableRollback: boolean;
  requireApprovalForRollback: boolean;
}

export interface VersionDiff {
  modelId: string;
  fromVersion: string;
  toVersion: string;
  changes: ModelChange[];
  summary: VersionSummary;
  createdAt: string;
  createdBy: string;
}

export interface VersionSummary {
  totalChanges: number;
  breakingChanges: number;
  nonBreakingChanges: number;
  addedFields: string[];
  removedFields: string[];
  modifiedFields: string[];
  impact: 'low' | 'medium' | 'high' | 'critical';
}

export interface RollbackRequest {
  modelId: string;
  targetVersion: string;
  reason: string;
  requestedBy: string;
  requiresApproval: boolean;
  approvedBy?: string;
  approvedAt?: string;
  status: 'pending' | 'approved' | 'rejected' | 'completed' | 'failed';
}

export interface RollbackResult {
  success: boolean;
  fromVersion: string;
  toVersion: string;
  changes: ModelChange[];
  duration: number;
  error?: string;
  timestamp: string;
}

export class VersionHistoryManager extends EventEmitter {
  private config: VersionHistoryConfig;
  private rollbackRequests: Map<string, RollbackRequest> = new Map();

  constructor(
    private storage: PersistentStorage,
    config: Partial<VersionHistoryConfig> = {}
  ) {
    super();
    this.config = this.mergeWithDefaults(config);

    if (this.config.autoCleanup) {
      this.startCleanupTimer();
    }
  }

  // Version management
  async createVersion(
    model: Model,
    changes: ModelChange[],
    createdBy: string = 'system'
  ): Promise<ModelVersion> {
    const currentVersions = await this.storage.getModelVersions(model.id);
    const latestVersion = currentVersions[0];
    const newVersionNumber = this.incrementVersion(latestVersion?.version || 'v0.0.0');

    const version: ModelVersion = {
      id: this.generateVersionId(),
      modelId: model.id,
      version: newVersionNumber,
      changes,
      createdAt: new Date().toISOString(),
      createdBy,
      rollbackAvailable: this.config.enableRollback,
    };

    // Store version (this would be handled by PersistentStorage in real implementation)
    await this.storage.saveModel(model, newVersionNumber);

    this.emit('versionCreated', { model, version });

    // Cleanup old versions if needed
    await this.cleanupOldVersions(model.id);

    return version;
  }

  async getVersionHistory(modelId: string, limit?: number): Promise<ModelVersion[]> {
    const versions = await this.storage.getModelVersions(modelId);

    if (limit) {
      return versions.slice(0, limit);
    }

    return versions;
  }

  async getVersion(modelId: string, version: string): Promise<ModelVersion | undefined> {
    const versions = await this.storage.getModelVersions(modelId);
    return versions.find((v) => v.version === version);
  }

  async getLatestVersion(modelId: string): Promise<ModelVersion | undefined> {
    const versions = await this.storage.getModelVersions(modelId);
    return versions[0];
  }

  // Diff operations
  async compareVersions(
    modelId: string,
    fromVersion: string,
    toVersion: string
  ): Promise<VersionDiff> {
    const fromVer = await this.getVersion(modelId, fromVersion);
    const toVer = await this.getVersion(modelId, toVersion);

    if (!fromVer || !toVer) {
      throw new Error(`One or both versions not found for model ${modelId}`);
    }

    const changes = this.calculateChanges(fromVer.changes, toVer.changes);
    const summary = this.generateSummary(changes);

    const diff: VersionDiff = {
      modelId,
      fromVersion,
      toVersion,
      changes,
      summary,
      createdAt: new Date().toISOString(),
      createdBy: 'system',
    };

    this.emit('versionDiffGenerated', diff);
    return diff;
  }

  async compareWithCurrent(modelId: string, previousVersion: string): Promise<VersionDiff> {
    const currentModel = await this.storage.getModel(modelId);
    if (!currentModel) {
      throw new Error(`Model ${modelId} not found`);
    }

    const currentVersion = currentModel.version;
    return this.compareVersions(modelId, previousVersion, currentVersion);
  }

  // Rollback operations
  async requestRollback(request: RollbackRequest): Promise<string> {
    const requestId = this.generateRequestId();
    request.id = requestId;

    if (this.config.requireApprovalForRollback) {
      request.status = 'pending';
      request.requiresApproval = true;
    } else {
      request.status = 'approved';
      request.requiresApproval = false;
    }

    this.rollbackRequests.set(requestId, request);

    this.emit('rollbackRequested', request);

    if (!request.requiresApproval) {
      // Auto-approve and execute
      await this.approveRollback(requestId, 'system');
    }

    return requestId;
  }

  async approveRollback(requestId: string, approvedBy: string): Promise<void> {
    const request = this.rollbackRequests.get(requestId);
    if (!request) {
      throw new Error(`Rollback request ${requestId} not found`);
    }

    request.status = 'approved';
    request.approvedBy = approvedBy;
    request.approvedAt = new Date().toISOString();

    this.emit('rollbackApproved', request);

    // Execute rollback
    await this.executeRollback(request);
  }

  async rejectRollback(requestId: string, reason: string): Promise<void> {
    const request = this.rollbackRequests.get(requestId);
    if (!request) {
      throw new Error(`Rollback request ${requestId} not found`);
    }

    request.status = 'rejected';

    this.emit('rollbackRejected', { request, reason });
    this.rollbackRequests.delete(requestId);
  }

  async executeRollback(request: RollbackRequest): Promise<RollbackResult> {
    const startTime = Date.now();

    try {
      request.status = 'completed';

      const success = await this.storage.rollbackModel(request.modelId, request.targetVersion);

      const result: RollbackResult = {
        success,
        fromVersion: await this.getCurrentVersion(request.modelId),
        toVersion: request.targetVersion,
        changes: [], // Would be populated by storage
        duration: Date.now() - startTime,
        timestamp: new Date().toISOString(),
      };

      this.emit('rollbackCompleted', { request, result });
      this.rollbackRequests.delete(request.id!);

      return result;
    } catch (error) {
      request.status = 'failed';

      const result: RollbackResult = {
        success: false,
        fromVersion: await this.getCurrentVersion(request.modelId),
        toVersion: request.targetVersion,
        changes: [],
        duration: Date.now() - startTime,
        error: error.message,
        timestamp: new Date().toISOString(),
      };

      this.emit('rollbackFailed', { request, result });
      return result;
    }
  }

  // Rollback request management
  getRollbackRequest(requestId: string): RollbackRequest | undefined {
    return this.rollbackRequests.get(requestId);
  }

  getRollbackRequests(filter?: RollbackRequestFilter): RollbackRequest[] {
    let requests = Array.from(this.rollbackRequests.values());

    if (filter) {
      if (filter.modelId) {
        requests = requests.filter((r) => r.modelId === filter.modelId);
      }
      if (filter.status) {
        requests = requests.filter((r) => r.status === filter.status);
      }
      if (filter.requestedBy) {
        requests = requests.filter((r) => r.requestedBy === filter.requestedBy);
      }
      if (filter.startTime) {
        requests = requests.filter(
          (r) =>
            new Date(r.id.includes('_') ? parseInt(r.id.split('_')[1]) : 0) >=
            filter.startTime!.getTime()
        );
      }
    }

    return requests.sort((a, b) => b.id!.localeCompare(a.id!));
  }

  // Analytics and reporting
  async getVersionStats(modelId?: string): Promise<VersionStats> {
    // This would query the storage for version statistics
    const allVersions = modelId
      ? await this.storage.getModelVersions(modelId)
      : await this.getAllVersions();

    const stats: VersionStats = {
      totalVersions: allVersions.length,
      versionsByModel: {},
      averageVersionsPerModel: 0,
      mostVersionedModel: '',
      rollbackCount: 0,
      recentVersions: 0,
      versionFrequency: {},
    };

    if (modelId) {
      stats.versionsByModel[modelId] = allVersions.length;
    } else {
      // Group by model
      for (const version of allVersions) {
        stats.versionsByModel[version.modelId] = (stats.versionsByModel[version.modelId] || 0) + 1;
      }

      const modelCounts = Object.values(stats.versionsByModel);
      stats.averageVersionsPerModel =
        modelCounts.reduce((sum, count) => sum + count, 0) / modelCounts.length;

      stats.mostVersionedModel = Object.entries(stats.versionsByModel).reduce(
        (max, [model, count]) => (count > max.count ? { model, count } : max),
        { model: '', count: 0 }
      ).model;
    }

    // Count rollbacks
    for (const request of this.rollbackRequests.values()) {
      if (request.status === 'completed') {
        stats.rollbackCount++;
      }
    }

    // Recent versions (last 30 days)
    const thirtyDaysAgo = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000);
    stats.recentVersions = allVersions.filter((v) => new Date(v.createdAt) >= thirtyDaysAgo).length;

    return stats;
  }

  async getChangeHistory(filter?: ChangeHistoryFilter): Promise<ModelChange[]> {
    // This would aggregate changes from all versions
    const allVersions = await this.getAllVersions();
    let changes: ModelChange[] = [];

    for (const version of allVersions) {
      changes.push(...version.changes);
    }

    if (filter) {
      if (filter.modelId) {
        changes = changes.filter((c) => c.modelId === filter.modelId);
      }
      if (filter.changeType) {
        changes = changes.filter((c) => c.changeType === filter.changeType);
      }
      if (filter.field) {
        changes = changes.filter((c) => c.field === filter.field);
      }
      if (filter.startTime) {
        changes = changes.filter((c) => new Date(c.timestamp || '') >= filter.startTime!);
      }
      if (filter.endTime) {
        changes = changes.filter((c) => new Date(c.timestamp || '') <= filter.endTime!);
      }
    }

    return changes.sort(
      (a, b) => new Date(b.timestamp || '').getTime() - new Date(a.timestamp || '').getTime()
    );
  }

  // Private helper methods
  private mergeWithDefaults(config: Partial<VersionHistoryConfig>): VersionHistoryConfig {
    return {
      maxVersionsPerModel: 10,
      retentionPeriod: 90,
      autoCleanup: true,
      enableRollback: true,
      requireApprovalForRollback: false,
      ...config,
    };
  }

  private incrementVersion(currentVersion: string): string {
    // Simple version increment - in reality, this would be more sophisticated
    const match = currentVersion.match(/v(\d+)\.(\d+)\.(\d+)/);
    if (match) {
      const major = parseInt(match[1]);
      const minor = parseInt(match[2]);
      const patch = parseInt(match[3]);
      return `v${major}.${minor}.${patch + 1}`;
    }
    return `v1.0.1`;
  }

  private calculateChanges(fromChanges: ModelChange[], toChanges: ModelChange[]): ModelChange[] {
    // This would calculate the actual differences between versions
    // For simplicity, return the toChanges
    return toChanges;
  }

  private generateSummary(changes: ModelChange[]): VersionSummary {
    const summary: VersionSummary = {
      totalChanges: changes.length,
      breakingChanges: 0,
      nonBreakingChanges: 0,
      addedFields: [],
      removedFields: [],
      modifiedFields: [],
      impact: 'low',
    };

    for (const change of changes) {
      switch (change.changeType) {
        case 'create':
          summary.addedFields.push(change.field);
          summary.nonBreakingChanges++;
          break;
        case 'delete':
          summary.removedFields.push(change.field);
          summary.breakingChanges++;
          break;
        case 'update':
          summary.modifiedFields.push(change.field);
          // Determine if breaking based on field
          if (this.isBreakingChange(change.field)) {
            summary.breakingChanges++;
          } else {
            summary.nonBreakingChanges++;
          }
          break;
      }
    }

    // Determine impact
    if (summary.breakingChanges > 0) {
      summary.impact = summary.breakingChanges > 3 ? 'critical' : 'high';
    } else if (summary.nonBreakingChanges > 5) {
      summary.impact = 'medium';
    }

    return summary;
  }

  private isBreakingChange(field: string): boolean {
    const breakingFields = [
      'max_tokens',
      'supports_streaming',
      'supports_function_calling',
      'supports_vision',
      'capabilities',
    ];

    return breakingFields.some((bf) => field.includes(bf));
  }

  private async cleanupOldVersions(modelId: string): Promise<void> {
    const versions = await this.storage.getModelVersions(modelId);

    if (versions.length > this.config.maxVersionsPerModel) {
      const versionsToDelete = versions.slice(this.config.maxVersionsPerModel);

      for (const version of versionsToDelete) {
        // Mark as not available for rollback
        version.rollbackAvailable = false;
      }
    }
  }

  private async getAllVersions(): Promise<ModelVersion[]> {
    // This would get all versions from storage
    return [];
  }

  private async getCurrentVersion(modelId: string): Promise<string> {
    const model = await this.storage.getModel(modelId);
    return model?.version || 'unknown';
  }

  private startCleanupTimer(): void {
    setInterval(
      async () => {
        try {
          await this.performCleanup();
        } catch (error) {
          this.emit('cleanupError', error);
        }
      },
      24 * 60 * 60 * 1000
    ); // Daily cleanup
  }

  private async performCleanup(): Promise<void> {
    const cutoffDate = new Date(Date.now() - this.config.retentionPeriod * 24 * 60 * 60 * 1000);

    // Clean up old rollback requests
    for (const [requestId, request] of this.rollbackRequests) {
      const requestDate = new Date(parseInt(requestId.split('_')[1]));
      if (requestDate < cutoffDate) {
        this.rollbackRequests.delete(requestId);
      }
    }

    this.emit('cleanupCompleted', { cutoffDate });
  }

  private generateVersionId(): string {
    return `ver_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private generateRequestId(): string {
    return `req_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  // Cleanup
  dispose(): void {
    this.rollbackRequests.clear();
    this.removeAllListeners();
  }
}

// Supporting interfaces
export interface RollbackRequestFilter {
  modelId?: string;
  status?: 'pending' | 'approved' | 'rejected' | 'completed' | 'failed';
  requestedBy?: string;
  startTime?: Date;
}

export interface ChangeHistoryFilter {
  modelId?: string;
  changeType?: 'create' | 'update' | 'delete';
  field?: string;
  startTime?: Date;
  endTime?: Date;
}

export interface VersionStats {
  totalVersions: number;
  versionsByModel: Record<string, number>;
  averageVersionsPerModel: number;
  mostVersionedModel: string;
  rollbackCount: number;
  recentVersions: number;
  versionFrequency: Record<string, number>;
}
```

## Files to Create

1. **Cache Implementation**:
   - `packages/providers/src/cache/model-cache.ts`
   - `packages/providers/src/cache/invalidation-manager.ts`

2. **Storage Implementation**:
   - `packages/providers/src/storage/persistent-storage.ts`
   - `packages/providers/src/versioning/version-history-manager.ts`

3. **Database Schema**:
   - `packages/providers/src/migrations/001_create_models.sql`
   - `packages/providers/src/migrations/002_create_model_versions.sql`

4. **Updated Files**:
   - `packages/providers/src/index.ts` (export new classes)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/cache/model-cache.test.ts
describe('ModelCache', () => {
  let cache: ModelCache;

  beforeEach(() => {
    cache = new ModelCache({
      defaultTTL: 60000,
      maxMemoryUsage: 10 * 1024 * 1024,
      cleanupInterval: 1000,
    });
  });

  describe('basic operations', () => {
    it('should store and retrieve models', async () => {
      const model = createMockModel();
      await cache.set(model.id, model);

      const retrieved = await cache.get(model.id);
      expect(retrieved).toEqual(model);
    });

    it('should handle cache misses', async () => {
      const retrieved = await cache.get('non-existent');
      expect(retrieved).toBeUndefined();
    });
  });

  describe('TTL functionality', () => {
    it('should expire entries after TTL', async () => {
      const model = createMockModel();
      await cache.set(model.id, model, { ttl: 100 });

      // Should be available immediately
      let retrieved = await cache.get(model.id);
      expect(retrieved).toEqual(model);

      // Wait for expiration
      await new Promise((resolve) => setTimeout(resolve, 150));

      retrieved = await cache.get(model.id);
      expect(retrieved).toBeUndefined();
    });
  });
});
```

```typescript
// packages/providers/src/__tests__/storage/persistent-storage.test.ts
describe('PersistentStorage', () => {
  let storage: PersistentStorage;
  let testConfig: StorageConfig;

  beforeAll(() => {
    testConfig = {
      host: 'localhost',
      port: 5432,
      database: 'test_models',
      username: 'test',
      password: 'test',
    };
  });

  beforeEach(async () => {
    storage = new PersistentStorage(testConfig);
    await storage.initialize();
  });

  afterEach(async () => {
    await storage.dispose();
  });

  describe('model operations', () => {
    it('should save and retrieve models', async () => {
      const model = createMockModel();
      await storage.saveModel(model);

      const retrieved = await storage.getModel(model.id);
      expect(retrieved).toBeDefined();
      expect(retrieved!.id).toBe(model.id);
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integration/cache-storage-integration.test.ts
describe('Cache and Storage Integration', () => {
  let cache: ModelCache;
  let storage: PersistentStorage;

  beforeEach(async () => {
    cache = new ModelCache();
    storage = new PersistentStorage(testConfig);
    await storage.initialize();
  });

  it('should sync cache with persistent storage', async () => {
    const model = createMockModel();

    // Save to storage
    await storage.saveModel(model);

    // Load into cache
    const cachedModel = await cache.get(model.id);
    expect(cachedModel).toBeDefined();
  });
});
```

## Security Considerations

1. **Cache Security**:
   - Encrypt sensitive model metadata in cache
   - Validate cache keys and values
   - Implement access controls for cache operations

2. **Storage Security**:
   - Use parameterized queries to prevent SQL injection
   - Encrypt sensitive data at rest
   - Implement proper database user permissions

3. **Version Security**:
   - Validate version changes and rollback requests
   - Audit all version operations
   - Implement approval workflows for critical changes

## Dependencies

### New Dependencies

```json
{
  "ioredis": "^5.3.2",
  "pg": "^8.11.3"
}
```

### Dev Dependencies

```json
{
  "@types/pg": "^8.10.7",
  "@types/ioredis": "^5.0.0"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Cache hit/miss rates and response times
   - Storage query performance and connection health
   - Version creation and rollback frequencies
   - Memory usage and eviction rates

2. **Logging**:
   - All cache operations with timing
   - Database queries and performance metrics
   - Version changes and rollback operations
   - Error details and recovery actions

3. **Alerts**:
   - High cache miss rates
   - Database connection issues
   - Failed rollback operations
   - Memory pressure warnings

## Acceptance Criteria

1. ✅ **TTL Support**: Complete TTL functionality with expiration
2. ✅ **Invalidation Strategies**: Multiple invalidation strategies
3. ✅ **Persistent Storage**: Reliable PostgreSQL integration
4. ✅ **Version History**: Comprehensive version tracking
5. ✅ **Rollback Capabilities**: Safe rollback functionality
6. ✅ **Performance**: High-performance caching and storage
7. ✅ **Reliability**: Error handling and recovery mechanisms
8. ✅ **Testing**: Complete test coverage
9. ✅ **Security**: Secure data handling and access controls
10. ✅ **Monitoring**: Comprehensive observability

## Success Metrics

- Cache hit rate > 80%
- Average cache response time < 10ms
- Database query performance < 100ms
- Version creation success rate > 99%
- Rollback success rate > 95%
- Zero data corruption incidents
- Complete audit trail for all operations
