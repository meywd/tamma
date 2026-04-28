/**
 * Server Log Viewer
 *
 * Displays log entries from an MCP server with level filtering.
 */

import React, { useState, type JSX } from 'react';
import type { MCPServerLog } from '@tamma/shared';

export interface ServerLogViewerProps {
  serverName: string;
  logs: MCPServerLog[];
  onRefresh?: () => void;
}

const LEVEL_COLORS: Record<string, { color: string; bg: string }> = {
  debug: { color: '#6b7280', bg: '#f3f4f6' },
  info: { color: '#1d4ed8', bg: '#dbeafe' },
  warn: { color: '#92400e', bg: '#fef3c7' },
  error: { color: '#991b1b', bg: '#fee2e2' },
};

export function ServerLogViewer({ serverName, logs, onRefresh }: ServerLogViewerProps): JSX.Element {
  const [filter, setFilter] = useState<string>('all');

  const filteredLogs = filter === 'all' ? logs : logs.filter((l) => l.level === filter);

  return (
    <div data-testid="server-log-viewer">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
        <h3 style={{ margin: 0, fontSize: '16px', fontWeight: 600 }}>
          Logs: {serverName}
        </h3>
        <div style={{ display: 'flex', gap: '8px' }}>
          <select
            data-testid="log-level-filter"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            style={{ padding: '4px 8px', border: '1px solid #d1d5db', borderRadius: '4px', fontSize: '13px' }}
          >
            <option value="all">All Levels</option>
            <option value="debug">Debug</option>
            <option value="info">Info</option>
            <option value="warn">Warning</option>
            <option value="error">Error</option>
          </select>
          {onRefresh && (
            <button
              onClick={onRefresh}
              style={{ padding: '4px 12px', border: '1px solid #d1d5db', borderRadius: '4px', backgroundColor: '#fff', cursor: 'pointer', fontSize: '13px' }}
            >
              Refresh
            </button>
          )}
        </div>
      </div>

      <div
        style={{
          border: '1px solid #e5e7eb',
          borderRadius: '8px',
          backgroundColor: '#1f2937',
          padding: '12px',
          maxHeight: '400px',
          overflow: 'auto',
          fontFamily: 'monospace',
          fontSize: '12px',
        }}
      >
        {filteredLogs.length === 0 ? (
          <div style={{ color: '#6b7280', textAlign: 'center', padding: '16px' }}>No logs available</div>
        ) : (
          filteredLogs.map((log, idx) => {
            const levelConfig = LEVEL_COLORS[log.level] ?? LEVEL_COLORS['info']!;
            return (
              <div
                key={idx}
                data-testid="log-entry"
                style={{ marginBottom: '4px', display: 'flex', gap: '8px', lineHeight: '1.6' }}
              >
                <span style={{ color: '#6b7280', flexShrink: 0 }}>
                  {new Date(log.timestamp).toLocaleTimeString()}
                </span>
                <span
                  style={{
                    padding: '0 6px',
                    borderRadius: '3px',
                    backgroundColor: levelConfig.bg,
                    color: levelConfig.color,
                    fontWeight: 600,
                    fontSize: '11px',
                    flexShrink: 0,
                    textTransform: 'uppercase',
                  }}
                >
                  {log.level}
                </span>
                <span style={{ color: '#e5e7eb' }}>{log.message}</span>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
