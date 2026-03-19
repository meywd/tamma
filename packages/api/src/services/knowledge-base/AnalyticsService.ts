/**
 * Analytics Service
 *
 * Provides usage analytics, quality metrics, and cost analysis
 * for the knowledge base system.
 *
 * Delegates to the real CostTracker from @tamma/cost-monitor
 * when available; otherwise returns zero state.
 */

import type {
  UsageAnalytics,
  QualityAnalytics,
  CostAnalytics,
  AnalyticsPeriodFilter,
} from '@tamma/shared';
import type { CostTracker } from '@tamma/cost-monitor';

export class AnalyticsService {
  private readonly costTracker: CostTracker | null;

  constructor(costTracker?: CostTracker) {
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

    const startDate = new Date(period.start);
    const endDate = new Date(period.end);

    // Query usage records from cost tracker
    const records = await this.costTracker.getUsage({
      startDate,
      endDate,
    });

    const totalQueries = records.length;
    let totalTokensRetrieved = 0;
    let totalLatencyMs = 0;

    // Aggregate metrics from usage records
    for (const record of records) {
      totalTokensRetrieved += (record.inputTokens ?? 0) + (record.outputTokens ?? 0);
      totalLatencyMs += record.latencyMs ?? 0;
    }

    return {
      period: { start: period.start, end: period.end },
      totalQueries,
      totalTokensRetrieved,
      avgLatencyMs: totalQueries > 0 ? totalLatencyMs / totalQueries : 0,
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

    const startDate = new Date(period.start);
    const endDate = new Date(period.end);

    const totalCostUsd = await this.costTracker.getTotalCost({
      startDate,
      endDate,
    });

    // Get per-model breakdown using aggregation
    const byModel = await this.costTracker.getAggregate(
      { startDate, endDate },
      ['model'],
    );

    const breakdown = byModel.map((agg) => ({
      category: agg.dimensionValue || 'Unknown',
      costUsd: agg.totalCostUsd,
      units: agg.totalCalls,
      unitCostUsd: agg.totalCalls > 0 ? agg.totalCostUsd / agg.totalCalls : 0,
    }));

    return {
      period: { start: period.start, end: period.end },
      totalCostUsd,
      embeddingCostUsd: 0,
      indexingCostUsd: 0,
      breakdown,
    };
  }
}
