---
title: "Story 6-5: Context Aggregator Service - Implementation Plan"
sidebar:
  order: 60
---

## Overview

The Context Aggregator Service is a unified context retrieval layer that orchestrates gathering relevant context from multiple sources (Vector DB, RAG, MCP, Web Search), manages token budgets, deduplicates results, and assembles optimal context windows for agent tasks.

## Package Location

**Package:** `@tamma/intelligence`
**Path:** `/packages/intelligence/src/context/`

The Context Aggregator naturally belongs in the `@tamma/intelligence` package as it is the core intelligence layer that powers agent context awareness. This package already has dependencies on `@tamma/shared`, `@tamma/providers`, and `@tamma/observability`.

## Files to Create/Modify

### New Files

```
packages/intelligence/src/
├── context/
│   ├── index.ts                           # Public exports
│   ├── types.ts                           # All type definitions
│   ├── aggregator.ts                      # Main ContextAggregator class
│   ├── budget-manager.ts                  # Token budget allocation
│   ├── deduplicator.ts                    # Deduplication logic (hash + semantic)
│   ├── ranker.ts                          # Chunk ranking and selection
│   ├── assembler.ts                       # Context assembly and formatting
│   ├── cache/
│   │   ├── index.ts                       # Cache exports
│   │   ├── types.ts                       # Cache interface definitions
│   │   ├── memory-cache.ts                # In-memory cache implementation
│   │   └── redis-cache.ts                 # Redis cache implementation
│   ├── sources/
│   │   ├── index.ts                       # Source adapter exports
│   │   ├── types.ts                       # Source interface definitions
│   │   ├── base-source.ts                 # Abstract base class for sources
│   │   ├── vector-db-source.ts            # Vector DB adapter
│   │   ├── rag-source.ts                  # RAG pipeline adapter
│   │   ├── mcp-source.ts                  # MCP client adapter
│   │   └── web-search-source.ts           # Web search adapter
│   └── __tests__/
│       ├── aggregator.test.ts             # Unit tests for aggregator
│       ├── budget-manager.test.ts         # Unit tests for budget allocation
│       ├── deduplicator.test.ts           # Unit tests for deduplication
│       ├── ranker.test.ts                 # Unit tests for ranking
│       ├── assembler.test.ts              # Unit tests for assembly
│       ├── cache.test.ts                  # Cache tests
│       ├── sources/
│       │   ├── vector-db-source.test.ts
│       │   ├── rag-source.test.ts
│       │   ├── mcp-source.test.ts
│       │   └── web-search-source.test.ts
│       └── integration/
│           └── aggregator.integration.test.ts
```

### Files to Modify

| File | Changes |
|------|---------|
| `packages/intelligence/src/index.ts` | Export context aggregator module |
| `packages/intelligence/package.json` | Add dependencies (ioredis, crypto-js) |
| `packages/shared/src/contracts/index.ts` | Add shared types if needed |

---

## Interfaces and Types

### Core Types (`types.ts`)

