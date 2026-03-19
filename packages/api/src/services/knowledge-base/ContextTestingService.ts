/**
 * Context Testing Service
 *
 * Provides interactive context retrieval testing and feedback collection.
 *
 * Delegates to the real ContextAggregator from @tamma/intelligence
 * when available; otherwise returns empty state.
 */

import type {
  ContextTestRequest,
  ContextTestResult,
  ContextFeedbackRequest,
  UIContextChunk,
  UIContextSource,
} from '@tamma/shared';
import type { ContextAggregator } from '@tamma/intelligence/context';

export class ContextTestingService {
  private readonly aggregator: ContextAggregator | null;
  private testHistory: ContextTestResult[] = [];
  private feedback: Map<string, ContextFeedbackRequest> = new Map();

  constructor(aggregator?: ContextAggregator) {
    this.aggregator = aggregator ?? null;
  }

  async testContext(request: ContextTestRequest): Promise<ContextTestResult> {
    if (!this.aggregator) {
      // Return empty result when no aggregator is configured
      const emptyResult: ContextTestResult = {
        requestId: '',
        context: {
          text: '',
          chunks: [],
          tokenCount: 0,
          format: request.options?.includeMetadata ? 'xml' : 'markdown',
        },
        sources: [],
        metrics: {
          totalLatencyMs: 0,
          totalTokens: 0,
          budgetUtilization: 0,
          deduplicationRate: 0,
          cacheHitRate: 0,
        },
      };
      return emptyResult;
    }

    const contextRequest: import('@tamma/intelligence/context').ContextRequest = {
      query: request.query,
      taskType: request.taskType,
      maxTokens: request.maxTokens,
    };
    if (request.sources) {
      contextRequest.sources = request.sources;
    }
    if (request.hints) {
      const hints: import('@tamma/intelligence/context').ContextHints = {};
      if (request.hints.relatedFiles) {
        hints.relatedFiles = request.hints.relatedFiles;
      }
      if (request.hints.relatedIssues) {
        hints.relatedIssues = request.hints.relatedIssues;
      }
      if (request.hints.language) {
        hints.language = request.hints.language;
      }
      if (request.hints.framework) {
        hints.framework = request.hints.framework;
      }
      contextRequest.hints = hints;
    }
    if (request.options) {
      const opts: import('@tamma/intelligence/context').ContextOptions = {};
      if (request.options.deduplicate !== undefined) {
        opts.deduplicate = request.options.deduplicate;
      }
      if (request.options.compress !== undefined) {
        opts.compress = request.options.compress;
      }
      if (request.options.summarize !== undefined) {
        opts.summarize = request.options.summarize;
      }
      if (request.options.includeMetadata !== undefined) {
        opts.includeMetadata = request.options.includeMetadata;
      }
      contextRequest.options = opts;
    }

    const response = await this.aggregator.getContext(contextRequest);

    // Map the ContextResponse to the UI ContextTestResult shape
    const chunks: UIContextChunk[] = response.context.chunks.map((chunk) => {
      const meta: UIContextChunk['metadata'] = {};
      if (chunk.metadata.filePath !== undefined) {
        meta.filePath = chunk.metadata.filePath;
      }
      if (chunk.metadata.startLine !== undefined) {
        meta.startLine = chunk.metadata.startLine;
      }
      if (chunk.metadata.endLine !== undefined) {
        meta.endLine = chunk.metadata.endLine;
      }
      if (chunk.metadata.language !== undefined) {
        meta.language = chunk.metadata.language;
      }
      if (chunk.metadata.symbolName !== undefined) {
        meta.symbolName = chunk.metadata.symbolName;
      }
      return {
        id: chunk.id,
        content: chunk.content,
        source: chunk.source as UIContextSource,
        relevance: chunk.relevance,
        metadata: meta,
      };
    });

    const result: ContextTestResult = {
      requestId: response.requestId,
      context: {
        text: response.context.text,
        chunks,
        tokenCount: response.context.tokenCount,
        format: response.context.format,
      },
      sources: response.sources.map((s) => ({
        source: s.source as UIContextSource,
        chunksProvided: s.chunksProvided,
        tokensUsed: s.tokensUsed,
        latencyMs: s.latencyMs,
        cacheHit: s.cacheHit,
      })),
      metrics: {
        totalLatencyMs: response.metrics.totalLatencyMs,
        totalTokens: response.metrics.totalTokens,
        budgetUtilization: response.metrics.budgetUtilization,
        deduplicationRate: response.metrics.deduplicationRate,
        cacheHitRate: response.metrics.cacheHitRate,
      },
    };

    this.testHistory.unshift(result);
    if (this.testHistory.length > 50) {
      this.testHistory.pop();
    }

    return result;
  }

  async submitFeedback(feedbackRequest: ContextFeedbackRequest): Promise<void> {
    this.feedback.set(feedbackRequest.requestId, feedbackRequest);
  }

  async getRecentTests(limit = 10): Promise<ContextTestResult[]> {
    return this.testHistory.slice(0, limit);
  }
}
