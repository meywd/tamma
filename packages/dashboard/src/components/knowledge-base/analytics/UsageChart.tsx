/**
 * Usage Chart
 *
 * Displays usage analytics with source breakdown.
 */

import React, { type JSX } from 'react';
import type { UsageAnalytics } from '@tamma/shared';

export interface UsageChartProps {
  analytics: UsageAnalytics;
}

export function UsageChart({ analytics }: UsageChartProps): JSX.Element {
  return (
    <div data-testid="usage-chart" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>Usage Overview</h3>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: '16px', marginBottom: '20px' }}>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Total Queries</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{analytics.totalQueries.toLocaleString()}</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Tokens Retrieved</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>
            {analytics.totalTokensRetrieved >= 1000000
              ? `${(analytics.totalTokensRetrieved / 1000000).toFixed(1)}M`
              : `${(analytics.totalTokensRetrieved / 1000).toFixed(0)}k`}
          </div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Avg Latency</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{analytics.avgLatencyMs.toFixed(0)}ms</div>
        </div>
      </div>

      <h4 style={{ margin: '0 0 12px 0', fontSize: '14px', fontWeight: 600, color: '#374151' }}>Source Breakdown</h4>
      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
        {Object.entries(analytics.sourceBreakdown).map(([source, usage]) => (
          <div key={source} style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
            <span style={{ width: '80px', fontSize: '13px', fontWeight: 500 }}>{source}</span>
            <div style={{ flex: 1, height: '8px', backgroundColor: '#e5e7eb', borderRadius: '4px', overflow: 'hidden' }}>
              <div
                style={{
                  width: `${Math.min((usage.queries / analytics.totalQueries) * 100, 100)}%`,
                  height: '100%',
                  backgroundColor: '#3b82f6',
                  borderRadius: '4px',
                }}
              />
            </div>
            <span style={{ fontSize: '12px', color: '#6b7280', width: '80px' }}>
              {usage.queries} queries
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
