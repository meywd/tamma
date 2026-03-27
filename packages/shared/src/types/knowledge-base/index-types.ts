/**
 * Index Management Types
 *
 * Types for codebase indexing status, history, and configuration.
 */

/** Current indexing status */
export interface IndexStatus {
  status: 'idle' | 'indexing' | 'error';
  lastRun: string | null;
  filesIndexed: number;
  chunksCreated: number;
  progress?: number;
  currentFile?: string;
  error?: string;
}

/** Record of a completed indexing run */
export interface IndexHistoryEntry {
  id: string;
  startTime: string;
  endTime: string;
  filesProcessed: number;
  chunksCreated: number;
  chunksUpdated: number;
  chunksDeleted: number;
  embeddingCost: number;
  durationMs: number;
  status: 'success' | 'partial' | 'failed';
  errors: IndexError[];
}

/** Error encountered during indexing */
export interface IndexError {
  filePath: string;
  error: string;
  timestamp: string;
}

/** Indexing configuration */
export interface IndexConfig {
  includePatterns: string[];
  excludePatterns: string[];
  chunkingConfig: ChunkingConfig;
  embeddingConfig: EmbeddingConfig;
  triggerConfig: TriggerConfig;
}

/** Chunking strategy configuration */
export interface ChunkingConfig {
  maxTokens: number;
  overlapTokens: number;
  preserveImports: boolean;
  groupRelatedCode: boolean;
}

/** Embedding model configuration */
export interface EmbeddingConfig {
  provider: 'openai' | 'cohere' | 'ollama';
  model: string;
  batchSize: number;
}

/** Index trigger configuration */
export interface TriggerConfig {
  gitHooks: boolean;
  watchMode: boolean;
  schedule: string | null;
}

/** Request to trigger an index operation */
export interface TriggerIndexRequest {
  fullReindex?: boolean;
  changedFiles?: string[];
  repositoryPath?: string;
}
