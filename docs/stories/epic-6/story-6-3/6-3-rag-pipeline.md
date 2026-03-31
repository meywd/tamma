# Story 6-3: RAG Pipeline Implementation

## User Story

As a **Tamma agent**, I need a RAG (Retrieval Augmented Generation) pipeline so that my responses are grounded in relevant context from the codebase, documentation, and historical data.

## Description

Implement a RAG pipeline that retrieves relevant context from multiple sources (vector DB, keyword search, documentation), ranks and assembles it within token budgets, and augments agent prompts with this context.

## Acceptance Criteria

### AC1: Multi-Source Retrieval
- [ ] Query vector database for semantic code search
- [ ] Query keyword index (BM25) for exact matches
- [ ] Query documentation store (markdown, wiki)
- [ ] Query historical data (past issues, PRs, commits)
- [ ] Support configurable source weights

### AC2: Query Processing
- [ ] Query expansion (synonyms, related terms)
- [ ] Query decomposition for complex queries
- [ ] Entity extraction (file names, function names)
- [ ] Language detection for multilingual repos

### AC3: Result Ranking
- [ ] Reciprocal Rank Fusion (RRF) for multi-source
- [ ] Relevance scoring normalization
- [ ] Recency boosting for recent changes
- [ ] Diversity via Max Marginal Relevance (MMR)
- [ ] Configurable ranking weights

### AC4: Context Assembly
- [ ] Token budget management
- [ ] Chunk deduplication
- [ ] Context window optimization
- [ ] Source attribution (file path, line numbers)
- [ ] Structured context formatting

### AC5: Caching & Performance
- [ ] Query result caching (TTL-based)
- [ ] Embedding cache for repeated queries
- [ ] Async parallel retrieval from sources
- [ ] Configurable timeout per source

### AC6: Feedback Loop
- [ ] Track which context was used
- [ ] Log relevance feedback (implicit/explicit)
- [ ] Support context quality rating
- [ ] Use feedback to improve ranking

## Technical Design

### RAG Pipeline Architecture

```typescript
interface IRAGPipeline {
  // Configuration
  configure(config: RAGConfig): Promise<void>;

  // Main retrieval
  retrieve(query: RAGQuery): Promise<RAGResult>;

  // Feedback
  recordFeedback(queryId: string, feedback: RelevanceFeedback): Promise<void>;
}

interface RAGQuery {
  text: string;                      // Natural language query
  context?: {
    issueNumber?: number;
    filePath?: string;
    language?: string;
  };
  sources?: RAGSource[];             // Which sources to query
  maxTokens?: number;                // Token budget for context
  topK?: number;                     // Max results per source
}

type RAGSource = 'vector_db' | 'keyword' | 'docs' | 'issues' | 'prs' | 'commits';

interface RAGResult {
  queryId: string;
  retrievedChunks: RetrievedChunk[];
  assembledContext: string;
  tokenCount: number;
  sources: SourceAttribution[];
  latencyMs: number;
}

interface RetrievedChunk {
  id: string;
  content: string;
  source: RAGSource;
  score: number;
  metadata: {
    filePath?: string;
    startLine?: number;
    endLine?: number;
    url?: string;
    date?: Date;
  };
}

interface SourceAttribution {
  source: RAGSource;
  count: number;
  avgScore: number;
}
```

### Retrieval Strategy

