/**
 * RAG Pipeline Types
 *
 * Type definitions for the RAG (Retrieval Augmented Generation) pipeline.
 * Supports multi-source retrieval, ranking, and context assembly.
 *
 * @module @tamma/intelligence/rag
 */

// === Source Types ===

/**
 * Available RAG retrieval sources
 */
export type RAGSourceType = 'vector_db' | 'keyword' | 'docs' | 'issues' | 'prs' | 'commits';

/**
 * Context format for output
 */
export type ContextFormat = 'xml' | 'markdown' | 'plain' | 'json';

/**
 * Fusion method for combining results from multiple sources
 */
export type FusionMethod = 'rrf' | 'linear' | 'learned';

// === Configuration Types ===

/**
 * Settings for a single RAG source
 */
export interface SourceSettings {
  /** Whether this source is enabled */
  enabled: boolean;
  /** Weight for ranking (higher = more important) */
  weight: number;
  /** Maximum results to fetch from this source */
  topK: number;
}

/**
 * Configuration for all RAG sources
 */
export interface RAGSourceConfig {
  vector_db: SourceSettings;
  keyword: SourceSettings;
  docs: SourceSettings;
  issues: SourceSettings;
  prs: SourceSettings;
  commits: SourceSettings;
}

/**
 * Ranking algorithm configuration
 */
export interface RankingConfig {
  /** Method for fusing multi-source results */
  fusionMethod: FusionMethod;
  /** RRF constant (typically 60) */
  rrfK: number;
  /** MMR lambda: 1.0 = pure relevance, 0.0 = pure diversity */
  mmrLambda: number;
  /** Boost factor for recent content */
  recencyBoost: number;
  /** Number of days for recency decay */
  recencyDecayDays: number;
}

/**
 * Context assembly configuration
 */
export interface AssemblyConfig {
  /** Maximum tokens for assembled context */
  maxTokens: number;
  /** Output format */
  format: ContextFormat;
  /** Whether to include relevance scores in output */
  includeScores: boolean;
  /** Similarity threshold for deduplication (0-1) */
  deduplicationThreshold: number;
}

/**
 * Caching configuration
 */
export interface CachingConfig {
  /** Whether caching is enabled */
  enabled: boolean;
  /** Time-to-live in seconds */
  ttlSeconds: number;
  /** Maximum cache entries */
  maxEntries: number;
}

/**
 * Timeout configuration
 */
export interface TimeoutConfig {
  /** Timeout per source in milliseconds */
  perSourceMs: number;
  /** Total pipeline timeout in milliseconds */
  totalMs: number;
}

/**
 * Main RAG pipeline configuration
 */
export interface RAGConfig {
  /** Source-specific settings */
  sources: RAGSourceConfig;
  /** Ranking algorithm settings */
  ranking: RankingConfig;
  /** Context assembly settings */
  assembly: AssemblyConfig;
  /** Caching settings */
  caching: CachingConfig;
  /** Timeout settings */
  timeouts: TimeoutConfig;
}

// === Query Types ===

/**
 * Additional context for a query
 */
export interface QueryContext {
  /** Related issue number */
  issueNumber?: number;
  /** Current file path */
  filePath?: string;
  /** Programming language filter */
  language?: string;
  /** Project identifier */
  projectId?: string;
  /** Recently viewed files */
  recentFiles?: string[];
}

/**
 * RAG query input
 */
export interface RAGQuery {
  /** Natural language query text */
  text: string;
  /** Additional context for the query */
  context?: QueryContext;
  /** Which sources to query (defaults to all enabled) */
  sources?: RAGSourceType[];
  /** Token budget for context (overrides config) */
  maxTokens?: number;
  /** Max results per source (overrides config) */
  topK?: number;
}

// === Result Types ===

/**
 * Metadata for a retrieved chunk
 */
