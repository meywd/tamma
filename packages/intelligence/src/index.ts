/**
 * @tamma/intelligence
 *
 * Context and knowledge management for the Tamma platform.
 * Provides vector database integration, codebase indexing, and RAG capabilities.
 *
 * @module @tamma/intelligence
 */

// Vector Store Module
export * from './vector-store/index.js';

// Codebase Indexer Module
export * from './indexer/index.js';

// Knowledge Base Module
export * from './knowledge-base/index.js';

// RAG Pipeline Module
export * from './rag/index.js';

// Disambiguate names exported by more than one of the modules above.
// An explicit named re-export takes precedence over `export *`, resolving TS2308.
export { InvalidConfigError, NotInitializedError } from './vector-store/index.js';
export { EmbeddingError } from './indexer/index.js';
export type { IEmbeddingProvider } from './indexer/index.js';

// Context Aggregator Module
// Re-export with explicit names to avoid conflicts with rag/vector-store modules
export type {
  ContextSourceType,
  TaskType,
  ContextRequest,
  ContextHints,
  ContextOptions,
  ContextResponse,
  ContextChunk,
  SourceContribution,
  ContextMetrics,
  AggregatorConfig,
  SourcesConfig,
  SourceConfig,
  BudgetConfig,
  DeduplicationConfig,
  OptimizationConfig,
  SourceQuery,
  SourceFilters,
  SourceResult,
  IContextAggregator,
  IContextSource,
  IContextCache,
} from './context/index.js';

export {
  DEFAULT_AGGREGATOR_CONFIG,
  ContextAggregator,
  createContextAggregator,
  BudgetManager,
  createBudgetManager,
  Deduplicator,
  createDeduplicator,
  ChunkRanker,
  createChunkRanker,
  ContextAssemblerAgg,
  createContextAssemblerAgg,
  MemoryCache,
  createMemoryCache,
  RedisCache,
  createRedisCache,
  BaseContextSource,
  VectorDBSource,
  createVectorDBSource,
  RAGSource,
  createRAGSource,
  MCPSource,
  createMCPSource,
  WebSearchSource,
  createWebSearchSource,
} from './context/index.js';