```typescript
/**
 * Context Source Types
 */
export type ContextSource = 'vector_db' | 'rag' | 'mcp' | 'web_search' | 'live_api';

export type TaskType =
  | 'analysis'
  | 'planning'
  | 'implementation'
  | 'review'
  | 'testing'
  | 'documentation';

export type ContextFormat = 'xml' | 'markdown' | 'plain';

/**
 * Context Request
 */
export interface ContextRequest {
  /** Natural language query */
  query: string;

  /** Type of task requiring context */
  taskType: TaskType;

  /** Maximum tokens for assembled context */
  maxTokens: number;

  /** Tokens reserved for system/user prompts */
  reservedTokens?: number;

  /** Specific sources to query (defaults based on taskType) */
  sources?: ContextSource[];

  /** Source priority weights for budget allocation */
  sourcePriorities?: Partial<Record<ContextSource, number>>;

  /** Hints to improve retrieval */
  hints?: ContextHints;

  /** Processing options */
  options?: ContextOptions;
}

export interface ContextHints {
  relatedFiles?: string[];
  relatedIssues?: number[];
  language?: string;
  framework?: string;
  recentCommits?: string[];
}

export interface ContextOptions {
  deduplicate?: boolean;
  compress?: boolean;
  summarize?: boolean;
  includeMetadata?: boolean;
  format?: ContextFormat;
  cacheKey?: string;
  skipCache?: boolean;
  timeout?: number;
}

/**
 * Context Response
 */
export interface ContextResponse {
  /** Unique request identifier */
  requestId: string;

  /** Assembled context ready for use */
  context: AssembledContext;

  /** Per-source contribution details */
  sources: SourceContribution[];

  /** Request metrics */
  metrics: ContextMetrics;
}

export interface AssembledContext {
  /** Formatted context text */
  text: string;

  /** Individual chunks that make up the context */
  chunks: ContextChunk[];

  /** Total token count */
  tokenCount: number;

  /** Format used for assembly */
  format: ContextFormat;
}

export interface ContextChunk {
  /** Unique chunk identifier */
  id: string;

  /** Chunk content */
  content: string;

  /** Source that provided this chunk */
  source: ContextSource;

  /** Relevance score (0-1) */
  relevance: number;

  /** Chunk metadata */
  metadata: ChunkMetadata;

  /** Token count for this chunk */
  tokenCount?: number;

  /** Embedding vector (for deduplication) */
  embedding?: number[];
}

export interface ChunkMetadata {
  filePath?: string;
  startLine?: number;
  endLine?: number;
  language?: string;
  symbolName?: string;
  symbolType?: 'function' | 'class' | 'interface' | 'module' | 'block';
  url?: string;
  title?: string;
  date?: Date;
  hash?: string;
}

export interface SourceContribution {
  source: ContextSource;
  chunksProvided: number;
  tokensUsed: number;
  latencyMs: number;
  cacheHit: boolean;
  error?: string;
}

export interface ContextMetrics {
  totalLatencyMs: number;
  totalTokens: number;
  budgetUtilization: number;
  deduplicationRate: number;
  cacheHitRate: number;
  sourcesQueried: number;
  sourcesSucceeded: number;
}

/**
 * Aggregator Configuration
 */
export interface AggregatorConfig {
  /** Source configurations */
  sources: SourcesConfig;

  /** Budget defaults */
  budget: BudgetConfig;

  /** Deduplication settings */
  deduplication: DeduplicationConfig;

  /** Cache configuration */
  caching: CachingConfig;

  /** Optimization settings */
  optimization: OptimizationConfig;
}

export interface SourcesConfig {
  vector_db?: SourceConfig;
  rag?: SourceConfig;
  mcp?: SourceConfig;
  web_search?: SourceConfig;
  live_api?: SourceConfig;
}

export interface SourceConfig {
  enabled: boolean;
  timeoutMs: number;
  maxChunks?: number;
  retryAttempts?: number;
}

export interface BudgetConfig {
  defaultMaxTokens: number;
  reservedTokens: number;
  minChunkTokens: number;
  maxChunkTokens: number;
}

export interface DeduplicationConfig {
  enabled: boolean;
  similarityThreshold: number;
  useSemantic: boolean;
  useContentHash: boolean;
}

export interface CachingConfig {
  enabled: boolean;
  ttlSeconds: number;
  maxEntries: number;
  provider: 'memory' | 'redis';
  redisUrl?: string;
}

export interface OptimizationConfig {
  compressLargeChunks: boolean;
  summarizeVerbose: boolean;
  smartTruncation: boolean;
  preserveCodeStructure: boolean;
}
```

### Context Aggregator Interface

```typescript
/**
 * Main Context Aggregator Interface
 */
export interface IContextAggregator {
  /** Configure the aggregator */
  configure(config: AggregatorConfig): Promise<void>;

  /** Retrieve context (main API) */
  getContext(request: ContextRequest): Promise<ContextResponse>;

  /** Stream context chunks as they become available */
  streamContext(request: ContextRequest): AsyncIterable<ContextChunk>;

  /** Invalidate cache entries */
  invalidateCache(pattern?: string): Promise<void>;

  /** Get cache statistics */
  getCacheStats(): CacheStats;

  /** Health check */
  healthCheck(): Promise<HealthStatus>;

  /** Dispose resources */
  dispose(): Promise<void>;
}

export interface CacheStats {
  hits: number;
  misses: number;
  size: number;
  maxSize: number;
  hitRate: number;
}

export interface HealthStatus {
  healthy: boolean;
  sources: Record<ContextSource, { healthy: boolean; latencyMs?: number; error?: string }>;
  cache: { healthy: boolean; provider: string };
}
```

