/**
 * RAG Metrics Chart
 *
 * Displays RAG pipeline metrics with visual breakdown of sources.
 */

import React, { type JSX } from 'react';
import type { RAGMetricsInfo } from '@tamma/shared';

export interface RAGMetricsChartProps {
  metrics: RAGMetricsInfo;
}

const SOURCE_COLORS: Record<string, string> = {
  vector_db: '#3b82f6',
  keyword: '#8b5cf6',
  docs: '#f59e0b',
  issues: '#22c55e',
};

export function RAGMetricsChart({ metrics }: RAGMetricsChartProps): JSX.Element {
  const sourceEntries = Object.entries(metrics.sourceBreakdown);

  return (
    <div data-testid="rag-metrics-chart" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>RAG Pipeline Metrics</h3>

      {/* Summary metrics */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: '12px', marginBottom: '20px' }}>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Total Queries</div>
          <div style={{ fontSize: '20px', fontWeight: 700 }}>{metrics.totalQueries.toLocaleString()}</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Avg Latency</div>
          <div style={{ fontSize: '20px', fontWeight: 700 }}>{metrics.avgLatencyMs.toFixed(0)}ms</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Cache Hit Rate</div>
          <div style={{ fontSize: '20px', fontWeight: 700 }}>{(metrics.cacheHitRate * 100).toFixed(0)}%</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Avg Tokens</div>
          <div style={{ fontSize: '20px', fontWeight: 700 }}>{metrics.avgTokensRetrieved.toLocaleString()}</div>
        </div>
      </div>

      {/* Source distribution */}
      <h4 style={{ margin: '0 0 12px 0', fontSize: '14px', fontWeight: 600, color: '#374151' }}>Source Distribution</h4>

      {/* Horizontal stacked bar */}
      <div style={{ display: 'flex', height: '24px', borderRadius: '12px', overflow: 'hidden', marginBottom: '12px' }}>
        {sourceEntries.map(([source, pct]) => (
          <div
            key={source}
            title={`${source}: ${(pct * 100).toFixed(0)}%`}
            style={{
              width: `${pct * 100}%`,
              height: '100%',
              backgroundColor: SOURCE_COLORS[source] ?? '#6b7280',
              transition: 'width 0.3s ease',
            }}
          />
        ))}
      </div>

      {/* Legend */}
      <div style={{ display: 'flex', gap: '16px', flexWrap: 'wrap' }}>
        {sourceEntries.map(([source, pct]) => (
          <div key={source} style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
            <span
              style={{
                width: '10px',
                height: '10px',
                borderRadius: '2px',
                backgroundColor: SOURCE_COLORS[source] ?? '#6b7280',
              }}
            />
            <span style={{ fontSize: '13px' }}>{source}</span>
            <span style={{ fontSize: '12px', color: '#6b7280' }}>({(pct * 100).toFixed(0)}%)</span>
          </div>
        ))}
      </div>
    </div>
  );
}
