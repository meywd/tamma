---
title: "Story 6-3: RAG Pipeline Implementation Plan"
sidebar:
  order: 60
---

## Overview

This document provides a detailed implementation plan for the RAG (Retrieval Augmented Generation) Pipeline, which retrieves relevant context from multiple sources, ranks and assembles it within token budgets, and augments agent prompts with this context.

## Package Location

**Primary Package:** `@tamma/intelligence`

**Location:** `/packages/intelligence/`

The RAG pipeline will be implemented within the `@tamma/intelligence` package, which is the designated package for research and context gathering capabilities. This package already has dependencies on `@tamma/shared`, `@tamma/providers`, and `@tamma/observability`.

## Dependencies

### Internal Dependencies
- `@tamma/shared` - Common types and utilities
- `@tamma/providers` - Embedding provider integration (for query embeddings)
- `@tamma/observability` - Metrics and logging

### External Dependencies (to add to package.json)
```json
{
  "dependencies": {
    "tiktoken": "^1.0.0",
    "lru-cache": "^10.0.0"
  },
  "devDependencies": {
    "vitest": "^1.0.0"
  }
}
```

### Cross-Story Dependencies
- **Story 6-1: Codebase Indexer** - Provides indexed code chunks with embeddings
- **Story 6-2: Vector Database Integration** - Provides `IVectorStore` interface for semantic search
- **Story 6-5: Context Aggregator** - Will consume RAG pipeline output

---

## Files to Create/Modify

### Directory Structure

```
packages/intelligence/
├── src/
│   ├── index.ts                          # (modify) Export RAG pipeline
│   ├── rag/
│   │   ├── index.ts                      # RAG module exports
│   │   ├── types.ts                      # RAG interfaces and types
│   │   ├── rag-pipeline.ts               # Main RAG pipeline implementation
│   │   ├── query-processor.ts            # Query processing and expansion
│   │   ├── retriever.ts                  # Multi-source retrieval
│   │   ├── ranker.ts                     # Result ranking (RRF, MMR)
│   │   ├── assembler.ts                  # Context assembly and formatting
│   │   ├── cache.ts                      # Query and embedding caching
│   │   ├── feedback.ts                   # Feedback collection and tracking
│   │   └── sources/
│   │       ├── index.ts                  # Source exports
│   │       ├── source-interface.ts       # IRAGSource interface
│   │       ├── vector-source.ts          # Vector DB source adapter
│   │       ├── keyword-source.ts         # Keyword/BM25 source adapter
│   │       ├── docs-source.ts            # Documentation source adapter
│   │       └── github-source.ts          # GitHub issues/PRs/commits source
│   └── __tests__/
│       ├── rag/
│       │   ├── rag-pipeline.test.ts
│       │   ├── query-processor.test.ts
│       │   ├── ranker.test.ts
│       │   ├── assembler.test.ts
│       │   └── cache.test.ts
├── package.json                          # (modify) Add dependencies
└── tsconfig.json
```

---

## Interfaces and Types

### Core Interfaces