### Source Adapter Interface

```typescript
/**
 * Source Adapter Interface
 */
export interface IContextSource {
  readonly name: ContextSource;

  /** Initialize the source */
  initialize(config: SourceConfig): Promise<void>;

  /** Check if source is available */
  isAvailable(): Promise<boolean>;

  /** Retrieve chunks from this source */
  retrieve(query: SourceQuery): Promise<SourceResult>;

  /** Dispose resources */
  dispose(): Promise<void>;
}

export interface SourceQuery {
  text: string;
  embedding?: number[];
  maxChunks: number;
  maxTokens: number;
  filters?: SourceFilters;
  timeout?: number;
}

export interface SourceFilters {
  filePaths?: string[];
  languages?: string[];
  dateRange?: { start?: Date; end?: Date };
  metadata?: Record<string, unknown>;
}

export interface SourceResult {
  chunks: ContextChunk[];
  latencyMs: number;
  cacheHit: boolean;
  error?: string;
}
```

### Cache Interface

```typescript
/**
 * Cache Interface
 */
export interface IContextCache {
  /** Get cached response */
  get(key: string): Promise<ContextResponse | null>;

  /** Store response in cache */
  set(key: string, value: ContextResponse, ttl?: number): Promise<void>;

  /** Delete cache entry */
  delete(key: string): Promise<void>;

  /** Clear cache entries matching pattern */
  clear(pattern?: string): Promise<void>;

  /** Get cache statistics */
  getStats(): CacheStats;

  /** Check cache health */
  healthCheck(): Promise<boolean>;
}
```

---

## Implementation Phases

### Phase 1: Foundation (Week 1)

**Objective:** Establish core types, interfaces, and basic infrastructure.

**Tasks:**
1. Create package directory structure
2. Implement all type definitions (`types.ts`)
3. Implement memory-based cache (`memory-cache.ts`)
4. Create base source adapter abstract class (`base-source.ts`)
5. Implement budget manager (`budget-manager.ts`)
6. Set up unit test infrastructure

**Deliverables:**
- All TypeScript interfaces defined
- MemoryCache implementation with TTL support
- BudgetManager with allocation algorithm
- Unit tests for budget allocation

**Budget Manager Algorithm:**
```typescript
class BudgetManager {
  allocateBudget(
    sources: ContextSource[],
    priorities: Record<ContextSource, number>,
    totalBudget: number
  ): Record<ContextSource, number> {
    const totalPriority = sources.reduce(
      (sum, s) => sum + (priorities[s] ?? 1),
      0
    );

    return Object.fromEntries(
      sources.map(source => [
        source,
        Math.floor(totalBudget * (priorities[source] ?? 1) / totalPriority)
      ])
    );
  }

  // Task-specific default sources
  getDefaultSources(taskType: TaskType): ContextSource[] { ... }

  // Task-specific default priorities
  getDefaultPriorities(taskType: TaskType): Record<ContextSource, number> { ... }
}
```

### Phase 2: Source Adapters (Week 2)

**Objective:** Implement adapters for each context source.

**Tasks:**
1. Implement `VectorDBSource` adapter (Story 6-2 dependency)
2. Implement `RAGSource` adapter (Story 6-3 dependency)
3. Implement `MCPSource` adapter (Story 6-4 dependency)
4. Implement `WebSearchSource` adapter
5. Create mock implementations for testing
6. Add timeout and retry logic to base adapter

**Deliverables:**
- All source adapters with interface compliance
- Mock adapters for unit testing
- Integration tests with mocked backends

