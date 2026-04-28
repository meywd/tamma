/**
 * Base Embedding Provider
 *
 * Abstract base class for embedding providers with common functionality.
 */

import type { IEmbeddingProvider, EmbeddingProviderConfig } from '../types.js';
import { EmbeddingError, EmbeddingNotInitializedError } from '../errors.js';

/**
 * Abstract base class for embedding providers
 */
export abstract class BaseEmbeddingProvider implements IEmbeddingProvider {
  abstract readonly name: string;
  abstract readonly dimensions: number;
  abstract readonly maxBatchSize: number;

  protected initialized = false;
  protected config: EmbeddingProviderConfig | null = null;

  /**
   * Initialize the provider
   */
  abstract initialize(config: EmbeddingProviderConfig): Promise<void>;

  /**
   * Generate embedding for a single text
   */
  abstract embed(text: string): Promise<number[]>;

  /**
   * Batch embed multiple texts
   */
  abstract embedBatch(texts: string[]): Promise<number[][]>;

  /**
   * Get estimated cost for embedding
   */
  abstract estimateCost(tokenCount: number): number;

  /**
   * Dispose resources
   */
  async dispose(): Promise<void> {
    this.initialized = false;
    this.config = null;
  }

  /**
   * Check if provider is initialized
   */
  protected ensureInitialized(): void {
    if (!this.initialized) {
      throw new EmbeddingNotInitializedError(this.name);
    }
  }

  /**
   * Validate embedding dimensions
   */
  protected validateEmbedding(embedding: number[]): void {
    if (embedding.length !== this.dimensions) {
      throw new EmbeddingError(
        `Invalid embedding dimensions: expected ${this.dimensions}, got ${embedding.length}`,
        { context: { expected: this.dimensions, actual: embedding.length } },
      );
    }
  }

  /**
   * Sleep for a specified duration (for rate limiting)
   */
  protected async sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}
