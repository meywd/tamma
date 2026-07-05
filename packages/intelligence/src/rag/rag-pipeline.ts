/**
 * RAG Pipeline
 *
 * Main RAG (Retrieval Augmented Generation) pipeline implementation.
 * Orchestrates query processing, multi-source retrieval, ranking,
 * and context assembly for LLM consumption.
 */

import { randomUUID } from 'node:crypto';
import type {
  IRAGPipeline,
  RAGConfig,
  RAGQuery,
  RAGResult,
  RAGSourceType,
  RelevanceFeedback,
  FeedbackStats,
  ProcessedQuery,
} from './types.js';
import { NotInitializedError } from './errors.js';
import { RAGCache, createRAGCache } from './cache.js';
import { QueryProcessor, createQueryProcessor } from './query-processor.js';
import { Ranker, createRanker } from './ranker.js';
import { ContextAssembler, createContextAssembler } from './assembler.js';
import { FeedbackTracker, createFeedbackTracker } from './feedback.js';
import { Retriever, createRetriever } from './retriever.js';
import type { EmbeddingService } from '../indexer/embedding/embedding-service.js';
import type { IVectorStore } from '../vector-store/interfaces.js';
import { VectorSource } from './sources/vector-source.js';
import { KeywordSource } from './sources/keyword-source.js';
import { DocsSource } from './sources/docs-source.js';
import { IssuesSource, PullRequestsSource, CommitsSource } from './sources/github-source.js';

/**
 * Generate unique query ID
 */
function generateQueryId(): string {
  return `rag-${randomUUID()}`;
}

/**
 * Deep merge configuration objects
 */
function mergeConfig(base: RAGConfig, overrides: Partial<RAGConfig>): RAGConfig {
  return {
    sources: { ...base.sources, ...overrides.sources },
    ranking: { ...base.ranking, ...overrides.ranking },
    assembly: { ...base.assembly, ...overrides.assembly },
    caching: { ...base.caching, ...overrides.caching },
    timeouts: { ...base.timeouts, ...overrides.timeouts },
  };
}

/**
 * RAG Pipeline implementation
 */
export class RAGPipeline implements IRAGPipeline {
  private config: RAGConfig;
  private initialized = false;

  // Components
  private cache: RAGCache;
  private queryProcessor: QueryProcessor;
  private retriever: Retriever;
  private ranker: Ranker;
  private assembler: ContextAssembler;
  private feedbackTracker: FeedbackTracker;

  // External dependencies
  private embeddingService?: EmbeddingService | undefined;
  private vectorStore?: IVectorStore | undefined;

  constructor(config?: Partial<RAGConfig>) {
    // Import default config
    const defaultConfig: RAGConfig = {
      sources: {
        vector_db: { enabled: true, weight: 1.0, topK: 20 },
        keyword: { enabled: true, weight: 0.5, topK: 10 },
        docs: { enabled: true, weight: 0.3, topK: 5 },
        issues: { enabled: true, weight: 0.2, topK: 5 },
        prs: { enabled: false, weight: 0.2, topK: 5 },
        commits: { enabled: false, weight: 0.1, topK: 5 },
      },
      ranking: {
        fusionMethod: 'rrf',
        rrfK: 60,
        mmrLambda: 0.7,
        recencyBoost: 0.1,
        recencyDecayDays: 30,
      },
      assembly: {
        maxTokens: 4000,
        format: 'xml',
        includeScores: false,
        deduplicationThreshold: 0.85,
      },
      caching: {
        enabled: true,
        ttlSeconds: 300,
        maxEntries: 1000,
      },
      timeouts: {
        perSourceMs: 2000,
        totalMs: 5000,
      },
    };

    this.config = config ? mergeConfig(defaultConfig, config) : defaultConfig;

    // Initialize components
    this.cache = createRAGCache(this.config.caching);
    this.queryProcessor = createQueryProcessor();
    this.retriever = createRetriever();
    this.ranker = createRanker();
    this.assembler = createContextAssembler();
    this.feedbackTracker = createFeedbackTracker();
  }

  /**
   * Configure the pipeline
   */
  async configure(config: Partial<RAGConfig>): Promise<void> {
    this.config = mergeConfig(this.config, config);

    // Update cache configuration
    this.cache.updateConfig(this.config.caching);

    // Re-initialize sources if already initialized
    if (this.initialized) {
      await this.retriever.initializeSources(this.config);
    }
  }

  /**
   * Initialize the pipeline with dependencies
   */
  async initialize(options: {
    embeddingService?: EmbeddingService;
    vectorStore?: IVectorStore;
    collectionName?: string;
  } = {}): Promise<void> {
    if (this.initialized) {
      return;
    }

    this.embeddingService = options.embeddingService;
    this.vectorStore = options.vectorStore;

    // Set up query processor with embedding service
    if (this.embeddingService) {
      this.queryProcessor.setEmbeddingService(this.embeddingService);
    }

    // Register source adapters
    this.registerSources(options.collectionName ?? 'default');

    // Initialize all sources
    await this.retriever.initializeSources(this.config);

    this.initialized = true;
  }