```typescript
// /packages/intelligence/src/rag/types.ts

/**
 * RAG Pipeline Configuration
 */
export interface RAGConfig {
  sources: RAGSourceConfig;
  ranking: RankingConfig;
  assembly: AssemblyConfig;
  caching: CachingConfig;
  timeouts: TimeoutConfig;
}

export interface RAGSourceConfig {
  vector_db: SourceSettings;
  keyword: SourceSettings;
  docs: SourceSettings;
  issues: SourceSettings;
  prs: SourceSettings;
  commits: SourceSettings;
}

export interface SourceSettings {
  enabled: boolean;
  weight: number;
  topK: number;
}

export interface RankingConfig {
  fusionMethod: 'rrf' | 'linear' | 'learned';
  rrfK: number;              // RRF constant (default: 60)
  mmrLambda: number;         // MMR balance (default: 0.7)
  recencyBoost: number;      // Boost for recent content (default: 0.1)
  recencyDecayDays: number;  // Days for recency decay (default: 30)
}

export interface AssemblyConfig {
  maxTokens: number;         // Token budget (default: 4000)
  format: ContextFormat;     // Output format
  includeScores: boolean;    // Include relevance scores
  deduplicationThreshold: number; // Similarity threshold for dedup
}

export interface CachingConfig {
  enabled: boolean;
  ttlSeconds: number;
  maxEntries: number;
}

export interface TimeoutConfig {
  perSourceMs: number;       // Timeout per source (default: 2000)
  totalMs: number;           // Total pipeline timeout (default: 5000)
}

/**
 * RAG Query
 */
export interface RAGQuery {
  text: string;                      // Natural language query
  context?: QueryContext;            // Additional context
  sources?: RAGSourceType[];         // Which sources to query
  maxTokens?: number;                // Token budget for context
  topK?: number;                     // Max results per source
}

export interface QueryContext {
  issueNumber?: number;
  filePath?: string;
  language?: string;
  projectId?: string;
  recentFiles?: string[];            // Recently viewed files
}

export type RAGSourceType = 'vector_db' | 'keyword' | 'docs' | 'issues' | 'prs' | 'commits';

/**
 * RAG Result
 */
export interface RAGResult {
  queryId: string;
  retrievedChunks: RetrievedChunk[];
  assembledContext: string;
  tokenCount: number;
  sources: SourceAttribution[];
  latencyMs: number;
  cacheHit: boolean;
}

export interface RetrievedChunk {
  id: string;
  content: string;
  source: RAGSourceType;
  score: number;
  fusedScore?: number;
  metadata: ChunkMetadata;
  embedding?: number[];              // For MMR calculation
}

export interface ChunkMetadata {
  filePath?: string;
  startLine?: number;
  endLine?: number;
  url?: string;
  date?: Date;
  author?: string;
  title?: string;
  language?: string;
  symbols?: string[];
}

export interface SourceAttribution {
  source: RAGSourceType;
  count: number;
  avgScore: number;
  latencyMs: number;
}

/**
 * Context Formatting
 */
export type ContextFormat = 'xml' | 'markdown' | 'plain' | 'json';

export interface AssembledContext {
  chunks: RetrievedChunk[];
  text: string;
  tokenCount: number;
  truncated: boolean;
}

/**
 * Feedback
 */
export interface RelevanceFeedback {
  queryId: string;
  chunkId: string;
  rating: 'helpful' | 'not_helpful' | 'partially_helpful';
  comment?: string;
  timestamp: Date;
}

export interface FeedbackStats {
  queryId: string;
  totalFeedback: number;
  helpfulCount: number;
  avgRating: number;
}

/**
 * Query Processing
 */
export interface ProcessedQuery {
  original: string;
  expanded: string[];                // Expanded query terms
  entities: ExtractedEntity[];       // Extracted entities
  decomposed?: string[];             // Sub-queries for complex queries
  language?: string;                 // Detected language
  embedding?: number[];              // Query embedding
}

export interface ExtractedEntity {
  type: 'file' | 'function' | 'class' | 'variable' | 'package';
  value: string;
  confidence: number;
}
```

### Source Interface

```typescript
// /packages/intelligence/src/rag/sources/source-interface.ts

export interface IRAGSource {
  readonly name: RAGSourceType;
  readonly enabled: boolean;

  initialize(config: SourceSettings): Promise<void>;
  retrieve(query: ProcessedQuery, options: RetrieveOptions): Promise<RetrievedChunk[]>;
  healthCheck(): Promise<boolean>;
  dispose(): Promise<void>;
}

export interface RetrieveOptions {
  topK: number;
  filter?: SourceFilter;
  timeout?: number;
}

export interface SourceFilter {
  filePaths?: string[];
  languages?: string[];
  dateRange?: { start: Date; end: Date };
  authors?: string[];
}
```

### Main Pipeline Interface

