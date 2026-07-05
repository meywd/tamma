/**
 * Mock Embedding Provider
 *
 * A mock embedding provider for testing purposes.
 * Generates deterministic embeddings based on content hash.
 */

import { BaseEmbeddingProvider } from './base-embedding-provider.js';
import type { EmbeddingProviderConfig } from '../types.js';
import { calculateHash } from '../metadata/hash-calculator.js';

/**
 * Mock embedding provider for testing
 */
export class MockEmbeddingProvider extends BaseEmbeddingProvider {
  readonly name = 'mock';
  private _dimensions: number = 1536;
  readonly maxBatchSize = 100;

  private embedDelay = 0;
  private failRate = 0;

  get dimensions(): number {
    return this._dimensions;
  }

  /**
   * Configure the mock provider
   * @param options - Mock configuration options
   */
  configure(options: {
    dimensions?: number;
    embedDelay?: number;
    failRate?: number;
  }): void {
    if (options.dimensions !== undefined) {
      this._dimensions = options.dimensions;
    }
    if (options.embedDelay !== undefined) {
      this.embedDelay = options.embedDelay;
    }
    if (options.failRate !== undefined) {
      this.failRate = options.failRate;
    }
  }

  /**
   * Initialize the mock provider
   */
  async initialize(config: EmbeddingProviderConfig): Promise<void> {
    this.config = config;
    this.initialized = true;
  }

  /**
   * Generate a deterministic embedding for text
   */
  async embed(text: string): Promise<number[]> {
    this.ensureInitialized();

    // Simulate delay
    if (this.embedDelay > 0) {
      await this.sleep(this.embedDelay);
    }

    // Simulate failure
    if (this.failRate > 0 && Math.random() < this.failRate) {
      throw new Error('Mock embedding failure');
    }

    return this.generateDeterministicEmbedding(text);
  }

  /**
   * Batch embed multiple texts
   */
  async embedBatch(texts: string[]): Promise<number[][]> {
    this.ensureInitialized();

    // Simulate delay (once for the whole batch)
    if (this.embedDelay > 0) {
      await this.sleep(this.embedDelay);
    }

    // Simulate failure
    if (this.failRate > 0 && Math.random() < this.failRate) {
      throw new Error('Mock embedding batch failure');
    }

    return texts.map((text) => this.generateDeterministicEmbedding(text));
  }

  /**
   * Generate deterministic embedding from text hash
   */
  private generateDeterministicEmbedding(text: string): number[] {
    const hash = calculateHash(text);
    const embedding: number[] = [];

    // Use the hash bytes to seed a simple PRNG
    let seed = 0;
    for (let i = 0; i < Math.min(hash.length, 8); i++) {
      seed = (seed << 4) | parseInt(hash[i]!, 16);
    }

    // Generate deterministic pseudo-random values
    for (let i = 0; i < this._dimensions; i++) {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      // Normalize to [-1, 1] range
      embedding.push((seed / 0x7fffffff) * 2 - 1);
    }

    // Normalize the vector
    const magnitude = Math.sqrt(embedding.reduce((sum, v) => sum + v * v, 0));
    return embedding.map((v) => v / magnitude);
  }

  /**
   * Estimate cost (always 0 for mock)
   */
  estimateCost(_tokenCount: number): number {
    return 0;
  }
}
