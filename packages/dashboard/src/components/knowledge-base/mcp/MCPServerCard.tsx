/**
 * MCP Server Card
 *
 * Card showing an MCP server's status with lifecycle controls.
 */

import { type JSX } from 'react';
import type { MCPServerInfo } from '@tamma/shared';

export interface MCPServerCardProps {
  server: MCPServerInfo;
  onStart: () => void;
  onStop: () => void;
  onRestart: () => void;
  onViewTools: () => void;
  onViewLogs: () => void;
}

const STATUS_CONFIG: Record<string, { color: string; bg: string; label: string }> = {
  connected: { color: '#166534', bg: '#dcfce7', label: 'Connected' },
  disconnected: { color: '#6b7280', bg: '#f3f4f6', label: 'Disconnected' },
  error: { color: '#991b1b', bg: '#fee2e2', label: 'Error' },
  starting: { color: '#92400e', bg: '#fef3c7', label: 'Starting...' },
};

export function MCPServerCard({
  server,
  onStart,
  onStop,
  onRestart,
  onViewTools,
  onViewLogs,
}: MCPServerCardProps): JSX.Element {
  const statusInfo = STATUS_CONFIG[server.status] ?? STATUS_CONFIG['disconnected']!;
  const isRunning = server.status === 'connected';

  return (
    <div
      data-testid="mcp-server-card"
      style={{
        border: '1px solid #e5e7eb',
        borderRadius: '8px',
        padding: '16px',
        backgroundColor: '#ffffff',
      }}
    >
      {/* Header */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
        <div>
          <div style={{ fontWeight: 600, fontSize: '15px' }}>{server.name}</div>
          <div style={{ fontSize: '12px', color: '#6b7280', marginTop: '2px' }}>
            {server.transport.toUpperCase()} transport
          </div>
        </div>
        <span
          data-testid="server-status"
          style={{
            padding: '3px 10px',
            borderRadius: '10px',
            fontSize: '12px',
            fontWeight: 500,
            backgroundColor: statusInfo.bg,
            color: statusInfo.color,
          }}
        >
          {statusInfo.label}
        </span>
      </div>

      {/* Metrics */}
      <div style={{ display: 'flex', gap: '16px', marginBottom: '12px', fontSize: '13px' }}>
        <div>
          <span style={{ color: '#6b7280' }}>Tools: </span>
          <span style={{ fontWeight: 600 }}>{server.toolCount}</span>
        </div>
        <div>
          <span style={{ color: '#6b7280' }}>Resources: </span>
          <span style={{ fontWeight: 600 }}>{server.resourceCount}</span>
        </div>
      </div>

      {server.error && (
        <div style={{ padding: '8px', backgroundColor: '#fef2f2', borderRadius: '4px', fontSize: '12px', color: '#991b1b', marginBottom: '12px' }}>
          {server.error}
        </div>
      )}

      {server.lastConnected && (
        <div style={{ fontSize: '12px', color: '#9ca3af', marginBottom: '12px' }}>
          Last connected: {new Date(server.lastConnected).toLocaleString()}
        </div>
      )}

      {/* Actions */}
      <div style={{ display: 'flex', gap: '6px', flexWrap: 'wrap' }}>
        {isRunning ? (
          <>
            <button
              data-testid="stop-button"
              onClick={onStop}
              style={{ padding: '5px 12px', border: '1px solid #dc2626', borderRadius: '4px', backgroundColor: 'transparent', color: '#dc2626', cursor: 'pointer', fontSize: '12px' }}
            >
              Stop
            </button>
            <button
              data-testid="restart-button"
              onClick={onRestart}
              style={{ padding: '5px 12px', border: '1px solid #f59e0b', borderRadius: '4px', backgroundColor: 'transparent', color: '#f59e0b', cursor: 'pointer', fontSize: '12px' }}
            >
              Restart
            </button>
          </>
        ) : (
          <button
            data-testid="start-button"
            onClick={onStart}
            style={{ padding: '5px 12px', border: 'none', borderRadius: '4px', backgroundColor: '#22c55e', color: '#fff', cursor: 'pointer', fontSize: '12px' }}
          >
            Start
          </button>
        )}
        <button
          data-testid="tools-button"
          onClick={onViewTools}
          style={{ padding: '5px 12px', border: '1px solid #d1d5db', borderRadius: '4px', backgroundColor: 'transparent', color: '#374151', cursor: 'pointer', fontSize: '12px' }}
        >
          Tools
        </button>
        <button
          data-testid="logs-button"
          onClick={onViewLogs}
          style={{ padding: '5px 12px', border: '1px solid #d1d5db', borderRadius: '4px', backgroundColor: 'transparent', color: '#374151', cursor: 'pointer', fontSize: '12px' }}
        >
          Logs
        </button>
      </div>
    </div>
  );
}