```typescript
// /packages/intelligence/src/rag/types.ts

export interface IRAGPipeline {
  // Configuration
  configure(config: RAGConfig): Promise<void>;

  // Main retrieval
  retrieve(query: RAGQuery): Promise<RAGResult>;

  // Feedback
  recordFeedback(feedback: RelevanceFeedback): Promise<void>;
  getFeedbackStats(queryId: string): Promise<FeedbackStats>;

  // Lifecycle
  dispose(): Promise<void>;
}
```

---

## Implementation Phases

### Phase 1: Core Infrastructure (Week 1)

**Objective:** Set up the basic RAG pipeline structure with types and core classes.

**Tasks:**
1. Create directory structure and type definitions
2. Implement `RAGPipeline` class skeleton
3. Implement `IRAGSource` interface
4. Create basic caching infrastructure using LRU cache
5. Set up token counting utilities (tiktoken integration)

**Deliverables:**
- `types.ts` - All interfaces and types
- `rag-pipeline.ts` - Pipeline class skeleton
- `cache.ts` - Caching implementation
- `sources/source-interface.ts` - Source interface

**Files:**
```
packages/intelligence/src/rag/
├── index.ts
├── types.ts
├── rag-pipeline.ts
├── cache.ts
└── sources/
    ├── index.ts
    └── source-interface.ts
```

---

### Phase 2: Query Processing (Week 1-2)

**Objective:** Implement query processing, expansion, and entity extraction.

**Tasks:**
1. Implement query expansion with synonyms and related terms
2. Implement entity extraction (file names, function names, etc.)
3. Implement query decomposition for complex queries
4. Add language detection for multilingual support
5. Integrate with embedding provider for query embeddings

**Deliverables:**
- `query-processor.ts` - Complete query processing module

**Key Functions:**
```typescript
class QueryProcessor {
  async process(query: RAGQuery): Promise<ProcessedQuery>;
  private expandQuery(text: string): string[];
  private extractEntities(text: string): ExtractedEntity[];
  private decomposeQuery(text: string): string[];
  private detectLanguage(text: string): string;
  private generateEmbedding(text: string): Promise<number[]>;
}
```

---

### Phase 3: Multi-Source Retrieval (Week 2)

**Objective:** Implement source adapters for vector DB, keyword search, and documentation.

**Tasks:**
1. Implement `VectorSource` adapter (integrates with Story 6-2)
2. Implement `KeywordSource` adapter (BM25/full-text search)
3. Implement `DocsSource` adapter (markdown documentation)
4. Create parallel retrieval orchestration with timeouts

**Deliverables:**
- `retriever.ts` - Multi-source retrieval orchestrator
- `sources/vector-source.ts` - Vector DB adapter
- `sources/keyword-source.ts` - Keyword search adapter
- `sources/docs-source.ts` - Documentation adapter

**Key Functions:**
```typescript
class Retriever {
  async retrieveFromAllSources(
    query: ProcessedQuery,
    sources: RAGSourceType[],
    config: RAGConfig
  ): Promise<Map<RAGSourceType, RetrievedChunk[]>>;

  private async retrieveWithTimeout(
    source: IRAGSource,
    query: ProcessedQuery,
    timeout: number
  ): Promise<RetrievedChunk[]>;
}
```

---

### Phase 4: GitHub Integration (Week 2-3)

**Objective:** Implement retrieval from GitHub issues, PRs, and commits.

**Tasks:**
1. Implement `GitHubSource` adapter for issues
2. Add PR retrieval support
3. Add commit history retrieval
4. Implement recency filtering for GitHub data

**Deliverables:**
- `sources/github-source.ts` - GitHub data adapter

---

### Phase 5: Result Ranking (Week 3)

**Objective:** Implement ranking algorithms (RRF, MMR) and score normalization.

