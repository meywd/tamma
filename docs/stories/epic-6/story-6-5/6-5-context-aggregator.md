# Story 6-5: Context Aggregator Service

## User Story

As a **Tamma engine**, I need a unified context aggregator so that I can gather relevant context from all available sources (Vector DB, RAG, MCP, live search) and provide optimized context to agents.

## Description

Implement a context aggregator service that orchestrates context retrieval from multiple sources, manages token budgets, deduplicates results, and assembles the optimal context window for each agent task.

## Acceptance Criteria

### AC1: Unified Context API
- [ ] Single API for all context retrieval
- [ ] Source-agnostic query interface
- [ ] Support for context requirements specification
- [ ] Async/streaming context delivery

### AC2: Multi-Source Orchestration
- [ ] Parallel queries to all enabled sources
- [ ] Configurable source priorities
- [ ] Graceful degradation on source failure
- [ ] Source timeout handling

### AC3: Token Budget Management
- [ ] Per-task token budget allocation
- [ ] Dynamic budget adjustment based on task complexity
- [ ] Reserved tokens for system/user prompts
- [ ] Overflow handling strategies

### AC4: Context Deduplication
- [ ] Semantic deduplication across sources
- [ ] Content hash deduplication
- [ ] Merge overlapping code chunks
- [ ] Preserve highest-relevance version

### AC5: Context Optimization
- [ ] Chunk ordering by relevance
- [ ] Context compression for large files
- [ ] Summary generation for verbose content
- [ ] Smart truncation preserving structure

### AC6: Caching Layer
- [ ] Query-level caching
- [ ] Source-level caching
- [ ] Cache invalidation on code changes
- [ ] Distributed cache support (Redis)

### AC7: Observability
- [ ] Context retrieval metrics
- [ ] Source latency tracking
- [ ] Token usage analytics
- [ ] Quality scoring (if feedback available)

## Technical Design

### Context Aggregator Interface

```typescript
interface IContextAggregator {
  // Configuration
  configure(config: AggregatorConfig): Promise<void>;

  // Main API
  getContext(request: ContextRequest): Promise<ContextResponse>;

  // Streaming API
  streamContext(request: ContextRequest): AsyncIterable<ContextChunk>;

  // Cache management
  invalidateCache(pattern?: string): Promise<void>;
  getCacheStats(): CacheStats;
}

interface ContextRequest {
  // Query
  query: string;
  taskType: TaskType;

  // Token budget
  maxTokens: number;
  reservedTokens?: number;  // For system/user prompts

  // Source configuration
  sources?: ContextSource[];
  sourcePriorities?: Record<ContextSource, number>;

  // Context hints
  hints?: {
    relatedFiles?: string[];
    relatedIssues?: number[];
    language?: string;
    framework?: string;
  };

  // Options
  options?: {
    deduplicate?: boolean;
    compress?: boolean;
    summarize?: boolean;
    includeMetadata?: boolean;
  };
}

type ContextSource = 'vector_db' | 'rag' | 'mcp' | 'web_search' | 'live_api';
type TaskType = 'analysis' | 'planning' | 'implementation' | 'review' | 'testing' | 'documentation';

interface ContextResponse {
  requestId: string;
  context: AssembledContext;
  sources: SourceContribution[];
  metrics: ContextMetrics;
}

interface AssembledContext {
  text: string;
  chunks: ContextChunk[];
  tokenCount: number;
  format: 'xml' | 'markdown' | 'plain';
}

interface ContextChunk {
  id: string;
  content: string;
  source: ContextSource;
  relevance: number;
  metadata: ChunkMetadata;
}

interface SourceContribution {
  source: ContextSource;
  chunksProvided: number;
  tokensUsed: number;
  latencyMs: number;
  cacheHit: boolean;
}

interface ContextMetrics {
  totalLatencyMs: number;
  totalTokens: number;
  budgetUtilization: number;
  deduplicationRate: number;
  cacheHitRate: number;
}
```

