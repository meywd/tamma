/**
 * Status Card Component
 *
 * A compact card showing the health status of a subsystem.
 */

import { type JSX } from 'react';

export interface StatusCardProps {
  title: string;
  status: 'healthy' | 'warning' | 'error' | 'unknown';
  metric: string;
  metricLabel: string;
  onClick?: () => void;
}

const STATUS_STYLES: Record<string, { bg: string; text: string; dot: string }> = {
  healthy: { bg: '#f0fdf4', text: '#166534', dot: '#22c55e' },
  warning: { bg: '#fffbeb', text: '#92400e', dot: '#f59e0b' },
  error: { bg: '#fef2f2', text: '#991b1b', dot: '#ef4444' },
  unknown: { bg: '#f9fafb', text: '#6b7280', dot: '#9ca3af' },
};

export function StatusCard({ title, status, metric, metricLabel, onClick }: StatusCardProps): JSX.Element {
  const styles = STATUS_STYLES[status] ?? STATUS_STYLES['unknown']!;

  return (
    <div
      data-testid="status-card"
      onClick={onClick}
      style={{
        backgroundColor: styles.bg,
        border: '1px solid #e5e7eb',
        borderRadius: '8px',
        padding: '16px',
        cursor: onClick ? 'pointer' : 'default',
        minWidth: '150px',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '8px' }}>
        <span
          style={{
            width: '8px',
            height: '8px',
            borderRadius: '50%',
            backgroundColor: styles.dot,
            display: 'inline-block',
          }}
        />
        <span style={{ fontWeight: 600, fontSize: '14px', color: styles.text }}>{title}</span>
      </div>
      <div style={{ fontSize: '24px', fontWeight: 700, color: '#111827' }}>{metric}</div>
      <div style={{ fontSize: '12px', color: '#6b7280', marginTop: '2px' }}>{metricLabel}</div>
    </div>
  );
}
