/**
 * Analytics Service
 *
 * Provides usage analytics, quality metrics, and cost analysis
 * for the knowledge base system.
 *
 * Delegates to a real ICostTracker implementation when available;
 * otherwise returns zero state.
 */

import type {
  UsageAnalytics,
  QualityAnalytics,
  CostAnalytics,
  AnalyticsPeriodFilter,
} from '@tamma/shared';
import type { ICostTracker } from './types.js';

export class AnalyticsService {
  private readonly costTracker: ICostTracker | null;

  constructor(costTracker?: ICostTracker) {
    this.costTracker = costTracker ?? null;
  }

  async getUsageAnalytics(period: AnalyticsPeriodFilter): Promise<UsageAnalytics> {
    if (!this.costTracker) {
      return {
        period: { start: period.start, end: period.end },
        totalQueries: 0,
        totalTokensRetrieved: 0,
        avgLatencyMs: 0,
        sourceBreakdown: {},
      };
    }

    const usage = this.costTracker.getUsage({ start: period.start, end: period.end });

    return {
      period: { start: period.start, end: period.end },
      totalQueries: usage.totalRequests,
      totalTokensRetrieved: usage.totalTokens,
      avgLatencyMs: 0,
      sourceBreakdown: {},
    };
  }

  async getQualityAnalytics(period: AnalyticsPeriodFilter): Promise<QualityAnalytics> {
    // Quality analytics require feedback data which the CostTracker does not track.
    // Return zero state; this can be enhanced when a dedicated feedback store is available.
    return {
      period: { start: period.start, end: period.end },
      totalFeedback: 0,
      relevanceRate: 0,
      avgRelevanceScore: 0,
      topPerformingSources: [],
      improvementTrend: 0,
    };
  }

  async getCostAnalytics(period: AnalyticsPeriodFilter): Promise<CostAnalytics> {
    if (!this.costTracker) {
      return {
        period: { start: period.start, end: period.end },
        totalCostUsd: 0,
        embeddingCostUsd: 0,
        indexingCostUsd: 0,
        breakdown: [],
      };
    }

    const totalCostUsd = this.costTracker.getTotalCost({ start: period.start, end: period.end });

    // Get per-model breakdown using aggregation if available
    const aggregate = this.costTracker.getAggregate?.({ start: period.start, end: period.end });
    const breakdown = aggregate
      ? Object.entries(aggregate.byModel).map(([model, costUsd]) => ({
          category: model,
          costUsd,
          units: 0,
          unitCostUsd: 0,
        }))
      : [];

    return {
      period: { start: period.start, end: period.end },
      totalCostUsd,
      embeddingCostUsd: 0,
      indexingCostUsd: 0,
      breakdown,
    };
  }
}
