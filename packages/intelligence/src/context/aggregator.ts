import { randomUUID } from 'node:crypto';
import type { ILogger } from '@tamma/shared';
import type {
  IContextAggregator,
  IContextSource,
  IContextCache,
  ContextRequest,
  ContextResponse,
  ContextChunk,
  SourceContribution,
  AggregatorConfig,
  CacheStats,
  HealthStatus,
  ContextSourceType,
  SourceQuery,
} from './types.js';
import { DEFAULT_AGGREGATOR_CONFIG } from './types.js';
import { BudgetManager } from './budget-manager.js';
import { Deduplicator } from './deduplicator.js';
import { ChunkRanker } from './ranker.js';
import { ContextAssemblerAgg } from './assembler.js';
import { MemoryCache } from './cache/memory-cache.js';

function generateId(): string {
  return `ctx-${randomUUID()}`;
}

export interface ContextAggregatorOptions {
  config?: Partial<AggregatorConfig>;
  logger?: ILogger;
}

export class ContextAggregator implements IContextAggregator {
  private sources = new Map<ContextSourceType, IContextSource>();
  private cache: IContextCache;
  private budgetManager: BudgetManager;
  private deduplicator: Deduplicator;
  private ranker: ChunkRanker;
  private assembler: ContextAssemblerAgg;
  private config: AggregatorConfig;
  private logger: ILogger | undefined;

  constructor(options: ContextAggregatorOptions = {}) {
    this.config = { ...DEFAULT_AGGREGATOR_CONFIG, ...options.config };
    this.logger = options.logger;
    this.budgetManager = new BudgetManager(this.config.budget);
    this.deduplicator = new Deduplicator();
    this.ranker = new ChunkRanker();
    this.assembler = new ContextAssemblerAgg();
    this.cache = new MemoryCache(this.config.caching);
  }

  async configure(config: Partial<AggregatorConfig>): Promise<void> {
    this.config = { ...this.config, ...config };
    if (config.budget) this.budgetManager.updateConfig(config.budget);
    if (config.caching) this.cache = new MemoryCache({ ...this.config.caching, ...config.caching });
  }

  registerSource(source: IContextSource): void {
    this.sources.set(source.name, source);
  }

  removeSource(name: ContextSourceType): void {
    this.sources.delete(name);
  }

  getAvailableSources(): ContextSourceType[] {
    return Array.from(this.sources.keys());
  }