**Source Adapter Pattern:**
```typescript
abstract class BaseContextSource implements IContextSource {
  protected config: SourceConfig;
  protected logger: ILogger;

  async retrieve(query: SourceQuery): Promise<SourceResult> {
    const startTime = Date.now();

    try {
      const result = await this.withTimeout(
        this.doRetrieve(query),
        query.timeout ?? this.config.timeoutMs
      );

      return {
        chunks: result,
        latencyMs: Date.now() - startTime,
        cacheHit: false,
      };
    } catch (error) {
      return {
        chunks: [],
        latencyMs: Date.now() - startTime,
        cacheHit: false,
        error: error instanceof Error ? error.message : String(error),
      };
    }
  }

  protected abstract doRetrieve(query: SourceQuery): Promise<ContextChunk[]>;
}
```

### Phase 3: Deduplication & Ranking (Week 3)

**Objective:** Implement content deduplication and relevance ranking.

**Tasks:**
1. Implement content hash deduplication
2. Implement semantic deduplication using embeddings
3. Implement chunk ranking algorithm
4. Implement Max Marginal Relevance (MMR) for diversity
5. Add support for embedding provider integration
6. Unit tests for deduplication accuracy

**Deliverables:**
- Deduplicator with hash and semantic modes
- Ranker with configurable scoring
- MMR implementation for diversity
- 90%+ deduplication test coverage

**Deduplication Algorithm:**
```typescript
class Deduplicator {
  async deduplicate(
    chunks: ContextChunk[],
    config: DeduplicationConfig
  ): Promise<{ chunks: ContextChunk[]; removedCount: number }> {
    let result = chunks;
    let removedCount = 0;

    // Phase 1: Content hash deduplication (fast)
    if (config.useContentHash) {
      const { unique, removed } = this.hashDeduplicate(result);
      result = unique;
      removedCount += removed;
    }

    // Phase 2: Semantic deduplication (slower but more thorough)
    if (config.useSemantic) {
      const { unique, removed } = await this.semanticDeduplicate(
        result,
        config.similarityThreshold
      );
      result = unique;
      removedCount += removed;
    }

    return { chunks: result, removedCount };
  }

  private hashDeduplicate(chunks: ContextChunk[]): { unique: ContextChunk[]; removed: number } {
    const seen = new Set<string>();
    const unique: ContextChunk[] = [];

    for (const chunk of chunks) {
      const hash = this.computeHash(chunk.content);
      if (!seen.has(hash)) {
        seen.add(hash);
        unique.push(chunk);
      }
    }

    return { unique, removed: chunks.length - unique.length };
  }

  private async semanticDeduplicate(
    chunks: ContextChunk[],
    threshold: number
  ): Promise<{ unique: ContextChunk[]; removed: number }> {
    // Use embeddings to find semantically similar chunks
    // Keep highest relevance chunk from each similarity group
    ...
  }
}
```

### Phase 4: Context Assembly (Week 4)

**Objective:** Implement context assembly and formatting.

**Tasks:**
1. Implement token counting utility
2. Implement smart truncation preserving code structure
3. Implement XML formatter (Claude-optimized)
4. Implement Markdown formatter
5. Implement plain text formatter
6. Add context compression for large chunks
7. Unit tests for formatting accuracy

**Deliverables:**
- Assembler with multi-format support
- Smart truncation algorithm
- Token-accurate assembly
- Format validation tests

**Context Assembly:**
```typescript
class ContextAssembler {
  assemble(
    chunks: ContextChunk[],
    maxTokens: number,
    options: ContextOptions
  ): AssembledContext {
    const selected: ContextChunk[] = [];
    let totalTokens = 0;

    // Select chunks within budget
    for (const chunk of chunks) {
      const chunkTokens = chunk.tokenCount ?? this.countTokens(chunk.content);

      if (totalTokens + chunkTokens > maxTokens) {
        // Try smart truncation for last chunk
        if (options.compress && selected.length === 0) {
          const truncated = this.smartTruncate(chunk, maxTokens - totalTokens);
          if (truncated) {
            selected.push(truncated);
            totalTokens += truncated.tokenCount!;
          }
        }
        break;
      }

      selected.push(chunk);
      totalTokens += chunkTokens;
    }

    // Format based on requested format
    const text = this.format(selected, options.format ?? 'xml');

    return {
      text,
      chunks: selected,
      tokenCount: totalTokens,
      format: options.format ?? 'xml',
    };
  }

  private formatXML(chunks: ContextChunk[]): string {
    return `<retrieved_context>
