/**
 * Codebase Indexer Types
 *
 * Type definitions for the codebase indexing system that processes source files,
 * chunks them intelligently based on code structure, generates embeddings,
 * and stores them for semantic search.
 *
 * @module @tamma/intelligence/indexer
 */

/**
 * Supported programming languages for indexing
 */
export type SupportedLanguage =
  | 'typescript'
  | 'javascript'
  | 'python'
  | 'go'
  | 'rust'
  | 'java'
  | 'unknown';

/**
 * Chunk type classification - semantic unit type
 */
export type ChunkType =
  | 'function'
  | 'class'
  | 'interface'
  | 'type'
  | 'enum'
  | 'module'
  | 'block'
  | 'imports';

/**
 * A semantic chunk of code extracted from a source file
 */
export interface CodeChunk {
  /** Unique chunk ID (content-hash based) */
  id: string;
  /** Parent file ID */
  fileId: string;
  /** Relative file path from project root */
  filePath: string;
  /** Programming language */
  language: SupportedLanguage;
  /** Semantic unit type */
  chunkType: ChunkType;
  /** Symbol name (function/class name, or 'anonymous') */
  name: string;
  /** Raw code content */
  content: string;
  /** 1-indexed start line */
  startLine: number;
  /** 1-indexed end line */
  endLine: number;
  /** Parent class/module name (for methods) */
  parentScope?: string | undefined;
  /** Import dependencies */
  imports: string[];
  /** Exported symbols */
  exports: string[];
  /** JSDoc/docstring if present */
  docstring?: string | undefined;
  /** Estimated token count */
  tokenCount: number;
  /** Content hash for change detection */
  hash: string;
}

/**
 * Chunk with embedding vector attached (ready for storage)
 */
export interface IndexedChunk extends CodeChunk {
  /** Vector embedding (e.g., 1536 dims for OpenAI) */
  embedding: number[];
  /** When this chunk was indexed */
  indexedAt: Date;
}

/**
 * Metadata for a processed file
 */
export interface FileMetadata {
  /** Unique file ID */
  id: string;
  /** Relative file path from project root */
  path: string;
  /** Absolute file path */
  absolutePath: string;
  /** Programming language */
  language: SupportedLanguage;
  /** File size in bytes */
  sizeBytes: number;
  /** Number of lines */
  lineCount: number;
  /** Content hash (SHA-256) */
  hash: string;
  /** Last modified timestamp */
  lastModified: Date;
  /** Number of chunks created from this file */
  chunkCount: number;
  /** All exported/public symbols */
  symbols: string[];
  /** All imports */
  imports: string[];
}

/**
 * Embedding provider type
 */
export type EmbeddingProviderType = 'openai' | 'cohere' | 'ollama' | 'local' | 'mock';

/**
 * Indexer configuration
 */
export interface IndexerConfig {
  // === File discovery ===
  /** Glob patterns to include */
  includePatterns: string[];
  /** Glob patterns to exclude */
  excludePatterns: string[];
  /** Whether to respect .gitignore */
  respectGitignore: boolean;

  // === Chunking ===
  /** Max tokens per chunk (default: 512) */
  maxChunkTokens: number;
  /** Overlap between chunks (default: 50) */
  overlapTokens: number;
  /** Keep imports as separate chunk */
  preserveImports: boolean;
  /** Group small related functions */
  groupRelatedCode: boolean;

  // === Embedding ===
  /** Embedding provider to use */
  embeddingProvider: EmbeddingProviderType;
  /** Embedding model ID */
  embeddingModel: string;
  /** Batch size for embedding requests */
  batchSize: number;

  // === Triggers ===
  /** Enable git hooks (post-commit, post-merge) */
  enableGitHooks: boolean;
  /** Enable file watcher */
  enableFileWatcher: boolean;
  /** Cron expression for scheduled indexing */
  scheduleCron?: string | undefined;

  // === Performance ===
  /** Max concurrent file processing */
  concurrency: number;
  /** Embedding rate limit per minute */
  embeddingRateLimitPerMin: number;
}

/**
 * Result of an indexing operation
 */
export interface IndexResult {
  /** Whether indexing completed successfully */
  success: boolean;
  /** Path to the indexed project */
  projectPath: string;
  /** Number of files processed */
  filesProcessed: number;
  /** Number of files skipped */
  filesSkipped: number;
  /** Number of chunks created */
  chunksCreated: number;
  /** Number of chunks updated */
  chunksUpdated: number;
  /** Number of chunks deleted */
  chunksDeleted: number;
  /** Estimated embedding cost in USD */
  embeddingCostUsd: number;
  /** Duration in milliseconds */
  durationMs: number;
  /** Errors encountered during indexing */
  errors: IndexError[];
}

/**
 * Indexing error with context
 */
export interface IndexError {
  /** File path where error occurred */
  filePath: string;
  /** Error message */
  error: string;
  /** Whether the operation can be retried */
  recoverable: boolean;
  /** When the error occurred */
  timestamp: Date;
}

/**
 * Progress phases
 */
export type IndexPhase = 'discovery' | 'chunking' | 'embedding' | 'storing';

/**
 * Progress callback for indexing operations
 */
export interface IndexProgress {
  /** Current phase of indexing */
  phase: IndexPhase;
  /** Total files to process */
  filesTotal: number;
  /** Files processed so far */
  filesProcessed: number;
  /** Total chunks created */
  chunksTotal: number;
  /** Chunks processed (embedded) so far */
  chunksProcessed: number;
  /** Current file being processed */
  currentFile?: string;
}

/**
 * Index status and metadata
 */