### Context Aggregator Implementation

```typescript
class ContextAggregator implements IContextAggregator {
  private vectorStore: IVectorStore;
  private ragPipeline: IRAGPipeline;
  private mcpClient: IMCPClient;
  private webSearch: IWebSearch;
  private cache: IContextCache;
  private embedder: IEmbeddingProvider;

  async getContext(request: ContextRequest): Promise<ContextResponse> {
    const requestId = generateId();
    const startTime = Date.now();

    // 1. Check cache
    const cacheKey = this.computeCacheKey(request);
    const cached = await this.cache.get(cacheKey);
    if (cached) {
      return { ...cached, metrics: { ...cached.metrics, cacheHit: true } };
    }

    // 2. Determine effective budget
    const effectiveBudget = request.maxTokens - (request.reservedTokens ?? 0);

    // 3. Allocate budget across sources
    const budgetAllocation = this.allocateBudget(
      request.sources ?? this.getDefaultSources(request.taskType),
      request.sourcePriorities ?? this.getDefaultPriorities(request.taskType),
      effectiveBudget
    );

    // 4. Parallel retrieval from all sources
    const retrievalPromises = Object.entries(budgetAllocation).map(
      ([source, budget]) => this.retrieveFromSource(source as ContextSource, request, budget)
    );

    const sourceResults = await Promise.allSettled(retrievalPromises);

    // 5. Collect successful results
    const allChunks: ContextChunk[] = [];
    const contributions: SourceContribution[] = [];

    for (const [index, result] of sourceResults.entries()) {
      const source = Object.keys(budgetAllocation)[index] as ContextSource;
      if (result.status === 'fulfilled') {
        allChunks.push(...result.value.chunks);
        contributions.push(result.value.contribution);
      } else {
        this.logger.warn(`Source ${source} failed: ${result.reason}`);
        contributions.push({
          source,
          chunksProvided: 0,
          tokensUsed: 0,
          latencyMs: 0,
          cacheHit: false,
        });
      }
    }

    // 6. Deduplicate
    const deduped = request.options?.deduplicate !== false
      ? await this.deduplicateChunks(allChunks)
      : allChunks;

    // 7. Rank and select within budget
    const ranked = this.rankChunks(deduped, request);
    const selected = this.selectWithinBudget(ranked, effectiveBudget);

    // 8. Assemble context
    const assembled = this.assembleContext(selected, request.options);

    // 9. Build response
    const response: ContextResponse = {
      requestId,
      context: assembled,
      sources: contributions,
      metrics: {
        totalLatencyMs: Date.now() - startTime,
        totalTokens: assembled.tokenCount,
        budgetUtilization: assembled.tokenCount / effectiveBudget,
        deduplicationRate: 1 - (deduped.length / allChunks.length),
        cacheHitRate: 0,
      },
    };

    // 10. Cache result
    await this.cache.set(cacheKey, response);

    return response;
  }

  private allocateBudget(
    sources: ContextSource[],
    priorities: Record<ContextSource, number>,
    totalBudget: number
  ): Record<ContextSource, number> {
    const totalPriority = sources.reduce((sum, s) => sum + (priorities[s] ?? 1), 0);

    return Object.fromEntries(
      sources.map(source => [
        source,
        Math.floor(totalBudget * (priorities[source] ?? 1) / totalPriority)
      ])
    );
  }

  private getDefaultSources(taskType: TaskType): ContextSource[] {
    const sourcesByTask: Record<TaskType, ContextSource[]> = {
      analysis: ['vector_db', 'rag'],
      planning: ['vector_db', 'rag', 'mcp'],
      implementation: ['vector_db', 'rag', 'mcp', 'web_search'],
      review: ['vector_db', 'rag'],
      testing: ['vector_db', 'rag'],
      documentation: ['vector_db', 'rag', 'web_search'],
    };
    return sourcesByTask[taskType];
  }

  private getDefaultPriorities(taskType: TaskType): Record<ContextSource, number> {
    // Higher = more budget allocation
    const prioritiesByTask: Record<TaskType, Record<ContextSource, number>> = {
      analysis: { vector_db: 3, rag: 2, mcp: 1, web_search: 0.5, live_api: 0.5 },
      planning: { vector_db: 2, rag: 3, mcp: 2, web_search: 1, live_api: 0.5 },
      implementation: { vector_db: 4, rag: 2, mcp: 2, web_search: 1, live_api: 0.5 },
      review: { vector_db: 4, rag: 2, mcp: 1, web_search: 0.5, live_api: 0.5 },
      testing: { vector_db: 3, rag: 2, mcp: 1, web_search: 1, live_api: 0.5 },
      documentation: { vector_db: 2, rag: 2, mcp: 1, web_search: 3, live_api: 0.5 },
    };
    return prioritiesByTask[taskType];
  }

  private async deduplicateChunks(chunks: ContextChunk[]): Promise<ContextChunk[]> {
    // Content hash deduplication
    const seen = new Set<string>();
    const unique: ContextChunk[] = [];

    for (const chunk of chunks) {
      const hash = this.hashContent(chunk.content);
      if (!seen.has(hash)) {
        seen.add(hash);
        unique.push(chunk);
      }
    }

    // Semantic deduplication (merge similar chunks)
    return this.mergeSimilarChunks(unique, 0.9); // 90% similarity threshold
  }

  private async mergeSimilarChunks(
    chunks: ContextChunk[],
    threshold: number
  ): Promise<ContextChunk[]> {
    // Get embeddings for all chunks
    const embeddings = await this.embedder.embedBatch(chunks.map(c => c.content));

    const merged: ContextChunk[] = [];
    const used = new Set<number>();

    for (let i = 0; i < chunks.length; i++) {
      if (used.has(i)) continue;

      const group = [chunks[i]];
      used.add(i);

      for (let j = i + 1; j < chunks.length; j++) {
        if (used.has(j)) continue;

        const similarity = this.cosineSimilarity(embeddings[i], embeddings[j]);
        if (similarity >= threshold) {
          group.push(chunks[j]);
          used.add(j);
        }
      }

      // Keep highest relevance chunk from group
      const best = group.reduce((a, b) => a.relevance > b.relevance ? a : b);
      merged.push(best);
    }

    return merged;
  }
}
```