${chunks.map(chunk => `  <chunk source="${chunk.source}" relevance="${chunk.relevance.toFixed(3)}">
    <location>${chunk.metadata.filePath ?? 'unknown'}${chunk.metadata.startLine ? `:${chunk.metadata.startLine}` : ''}</location>
    <content>
${chunk.content}
    </content>
  </chunk>`).join('\n')}
</retrieved_context>`;
  }
}
```

### Phase 5: Main Aggregator (Week 5)

**Objective:** Implement the main ContextAggregator class.

**Tasks:**
1. Implement `ContextAggregator` class
2. Wire up all components (sources, cache, deduplicator, ranker, assembler)
3. Implement parallel source retrieval with Promise.allSettled
4. Implement graceful degradation on source failures
5. Implement streaming context delivery
6. Add comprehensive logging and metrics
7. Integration tests

**Deliverables:**
- Fully functional ContextAggregator
- End-to-end integration tests
- Error handling for all failure modes
- Metrics emission

**Main Aggregator Flow:**
```typescript
class ContextAggregator implements IContextAggregator {
  private sources: Map<ContextSource, IContextSource> = new Map();
  private cache: IContextCache;
  private budgetManager: BudgetManager;
  private deduplicator: Deduplicator;
  private ranker: ChunkRanker;
  private assembler: ContextAssembler;
  private logger: ILogger;
  private metrics: IMetricsEmitter;

  async getContext(request: ContextRequest): Promise<ContextResponse> {
    const requestId = this.generateId();
    const startTime = Date.now();

    // 1. Check cache
    if (!request.options?.skipCache) {
      const cached = await this.cache.get(this.computeCacheKey(request));
      if (cached) {
        this.metrics.emit('context.cache_hit', { requestId });
        return { ...cached, requestId };
      }
    }

    // 2. Calculate effective budget
    const effectiveBudget = request.maxTokens - (request.reservedTokens ?? 0);

    // 3. Allocate budget across sources
    const sources = request.sources ?? this.budgetManager.getDefaultSources(request.taskType);
    const priorities = {
      ...this.budgetManager.getDefaultPriorities(request.taskType),
      ...request.sourcePriorities,
    };
    const budgetAllocation = this.budgetManager.allocateBudget(sources, priorities, effectiveBudget);

    // 4. Parallel retrieval from all sources
    const retrievalPromises = sources.map(source =>
      this.retrieveFromSource(source, request, budgetAllocation[source])
    );
    const sourceResults = await Promise.allSettled(retrievalPromises);

    // 5. Collect successful results
    const allChunks: ContextChunk[] = [];
    const contributions: SourceContribution[] = [];

    for (const [index, result] of sourceResults.entries()) {
      const source = sources[index];
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
        this.logger.warn(`Source ${source} failed`, { error: result.reason });
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

    // 7. Rank and select
    const ranked = this.ranker.rank(deduped, request);

    // 8. Assemble context
    const assembled = this.assembler.assemble(ranked, effectiveBudget, request.options ?? {});

    // 9. Build response
    const response: ContextResponse = {
      requestId,
      context: assembled,
      sources: contributions,
      metrics: {
        totalLatencyMs: Date.now() - startTime,
        totalTokens: assembled.tokenCount,
        budgetUtilization: assembled.tokenCount / effectiveBudget,
        deduplicationRate: allChunks.length > 0 ? removedCount / allChunks.length : 0,
        cacheHitRate: 0,
        sourcesQueried: sources.length,
        sourcesSucceeded: contributions.filter(c => !c.error).length,
      },
    };

    // 10. Cache result
    if (this.config.caching.enabled && !request.options?.skipCache) {
      await this.cache.set(this.computeCacheKey(request), response);
    }

    // 11. Emit metrics
    this.metrics.emit('context.retrieved', {
      requestId,
      taskType: request.taskType,
      ...response.metrics,
    });

    return response;
  }
}
```