export interface ChunkMetadata {
  /** Source file path */
  filePath?: string | undefined;
  /** Starting line number */
  startLine?: number | undefined;
  /** Ending line number */
  endLine?: number | undefined;
  /** URL (for web sources) */
  url?: string | undefined;
  /** Date of the content */
  date?: Date;
  /** Author of the content */
  author?: string;
  /** Title of the content */
  title?: string | undefined;
  /** Programming language */
  language?: string | undefined;
  /** Symbols (function names, class names, etc.) */
  symbols?: string[] | undefined;
}

/**
 * A retrieved chunk of content
 */
export interface RetrievedChunk {
  /** Unique chunk identifier */
  id: string;
  /** Chunk content */
  content: string;
  /** Source type */
  source: RAGSourceType;
  /** Relevance score from source */
  score: number;
  /** Fused score after RRF/ranking */
  fusedScore?: number | undefined;
  /** Chunk metadata */
  metadata: ChunkMetadata;
  /** Embedding vector (for MMR calculation) */
  embedding?: number[] | undefined;
}

/**
 * Attribution statistics for a source
 */
export interface SourceAttribution {
  /** Source type */
  source: RAGSourceType;
  /** Number of chunks from this source */
  count: number;
  /** Average score of chunks from this source */
  avgScore: number;
  /** Latency for this source in milliseconds */
  latencyMs: number;
}

/**
 * Assembled context ready for LLM
 */
export interface AssembledContext {
  /** Chunks included in the context */
  chunks: RetrievedChunk[];
  /** Formatted context text */
  text: string;
  /** Total token count */
  tokenCount: number;
  /** Whether context was truncated */
  truncated: boolean;
}

/**
 * Complete RAG retrieval result
 */
export interface RAGResult {
  /** Unique query identifier */
  queryId: string;
  /** All retrieved chunks */
  retrievedChunks: RetrievedChunk[];
  /** Assembled context for LLM */
  assembledContext: string;
  /** Token count of assembled context */
  tokenCount: number;
  /** Source attribution statistics */
  sources: SourceAttribution[];
  /** Total latency in milliseconds */
  latencyMs: number;
  /** Whether result was from cache */
  cacheHit: boolean;
}

// === Query Processing Types ===

/**
 * Entity types that can be extracted from queries
 */
export type EntityType = 'file' | 'function' | 'class' | 'variable' | 'package' | 'symbol';

/**
 * An extracted entity from a query
 */
export interface ExtractedEntity {
  /** Entity type */
  type: EntityType;
  /** Entity value */
  value: string;
  /** Confidence score (0-1) */
  confidence: number;
}

/**
 * Query intent classification
 */
export type QueryIntent =
  | 'code_search'
  | 'explanation'
  | 'implementation'
  | 'debugging'
  | 'documentation'
  | 'refactoring'
  | 'general';

/**
 * Processed query with expansions and entities
 */
export interface ProcessedQuery {
  /** Original query text */
  original: string;
  /** Expanded query terms */
  expanded: string[];
  /** Extracted entities */
  entities: ExtractedEntity[];
  /** Decomposed sub-queries (for complex queries) */
  decomposed?: string[] | undefined;
  /** Detected language */
  language?: string | undefined;
  /** Query embedding */
  embedding?: number[] | undefined;
  /** Classified intent */
  intent?: QueryIntent;
}

// === Feedback Types ===

/**
 * Relevance rating for feedback
 */
export type RelevanceRating = 'helpful' | 'not_helpful' | 'partially_helpful';

/**
 * User feedback on retrieval relevance
 */
export interface RelevanceFeedback {
  /** Query identifier */
  queryId: string;
  /** Chunk identifier */
  chunkId: string;
  /** Relevance rating */
  rating: RelevanceRating;
  /** Optional comment */
  comment?: string | undefined;
  /** Feedback timestamp */
  timestamp: Date;
}

/**
 * Aggregated feedback statistics
 */
export interface FeedbackStats {
  /** Query identifier */
  queryId: string;
  /** Total feedback count */
  totalFeedback: number;
  /** Helpful count */
  helpfulCount: number;
  /** Average rating (1 = not helpful, 2 = partial, 3 = helpful) */
  avgRating: number;
}

// === Source Interface Types ===

/**
 * Filter options for source retrieval
 */
