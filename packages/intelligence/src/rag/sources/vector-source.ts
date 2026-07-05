/**
 * Vector Database Source
 *
 * Retrieves chunks from vector database using semantic similarity search.
 * Integrates with the Vector Store from Story 6-2.
 */

import type {
  RAGSourceType,
  SourceSettings,
  ProcessedQuery,
  RetrieveOptions,
  RetrievedChunk,
} from '../types.js';
import type { IVectorStore, SearchQuery, SearchResult } from '../../vector-store/interfaces.js';
import { BaseRAGSource } from './source-interface.js';
import { RetrievalError } from '../errors.js';

/**
 * Configuration for vector source
 */
export interface VectorSourceConfig {
  /** Vector store instance */
  vectorStore: IVectorStore;
  /** Collection name to search */
  collectionName: string;
  /** Score threshold for results */
  scoreThreshold?: number;
}

/**
 * Vector database source adapter
 */
export class VectorSource extends BaseRAGSource {
  readonly name: RAGSourceType = 'vector_db';
  private vectorStore: IVectorStore | null = null;
  private collectionName = '';
  private scoreThreshold = 0.0;

  constructor(config?: VectorSourceConfig) {
    super();
    if (config) {
      this.vectorStore = config.vectorStore;
      this.collectionName = config.collectionName;
      this.scoreThreshold = config.scoreThreshold ?? 0.0;
    }
  }

  /**
   * Set vector store instance (for dependency injection)
   */
  setVectorStore(store: IVectorStore, collectionName: string): void {
    this.vectorStore = store;
    this.collectionName = collectionName;
  }

  protected async doInitialize(_config: SourceSettings): Promise<void> {
    // Vector store should already be initialized externally
    if (this.vectorStore) {
      const exists = await this.vectorStore.collectionExists(this.collectionName);
      if (!exists) {
        throw new RetrievalError(
          `Collection '${this.collectionName}' does not exist`,
          this.name
        );
      }
    }
  }

  protected async doRetrieve(
    query: ProcessedQuery,
    options: RetrieveOptions
  ): Promise<RetrievedChunk[]> {
    if (!this.vectorStore) {
      throw new RetrievalError('Vector store not configured', this.name);
    }

    if (!query.embedding) {
      throw new RetrievalError('Query embedding is required for vector search', this.name);
    }

    const searchQuery: SearchQuery = {
      embedding: query.embedding,
      topK: options.topK,
      scoreThreshold: this.scoreThreshold,
      includeMetadata: true,
      includeContent: true,
      includeEmbedding: true, // For MMR calculation
    };

    // Apply filters if specified
    if (options.filter) {
      const builtFilter = this.buildFilter(options.filter);
      if (builtFilter) {
        searchQuery.filter = builtFilter;
      }
    }

    try {
      const results = await this.vectorStore.search(this.collectionName, searchQuery);
      return this.transformResults(results);
    } catch (error) {
      throw new RetrievalError(
        `Vector search failed: ${error instanceof Error ? error.message : 'Unknown error'}`,
        this.name,
        error instanceof Error ? error : undefined
      );
    }
  }

  protected async doHealthCheck(): Promise<boolean> {
    if (!this.vectorStore) {
      return false;
    }

    try {
      const health = await this.vectorStore.healthCheck();
      return health.healthy;
    } catch {
      return false;
    }
  }

  protected async doDispose(): Promise<void> {
    // Don't dispose the vector store - it's managed externally
    this.vectorStore = null;
  }

  /**
   * Transform vector store results to RetrievedChunk format
   */
  private transformResults(results: SearchResult[]): RetrievedChunk[] {
    return results.map((result) => ({
      id: result.id,
      content: result.content ?? '',
      source: this.name,
      score: result.score,
      metadata: {
        filePath: result.metadata?.filePath,
        startLine: result.metadata?.startLine,
        endLine: result.metadata?.endLine,
        language: result.metadata?.language,
        symbols: this.extractSymbols(result.metadata),
      },
      embedding: result.embedding,
    }));
  }

  /**
   * Extract symbols from metadata
   */
  private extractSymbols(metadata?: Record<string, unknown>): string[] | undefined {
    if (!metadata) {
      return undefined;
    }

    const symbols: string[] = [];

    if (metadata.name && typeof metadata.name === 'string') {
      symbols.push(metadata.name);
    }

    if (Array.isArray(metadata.exports)) {
      symbols.push(...metadata.exports.filter((e): e is string => typeof e === 'string'));
    }

    return symbols.length > 0 ? symbols : undefined;
  }

  /**
   * Build metadata filter for search
   */
  private buildFilter(filter: RetrieveOptions['filter']): SearchQuery['filter'] {
    if (!filter) {
      return undefined;
    }

    const conditions: Record<string, unknown> = {};

    if (filter.filePaths?.length) {
      conditions.filePath = filter.filePaths[0]; // Simple single path filter
    }

    if (filter.languages?.length) {
      conditions.language = filter.languages[0];
    }

    return Object.keys(conditions).length > 0
      ? { where: conditions }
      : undefined;
  }
}

/**
 * Create a vector source instance
 */
export function createVectorSource(config?: VectorSourceConfig): VectorSource {
  return new VectorSource(config);
}