**Tasks:**
1. Implement Reciprocal Rank Fusion (RRF) for multi-source merging
2. Implement relevance score normalization
3. Implement recency boosting
4. Implement Max Marginal Relevance (MMR) for diversity
5. Add configurable ranking weights

**Deliverables:**
- `ranker.ts` - Complete ranking module

**Key Functions:**
```typescript
class Ranker {
  mergeWithRRF(sourceResults: Map<RAGSourceType, RetrievedChunk[]>): RetrievedChunk[];
  normalizeScores(chunks: RetrievedChunk[]): RetrievedChunk[];
  applyRecencyBoost(chunks: RetrievedChunk[], config: RankingConfig): RetrievedChunk[];
  applyMMR(chunks: RetrievedChunk[], k: number, lambda: number): RetrievedChunk[];
}
```

**Algorithm Details:**

```typescript
// Reciprocal Rank Fusion
private mergeWithRRF(
  sourceResults: Map<RAGSourceType, RetrievedChunk[]>,
  k: number = 60
): RetrievedChunk[] {
  const scores = new Map<string, number>();
  const chunks = new Map<string, RetrievedChunk>();

  for (const [source, results] of sourceResults) {
    const weight = this.config.sources[source].weight;
    results.forEach((result, rank) => {
      const rrf = weight * (1 / (k + rank + 1));
      const current = scores.get(result.id) ?? 0;
      scores.set(result.id, current + rrf);
      chunks.set(result.id, result);
    });
  }

  return Array.from(scores.entries())
    .map(([id, score]) => ({ ...chunks.get(id)!, fusedScore: score }))
    .sort((a, b) => b.fusedScore! - a.fusedScore!);
}
```

---

### Phase 6: Context Assembly (Week 3-4)

**Objective:** Implement context assembly with token budget management.

**Tasks:**
1. Implement token budget management
2. Implement chunk deduplication
3. Implement multiple output formats (XML, Markdown, Plain, JSON)
4. Add source attribution
5. Optimize context window usage

**Deliverables:**
- `assembler.ts` - Context assembly module

**Key Functions:**
```typescript
class ContextAssembler {
  assemble(chunks: RetrievedChunk[], config: AssemblyConfig): AssembledContext;
  private deduplicateChunks(chunks: RetrievedChunk[], threshold: number): RetrievedChunk[];
  private formatContext(chunks: RetrievedChunk[], format: ContextFormat): string;
  private countTokens(text: string): number;
}
```

**Format Examples:**

```typescript
// XML Format (recommended for Claude)
private formatAsXML(chunks: RetrievedChunk[]): string {
  return `<retrieved_context>
${chunks.map(chunk => `  <chunk source="${chunk.source}" score="${chunk.score.toFixed(3)}">
    <location>${chunk.metadata.filePath ?? 'unknown'}:${chunk.metadata.startLine ?? '?'}</location>
    <content>
${chunk.content}
    </content>
  </chunk>`).join('\n')}
</retrieved_context>`;
}

// Markdown Format
private formatAsMarkdown(chunks: RetrievedChunk[]): string {
  return chunks.map(chunk => {
    const header = chunk.metadata.filePath
      ? `### ${chunk.metadata.filePath}:${chunk.metadata.startLine}-${chunk.metadata.endLine}`
      : `### ${chunk.source}`;
    return `${header}\n\n\`\`\`${chunk.metadata.language ?? ''}\n${chunk.content}\n\`\`\``;
  }).join('\n\n---\n\n');
}
```

---

### Phase 7: Feedback System (Week 4)

**Objective:** Implement feedback collection and relevance tracking.

**Tasks:**
1. Implement feedback recording
2. Track which context chunks were used
3. Store relevance feedback (implicit and explicit)
4. Implement feedback statistics calculation
5. Design feedback-based ranking improvements (future)

**Deliverables:**
- `feedback.ts` - Feedback collection module

**Key Functions:**
```typescript
class FeedbackTracker {
  async recordFeedback(feedback: RelevanceFeedback): Promise<void>;
  async getQueryFeedback(queryId: string): Promise<RelevanceFeedback[]>;
  async getFeedbackStats(queryId: string): Promise<FeedbackStats>;
  async trackContextUsage(queryId: string, usedChunkIds: string[]): Promise<void>;
}
```

---

### Phase 8: Caching & Performance (Week 4)

**Objective:** Implement caching and performance optimizations.

**Tasks:**
1. Implement query result caching with TTL
2. Implement embedding cache for repeated queries
3. Optimize parallel retrieval
4. Add configurable timeouts per source
5. Implement cache invalidation strategies

**Deliverables:**
- Enhanced `cache.ts` with query and embedding caching

**Key Functions:**
```typescript
class RAGCache {
  // Query result cache
  async getCachedResult(query: RAGQuery): Promise<RAGResult | null>;
  async cacheResult(query: RAGQuery, result: RAGResult): Promise<void>;

