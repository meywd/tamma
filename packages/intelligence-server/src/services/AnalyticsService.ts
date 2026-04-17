/**
 * Analytics Service
 *
 * Exposes the 3 C# /kb/analytics* endpoints: overall analytics, usage breakdown,
 * and cost breakdown. Wraps a real ICostTrackerAdapter when configured.
 */

import type { ICostTrackerAdapter } from '../types.js';

export interface KbAnalyticsResponse {
  queries: number;
  indexedDocs: number;
  hitRate: number;
  totalTokens: number;
}

export interface KbUsageResponse {
  daily: Array<{
    date: string;
    queries: number;
    tokens: number;
    costUsd: number;
  }>;
}

export interface KbCostsResponse {
  totalCost: number;
  breakdown: Array<{
    category: string;
    costUsd: number;
    percent: number;
  }>;
  period?: { start: string; end: string };
}

export interface AnalyticsPeriod {
  start?: string;
  end?: string;
}

export class AnalyticsService {
  private readonly costTracker: ICostTrackerAdapter | null;

  constructor(costTracker?: ICostTrackerAdapter) {
    this.costTracker = costTracker ?? null;
  }

  async getAnalytics(period?: AnalyticsPeriod): Promise<KbAnalyticsResponse> {
    if (!this.costTracker) {
      return { queries: 0, indexedDocs: 0, hitRate: 0, totalTokens: 0 };
    }
    const bound = this.toPeriod(period);
    const usage = await this.costTracker.getUsage(bound);
    const totalTokens = usage.reduce((sum, row) => sum + row.tokens, 0);
    return {
      queries: usage.length,
      indexedDocs: 0,
      hitRate: 0,
      totalTokens,
    };
  }

  async getUsage(period?: AnalyticsPeriod): Promise<KbUsageResponse> {
    if (!this.costTracker) {
      return { daily: [] };
    }
    const bound = this.toPeriod(period);
    const usage = await this.costTracker.getUsage(bound);
    // Bucket by YYYY-MM-DD derived from the period start; without per-row
    // timestamps this is a single-row aggregate that still matches the
    // documented shape (`daily: []` is also fine when there is no data).
    const totalCost = usage.reduce((sum, row) => sum + row.cost, 0);
    const totalTokens = usage.reduce((sum, row) => sum + row.tokens, 0);
    if (usage.length === 0) return { daily: [] };
    return {
      daily: [
        {
          date: (bound?.start ?? new Date().toISOString()).slice(0, 10),
          queries: usage.length,
          tokens: totalTokens,
          costUsd: totalCost,
        },
      ],
    };
  }

  async getCosts(period?: AnalyticsPeriod): Promise<KbCostsResponse> {
    if (!this.costTracker) {
      return { totalCost: 0, breakdown: [] };
    }
    const bound = this.toPeriod(period);
    const totalCost = await this.costTracker.getTotalCost(bound);
    const aggregate = await this.costTracker.getAggregate?.(bound);
    const breakdown = aggregate
      ? Object.entries(aggregate.byModel).map(([category, costUsd]) => ({
          category,
          costUsd,
          percent: totalCost > 0 ? (costUsd / totalCost) * 100 : 0,
        }))
      : [];
    const result: KbCostsResponse = { totalCost, breakdown };
    if (bound) result.period = bound;
    return result;
  }

  private toPeriod(period?: AnalyticsPeriod): { start: string; end: string } | undefined {
    if (!period?.start || !period?.end) return undefined;
    return { start: period.start, end: period.end };
  }
}
