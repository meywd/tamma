/**
 * Quality Metrics
 *
 * Displays quality analytics including relevance rates and trends.
 */

import { type JSX } from 'react';
import type { QualityAnalytics } from '@tamma/shared';

export interface QualityMetricsProps {
  analytics: QualityAnalytics;
}

export function QualityMetrics({ analytics }: QualityMetricsProps): JSX.Element {
  const trendColor = analytics.improvementTrend >= 0 ? '#22c55e' : '#ef4444';
  const trendSign = analytics.improvementTrend >= 0 ? '+' : '';

  return (
    <div data-testid="quality-metrics" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>Quality Metrics</h3>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gap: '16px' }}>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Total Feedback</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{analytics.totalFeedback}</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Relevance Rate</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{(analytics.relevanceRate * 100).toFixed(0)}%</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Avg Relevance Score</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{analytics.avgRelevanceScore.toFixed(2)}</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Trend</div>
          <div style={{ fontSize: '24px', fontWeight: 700, color: trendColor }}>
            {trendSign}{(analytics.improvementTrend * 100).toFixed(1)}%
          </div>
        </div>
      </div>

      {analytics.topPerformingSources.length > 0 && (
        <div style={{ marginTop: '16px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280', marginBottom: '4px' }}>Top Performing Sources</div>
          <div style={{ display: 'flex', gap: '8px' }}>
            {analytics.topPerformingSources.map((source, idx) => (
              <span
                key={source}
                style={{
                  padding: '4px 10px',
                  borderRadius: '12px',
                  backgroundColor: idx === 0 ? '#dcfce7' : '#f3f4f6',
                  color: idx === 0 ? '#166534' : '#374151',
                  fontSize: '12px',
                  fontWeight: 500,
                }}
              >
                {source}
              </span>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
