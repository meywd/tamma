/**
 * Cohere Embedding Provider
 *
 * Generates embeddings using Cohere's embed-english-v3.0 model via the v2 API.
 */

import { BaseEmbeddingProvider } from './base-embedding-provider.js';
import type { EmbeddingProviderConfig } from '../types.js';
import { EmbeddingError, EmbeddingRateLimitError } from '../errors.js';

/**
 * Cohere embedding model configurations
 */
const COHERE_MODELS: Record<
  string,
  { dimensions: number; costPer1kTokens: number }
> = {
  'embed-english-v3.0': { dimensions: 1024, costPer1kTokens: 0.0001 },
  'embed-multilingual-v3.0': { dimensions: 1024, costPer1kTokens: 0.0001 },
  'embed-english-light-v3.0': { dimensions: 384, costPer1kTokens: 0.0001 },
  'embed-multilingual-light-v3.0': { dimensions: 384, costPer1kTokens: 0.0001 },
};

/**
 * Cohere v2 embed API response type
 */
interface CohereEmbeddingResponse {
  id: string;
  embeddings: {
    float: number[][];
  };
  texts: string[];
  meta: {
    api_version: {
      version: string;
    };
    billed_units: {
      input_tokens: number;
    };
  };
}

/**
 * Cohere API error response type
 */
interface CohereErrorResponse {
  message: string;
}

/**
 * Cohere embedding provider implementation
 */
export class CohereEmbeddingProvider extends BaseEmbeddingProvider {
  readonly name = 'cohere';
  private _dimensions = 1024;
  readonly maxBatchSize = 96; // Cohere's batch limit for embed v2

  private apiKey: string = '';
  private baseUrl = 'https://api.cohere.ai/v2';
  private model = 'embed-english-v3.0';
  private timeout = 30000;
  private costPer1kTokens = 0.0001;

  get dimensions(): number {
    return this._dimensions;
  }

  /**
   * Initialize the Cohere provider
   */
  async initialize(config: EmbeddingProviderConfig): Promise<void> {
    if (!config.apiKey) {
      throw new EmbeddingError('Cohere API key is required', {
        context: { provider: this.name },
      });
    }

    this.apiKey = config.apiKey;
    this.model = config.model || 'embed-english-v3.0';
    this.baseUrl = config.baseUrl || 'https://api.cohere.ai/v2';
    this.timeout = config.timeout || 30000;

    // Set dimensions based on model
    const modelConfig = COHERE_MODELS[this.model];
    if (modelConfig) {
      this._dimensions = modelConfig.dimensions;
      this.costPer1kTokens = modelConfig.costPer1kTokens;
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
      const response = await fetch(`${this.baseUrl}/embed`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${this.apiKey}`,
        },
        body: JSON.stringify({
          texts,
          model: this.model,
          input_type: 'search_document',
          embedding_types: ['float'],
        }),
        signal: controller.signal,
      });

      clearTimeout(timeoutId);

      if (!response.ok) {
        const errorData = (await response.json().catch(() => null)) as CohereErrorResponse | null;

        // Handle rate limiting
        if (response.status === 429) {
          const retryAfter = response.headers.get('retry-after');
          const retryAfterMs = retryAfter ? parseInt(retryAfter, 10) * 1000 : 60000;
          throw new EmbeddingRateLimitError(retryAfterMs);
        }

        // Retry on server errors
        if (response.status >= 500 && retries > 0) {
          await this.sleep(1000 * (4 - retries)); // Exponential backoff
          return this.embedBatchInternal(texts, retries - 1);
        }

        throw new EmbeddingError(
          `Cohere API error: ${errorData?.message || response.statusText}`,
          {
            context: {
              status: response.status,
              error: errorData?.message,
            },
            retryable: response.status >= 500,
          },
        );
      }

      const data = (await response.json()) as CohereEmbeddingResponse;

      return data.embeddings.float;
    } catch (error) {
      clearTimeout(timeoutId);

      if (error instanceof EmbeddingError || error instanceof EmbeddingRateLimitError) {
        throw error;
      }

      if ((error as Error).name === 'AbortError') {
        throw new EmbeddingError('Cohere API request timed out', {
          retryable: true,
        });
      }

      // Retry on network errors
      if (retries > 0) {
        await this.sleep(1000 * (4 - retries));
        return this.embedBatchInternal(texts, retries - 1);
      }

      throw new EmbeddingError(
        `Cohere API request failed: ${(error as Error).message}`,
        {
          cause: error instanceof Error ? error : new Error(String(error)),
          retryable: true,
        },
      );
    }
  }

  /**
   * Estimate cost for embedding
   */
  estimateCost(tokenCount: number): number {
    return (tokenCount / 1000) * this.costPer1kTokens;
  }
}
