/**
 * Collection Stats
 *
 * Detailed statistics view for a vector collection.
 */

import { type JSX } from 'react';
import type { CollectionStatsInfo } from '@tamma/shared';

export interface CollectionStatsProps {
  stats: CollectionStatsInfo;
}

function formatBytes(bytes: number): string {
  if (bytes >= 1073741824) return `${(bytes / 1073741824).toFixed(1)} GB`;
  if (bytes >= 1048576) return `${(bytes / 1048576).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

export function CollectionStats({ stats }: CollectionStatsProps): JSX.Element {
  return (
    <div data-testid="collection-stats" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>
        Collection: {stats.name}
      </h3>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: '16px', marginBottom: '20px' }}>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Vectors</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{stats.vectorCount.toLocaleString()}</div>
        </div>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Dimensions</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{stats.dimensions}</div>
        </div>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Storage</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>{formatBytes(stats.storageBytes)}</div>
        </div>
      </div>

      <h4 style={{ margin: '0 0 12px 0', fontSize: '14px', fontWeight: 600, color: '#374151' }}>Query Performance</h4>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gap: '12px' }}>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Total Queries</div>
          <div style={{ fontSize: '18px', fontWeight: 600 }}>{stats.queryMetrics.totalQueries}</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Avg Latency</div>
          <div style={{ fontSize: '18px', fontWeight: 600 }}>{stats.queryMetrics.avgLatencyMs.toFixed(1)}ms</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>p95 Latency</div>
          <div style={{ fontSize: '18px', fontWeight: 600 }}>{stats.queryMetrics.p95LatencyMs.toFixed(1)}ms</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Queries/min</div>
          <div style={{ fontSize: '18px', fontWeight: 600 }}>{stats.queryMetrics.queriesPerMinute.toFixed(1)}</div>
        </div>
      </div>
    </div>
  );
}