## Dependencies

- Story 6-2: Vector Database Integration
- Story 6-3: RAG Pipeline
- Story 6-4: MCP Client Integration
- Redis (optional, for distributed caching)
- Embedding provider

## Testing Strategy

### Unit Tests
- Budget allocation logic
- Deduplication algorithms
- Chunk ranking
- Context assembly

### Integration Tests
- Multi-source retrieval
- Cache behavior
- Timeout handling
- Graceful degradation

### Performance Tests
- Latency under load
- Memory usage
- Cache efficiency

## Configuration

```yaml
context_aggregator:
  sources:
    vector_db:
      enabled: true
      timeout_ms: 2000
    rag:
      enabled: true
      timeout_ms: 3000
    mcp:
      enabled: true
      timeout_ms: 5000
    web_search:
      enabled: true
      timeout_ms: 5000

  budget:
    default_max_tokens: 8000
    reserved_tokens: 1000

  deduplication:
    enabled: true
    similarity_threshold: 0.9
    use_semantic: true

  caching:
    enabled: true
    ttl_seconds: 300
    provider: memory  # memory | redis

  optimization:
    compress_large_chunks: true
    summarize_verbose: false
    smart_truncation: true
```

## Success Metrics

- Context retrieval p95 latency < 500ms
- Budget utilization > 80%
- Deduplication rate > 20%
- Cache hit rate > 50%
- Agent improvement with aggregated context > 20%

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
