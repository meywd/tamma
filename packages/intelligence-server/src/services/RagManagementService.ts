/**
 * RAG Management Service
 *
 * Wraps a real IRagPipeline to expose the 4 C# /kb/rag/* endpoints:
 * get config, update config, query, metrics.
 */

import type { IRagPipeline } from '../types.js';

export interface RagConfigResponse {
  enabled: boolean;
  sources: {
    vectorDb: { enabled: boolean; weight: number; topK: number };
    keyword: { enabled: boolean; weight: number; topK: number };
    docs: { enabled: boolean; weight: number; topK: number };
    issues: { enabled: boolean; weight: number; topK: number };
  };
  ranking: {
    fusionMethod: 'rrf' | 'weighted';
    mmrLambda: number;
    recencyBoost: number;
  };
  assembly: {
    maxTokens: number;
    format: 'xml' | 'markdown';
    includeScores: boolean;
  };
  caching: {
    enabled: boolean;
    ttlSeconds: number;
    maxEntries: number;
  };
}

export interface RagQueryRequest {
  query: string;
  topK?: number;
  sources?: string[];
  maxTokens?: number;
}

export interface RagQueryResponse {
  answer: string;
  sources: Array<{
    content: string;
    source: string;
    score: number;
    metadata: Record<string, unknown>;
  }>;
  queryId: string;
  latencyMs: number;
}

export interface RagMetricsResponse {
  queries: number;
  avgLatencyMs: number;
  cacheHitRate: number;
  avgTokensRetrieved: number;
}

const DEFAULT_CONFIG: RagConfigResponse = {
  enabled: false,
  sources: {
    vectorDb: { enabled: false, weight: 0, topK: 0 },
    keyword: { enabled: false, weight: 0, topK: 0 },
    docs: { enabled: false, weight: 0, topK: 0 },
    issues: { enabled: false, weight: 0, topK: 0 },
  },
  ranking: { fusionMethod: 'rrf', mmrLambda: 0.7, recencyBoost: 0.1 },
  assembly: { maxTokens: 4000, format: 'xml', includeScores: false },
  caching: { enabled: true, ttlSeconds: 300, maxEntries: 1000 },
};

export class RagManagementService {
  private readonly pipeline: IRagPipeline | null;
  private config: RagConfigResponse;
  private queryCount = 0;
  private totalLatencyMs = 0;

  constructor(pipeline?: IRagPipeline) {
    this.pipeline = pipeline ?? null;
    this.config = { ...DEFAULT_CONFIG, enabled: Boolean(pipeline) };
  }

  async getConfig(): Promise<RagConfigResponse> {
    return { ...this.config };
  }

  async updateConfig(
    patch: Partial<RagConfigResponse>,
  ): Promise<RagConfigResponse & { message: string }> {
    if (patch.enabled !== undefined) this.config.enabled = patch.enabled;
    if (patch.sources) this.config.sources = { ...this.config.sources, ...patch.sources };
    if (patch.ranking) this.config.ranking = { ...this.config.ranking, ...patch.ranking };
    if (patch.assembly) this.config.assembly = { ...this.config.assembly, ...patch.assembly };
    if (patch.caching) this.config.caching = { ...this.config.caching, ...patch.caching };
    if (this.pipeline?.configure) {
      this.pipeline.configure({
        sources: this.config.sources,
        ranking: this.config.ranking,
        assembly: this.config.assembly,
        caching: this.config.caching,
      });
    }
    return { ...this.config, message: 'RAG config updated' };
  }

  async query(req: RagQueryRequest): Promise<RagQueryResponse> {
    if (!this.pipeline) {
      return { answer: '', sources: [], queryId: '', latencyMs: 0 };
    }
    const start = Date.now();
    this.queryCount++;
    const retrieveQuery: { text: string; maxResults?: number; sources?: string[] } = {
      text: req.query,
    };
    if (req.topK !== undefined) retrieveQuery.maxResults = req.topK;
    if (req.sources) retrieveQuery.sources = req.sources;
    const options: Record<string, unknown> = {};
    if (req.maxTokens !== undefined) options.maxTokens = req.maxTokens;

    const result = await this.pipeline.retrieve(retrieveQuery, options);
    const latencyMs = result.latencyMs ?? Date.now() - start;
    this.totalLatencyMs += latencyMs;

    return {
      answer: result.retrievedChunks.map((c) => c.content).join('\n\n'),
      sources: result.retrievedChunks.map((c) => ({
        content: c.content,
        source: c.source,
        score: c.score,
        metadata: c.metadata ?? {},
      })),
      queryId: result.queryId,
      latencyMs,
    };
  }

  async getMetrics(): Promise<RagMetricsResponse> {
    if (!this.pipeline) {
      return { queries: 0, avgLatencyMs: 0, cacheHitRate: 0, avgTokensRetrieved: 0 };
    }
    const cache = this.pipeline.getCacheStats?.();
    const hits = cache?.hits ?? 0;
    const misses = cache?.misses ?? 0;
    const total = hits + misses;
    const cacheHitRate = total > 0 ? hits / total : 0;
    return {
      queries: this.queryCount,
      avgLatencyMs: this.queryCount > 0 ? this.totalLatencyMs / this.queryCount : 0,
      cacheHitRate,
      avgTokensRetrieved: 0,
    };
  }
}