export interface SourceFilter {
  /** Filter by file paths */
  filePaths?: string[];
  /** Filter by programming languages */
  languages?: string[];
  /** Filter by date range */
  dateRange?: { start: Date; end: Date };
  /** Filter by authors */
  authors?: string[];
}

/**
 * Options for retrieval from a source
 */
export interface RetrieveOptions {
  /** Maximum results to return */
  topK: number;
  /** Optional filters */
  filter?: SourceFilter;
  /** Timeout in milliseconds */
  timeout?: number;
}

// === Pipeline Interface ===

/**
 * RAG Pipeline interface
 */
export interface IRAGPipeline {
  /**
   * Configure the pipeline with settings
   * @param config - RAG configuration
   */
  configure(config: Partial<RAGConfig>): Promise<void>;

  /**
   * Main retrieval method - retrieves and assembles context
   * @param query - RAG query
   * @returns RAG result with assembled context
   */
  retrieve(query: RAGQuery): Promise<RAGResult>;

  /**
   * Record relevance feedback
   * @param feedback - Relevance feedback
   */
  recordFeedback(feedback: RelevanceFeedback): Promise<void>;

  /**
   * Get feedback statistics for a query
   * @param queryId - Query identifier
   * @returns Feedback statistics
   */
  getFeedbackStats(queryId: string): Promise<FeedbackStats>;

  /**
   * Dispose of resources
   */
  dispose(): Promise<void>;
}

/**
 * RAG Source interface for implementing source adapters
 */
export interface IRAGSource {
  /** Source type name */
  readonly name: RAGSourceType;
  /** Whether source is enabled */
  readonly enabled: boolean;

  /**
   * Initialize the source
   * @param config - Source settings
   */
  initialize(config: SourceSettings): Promise<void>;

  /**
   * Retrieve chunks from this source
   * @param query - Processed query
   * @param options - Retrieval options
   * @returns Retrieved chunks
   */
  retrieve(query: ProcessedQuery, options: RetrieveOptions): Promise<RetrievedChunk[]>;

  /**
   * Check if source is healthy
   * @returns True if healthy
   */
  healthCheck(): Promise<boolean>;

  /**
   * Dispose of resources
   */
  dispose(): Promise<void>;
}

/**
 * Query processor interface
 */
export interface IQueryProcessor {
  /**
   * Process a raw query into an expanded, analyzed form
   * @param query - RAG query
   * @returns Processed query
   */
  process(query: RAGQuery): Promise<ProcessedQuery>;
}

/**
 * Ranker interface
 */
export interface IRanker {
  /**
   * Merge and rank results from multiple sources using RRF
   * @param sourceResults - Results from each source
   * @param config - Ranking configuration
   * @returns Merged and ranked chunks
   */
  mergeWithRRF(
    sourceResults: Map<RAGSourceType, RetrievedChunk[]>,
    config: RankingConfig
  ): RetrievedChunk[];

  /**
   * Apply Max Marginal Relevance for diversity
   * @param chunks - Ranked chunks
   * @param k - Number to select
   * @param lambda - Diversity parameter
   * @returns Diverse selection of chunks
   */
  applyMMR(chunks: RetrievedChunk[], k: number, lambda: number): RetrievedChunk[];

  /**
   * Apply recency boost to chunks
   * @param chunks - Chunks to boost
   * @param config - Ranking configuration
   * @returns Boosted chunks
   */
  applyRecencyBoost(chunks: RetrievedChunk[], config: RankingConfig): RetrievedChunk[];
}

/**
 * Context assembler interface
 */
export interface IContextAssembler {
  /**
   * Assemble chunks into formatted context
   * @param chunks - Ranked chunks
   * @param config - Assembly configuration
   * @returns Assembled context
   */
  assemble(chunks: RetrievedChunk[], config: AssemblyConfig): AssembledContext;

  /**
   * Count tokens in text
   * @param text - Text to count
   * @returns Token count
   */
  countTokens(text: string): number;
}

// === Default Configuration ===

/**
 * Default RAG configuration
 */
export const DEFAULT_RAG_CONFIG: RAGConfig = {
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
