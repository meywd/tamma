/**
 * RAG Management Service
 *
 * Manages RAG pipeline configuration, testing, and metrics.
 *
 * Delegates to the real RAGPipeline from @tamma/intelligence
 * when available; otherwise returns empty/zero state.
 */

import type {
  RAGConfigInfo,
  RAGMetricsInfo,
  RAGTestRequest,
  RAGTestResult,
} from '@tamma/shared';
import type { RAGPipeline } from '@tamma/intelligence/rag';

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
  private readonly pipeline: RAGPipeline | null;
  private config: RAGConfigInfo;
  private queryCount = 0;
  private totalLatencyMs = 0;

  constructor(pipeline?: RAGPipeline) {
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
    if (this.pipeline) {
      await this.pipeline.configure({
        sources: {
          vector_db: {
            enabled: this.config.sources.vectorDb.enabled,
            weight: this.config.sources.vectorDb.weight,
            topK: this.config.sources.vectorDb.topK,
          },
          keyword: {
            enabled: this.config.sources.keyword.enabled,
            weight: this.config.sources.keyword.weight,
            topK: this.config.sources.keyword.topK,
          },
          docs: {
            enabled: this.config.sources.docs.enabled,
            weight: this.config.sources.docs.weight,
            topK: this.config.sources.docs.topK,
          },
          issues: {
            enabled: this.config.sources.issues.enabled,
            weight: this.config.sources.issues.weight,
            topK: this.config.sources.issues.topK,
          },
          // prs and commits are not exposed in the UI config
          prs: { enabled: false, weight: 0.2, topK: 5 },
          commits: { enabled: false, weight: 0.1, topK: 5 },
        },
        ranking: {
          fusionMethod: this.config.ranking.fusionMethod,
          rrfK: 60,
          mmrLambda: this.config.ranking.mmrLambda,
          recencyBoost: this.config.ranking.recencyBoost,
          recencyDecayDays: 30,
        },
        assembly: {
          maxTokens: this.config.assembly.maxTokens,
          format: this.config.assembly.format,
          includeScores: this.config.assembly.includeScores,
          deduplicationThreshold: 0.85,
        },
        caching: {
          enabled: this.config.caching.enabled,
          ttlSeconds: this.config.caching.ttlSeconds,
          maxEntries: this.config.caching.maxEntries,
        },
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

    const cacheStats = this.pipeline.getCacheStats();
    const feedbackOverview = this.pipeline.getFeedbackOverview();

    return {
      totalQueries: feedbackOverview.totalQueries > 0 ? feedbackOverview.totalQueries : this.queryCount,
      avgLatencyMs: this.queryCount > 0 ? this.totalLatencyMs / this.queryCount : 0,
      cacheHitRate: cacheStats.hitRate,
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

    const ragQuery: import('@tamma/intelligence/rag').RAGQuery = {
      text: request.query,
    };
    if (request.sources) {
      ragQuery.sources = request.sources as Array<'vector_db' | 'keyword' | 'docs' | 'issues' | 'prs' | 'commits'>;
    }
    if (request.maxTokens !== undefined) {
      ragQuery.maxTokens = request.maxTokens;
    }
    if (request.topK !== undefined) {
      ragQuery.topK = request.topK;
    }

    const result = await this.pipeline.retrieve(ragQuery);

    const latencyMs = Date.now() - startTime;
    this.totalLatencyMs += latencyMs;

    return {
      queryId: result.queryId,
      chunks: result.retrievedChunks.map((chunk) => {
        const meta: RAGTestResult['chunks'][number]['metadata'] = {};
        if (chunk.metadata.filePath !== undefined) {
          meta.filePath = chunk.metadata.filePath;
        }
        if (chunk.metadata.startLine !== undefined) {
          meta.startLine = chunk.metadata.startLine;
        }
        if (chunk.metadata.endLine !== undefined) {
          meta.endLine = chunk.metadata.endLine;
        }
        if (chunk.metadata.url !== undefined) {
          meta.url = chunk.metadata.url;
        }
        if (chunk.metadata.date) {
          meta.date = chunk.metadata.date.toISOString();
        }
        return {
          id: chunk.id,
          content: chunk.content,
          source: chunk.source,
          score: chunk.fusedScore ?? chunk.score,
          metadata: meta,
        };
      }),
      assembledContext: result.assembledContext,
      tokenCount: result.tokenCount,
      latencyMs: result.latencyMs,
      sources: result.sources.map((s) => ({
        source: s.source,
        count: s.count,
        avgScore: s.avgScore,
        tokensUsed: 0,
      })),
    };
  }
}