### Phase 6: Redis Cache & Production Hardening (Week 6)

**Objective:** Add Redis cache support and production-ready features.

**Tasks:**
1. Implement Redis cache adapter
2. Add cache invalidation on code changes
3. Add health check endpoint
4. Add comprehensive error handling
5. Add rate limiting for web search
6. Performance optimization pass
7. Load testing

**Deliverables:**
- Redis cache implementation
- Health check API
- Error recovery mechanisms
- Performance benchmarks
- Production configuration guide

---

## Dependencies

### Internal Dependencies

| Dependency | Story | Status | Notes |
|-----------|-------|--------|-------|
| Vector Store | Story 6-2 | Required | `IVectorStore` interface |
| RAG Pipeline | Story 6-3 | Required | `IRAGPipeline` interface |
| MCP Client | Story 6-4 | Required | `IMCPClient` interface |
| Embedding Provider | Story 6-1 | Required | For semantic deduplication |
| Logger | @tamma/observability | Available | `ILogger` interface |
| Metrics | @tamma/observability | Available | Metrics emission |

### External Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `ioredis` | ^5.x | Redis cache support |
| `crypto-js` | ^4.x | Content hashing |
| `tiktoken` | ^1.x | Token counting (optional) |
| `uuid` | ^9.x | Request ID generation |

### Package.json Updates

```json
{
  "dependencies": {
    "@tamma/shared": "workspace:*",
    "@tamma/providers": "workspace:*",
    "@tamma/observability": "workspace:*",
    "ioredis": "^5.3.2",
    "crypto-js": "^4.2.0",
    "uuid": "^9.0.0"
  },
  "devDependencies": {
    "@types/crypto-js": "^4.2.0",
    "@types/uuid": "^9.0.0",
    "typescript": "~5.7.2",
    "vitest": "^1.0.0"
  }
}
```

---

## Testing Strategy

### Unit Tests

| Component | Test Focus | Coverage Target |
|-----------|------------|-----------------|
| BudgetManager | Budget allocation math, task defaults | 100% |
| Deduplicator | Hash dedup, semantic dedup, threshold | 95% |
| Ranker | Scoring, ordering, MMR diversity | 95% |
| Assembler | Token counting, formatting, truncation | 95% |
| MemoryCache | TTL, eviction, stats | 90% |
| RedisCache | Connection, operations, errors | 85% |
| Source Adapters | Query building, response parsing | 90% |

### Integration Tests

| Scenario | Description |
|----------|-------------|
| Multi-source retrieval | Query all sources, verify aggregation |
| Source failure | One source fails, verify graceful degradation |
| Cache behavior | Cache hit/miss, TTL expiration |
| Timeout handling | Source exceeds timeout, verify fallback |
| Budget enforcement | Verify token limits are respected |
| Deduplication | Cross-source duplicate removal |

### Performance Tests

| Metric | Target | Test Method |
|--------|--------|-------------|
| Latency (p95) | < 500ms | 1000 sequential requests |
| Throughput | > 50 req/s | Concurrent load test |
| Memory | < 500MB | Long-running test |
| Cache hit rate | > 50% | Repeated query patterns |

### Test Utilities

```typescript
// packages/intelligence/src/context/__tests__/test-utils.ts

export function createMockVectorStore(): IVectorStore {
  return {
    search: vi.fn().mockResolvedValue([
      { id: '1', score: 0.95, content: 'function foo() {}', metadata: { filePath: 'src/foo.ts' } },
      { id: '2', score: 0.87, content: 'function bar() {}', metadata: { filePath: 'src/bar.ts' } },
    ]),
    // ...other methods
  };
}

export function createMockRAGPipeline(): IRAGPipeline {
  return {
    retrieve: vi.fn().mockResolvedValue({
      chunks: [/* mock chunks */],
      latencyMs: 50,
    }),
    // ...other methods
  };
}

export function createTestContextRequest(overrides?: Partial<ContextRequest>): ContextRequest {
  return {
    query: 'How does authentication work?',
    taskType: 'analysis',
    maxTokens: 4000,
    ...overrides,
  };
}
```

