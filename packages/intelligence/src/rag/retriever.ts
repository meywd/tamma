/**
 * Multi-Source Retriever
 *
 * Orchestrates retrieval from multiple RAG sources with parallel execution,
 * timeout handling, and error resilience.
 */

import type {
  IRAGSource,
  RAGSourceType,
  RAGConfig,
  ProcessedQuery,
  RetrievedChunk,
  SourceAttribution,
  RetrieveOptions,
} from './types.js';
import { SourceTimeoutError, SourceUnavailableError } from './errors.js';

/**
 * Result from a single source retrieval
 */
interface SourceRetrievalResult {
  source: RAGSourceType;
  chunks: RetrievedChunk[];
  latencyMs: number;
  error?: Error;
}

/**
 * Multi-source retriever
 */
export class Retriever {
  private sources: Map<RAGSourceType, IRAGSource>;

  constructor() {
    this.sources = new Map();
  }

  /**
   * Register a source adapter
   */
  registerSource(source: IRAGSource): void {
    this.sources.set(source.name, source);
  }

  /**
   * Unregister a source adapter
   */
  unregisterSource(name: RAGSourceType): void {
    this.sources.delete(name);
  }

  /**
   * Get a registered source
   */
  getSource(name: RAGSourceType): IRAGSource | undefined {
    return this.sources.get(name);
  }

  /**
   * Get all registered sources
   */
  getAllSources(): IRAGSource[] {
    return Array.from(this.sources.values());
  }

  /**
   * Initialize all sources with configuration
   */
  async initializeSources(config: RAGConfig): Promise<void> {
    const initPromises: Promise<void>[] = [];

    for (const [name, source] of this.sources) {
      const sourceConfig = config.sources[name];
      if (sourceConfig) {
        initPromises.push(source.initialize(sourceConfig));
      }
    }

    await Promise.all(initPromises);
  }

  /**
   * Retrieve from all specified sources in parallel
   */
  async retrieveFromAllSources(
    query: ProcessedQuery,
    requestedSources: RAGSourceType[],
    config: RAGConfig
  ): Promise<{
    results: Map<RAGSourceType, RetrievedChunk[]>;
    attributions: SourceAttribution[];
  }> {
    // Filter to enabled and registered sources
    const activeSources = requestedSources.filter((name) => {
      const source = this.sources.get(name);
      const sourceConfig = config.sources[name];
      return source && sourceConfig?.enabled && source.enabled;
    });

    // Create retrieval promises
    const retrievalPromises = activeSources.map(async (name) =>
      this.retrieveFromSource(name, query, config)
    );

    // Execute with overall timeout
    const results = await Promise.race([
      Promise.all(retrievalPromises),
      this.createTimeoutPromise(config.timeouts.totalMs, activeSources.length),
    ]);

    // Aggregate results
    const resultMap = new Map<RAGSourceType, RetrievedChunk[]>();
    const attributions: SourceAttribution[] = [];

    for (const result of results) {
      if (result.error) {
        // Log error but continue with other sources
        console.warn(`Source '${result.source}' failed:`, result.error.message);
        continue;
      }

      resultMap.set(result.source, result.chunks);

      // Calculate attribution
      if (result.chunks.length > 0) {
        const avgScore =
          result.chunks.reduce((sum, c) => sum + c.score, 0) / result.chunks.length;

        attributions.push({
          source: result.source,
          count: result.chunks.length,
          avgScore,
          latencyMs: result.latencyMs,
        });
      }
    }

    return { results: resultMap, attributions };
  }

  /**
   * Retrieve from a single source with timeout
   */
  private async retrieveFromSource(
    name: RAGSourceType,
    query: ProcessedQuery,
    config: RAGConfig
  ): Promise<SourceRetrievalResult> {
    const source = this.sources.get(name);
    const sourceConfig = config.sources[name];

    if (!source || !sourceConfig) {
      return {
        source: name,
        chunks: [],
        latencyMs: 0,
        error: new SourceUnavailableError(name),
      };
    }

    const startTime = Date.now();
    const options: RetrieveOptions = {
      topK: sourceConfig.topK,
      timeout: config.timeouts.perSourceMs,
    };

    try {
      const chunks = await source.retrieve(query, options);
      return {
        source: name,
        chunks,
        latencyMs: Date.now() - startTime,
      };
    } catch (error) {
      return {
        source: name,
        chunks: [],
        latencyMs: Date.now() - startTime,
        error: error instanceof Error ? error : new Error(String(error)),
      };
    }
  }

  /**
   * Create a timeout promise that returns empty results
   */
  private async createTimeoutPromise(
    timeoutMs: number,
    numSources: number
  ): Promise<SourceRetrievalResult[]> {
    return new Promise((resolve) => {
      setTimeout(() => {
        // Return empty results for all sources on timeout
        const results: SourceRetrievalResult[] = Array.from(this.sources.keys()).map(
          (source) => ({
            source,
            chunks: [],
            latencyMs: timeoutMs,
            error: new SourceTimeoutError(source, timeoutMs),
          })
        );
        resolve(results.slice(0, numSources));
      }, timeoutMs);
    });
  }

  /**
   * Check health of all sources
   */
  async checkHealth(): Promise<Map<RAGSourceType, boolean>> {
    const healthMap = new Map<RAGSourceType, boolean>();

    const healthChecks = Array.from(this.sources.entries()).map(
      async ([name, source]) => {
        const healthy = await source.healthCheck();
        healthMap.set(name, healthy);
      }
    );

    await Promise.all(healthChecks);
    return healthMap;
  }

  /**
   * Dispose all sources
   */
  async dispose(): Promise<void> {
    const disposePromises = Array.from(this.sources.values()).map(async (source) =>
      source.dispose()
    );
    await Promise.all(disposePromises);
    this.sources.clear();
  }
}

/**
 * Create a retriever instance
 */
export function createRetriever(): Retriever {
  return new Retriever();
}