  async getContext(request: ContextRequest): Promise<ContextResponse> {
    const requestId = generateId();
    const startTime = Date.now();
    this.logger?.debug('Context request started', { requestId, taskType: request.taskType });

    // 1. Check cache
    if (this.config.caching.enabled && !request.options?.skipCache) {
      const cacheKey = request.options?.cacheKey ?? this.computeCacheKey(request);
      const cached = await this.cache.get(cacheKey);
      if (cached) {
        this.logger?.debug('Cache hit', { requestId });
        return { ...cached, requestId };
      }
    }

    // 2. Calculate effective budget
    const effectiveBudget = this.budgetManager.calculateEffectiveBudget(
      request.maxTokens,
      request.reservedTokens
    );

    // 3. Determine sources and priorities
    const requestedSources = request.sources ?? this.budgetManager.getDefaultSources(request.taskType);
    const availableSources = requestedSources.filter(s => this.sources.has(s));
    const priorities = {
      ...this.budgetManager.getDefaultPriorities(request.taskType),
      ...request.sourcePriorities,
    };
    const budgetAllocation = this.budgetManager.allocateBudget(availableSources, priorities, effectiveBudget);

    // 4. Parallel retrieval from all sources
    const retrievalPromises = availableSources.map(source => {
      const sourceConfig = this.config.sources[source];
      const timeoutValue = request.options?.timeout ?? sourceConfig?.timeoutMs;
      const sourceQuery: SourceQuery = {
        text: request.query,
        maxChunks: sourceConfig?.maxChunks ?? 10,
        maxTokens: budgetAllocation[source] ?? effectiveBudget,
        ...(timeoutValue != null ? { timeout: timeoutValue } : {}),
        ...(request.hints?.language
          ? { filters: { languages: [request.hints.language] } }
          : {}),
      };
      return this.sources.get(source)!.retrieve(sourceQuery);
    });
    const sourceResults = await Promise.allSettled(retrievalPromises);

    // 5. Collect results
    const allChunks: ContextChunk[] = [];
    const contributions: SourceContribution[] = [];

    for (let index = 0; index < sourceResults.length; index++) {
      const source = availableSources[index]!;
      const result = sourceResults[index]!;
      if (result.status === 'fulfilled') {
        allChunks.push(...result.value.chunks);
        contributions.push({
          source,
          chunksProvided: result.value.chunks.length,
          tokensUsed: result.value.chunks.reduce((sum, c) => sum + (c.tokenCount ?? 0), 0),
          latencyMs: result.value.latencyMs,
          cacheHit: result.value.cacheHit,
        });
      } else {
        this.logger?.warn(`Source ${source} failed`, { error: String(result.reason) });
        contributions.push({
          source,
          chunksProvided: 0,
          tokensUsed: 0,
          latencyMs: 0,
          cacheHit: false,
          error: String(result.reason),
        });
      }
    }

    // 6. Deduplicate
    const { chunks: deduped, removedCount } = request.options?.deduplicate !== false
      ? await this.deduplicator.deduplicate(allChunks, this.config.deduplication)
      : { chunks: allChunks, removedCount: 0 };

    // 7. Rank
    const ranked = this.ranker.rank(deduped, request);

    // 8. Select within budget
    const selected = this.ranker.selectWithinBudget(ranked, effectiveBudget);

    // 9. Assemble context
    const assembled = this.assembler.assemble(selected, effectiveBudget, request.options ?? {});

    // 10. Build response
    const response: ContextResponse = {
      requestId,
      context: assembled,
      sources: contributions,
      metrics: {
        totalLatencyMs: Date.now() - startTime,
        totalTokens: assembled.tokenCount,
        budgetUtilization: effectiveBudget > 0 ? assembled.tokenCount / effectiveBudget : 0,
        deduplicationRate: allChunks.length > 0 ? removedCount / allChunks.length : 0,
        cacheHitRate: 0,
        sourcesQueried: availableSources.length,
        sourcesSucceeded: contributions.filter(c => !c.error).length,
      },
    };

    // 11. Cache result
    if (this.config.caching.enabled && !request.options?.skipCache) {
      const cacheKey = request.options?.cacheKey ?? this.computeCacheKey(request);
      await this.cache.set(cacheKey, response);
    }

    this.logger?.debug('Context request completed', {
      requestId,
      totalTokens: assembled.tokenCount,
      sources: availableSources.length,
      latencyMs: response.metrics.totalLatencyMs,
    });

    return response;
  }

  async *streamContext(request: ContextRequest): AsyncIterable<ContextChunk> {
    const response = await this.getContext(request);
    for (const chunk of response.context.chunks) {
      yield chunk;
    }
  }

  async invalidateCache(pattern?: string): Promise<void> {
    await this.cache.clear(pattern);
  }

  getCacheStats(): CacheStats {
    return this.cache.getStats();
  }

  async healthCheck(): Promise<HealthStatus> {
    const sourceStatus: Record<string, { healthy: boolean; latencyMs?: number; error?: string }> = {};

    for (const [name, source] of this.sources) {
      const start = Date.now();
      try {
        const available = await source.isAvailable();
        sourceStatus[name] = { healthy: available, latencyMs: Date.now() - start };
      } catch (error) {
        sourceStatus[name] = { healthy: false, error: String(error) };
      }
    }

    const cacheHealthy = await this.cache.healthCheck();
    const allHealthy = Object.values(sourceStatus).some(s => s.healthy) && cacheHealthy;

    return {
      healthy: allHealthy,
      sources: sourceStatus as Record<ContextSourceType, { healthy: boolean; latencyMs?: number; error?: string }>,
      cache: { healthy: cacheHealthy, provider: this.config.caching.provider },
    };
  }

  async dispose(): Promise<void> {
    for (const source of this.sources.values()) {
      await source.dispose();
    }
    this.sources.clear();
    await this.cache.clear();
  }

  private computeCacheKey(request: ContextRequest): string {
    const parts = [
      request.query,
      request.taskType,
      String(request.maxTokens),
      JSON.stringify(request.sources ?? []),
      JSON.stringify(request.hints ?? {}),
    ];
    let hash = 2166136261;
    const str = parts.join('|');
    for (let i = 0; i < str.length; i++) {
      hash ^= str.charCodeAt(i);
      hash = Math.imul(hash, 16777619);
    }
    return `ctx:${(hash >>> 0).toString(36)}`;
  }
}

export function createContextAggregator(options?: ContextAggregatorOptions): ContextAggregator {
  return new ContextAggregator(options);
}