---

## Configuration

### Default Configuration File

```yaml
# config/context-aggregator.yaml

context_aggregator:
  sources:
    vector_db:
      enabled: true
      timeout_ms: 2000
      max_chunks: 20
      retry_attempts: 2

    rag:
      enabled: true
      timeout_ms: 3000
      max_chunks: 15
      retry_attempts: 2

    mcp:
      enabled: true
      timeout_ms: 5000
      max_chunks: 10
      retry_attempts: 1

    web_search:
      enabled: true
      timeout_ms: 5000
      max_chunks: 5
      retry_attempts: 1

    live_api:
      enabled: false
      timeout_ms: 10000
      max_chunks: 5

  budget:
    default_max_tokens: 8000
    reserved_tokens: 1000
    min_chunk_tokens: 50
    max_chunk_tokens: 1000

  deduplication:
    enabled: true
    similarity_threshold: 0.90
    use_semantic: true
    use_content_hash: true

  caching:
    enabled: true
    ttl_seconds: 300
    max_entries: 1000
    provider: memory  # memory | redis
    redis_url: ${REDIS_URL}

  optimization:
    compress_large_chunks: true
    summarize_verbose: false
    smart_truncation: true
    preserve_code_structure: true

  # Task-specific source priorities (higher = more budget)
  task_priorities:
    analysis:
      vector_db: 3
      rag: 2
      mcp: 1
      web_search: 0.5

    planning:
      vector_db: 2
      rag: 3
      mcp: 2
      web_search: 1

    implementation:
      vector_db: 4
      rag: 2
      mcp: 2
      web_search: 1

    review:
      vector_db: 4
      rag: 2
      mcp: 1
      web_search: 0.5

    testing:
      vector_db: 3
      rag: 2
      mcp: 1
      web_search: 1

    documentation:
      vector_db: 2
      rag: 2
      mcp: 1
      web_search: 3
```

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `CONTEXT_AGGREGATOR_CACHE_PROVIDER` | Cache provider (memory/redis) | memory |
| `REDIS_URL` | Redis connection URL | - |
| `CONTEXT_AGGREGATOR_DEFAULT_TIMEOUT_MS` | Default source timeout | 5000 |
| `CONTEXT_AGGREGATOR_MAX_TOKENS` | Default max tokens | 8000 |
| `CONTEXT_AGGREGATOR_CACHE_TTL` | Cache TTL in seconds | 300 |

---

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Context retrieval p95 latency | < 500ms | Percentile calculation over 24h |
| Budget utilization | > 80% | Avg(assembled_tokens / max_tokens) |
| Deduplication rate | > 20% | Avg(removed_chunks / total_chunks) |
| Cache hit rate | > 50% | hits / (hits + misses) |
| Source success rate | > 95% | successful_sources / total_sources |
| Agent task improvement | > 20% | A/B test with/without aggregator |

---

## Risk Mitigation

| Risk | Impact | Mitigation |
|------|--------|------------|
| Source dependencies not ready | High | Mock adapters for development, phased rollout |
| Semantic dedup too slow | Medium | Make semantic dedup optional, optimize batch embedding |
| Token counting inaccuracy | Medium | Use tiktoken library, add calibration tests |
| Redis unavailability | Low | Fallback to memory cache automatically |
| High latency under load | Medium | Connection pooling, query caching, timeout tuning |

---

## Open Questions

1. **Embedding Provider:** Should the aggregator use its own embedding provider or delegate to the RAG pipeline?
   - **Recommendation:** Use a shared embedding service from `@tamma/intelligence`

2. **Cache Invalidation:** How should cache be invalidated when code changes?
   - **Recommendation:** Listen to git hooks via event bus, invalidate by file path patterns

3. **Web Search Rate Limiting:** How to handle rate limits from web search providers?
   - **Recommendation:** Add per-source rate limiter with configurable limits

4. **Context Quality Feedback:** How to collect and use feedback for ranking improvement?
   - **Recommendation:** Defer to future story, design interface now for extensibility
