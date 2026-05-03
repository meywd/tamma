/**
 * Index Status Card
 *
 * Displays current indexing status with progress indicator
 * and trigger/cancel controls.
 */

import React, { type JSX } from 'react';
import type { IndexStatus } from '@tamma/shared';

export interface IndexStatusCardProps {
  status: IndexStatus;
  onTriggerIndex: () => void;
  onCancelIndex?: () => void;
}

export function IndexStatusCard({
  status,
  onTriggerIndex,
  onCancelIndex,
}: IndexStatusCardProps): JSX.Element {
  const statusColors: Record<string, string> = {
    idle: '#22c55e',
    indexing: '#3b82f6',
    error: '#ef4444',
  };

  return (
    <div
      data-testid="index-status-card"
      style={{
        border: '1px solid #e5e7eb',
        borderRadius: '8px',
        padding: '24px',
        backgroundColor: '#ffffff',
      }}
    >
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
        <h3 style={{ margin: 0, fontSize: '16px', fontWeight: 600 }}>Index Status</h3>
        <span
          data-testid="status-badge"
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '6px',
            padding: '4px 12px',
            borderRadius: '12px',
            fontSize: '13px',
            fontWeight: 500,
            backgroundColor: `${statusColors[status.status] ?? '#6b7280'}20`,
            color: statusColors[status.status] ?? '#6b7280',
          }}
        >
          <span
            style={{
              width: '6px',
              height: '6px',
              borderRadius: '50%',
              backgroundColor: statusColors[status.status] ?? '#6b7280',
            }}
          />
          {status.status.charAt(0).toUpperCase() + status.status.slice(1)}
        </span>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px', marginBottom: '16px' }}>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Files Indexed</div>
          <div style={{ fontSize: '20px', fontWeight: 600 }}>{(status.filesIndexed ?? 0).toLocaleString()}</div>
        </div>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Chunks Created</div>
          <div style={{ fontSize: '20px', fontWeight: 600 }}>{(status.chunksCreated ?? 0).toLocaleString()}</div>
        </div>
        <div>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Last Run</div>
          <div style={{ fontSize: '14px' }}>
            {status.lastRun ? new Date(status.lastRun).toLocaleString() : 'Never'}
          </div>
        </div>
        {status.currentFile && (
          <div>
            <div style={{ fontSize: '12px', color: '#6b7280' }}>Current File</div>
            <div style={{ fontSize: '14px', fontFamily: 'monospace', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {status.currentFile}
            </div>
          </div>
        )}
      </div>

      {/* Progress bar */}
      {status.status === 'indexing' && status.progress !== undefined && (
        <div style={{ marginBottom: '16px' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '4px' }}>
            <span style={{ fontSize: '12px', color: '#6b7280' }}>Progress</span>
            <span style={{ fontSize: '12px', color: '#6b7280' }}>{status.progress}%</span>
          </div>
          <div
            role="progressbar"
            aria-valuenow={status.progress}
            aria-valuemin={0}
            aria-valuemax={100}
            style={{
              width: '100%',
              height: '8px',
              backgroundColor: '#e5e7eb',
              borderRadius: '4px',
              overflow: 'hidden',
            }}
          >
            <div
              style={{
                width: `${status.progress}%`,
                height: '100%',
                backgroundColor: '#3b82f6',
                borderRadius: '4px',
                transition: 'width 0.3s ease',
              }}
            />
          </div>
        </div>
      )}

      {/* Error message */}
      {status.error && (
        <div
          data-testid="error-message"
          style={{
            padding: '8px 12px',
            backgroundColor: '#fef2f2',
            color: '#991b1b',
            borderRadius: '6px',
            fontSize: '13px',
            marginBottom: '16px',
          }}
        >
          {status.error}
        </div>
      )}

      {/* Actions */}
      <div style={{ display: 'flex', gap: '8px' }}>
        {status.status === 'indexing' ? (
          <button
            data-testid="cancel-button"
            onClick={onCancelIndex}
            style={{
              padding: '8px 16px',
              border: '1px solid #dc2626',
              borderRadius: '6px',
              backgroundColor: '#ffffff',
              color: '#dc2626',
              cursor: 'pointer',
              fontSize: '14px',
            }}
          >
            Cancel
          </button>
        ) : (
          <button
            data-testid="reindex-button"
            onClick={onTriggerIndex}
            style={{
              padding: '8px 16px',
              border: 'none',
              borderRadius: '6px',
              backgroundColor: '#3b82f6',
              color: '#ffffff',
              cursor: 'pointer',
              fontSize: '14px',
              fontWeight: 500,
            }}
          >
            Re-index
          </button>
        )}
      </div>
    </div>
  );
}
