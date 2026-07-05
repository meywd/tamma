/**
 * OpenAI Embedding Provider
 *
 * Generates embeddings using OpenAI's text-embedding-3-small model.
 */

import { BaseEmbeddingProvider } from './base-embedding-provider.js';
import type { EmbeddingProviderConfig } from '../types.js';
import { EmbeddingError, EmbeddingRateLimitError } from '../errors.js';

/**
 * OpenAI embedding model configurations
 */
const OPENAI_MODELS: Record<
  string,
  { dimensions: number; costPer1kTokens: number }
> = {
  'text-embedding-3-small': { dimensions: 1536, costPer1kTokens: 0.00002 },
  'text-embedding-3-large': { dimensions: 3072, costPer1kTokens: 0.00013 },
  'text-embedding-ada-002': { dimensions: 1536, costPer1kTokens: 0.0001 },
};

/**
 * OpenAI API response types
 */
interface OpenAIEmbeddingResponse {
  object: string;
  data: Array<{
    object: string;
    index: number;
    embedding: number[];
  }>;
  model: string;
  usage: {
    prompt_tokens: number;
    total_tokens: number;
  };
}

interface OpenAIErrorResponse {
  error: {
    message: string;
    type: string;
    param?: string;
    code?: string;
  };
}

/**
 * OpenAI embedding provider implementation
 */
export class OpenAIEmbeddingProvider extends BaseEmbeddingProvider {
  readonly name = 'openai';
  private _dimensions = 1536;
  readonly maxBatchSize = 2048; // OpenAI's batch limit

  private apiKey: string = '';
  private baseUrl = 'https://api.openai.com/v1';
  private model = 'text-embedding-3-small';
  private timeout = 30000;
  private costPer1kTokens = 0.00002;

  get dimensions(): number {
    return this._dimensions;
  }

  /**
   * Initialize the OpenAI provider
   */
  async initialize(config: EmbeddingProviderConfig): Promise<void> {
    if (!config.apiKey) {
      throw new EmbeddingError('OpenAI API key is required', {
        context: { provider: this.name },
      });
    }

    this.apiKey = config.apiKey;
    this.model = config.model || 'text-embedding-3-small';
    this.baseUrl = config.baseUrl || 'https://api.openai.com/v1';
    this.timeout = config.timeout || 30000;

    // Set dimensions based on model
    const modelConfig = OPENAI_MODELS[this.model];
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
      const response = await fetch(`${this.baseUrl}/embeddings`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${this.apiKey}`,
        },
        body: JSON.stringify({
          input: texts,
          model: this.model,
        }),
        signal: controller.signal,
      });

      clearTimeout(timeoutId);

      if (!response.ok) {
        const errorData = (await response.json().catch(() => null)) as OpenAIErrorResponse | null;

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
          `OpenAI API error: ${errorData?.error?.message || response.statusText}`,
          {
            context: {
              status: response.status,
              error: errorData?.error,
            },
            retryable: response.status >= 500,
          },
        );
      }

      const data = (await response.json()) as OpenAIEmbeddingResponse;

      // Sort by index to ensure correct order
      const sortedData = [...data.data].sort((a, b) => a.index - b.index);
      return sortedData.map((d) => d.embedding);
    } catch (error) {
      clearTimeout(timeoutId);

      if (error instanceof EmbeddingError || error instanceof EmbeddingRateLimitError) {
        throw error;
      }

      if ((error as Error).name === 'AbortError') {
        throw new EmbeddingError('OpenAI API request timed out', {
          retryable: true,
        });
      }

      // Retry on network errors
      if (retries > 0) {
        await this.sleep(1000 * (4 - retries));
        return this.embedBatchInternal(texts, retries - 1);
      }

      throw new EmbeddingError(
        `OpenAI API request failed: ${(error as Error).message}`,
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
