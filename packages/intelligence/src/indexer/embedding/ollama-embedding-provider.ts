/**
 * Ollama Embedding Provider
 *
 * Generates embeddings using local Ollama models via the REST API.
 */

import { BaseEmbeddingProvider } from './base-embedding-provider.js';
import type { EmbeddingProviderConfig } from '../types.js';
import { EmbeddingError } from '../errors.js';

/**
 * Ollama embedding model configurations
 */
const OLLAMA_MODELS: Record<string, { dimensions: number }> = {
  'nomic-embed-text': { dimensions: 768 },
  'mxbai-embed-large': { dimensions: 1024 },
  'all-minilm': { dimensions: 384 },
  'snowflake-arctic-embed': { dimensions: 1024 },
};

/**
 * Ollama embed API response type
 */
interface OllamaEmbedResponse {
  model: string;
  embeddings: number[][];
}

/**
 * Ollama API error response type
 */
interface OllamaErrorResponse {
  error: string;
}

/**
 * Ollama embedding provider implementation for local models
 */
export class OllamaEmbeddingProvider extends BaseEmbeddingProvider {
  readonly name = 'ollama';
  private _dimensions = 768;
  readonly maxBatchSize = 512; // Ollama can handle larger batches locally

  private baseUrl = 'http://localhost:11434';
  private model = 'nomic-embed-text';
  private timeout = 60000; // Longer timeout for local models

  get dimensions(): number {
    return this._dimensions;
  }

  /**
   * Initialize the Ollama provider
   */
  async initialize(config: EmbeddingProviderConfig): Promise<void> {
    this.model = config.model || 'nomic-embed-text';
    this.baseUrl = config.baseUrl || 'http://localhost:11434';
    this.timeout = config.timeout || 60000;

    // Set dimensions based on known models
    const modelConfig = OLLAMA_MODELS[this.model];
    if (modelConfig) {
      this._dimensions = modelConfig.dimensions;
    }

    this.config = config;
    this.initialized = true;
  }

  /**
   * Generate embedding for a single text
   */
  async embed(text: string): Promise<number[]> {
    this.ensureInitialized();

    const embeddings = await this.embedBatch([text]);
    return embeddings[0]!;
  }

  /**
   * Batch embed multiple texts
   */
  async embedBatch(texts: string[]): Promise<number[][]> {
    this.ensureInitialized();

    if (texts.length === 0) {
      return [];
    }

    // Split into batches if needed
    if (texts.length > this.maxBatchSize) {
      const batches: string[][] = [];
      for (let i = 0; i < texts.length; i += this.maxBatchSize) {
        batches.push(texts.slice(i, i + this.maxBatchSize));
      }

      const results: number[][] = [];
      for (const batch of batches) {
        const batchResults = await this.embedBatchInternal(batch);
        results.push(...batchResults);
      }
      return results;
    }

    return this.embedBatchInternal(texts);
  }

  /**
   * Internal batch embedding with retry logic
   */
  private async embedBatchInternal(
    texts: string[],
    retries = 3,
  ): Promise<number[][]> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.timeout);

    try {
      const response = await fetch(`${this.baseUrl}/api/embed`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          model: this.model,
          input: texts,
        }),
        signal: controller.signal,
      });

      clearTimeout(timeoutId);

      if (!response.ok) {
        const errorData = (await response.json().catch(() => null)) as OllamaErrorResponse | null;

        // Retry on server errors
        if (response.status >= 500 && retries > 0) {
          await this.sleep(1000 * (4 - retries)); // Exponential backoff
          return this.embedBatchInternal(texts, retries - 1);
        }

        throw new EmbeddingError(
          `Ollama API error: ${errorData?.error || response.statusText}`,
          {
            context: {
              status: response.status,
              error: errorData?.error,
            },
            retryable: response.status >= 500,
          },
        );
      }

      const data = (await response.json()) as OllamaEmbedResponse;

      // Update dimensions from actual response if not known
      if (data.embeddings.length > 0 && data.embeddings[0]!.length !== this._dimensions) {
        this._dimensions = data.embeddings[0]!.length;
      }

      return data.embeddings;
    } catch (error) {
      clearTimeout(timeoutId);

      if (error instanceof EmbeddingError) {
        throw error;
      }

      if ((error as Error).name === 'AbortError') {
        throw new EmbeddingError('Ollama API request timed out', {
          retryable: true,
        });
      }

      // Retry on network errors (Ollama might not be running)
      if (retries > 0) {
        await this.sleep(1000 * (4 - retries));
        return this.embedBatchInternal(texts, retries - 1);
      }

      throw new EmbeddingError(
        `Ollama API request failed: ${(error as Error).message}. Is Ollama running?`,
        {
          cause: error instanceof Error ? error : new Error(String(error)),
          retryable: true,
        },
      );
    }
  }

  /**
   * Estimate cost for embedding (always 0 for local models)
   */
  estimateCost(_tokenCount: number): number {
    return 0; // Local models have no API cost
  }
}