  // Embedding cache
  async getCachedEmbedding(text: string): Promise<number[] | null>;
  async cacheEmbedding(text: string, embedding: number[]): Promise<void>;

  // Cache management
  invalidate(pattern?: string): Promise<void>;
  getStats(): CacheStats;
}
```

---

### Phase 9: Integration & Testing (Week 5)

**Objective:** Complete integration testing and performance validation.

**Tasks:**
1. Integration tests with vector store (Story 6-2)
2. End-to-end pipeline tests
3. Performance benchmarking
4. Load testing
5. Documentation

---

## Testing Strategy

### Unit Tests

**Location:** `/packages/intelligence/src/__tests__/rag/`

```typescript
// rag-pipeline.test.ts
describe('RAGPipeline', () => {
  describe('configure', () => {
    it('should initialize with valid config');
    it('should throw on invalid config');
    it('should enable only specified sources');
  });

  describe('retrieve', () => {
    it('should return results for valid query');
    it('should respect token budget');
    it('should return cache hit when available');
    it('should handle source timeouts gracefully');
  });
});

// query-processor.test.ts
describe('QueryProcessor', () => {
  describe('expandQuery', () => {
    it('should expand query with synonyms');
    it('should handle empty query');
  });

  describe('extractEntities', () => {
    it('should extract file paths');
    it('should extract function names');
    it('should extract class names');
  });

  describe('decomposeQuery', () => {
    it('should split complex queries');
    it('should not split simple queries');
  });
});

// ranker.test.ts
describe('Ranker', () => {
  describe('mergeWithRRF', () => {
    it('should correctly fuse results from multiple sources');
    it('should respect source weights');
    it('should handle empty source results');
  });

  describe('applyMMR', () => {
    it('should select diverse results');
    it('should balance relevance and diversity');
  });

  describe('applyRecencyBoost', () => {
    it('should boost recent content');
    it('should decay boost for older content');
  });
});

// assembler.test.ts
describe('ContextAssembler', () => {
  describe('assemble', () => {
    it('should respect token budget');
    it('should deduplicate similar chunks');
    it('should format as XML correctly');
    it('should format as Markdown correctly');
  });
});

