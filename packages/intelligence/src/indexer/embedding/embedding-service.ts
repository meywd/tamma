/**
 * Embedding Service
 *
 * High-level service for generating embeddings with caching and rate limiting.
 */

import type {
  IEmbeddingProvider,
  EmbeddingProviderConfig,
  EmbeddingProviderType,
  CodeChunk,
  IndexedChunk,
} from '../types.js';
import { OpenAIEmbeddingProvider } from './openai-embedding-provider.js';
import { CohereEmbeddingProvider } from './cohere-embedding-provider.js';
import { OllamaEmbeddingProvider } from './ollama-embedding-provider.js';
import { MockEmbeddingProvider } from './mock-embedding-provider.js';
import { EmbeddingError } from '../errors.js';
import { calculateHash } from '../metadata/hash-calculator.js';

/**
 * Embedding cache entry
 */
interface CacheEntry {
  embedding: number[];
  timestamp: number;
}

/**
 * Embedding service configuration
 */
export interface EmbeddingServiceConfig {
  /** Embedding provider type */
  provider: EmbeddingProviderType;
  /** Provider-specific configuration */
  providerConfig: EmbeddingProviderConfig;
  /** Enable caching (default: true) */
  enableCache?: boolean;
  /** Cache TTL in milliseconds (default: 24 hours) */
  cacheTtlMs?: number;
  /** Max cache entries (default: 10000) */
  maxCacheEntries?: number;
  /** Batch size for embedding requests (default: 100) */
  batchSize?: number;
  /** Rate limit (requests per minute, default: 3000) */
  rateLimitPerMin?: number;
}

/**
 * Embedding service for generating and caching embeddings
 */
export class EmbeddingService {
  private provider: IEmbeddingProvider | null = null;
  private cache: Map<string, CacheEntry> = new Map();
  private config: EmbeddingServiceConfig;
  private initialized = false;

  // Rate limiting state
  private requestCount = 0;
  private windowStart = Date.now();
  private rateLimitPerMin: number;

  constructor(config: EmbeddingServiceConfig) {
    this.config = {
      enableCache: true,
      cacheTtlMs: 24 * 60 * 60 * 1000, // 24 hours
      maxCacheEntries: 10000,
      batchSize: 100,
      rateLimitPerMin: 3000,
      ...config,
    };
    this.rateLimitPerMin = this.config.rateLimitPerMin!;
  }

  /**
   * Initialize the embedding service
   */
  async initialize(): Promise<void> {
    if (this.initialized) {
      return;
    }

    this.provider = this.createProvider(this.config.provider);
    await this.provider.initialize(this.config.providerConfig);
    this.initialized = true;
  }

  /**
   * Create embedding provider by type
   */
  private createProvider(type: EmbeddingProviderType): IEmbeddingProvider {
    switch (type) {
      case 'openai':
        return new OpenAIEmbeddingProvider();
      case 'cohere':
        return new CohereEmbeddingProvider();
      case 'ollama':
      case 'local':
        return new OllamaEmbeddingProvider();
      case 'mock':
        return new MockEmbeddingProvider();
      default:
        throw new EmbeddingError(`Unknown embedding provider: ${type}`);
    }
  }

  /**
   * Get the embedding dimensions
   */
  getDimensions(): number {
    if (!this.provider) {
      throw new EmbeddingError('Embedding service not initialized');
    }
    return this.provider.dimensions;
  }

  /**
   * Generate embedding for a single text
   */
  async embed(text: string): Promise<number[]> {
    this.ensureInitialized();

    // Check cache
    if (this.config.enableCache) {
      const cached = this.getFromCache(text);
      if (cached) {
        return cached;
      }
    }

    await this.checkRateLimit();

    const embedding = await this.provider!.embed(text);

    // Cache the result
    if (this.config.enableCache) {
      this.addToCache(text, embedding);
    }

    return embedding;
  }

  /**
   * Generate embeddings for multiple texts
   */
  async embedBatch(texts: string[]): Promise<number[][]> {
    this.ensureInitialized();

    if (texts.length === 0) {
      return [];
    }

    const results: (number[] | null)[] = new Array(texts.length).fill(null);
    const uncachedTexts: { index: number; text: string }[] = [];

    // Check cache for each text
    if (this.config.enableCache) {
      for (let i = 0; i < texts.length; i++) {
        const cached = this.getFromCache(texts[i]!);
        if (cached) {
          results[i] = cached;
        } else {
          uncachedTexts.push({ index: i, text: texts[i]! });
        }
      }
    } else {
      for (let i = 0; i < texts.length; i++) {
        uncachedTexts.push({ index: i, text: texts[i]! });
      }
    }

    // Embed uncached texts in batches
    const batchSize = this.config.batchSize!;
    for (let i = 0; i < uncachedTexts.length; i += batchSize) {
      const batch = uncachedTexts.slice(i, i + batchSize);
      const batchTexts = batch.map((item) => item.text);

      await this.checkRateLimit();

      const embeddings = await this.provider!.embedBatch(batchTexts);

      // Store results and cache
      for (let j = 0; j < batch.length; j++) {
        const { index, text } = batch[j]!;
        const embedding = embeddings[j]!;
        results[index] = embedding;

        if (this.config.enableCache) {
          this.addToCache(text, embedding);
        }
      }
    }

    return results as number[][];
  }