  /**
   * Register default source adapters
   */
  private registerSources(collectionName: string): void {
    // Vector source
    const vectorSource = new VectorSource();
    if (this.vectorStore) {
      vectorSource.setVectorStore(this.vectorStore, collectionName);
    }
    this.retriever.registerSource(vectorSource);

    // Keyword source
    this.retriever.registerSource(new KeywordSource());

    // Docs source
    this.retriever.registerSource(new DocsSource());

    // GitHub sources
    this.retriever.registerSource(new IssuesSource());
    this.retriever.registerSource(new PullRequestsSource());
    this.retriever.registerSource(new CommitsSource());
  }

  /**
   * Main retrieval method
   */
  async retrieve(query: RAGQuery): Promise<RAGResult> {
    this.ensureInitialized();

    const startTime = Date.now();
    const queryId = generateQueryId();

    // Check cache first
    const cached = this.cache.getCachedResult(query);
    if (cached) {
      return {
        ...cached,
        queryId,
        latencyMs: Date.now() - startTime,
      };
    }

    // 1. Process query
    const processedQuery = await this.processQuery(query);

    // 2. Determine which sources to query
    const sourcesToQuery = this.determineSources(query);

    // 3. Retrieve from all sources in parallel
    const { results: sourceResults, attributions } = await this.retriever.retrieveFromAllSources(
      processedQuery,
      sourcesToQuery,
      this.config
    );

    // 4. Merge and rank results
    let rankedChunks = this.ranker.mergeWithRRF(sourceResults, this.config.ranking);

    // 5. Apply recency boost
    rankedChunks = this.ranker.applyRecencyBoost(rankedChunks, this.config.ranking);

    // 6. Deduplicate
    rankedChunks = this.ranker.deduplicateChunks(
      rankedChunks,
      this.config.assembly.deduplicationThreshold
    );

    // 7. Apply MMR for diversity
    const diverseChunks = this.ranker.applyMMR(
      rankedChunks,
      query.topK ?? this.getMaxResults(),
      this.config.ranking.mmrLambda
    );

    // 8. Assemble context
    const maxTokens = query.maxTokens ?? this.config.assembly.maxTokens;
    const assembled = this.assembler.assemble(diverseChunks, {
      ...this.config.assembly,
      maxTokens,
    });

    // Build result
    const result: RAGResult = {
      queryId,
      retrievedChunks: assembled.chunks,
      assembledContext: assembled.text,
      tokenCount: assembled.tokenCount,
      sources: attributions,
      latencyMs: Date.now() - startTime,
      cacheHit: false,
    };

    // Cache the result
    this.cache.cacheResult(query, result);

    return result;
  }

  /**
   * Record relevance feedback
   */
  async recordFeedback(feedback: RelevanceFeedback): Promise<void> {
    await this.feedbackTracker.recordFeedback(feedback);
  }

  /**
   * Get feedback statistics for a query
   */
  async getFeedbackStats(queryId: string): Promise<FeedbackStats> {
    return this.feedbackTracker.getFeedbackStats(queryId);
  }

  /**
   * Dispose of resources
   */
  async dispose(): Promise<void> {
    await this.retriever.dispose();
    this.cache.clear();
    this.feedbackTracker.clear();
    this.initialized = false;
  }

  // === Helper Methods ===

  /**
   * Get the retriever for direct source access
   */
  getRetriever(): Retriever {
    return this.retriever;
  }

  /**
   * Get cache statistics
   */
  getCacheStats(): { hitRate: number; hits: number; misses: number } {
    const stats = this.cache.getStats();
    return {
      hitRate: stats.hitRate,
      hits: stats.hits,
      misses: stats.misses,
    };
  }

  /**
   * Get feedback statistics
   */
  getFeedbackOverview(): { totalQueries: number; avgHelpfulRate: number } {
    const stats = this.feedbackTracker.getOverallStats();
    return {
      totalQueries: stats.totalQueries,
      avgHelpfulRate: stats.avgHelpfulRate,
    };
  }

  /**
   * Invalidate cache
   */
  invalidateCache(pattern?: string): void {
    this.cache.invalidate(pattern);
  }

  /**
   * Check health of all sources
   */
  async checkHealth(): Promise<Map<RAGSourceType, boolean>> {
    return this.retriever.checkHealth();
  }

  // === Private Methods ===

  /**
   * Process query with expansion and entity extraction
   */
  private async processQuery(query: RAGQuery): Promise<ProcessedQuery> {
    return this.queryProcessor.process(query);
  }

  /**
   * Determine which sources to query based on config and query
   */
  private determineSources(query: RAGQuery): RAGSourceType[] {
    if (query.sources && query.sources.length > 0) {
      return query.sources;
    }

    // Return all enabled sources
    const enabledSources: RAGSourceType[] = [];
    for (const [source, settings] of Object.entries(this.config.sources)) {
      if (settings.enabled) {
        enabledSources.push(source as RAGSourceType);
      }
    }

    return enabledSources;
  }

  /**
   * Calculate maximum results based on source configurations
   */
  private getMaxResults(): number {
    let total = 0;
    for (const settings of Object.values(this.config.sources)) {
      if (settings.enabled) {
        total += settings.topK;
      }
    }
    return Math.min(total, 50); // Cap at 50
  }

  /**
   * Ensure pipeline is initialized
   */
  private ensureInitialized(): void {
    if (!this.initialized) {
      throw new NotInitializedError();
    }
  }
}

/**
 * Create a RAG pipeline instance
 */
export function createRAGPipeline(config?: Partial<RAGConfig>): RAGPipeline {
  return new RAGPipeline(config);
}