```typescript
class RAGPipeline implements IRAGPipeline {
  private vectorStore: IVectorStore;
  private keywordIndex: IKeywordIndex;
  private docStore: IDocumentStore;
  private embedder: IEmbeddingProvider;

  async retrieve(query: RAGQuery): Promise<RAGResult> {
    const queryId = generateId();
    const startTime = Date.now();

    // 1. Process query
    const processedQuery = await this.processQuery(query);

    // 2. Parallel retrieval from all sources
    const sources = query.sources ?? ['vector_db', 'keyword', 'docs'];
    const retrievalPromises = sources.map(source =>
      this.retrieveFromSource(source, processedQuery)
    );

    const sourceResults = await Promise.all(retrievalPromises);

    // 3. Merge and rank results
    const mergedResults = this.mergeResults(sourceResults);
    const rankedResults = this.rankResults(mergedResults, query);

    // 4. Apply MMR for diversity
    const diverseResults = this.applyMMR(rankedResults, query.topK ?? 10);

    // 5. Assemble context within token budget
    const assembled = this.assembleContext(diverseResults, query.maxTokens ?? 4000);

    return {
      queryId,
      retrievedChunks: assembled.chunks,
      assembledContext: assembled.text,
      tokenCount: assembled.tokenCount,
      sources: this.computeAttribution(assembled.chunks),
      latencyMs: Date.now() - startTime,
    };
  }

  private mergeResults(sourceResults: SourceResult[][]): MergedResult[] {
    // Reciprocal Rank Fusion
    const k = 60; // RRF constant
    const scores = new Map<string, number>();
    const chunks = new Map<string, RetrievedChunk>();

    for (const results of sourceResults) {
      results.forEach((result, rank) => {
        const rrf = 1 / (k + rank + 1);
        const current = scores.get(result.id) ?? 0;
        scores.set(result.id, current + rrf);
        chunks.set(result.id, result);
      });
    }

    return Array.from(scores.entries())
      .map(([id, score]) => ({ ...chunks.get(id)!, fusedScore: score }))
      .sort((a, b) => b.fusedScore - a.fusedScore);
  }

  private applyMMR(results: RankedResult[], k: number): RankedResult[] {
    // Max Marginal Relevance for diversity
    const lambda = 0.7; // Balance relevance vs diversity
    const selected: RankedResult[] = [];
    const remaining = [...results];

    while (selected.length < k && remaining.length > 0) {
      let bestIdx = 0;
      let bestScore = -Infinity;

      for (let i = 0; i < remaining.length; i++) {
        const relevance = remaining[i].fusedScore;
        const maxSimilarity = selected.length > 0
          ? Math.max(...selected.map(s => this.cosineSimilarity(s.embedding, remaining[i].embedding)))
          : 0;

        const mmrScore = lambda * relevance - (1 - lambda) * maxSimilarity;

        if (mmrScore > bestScore) {
          bestScore = mmrScore;
          bestIdx = i;
        }
      }

      selected.push(remaining[bestIdx]);
      remaining.splice(bestIdx, 1);
    }

    return selected;
  }

  private assembleContext(chunks: RetrievedChunk[], maxTokens: number): AssembledContext {
    const assembled: RetrievedChunk[] = [];
    let totalTokens = 0;

    for (const chunk of chunks) {
      const chunkTokens = this.countTokens(chunk.content);
      if (totalTokens + chunkTokens > maxTokens) break;

      assembled.push(chunk);
      totalTokens += chunkTokens;
    }

    const text = assembled.map(chunk => {
      const header = chunk.metadata.filePath
        ? `// File: ${chunk.metadata.filePath}:${chunk.metadata.startLine}-${chunk.metadata.endLine}`
        : `// Source: ${chunk.source}`;
      return `${header}\n${chunk.content}`;
    }).join('\n\n---\n\n');

    return { chunks: assembled, text, tokenCount: totalTokens };
  }
}
```

### Context Formatting

```typescript
interface ContextFormatter {
  format(chunks: RetrievedChunk[], format: ContextFormat): string;
}

type ContextFormat = 'xml' | 'markdown' | 'plain';

// XML format (recommended for Claude)
const xmlFormat = (chunks: RetrievedChunk[]) => `
<retrieved_context>
  ${chunks.map(chunk => `
  <chunk source="${chunk.source}" score="${chunk.score.toFixed(3)}">
    <location>${chunk.metadata.filePath ?? 'unknown'}:${chunk.metadata.startLine ?? '?'}</location>
    <content>
${chunk.content}
    </content>
  </chunk>
  `).join('')}
</retrieved_context>
`;
```

## Dependencies

- Story 6-1: Codebase Indexer (source data)
- Story 6-2: Vector Database (vector search)
- Keyword index (Elasticsearch, Meilisearch, or SQLite FTS)
- Embedding provider for query embedding

## Testing Strategy

### Unit Tests
- Query processing and expansion
- RRF fusion calculation
- MMR diversity selection
- Token budget enforcement
- Context formatting

### Integration Tests
- End-to-end retrieval flow
- Multi-source aggregation
- Cache hit/miss scenarios
- Timeout handling

### Quality Tests
- Retrieval accuracy (manual evaluation)
- Context relevance scoring
- A/B testing with/without RAG

## Configuration

```yaml
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

  ranking:
    fusion_method: rrf  # rrf | linear | learned
    mmr_lambda: 0.7
    recency_boost: 0.1

  assembly:
    max_tokens: 4000
    format: xml
    include_scores: false

  caching:
    enabled: true
    ttl_seconds: 300
    max_entries: 1000

  timeouts:
    per_source_ms: 2000
    total_ms: 5000
```

## Success Metrics

- Retrieval latency p95 < 200ms
- Context relevance score > 0.8 (human eval)
- Agent task improvement with RAG > 15%
- Cache hit rate > 60%

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
