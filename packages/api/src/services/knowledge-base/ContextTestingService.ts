/**
 * Context Testing Service
 *
 * Provides interactive context retrieval testing and feedback collection.
 *
 * Delegates to a real IContextAggregator implementation when available;
 * otherwise returns empty state.
 */

import type {
  ContextTestRequest,
  ContextTestResult,
  ContextFeedbackRequest,
  UIContextChunk,
  UIContextSource,
} from '@tamma/shared';
import type { IContextAggregator } from './types.js';

export class ContextTestingService {
  private readonly aggregator: IContextAggregator | null;
  private testHistory: ContextTestResult[] = [];
  private feedback: Map<string, ContextFeedbackRequest> = new Map();

  constructor(aggregator?: IContextAggregator) {
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

    const options: Record<string, unknown> = {};
    if (request.taskType) {
      options.taskType = request.taskType;
    }
    if (request.maxTokens !== undefined) {
      options.maxTokens = request.maxTokens;
    }
    if (request.sources) {
      options.sources = request.sources;
    }
    if (request.hints) {
      options.hints = request.hints;
    }
    if (request.options) {
      options.options = request.options;
    }

    const response = await this.aggregator.getContext(request.query, options);

    // Map the response to the UI ContextTestResult shape
    const chunks: UIContextChunk[] = response.chunks.map((chunk) => ({
      id: '',
      content: chunk.content,
      source: chunk.source as UIContextSource,
      relevance: chunk.score,
      metadata: (chunk.metadata ?? {}) as UIContextChunk['metadata'],
    }));

    const result: ContextTestResult = {
      requestId: '',
      context: {
        text: response.chunks.map((c) => c.content).join('\n\n'),
        chunks,
        tokenCount: response.totalTokens,
        format: request.options?.includeMetadata ? 'xml' : 'markdown',
      },
      sources: response.sources.map((s) => ({
        source: s.name as UIContextSource,
        chunksProvided: s.chunks,
        tokensUsed: 0,
        latencyMs: s.durationMs,
        cacheHit: false,
      })),
      metrics: {
        totalLatencyMs: response.durationMs,
        totalTokens: response.totalTokens,
        budgetUtilization: 0,
        deduplicationRate: 0,
        cacheHitRate: 0,
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