export interface IndexStatus {
  /** Path to the indexed project */
  projectPath: string;
  /** When the index was last updated */
  lastIndexedAt?: Date | undefined;
  /** Total number of indexed files */
  totalFiles: number;
  /** Total number of chunks */
  totalChunks: number;
  /** Index size in bytes (estimated) */
  indexSizeBytes: number;
  /** Whether the index is stale (files changed since last index) */
  isStale: boolean;
  /** When the index became stale */
  staleSince?: Date;
}

/**
 * Discovered file information
 */
export interface DiscoveredFile {
  /** Absolute path to the file */
  absolutePath: string;
  /** Relative path from project root */
  relativePath: string;
  /** Detected programming language */
  language: SupportedLanguage;
  /** File size in bytes */
  sizeBytes: number;
  /** Last modified timestamp */
  lastModified: Date;
}

/**
 * Language-specific chunking strategy
 */
export interface ChunkingStrategy {
  /** Language this strategy is for */
  language: SupportedLanguage;
  /** Parser to use */
  parser: 'typescript' | 'tree-sitter' | 'custom' | 'generic';
  /** Max tokens per chunk */
  maxChunkTokens: number;
  /** Overlap between chunks */
  overlapTokens: number;
  /** Preserve imports as separate chunk */
  preserveImports: boolean;
  /** Group small related functions */
  groupRelatedCode: boolean;
}

/**
 * Abstract chunker interface for language-specific implementations
 */
export interface ICodeChunker {
  /** Languages this chunker supports */
  readonly supportedLanguages: SupportedLanguage[];

  /**
   * Chunk a file's content into semantic units
   * @param content - File content to chunk
   * @param filePath - Relative file path
   * @param fileId - Unique file identifier
   * @param strategy - Chunking strategy to use
   * @returns Array of code chunks
   */
  chunk(
    content: string,
    filePath: string,
    fileId: string,
    strategy: ChunkingStrategy,
  ): Promise<CodeChunk[]>;

  /**
   * Estimate token count for content
   * @param content - Text content
   * @returns Estimated token count
   */
  estimateTokens(content: string): number;
}

/**
 * Embedding provider configuration
 */
export interface EmbeddingProviderConfig {
  /** API key (for cloud providers) */
  apiKey?: string | undefined;
  /** Base URL (for self-hosted) */
  baseUrl?: string;
  /** Model identifier */
  model: string;
  /** Request timeout in ms */
  timeout?: number;
}

/**
 * Embedding provider interface
 */
export interface IEmbeddingProvider {
  /** Provider name */
  readonly name: string;
  /** Embedding dimensions */
  readonly dimensions: number;
  /** Max batch size supported */
  readonly maxBatchSize: number;

  /**
   * Initialize the provider
   * @param config - Provider configuration
   */
  initialize(config: EmbeddingProviderConfig): Promise<void>;

  /**
   * Generate embedding for a single text
   * @param text - Text to embed
   * @returns Embedding vector
   */
  embed(text: string): Promise<number[]>;

  /**
   * Batch embed multiple texts
   * @param texts - Array of texts to embed
   * @returns Array of embedding vectors
   */
  embedBatch(texts: string[]): Promise<number[][]>;

  /**
   * Get estimated cost for embedding (in USD)
   * @param tokenCount - Number of tokens
   * @returns Estimated cost in USD
   */
  estimateCost(tokenCount: number): number;

  /**
   * Dispose resources
   */
  dispose(): Promise<void>;
}

/**
 * Event types for indexer
 */
export type IndexerEventType = 'progress' | 'error' | 'complete';

/**
 * Event handler for progress events
 */
export type ProgressHandler = (progress: IndexProgress) => void;

/**
 * Event handler for error events
 */
export type ErrorHandler = (error: IndexError) => void;

/**
 * Event handler for completion events
 */
export type CompleteHandler = (result: IndexResult) => void;

/**
 * Main codebase indexer service interface
 */
export interface ICodebaseIndexer {
  /**
   * Configure the indexer with project-specific settings
   * @param config - Partial configuration to merge with defaults
   */
  configure(config: Partial<IndexerConfig>): Promise<void>;

  /**
   * Get current configuration
   * @returns Current indexer configuration
   */
  getConfig(): IndexerConfig;

  /**
   * Full project indexing (scans all files)
   * @param projectPath - Path to the project root
   * @returns Indexing result
   */
  indexProject(projectPath: string): Promise<IndexResult>;

  /**
   * Incremental update (only changed files)
   * If changedFiles not provided, detects changes via git diff or file hashes
   * @param projectPath - Path to the project root
   * @param changedFiles - Optional list of changed file paths
   * @returns Indexing result
   */
  updateIndex(projectPath: string, changedFiles?: string[]): Promise<IndexResult>;

  /**
   * Remove specific files from the index
   * @param filePaths - Array of file paths to remove
   */
  removeFromIndex(filePaths: string[]): Promise<void>;

  /**
   * Clear entire index for a project
   * @param projectPath - Path to the project root
   */
  clearIndex(projectPath: string): Promise<void>;

  /**
   * Get current index status and metadata
   * @param projectPath - Path to the project root
   * @returns Index status
   */
  getIndexStatus(projectPath: string): Promise<IndexStatus>;

  /**
   * Check if a file needs re-indexing
   * @param filePath - Path to the file
   * @returns True if file is stale
   */
  isFileStale(filePath: string): Promise<boolean>;

  /**
   * Event subscription for progress updates
   */
  on(event: 'progress', handler: ProgressHandler): void;
  on(event: 'error', handler: ErrorHandler): void;
  on(event: 'complete', handler: CompleteHandler): void;

  /**
   * Remove event listener
   */
  off(event: IndexerEventType, handler: Function): void;

  /**
   * Gracefully stop any ongoing indexing
   */
  stop(): Promise<void>;

  /**
   * Dispose resources
   */
  dispose(): Promise<void>;
}