  /**
   * Embed code chunks and return indexed chunks
   */
  async embedChunks(chunks: CodeChunk[]): Promise<IndexedChunk[]> {
    if (chunks.length === 0) {
      return [];
    }

    // Prepare texts for embedding
    const texts = chunks.map((chunk) => this.prepareTextForEmbedding(chunk));

    // Generate embeddings
    const embeddings = await this.embedBatch(texts);

    // Create indexed chunks
    const indexedChunks: IndexedChunk[] = chunks.map((chunk, i) => ({
      ...chunk,
      embedding: embeddings[i]!,
      indexedAt: new Date(),
    }));

    return indexedChunks;
  }

  /**
   * Prepare chunk content for embedding
   */
  private prepareTextForEmbedding(chunk: CodeChunk): string {
    const parts: string[] = [];

    // Add file context
    parts.push(`File: ${chunk.filePath}`);

    // Add name if available
    if (chunk.name && chunk.name !== 'anonymous') {
      parts.push(`${chunk.chunkType}: ${chunk.name}`);
    }

    // Add docstring if available
    if (chunk.docstring) {
      parts.push(`Documentation: ${chunk.docstring}`);
    }

    // Add the code content
    parts.push('Code:');
    parts.push(chunk.content);

    return parts.join('\n');
  }

  /**
   * Estimate cost for embedding chunks
   */
  estimateCost(chunks: CodeChunk[]): number {
    if (!this.provider) {
      return 0;
    }

    const totalTokens = chunks.reduce((sum, chunk) => sum + chunk.tokenCount, 0);
    return this.provider.estimateCost(totalTokens);
  }

  /**
   * Get from cache
   */
  private getFromCache(text: string): number[] | null {
    const key = calculateHash(text);
    const entry = this.cache.get(key);

    if (!entry) {
      return null;
    }

    // Check TTL
    if (Date.now() - entry.timestamp > this.config.cacheTtlMs!) {
      this.cache.delete(key);
      return null;
    }

    return entry.embedding;
  }

  /**
   * Add to cache
   */
  private addToCache(text: string, embedding: number[]): void {
    const key = calculateHash(text);

    // Evict oldest entries if cache is full
    if (this.cache.size >= this.config.maxCacheEntries!) {
      const oldestKey = this.cache.keys().next().value;
      if (oldestKey) {
        this.cache.delete(oldestKey);
      }
    }

    this.cache.set(key, {
      embedding,
      timestamp: Date.now(),
    });
  }

  /**
   * Check and enforce rate limit
   */
  private async checkRateLimit(): Promise<void> {
    const now = Date.now();
    const windowDuration = 60000; // 1 minute

    // Reset window if needed
    if (now - this.windowStart >= windowDuration) {
      this.windowStart = now;
      this.requestCount = 0;
    }

    // Check if we're at the limit
    if (this.requestCount >= this.rateLimitPerMin) {
      const waitTime = windowDuration - (now - this.windowStart);
      if (waitTime > 0) {
        await new Promise((resolve) => setTimeout(resolve, waitTime));
        this.windowStart = Date.now();
        this.requestCount = 0;
      }
    }

    this.requestCount++;
  }

  /**
   * Ensure service is initialized
   */
  private ensureInitialized(): void {
    if (!this.initialized || !this.provider) {
      throw new EmbeddingError('Embedding service not initialized');
    }
  }

  /**
   * Clear the embedding cache
   */
  clearCache(): void {
    this.cache.clear();
  }

  /**
   * Get cache statistics
   */
  getCacheStats(): { size: number; maxSize: number } {
    return {
      size: this.cache.size,
      maxSize: this.config.maxCacheEntries!,
    };
  }

  /**
   * Dispose resources
   */
  async dispose(): Promise<void> {
    if (this.provider) {
      await this.provider.dispose();
      this.provider = null;
    }
    this.cache.clear();
    this.initialized = false;
  }
}
