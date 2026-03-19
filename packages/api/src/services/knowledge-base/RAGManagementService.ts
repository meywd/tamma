/**
 * RAG Management Service
 *
 * Manages RAG pipeline configuration, testing, and metrics.
 *
 * Delegates to a real IRAGPipeline implementation when available;
 * otherwise returns empty/zero state.
 */

import type {
  RAGConfigInfo,
  RAGMetricsInfo,
  RAGTestRequest,
  RAGTestResult,
} from '@tamma/shared';
import type { IRAGPipeline } from './types.js';

const DEFAULT_RAG_CONFIG: RAGConfigInfo = {
  sources: {
    vectorDb: { enabled: false, weight: 0, topK: 0 },
    keyword: { enabled: false, weight: 0, topK: 0 },
    docs: { enabled: false, weight: 0, topK: 0 },
    issues: { enabled: false, weight: 0, topK: 0 },
  },
  ranking: {
    fusionMethod: 'rrf',
    mmrLambda: 0.7,
    recencyBoost: 0.1,
  },
  assembly: {
    maxTokens: 4000,
    format: 'xml',
    includeScores: false,
  },
  caching: {
    enabled: true,
    ttlSeconds: 300,
    maxEntries: 1000,
  },
};

export class RAGManagementService {
  private readonly pipeline: IRAGPipeline | null;
  private config: RAGConfigInfo;
  private queryCount = 0;
  private totalLatencyMs = 0;

  constructor(pipeline?: IRAGPipeline) {
    this.pipeline = pipeline ?? null;
    this.config = { ...DEFAULT_RAG_CONFIG };
  }

  async getConfig(): Promise<RAGConfigInfo> {
    return { ...this.config };
  }

  async updateConfig(config: Partial<RAGConfigInfo>): Promise<RAGConfigInfo> {
    if (config.sources) {
      this.config.sources = { ...this.config.sources, ...config.sources };
    }
    if (config.ranking) {
      this.config.ranking = { ...this.config.ranking, ...config.ranking };
    }
    if (config.assembly) {
      this.config.assembly = { ...this.config.assembly, ...config.assembly };
    }
    if (config.caching) {
      this.config.caching = { ...this.config.caching, ...config.caching };
    }

    // Push config to real pipeline if available
    if (this.pipeline?.configure) {
      this.pipeline.configure({
        sources: this.config.sources,
        ranking: this.config.ranking,
        assembly: this.config.assembly,
        caching: this.config.caching,
      });
    }

    return { ...this.config };
  }

  async getMetrics(): Promise<RAGMetricsInfo> {
    if (!this.pipeline) {
      return {
        totalQueries: 0,
        avgLatencyMs: 0,
        cacheHitRate: 0,
        avgTokensRetrieved: 0,
        sourceBreakdown: {},
      };
    }

    const cacheStats = this.pipeline.getCacheStats?.();
    const feedbackOverview = this.pipeline.getFeedbackOverview?.();

    const totalHits = cacheStats ? cacheStats.hits : 0;
    const totalMisses = cacheStats ? cacheStats.misses : 0;
    const cacheTotal = totalHits + totalMisses;
    const cacheHitRate = cacheTotal > 0 ? totalHits / cacheTotal : 0;

    return {
      totalQueries: feedbackOverview && feedbackOverview.totalFeedback > 0
        ? feedbackOverview.totalFeedback
        : this.queryCount,
      avgLatencyMs: this.queryCount > 0 ? this.totalLatencyMs / this.queryCount : 0,
      cacheHitRate,
      avgTokensRetrieved: 0,
      sourceBreakdown: {},
    };
  }

  async testQuery(request: RAGTestRequest): Promise<RAGTestResult> {
    if (!this.pipeline) {
      return {
        queryId: '',
        chunks: [],
        assembledContext: '',
        tokenCount: 0,
        latencyMs: 0,
        sources: [],
      };
    }

    const startTime = Date.now();
    this.queryCount++;

    const options: Record<string, unknown> = {};
    if (request.sources) {
      options.sources = request.sources;
    }
    if (request.maxTokens !== undefined) {
      options.maxTokens = request.maxTokens;
    }
    if (request.topK !== undefined) {
      options.topK = request.topK;
    }

    const result = await this.pipeline.retrieve(request.query, options);

    const latencyMs = result.durationMs ?? (Date.now() - startTime);
    this.totalLatencyMs += latencyMs;

    return {
      queryId: '',
      chunks: result.chunks.map((chunk) => ({
        id: '',
        content: chunk.content,
        source: chunk.source,
        score: chunk.score,
        metadata: chunk.metadata ?? {},
      })),
      assembledContext: result.chunks.map((c) => c.content).join('\n\n'),
      tokenCount: 0,
      latencyMs,
      sources: [],
    };
  }
}