// cache.test.ts
describe('RAGCache', () => {
  describe('query caching', () => {
    it('should cache and retrieve results');
    it('should respect TTL');
    it('should evict LRU entries');
  });
});
```

### Integration Tests

```typescript
// rag-integration.test.ts
describe('RAG Pipeline Integration', () => {
  it('should retrieve from vector store');
  it('should retrieve from keyword index');
  it('should merge results from multiple sources');
  it('should handle partial source failures');
  it('should record and retrieve feedback');
});
```

### Performance Tests

```typescript
// rag-performance.test.ts
describe('RAG Pipeline Performance', () => {
  it('should complete retrieval in < 200ms (p95)', async () => {
    const latencies: number[] = [];
    for (let i = 0; i < 100; i++) {
      const start = Date.now();
      await pipeline.retrieve({ text: testQueries[i % testQueries.length] });
      latencies.push(Date.now() - start);
    }
    const p95 = percentile(latencies, 95);
    expect(p95).toBeLessThan(200);
  });

  it('should achieve > 60% cache hit rate');
  it('should handle 100 concurrent queries');
});
```

---

## Configuration

### Default Configuration

```yaml
# /packages/intelligence/config/rag-defaults.yaml
rag:
  sources:
    vector_db:
      enabled: true
      weight: 1.0
      top_k: 20
    keyword:
      enabled: true
      weight: 0.5
      top_k: 10
    docs:
      enabled: true
      weight: 0.3
      top_k: 5
    issues:
      enabled: true
      weight: 0.2
      top_k: 5
    prs:
      enabled: false
      weight: 0.2
      top_k: 5
    commits:
      enabled: false
      weight: 0.1
      top_k: 5

  ranking:
    fusion_method: rrf
    rrf_k: 60
    mmr_lambda: 0.7
    recency_boost: 0.1
    recency_decay_days: 30

  assembly:
    max_tokens: 4000
    format: xml
    include_scores: false
    deduplication_threshold: 0.85

  caching:
    enabled: true
    ttl_seconds: 300
    max_entries: 1000

  timeouts:
    per_source_ms: 2000
    total_ms: 5000
```

### Environment Variables

```bash
# Embedding provider
RAG_EMBEDDING_PROVIDER=openai
RAG_EMBEDDING_MODEL=text-embedding-3-small

# Cache settings
RAG_CACHE_ENABLED=true
RAG_CACHE_TTL_SECONDS=300

# Performance settings
RAG_MAX_TOKENS=4000
RAG_PER_SOURCE_TIMEOUT_MS=2000
RAG_TOTAL_TIMEOUT_MS=5000
```

---

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Retrieval latency p95 | < 200ms | Timer in `retrieve()` |
| Context relevance score | > 0.8 | Human evaluation |
| Cache hit rate | > 60% | Cache stats |
| Agent task improvement | > 15% | A/B testing |
| Token budget adherence | 100% | Assembly validation |

---

## Migration & Rollout

### Phase 1: Internal Testing
- Deploy to development environment
- Run against test projects
- Collect baseline metrics

### Phase 2: Beta Rollout
- Enable for select projects
- Monitor performance and relevance
- Gather user feedback

### Phase 3: General Availability
- Enable by default for all projects
- Full documentation
- Performance tuning based on production data

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Source timeout causing delays | Parallel retrieval with individual timeouts; graceful degradation |
| Low relevance results | Feedback loop for continuous improvement; tunable ranking weights |
| High embedding costs | Embedding caching; batch processing |
| Memory pressure from large results | Token budget enforcement; streaming assembly |
| Cache staleness | Configurable TTL; cache invalidation on index updates |

---

## Open Questions

1. **Embedding provider selection:** Should RAG use the same embedding provider as the indexer, or should it be configurable separately?
   - **Recommendation:** Same provider for consistency, but allow override via config.

2. **Keyword search backend:** Which keyword search to use (Elasticsearch, Meilisearch, SQLite FTS)?
   - **Recommendation:** Start with SQLite FTS for simplicity, add Elasticsearch adapter later.

3. **Feedback storage:** Where should feedback be stored (in-memory, database, separate service)?
   - **Recommendation:** Database (PostgreSQL) for persistence and analytics.

4. **Real-time vs. batch feedback processing:** Should feedback immediately affect ranking, or be processed in batches?
   - **Recommendation:** Batch processing initially; real-time as a future enhancement.

---

## References

- [Story 6-3: RAG Pipeline](./6-3-rag-pipeline.md)
- [Story 6-1: Codebase Indexer](../story-6-1/6-1-codebase-indexer.md)
- [Story 6-2: Vector Database Integration](../story-6-2/6-2-vector-database-integration.md)
- [Engine Flow Architecture](../../../architecture/engine-flow.md)
- [Provider Research](../../../architecture/provider-research.md)
