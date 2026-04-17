/**
 * Context Testing Service
 *
 * Exposes the 3 C# /kb/context/* endpoints: get history, post feedback,
 * get config. Wraps a real IContextAggregatorAdapter when available.
 */

import type { IContextAggregatorAdapter } from '../types.js';

export interface ContextHistoryEntry {
  id: string;
  query: string;
  timestamp: string;
  tokenCount: number;
  chunksCount: number;
}

export interface ContextHistoryResponse {
  history: ContextHistoryEntry[];
}

export interface ContextFeedbackRequest {
  requestId: string;
  helpful: boolean;
  notes?: string;
}

export interface ContextConfigResponse {
  maxTokens: number;
  strategy: 'sliding_window' | 'full' | 'compressive';
  deduplication: boolean;
}

export class ContextTestingService {
  private readonly aggregator: IContextAggregatorAdapter | null;
  private history: ContextHistoryEntry[] = [];
  private feedback: Map<string, ContextFeedbackRequest> = new Map();
  private config: ContextConfigResponse = {
    maxTokens: 100000,
    strategy: 'sliding_window',
    deduplication: true,
  };

  constructor(aggregator?: IContextAggregatorAdapter) {
    this.aggregator = aggregator ?? null;
  }

  async getHistory(limit = 50): Promise<ContextHistoryResponse> {
    return { history: this.history.slice(0, limit) };
  }

  async submitFeedback(req: ContextFeedbackRequest): Promise<{ message: string }> {
    this.feedback.set(req.requestId, req);
    return { message: 'Feedback recorded' };
  }

  async getConfig(): Promise<ContextConfigResponse> {
    return { ...this.config };
  }

  /**
   * Test helper: record a history entry. Exposed for tests that want to
   * seed the service without running a real aggregator.
   */
  recordTest(entry: ContextHistoryEntry): void {
    this.history.unshift(entry);
    if (this.history.length > 50) this.history.length = 50;
  }

  /**
   * Optional: exercise the aggregator. Not exposed as an endpoint today
   * (the 3 KB routes are history/feedback/config) but kept so the
   * aggregator dependency is not dead weight when wiring real intelligence.
   */
  async runQuery(
    req: { query: string; taskType?: string; maxTokens?: number },
  ): Promise<{ tokenCount: number; chunks: number; requestId: string } | null> {
    if (!this.aggregator) return null;
    const agRequest: { query: string; taskType?: string; maxTokens?: number } = {
      query: req.query,
    };
    if (req.taskType !== undefined) agRequest.taskType = req.taskType;
    if (req.maxTokens !== undefined) agRequest.maxTokens = req.maxTokens;
    const response = await this.aggregator.getContext(agRequest);
    const entry: ContextHistoryEntry = {
      id: response.requestId,
      query: req.query,
      timestamp: new Date().toISOString(),
      tokenCount: response.context.tokenCount,
      chunksCount: response.context.chunks.length,
    };
    this.recordTest(entry);
    return {
      tokenCount: response.context.tokenCount,
      chunks: response.context.chunks.length,
      requestId: response.requestId,
    };
  }
}
