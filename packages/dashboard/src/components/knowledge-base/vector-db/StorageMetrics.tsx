/**
 * Storage Metrics
 *
 * Visualizes vector database storage usage across collections.
 */

import { type JSX } from 'react';
import type { StorageUsage } from '@tamma/shared';

export interface StorageMetricsProps {
  usage: StorageUsage;
}

function formatBytes(bytes: number): string {
  if (bytes >= 1073741824) return `${(bytes / 1073741824).toFixed(1)} GB`;
  if (bytes >= 1048576) return `${(bytes / 1048576).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

const COLORS = ['#3b82f6', '#8b5cf6', '#f59e0b', '#22c55e', '#ef4444', '#ec4899'];

export function StorageMetrics({ usage }: StorageMetricsProps): JSX.Element {
  const entries = Object.entries(usage.byCollection);

  return (
    <div data-testid="storage-metrics" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>Storage Usage</h3>

      <div style={{ marginBottom: '20px' }}>
        <div style={{ fontSize: '12px', color: '#6b7280', marginBottom: '4px' }}>Total Storage</div>
        <div style={{ fontSize: '28px', fontWeight: 700 }}>{formatBytes(usage.totalBytes)}</div>
      </div>

      {/* Stacked bar */}
      {entries.length > 0 && (
        <div style={{ marginBottom: '16px' }}>
          <div style={{ display: 'flex', height: '20px', borderRadius: '10px', overflow: 'hidden', backgroundColor: '#e5e7eb' }}>
            {entries.map(([name, bytes], idx) => {
              const pct = usage.totalBytes > 0 ? (bytes / usage.totalBytes) * 100 : 0;
              return (
                <div
                  key={name}
                  title={`${name}: ${formatBytes(bytes)}`}
                  style={{
                    width: `${pct}%`,
                    height: '100%',
                    backgroundColor: COLORS[idx % COLORS.length],
                    transition: 'width 0.3s ease',
                  }}
                />
              );
            })}
          </div>
        </div>
      )}

      {/* Legend / breakdown */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
        {entries.map(([name, bytes], idx) => {
          const pct = usage.totalBytes > 0 ? (bytes / usage.totalBytes) * 100 : 0;
          return (
            <div key={name} data-testid="storage-entry" style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
              <span
                style={{
                  width: '10px',
                  height: '10px',
                  borderRadius: '2px',
                  backgroundColor: COLORS[idx % COLORS.length],
                  flexShrink: 0,
                }}
              />
              <span style={{ fontSize: '13px', fontWeight: 500, flex: 1 }}>{name}</span>
              <span style={{ fontSize: '13px', color: '#6b7280' }}>{formatBytes(bytes)}</span>
              <span style={{ fontSize: '12px', color: '#9ca3af', width: '40px', textAlign: 'right' }}>
                {pct.toFixed(0)}%
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
