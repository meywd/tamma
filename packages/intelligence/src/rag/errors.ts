/**
 * RAG Pipeline Errors
 *
 * Custom error types for the RAG pipeline.
 */

/**
 * Error codes for RAG pipeline errors
 */
export enum RAGErrorCode {
  NOT_INITIALIZED = 'RAG_NOT_INITIALIZED',
  INVALID_CONFIG = 'RAG_INVALID_CONFIG',
  QUERY_PROCESSING_FAILED = 'RAG_QUERY_PROCESSING_FAILED',
  RETRIEVAL_FAILED = 'RAG_RETRIEVAL_FAILED',
  RANKING_FAILED = 'RAG_RANKING_FAILED',
  ASSEMBLY_FAILED = 'RAG_ASSEMBLY_FAILED',
  SOURCE_TIMEOUT = 'RAG_SOURCE_TIMEOUT',
  SOURCE_UNAVAILABLE = 'RAG_SOURCE_UNAVAILABLE',
  EMBEDDING_FAILED = 'RAG_EMBEDDING_FAILED',
  CACHE_ERROR = 'RAG_CACHE_ERROR',
  FEEDBACK_ERROR = 'RAG_FEEDBACK_ERROR',
}

/**
 * Base error class for RAG pipeline errors
 */
export class RAGError extends Error {
  readonly code: RAGErrorCode;
  override readonly cause?: Error | undefined;

  constructor(code: RAGErrorCode, message: string, cause?: Error) {
    super(message);
    this.name = 'RAGError';
    this.code = code;
    this.cause = cause;
    Object.setPrototypeOf(this, RAGError.prototype);
  }
}

/**
 * Error thrown when pipeline is not initialized
 */
export class NotInitializedError extends RAGError {
  constructor(message = 'RAG pipeline is not initialized') {
    super(RAGErrorCode.NOT_INITIALIZED, message);
    this.name = 'NotInitializedError';
    Object.setPrototypeOf(this, NotInitializedError.prototype);
  }
}

/**
 * Error thrown for invalid configuration
 */
export class InvalidConfigError extends RAGError {
  readonly field?: string | undefined;

  constructor(message: string, field?: string) {
    super(RAGErrorCode.INVALID_CONFIG, message);
    this.name = 'InvalidConfigError';
    this.field = field;
    Object.setPrototypeOf(this, InvalidConfigError.prototype);
  }
}

/**
 * Error thrown when query processing fails
 */
export class QueryProcessingError extends RAGError {
  readonly query: string;

  constructor(message: string, query: string, cause?: Error) {
    super(RAGErrorCode.QUERY_PROCESSING_FAILED, message, cause);
    this.name = 'QueryProcessingError';
    this.query = query;
    Object.setPrototypeOf(this, QueryProcessingError.prototype);
  }
}

/**
 * Error thrown when retrieval fails
 */
export class RetrievalError extends RAGError {
  readonly source?: string | undefined;

  constructor(message: string, source?: string, cause?: Error) {
    super(RAGErrorCode.RETRIEVAL_FAILED, message, cause);
    this.name = 'RetrievalError';
    this.source = source;
    Object.setPrototypeOf(this, RetrievalError.prototype);
  }
}

/**
 * Error thrown when source times out
 */
export class SourceTimeoutError extends RAGError {
  readonly source: string;
  readonly timeoutMs: number;

  constructor(source: string, timeoutMs: number) {
    super(
      RAGErrorCode.SOURCE_TIMEOUT,
      `Source '${source}' timed out after ${timeoutMs}ms`
    );
    this.name = 'SourceTimeoutError';
    this.source = source;
    this.timeoutMs = timeoutMs;
    Object.setPrototypeOf(this, SourceTimeoutError.prototype);
  }
}

/**
 * Error thrown when a source is unavailable
 */
export class SourceUnavailableError extends RAGError {
  readonly source: string;

  constructor(source: string, cause?: Error) {
    super(
      RAGErrorCode.SOURCE_UNAVAILABLE,
      `Source '${source}' is unavailable`,
      cause
    );
    this.name = 'SourceUnavailableError';
    this.source = source;
    Object.setPrototypeOf(this, SourceUnavailableError.prototype);
  }
}

/**
 * Error thrown when ranking fails
 */
export class RankingError extends RAGError {
  constructor(message: string, cause?: Error) {
    super(RAGErrorCode.RANKING_FAILED, message, cause);
    this.name = 'RankingError';
    Object.setPrototypeOf(this, RankingError.prototype);
  }
}

/**
 * Error thrown when context assembly fails
 */
export class AssemblyError extends RAGError {
  constructor(message: string, cause?: Error) {
    super(RAGErrorCode.ASSEMBLY_FAILED, message, cause);
    this.name = 'AssemblyError';
    Object.setPrototypeOf(this, AssemblyError.prototype);
  }
}

/**
 * Error thrown when embedding generation fails
 */
export class EmbeddingError extends RAGError {
  constructor(message: string, cause?: Error) {
    super(RAGErrorCode.EMBEDDING_FAILED, message, cause);
    this.name = 'EmbeddingError';
    Object.setPrototypeOf(this, EmbeddingError.prototype);
  }
}

/**
 * Error thrown when cache operations fail
 */
export class CacheError extends RAGError {
  constructor(message: string, cause?: Error) {
    super(RAGErrorCode.CACHE_ERROR, message, cause);
    this.name = 'CacheError';
    Object.setPrototypeOf(this, CacheError.prototype);
  }
}

/**
 * Error thrown when feedback operations fail
 */
export class FeedbackError extends RAGError {
  constructor(message: string, cause?: Error) {
    super(RAGErrorCode.FEEDBACK_ERROR, message, cause);
    this.name = 'FeedbackError';
    Object.setPrototypeOf(this, FeedbackError.prototype);
  }
}
