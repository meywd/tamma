/**
 * Token Usage Report
 *
 * Tabular report of token usage across sources with period breakdown.
 */

import { type JSX } from 'react';
import type { UsageAnalytics } from '@tamma/shared';

export interface TokenUsageReportProps {
  analytics: UsageAnalytics;
}

function formatTokens(n: number): string {
  if (n >= 1000000) return `${(n / 1000000).toFixed(2)}M`;
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return String(n);
}

export function TokenUsageReport({ analytics }: TokenUsageReportProps): JSX.Element {
  const sources = Object.entries(analytics.sourceBreakdown);
  const periodStart = new Date(analytics.period.start).toLocaleDateString();
  const periodEnd = new Date(analytics.period.end).toLocaleDateString();

  return (
    <div data-testid="token-usage-report" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
        <h3 style={{ margin: 0, fontSize: '16px', fontWeight: 600 }}>Token Usage Report</h3>
        <span style={{ fontSize: '12px', color: '#6b7280' }}>
          {periodStart} - {periodEnd}
        </span>
      </div>

      {/* Summary */}
      <div style={{ display: 'flex', gap: '24px', marginBottom: '20px', padding: '12px 16px', backgroundColor: '#f9fafb', borderRadius: '8px' }}>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Total Tokens</div>
          <div style={{ fontSize: '20px', fontWeight: 700 }}>{formatTokens(analytics.totalTokensRetrieved)}</div>
        </div>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Total Queries</div>
          <div style={{ fontSize: '20px', fontWeight: 700 }}>{analytics.totalQueries.toLocaleString()}</div>
        </div>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Tokens/Query</div>
          <div style={{ fontSize: '20px', fontWeight: 700 }}>
            {analytics.totalQueries > 0
              ? formatTokens(Math.round(analytics.totalTokensRetrieved / analytics.totalQueries))
              : '0'}
          </div>
        </div>
      </div>

      {/* Breakdown table */}
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '14px' }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e5e7eb' }}>
            <th style={{ textAlign: 'left', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Source</th>
            <th style={{ textAlign: 'right', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Queries</th>
            <th style={{ textAlign: 'right', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Tokens</th>
            <th style={{ textAlign: 'right', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Avg Latency</th>
            <th style={{ textAlign: 'right', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Cache Hit Rate</th>
          </tr>
        </thead>
        <tbody>
          {sources.map(([source, usage]) => (
            <tr key={source} data-testid="token-usage-row" style={{ borderBottom: '1px solid #f3f4f6' }}>
              <td style={{ padding: '8px', fontWeight: 500 }}>{source}</td>
              <td style={{ padding: '8px', textAlign: 'right' }}>{usage.queries.toLocaleString()}</td>
              <td style={{ padding: '8px', textAlign: 'right' }}>{formatTokens(usage.tokensRetrieved)}</td>
              <td style={{ padding: '8px', textAlign: 'right' }}>{usage.avgLatencyMs.toFixed(0)}ms</td>
              <td style={{ padding: '8px', textAlign: 'right' }}>{(usage.cacheHitRate * 100).toFixed(0)}%</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr style={{ borderTop: '2px solid #e5e7eb', fontWeight: 600 }}>
            <td style={{ padding: '8px' }}>Total</td>
            <td style={{ padding: '8px', textAlign: 'right' }}>{analytics.totalQueries.toLocaleString()}</td>
            <td style={{ padding: '8px', textAlign: 'right' }}>{formatTokens(analytics.totalTokensRetrieved)}</td>
            <td style={{ padding: '8px', textAlign: 'right' }}>{analytics.avgLatencyMs.toFixed(0)}ms</td>
            <td style={{ padding: '8px', textAlign: 'right' }}>-</td>
          </tr>
        </tfoot>
      </table>
    </div>
  );
}
