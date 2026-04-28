/**
 * Quick Status Panel
 *
 * Displays an overview of all subsystem statuses at a glance.
 */

import React, { type JSX } from 'react';
import { StatusCard } from './StatusCard.js';
import type { IndexStatus, MCPServerInfo, RAGMetricsInfo } from '@tamma/shared';

export interface QuickStatusPanelProps {
  indexStatus?: IndexStatus | null;
  vectorCount?: number;
  ragMetrics?: RAGMetricsInfo | null;
  mcpServers?: MCPServerInfo[];
  onNavigate?: (section: string) => void;
}

function formatNumber(n: number): string {
  if (n >= 1000000) return `${(n / 1000000).toFixed(1)}M`;
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return String(n);
}

export function QuickStatusPanel({
  indexStatus,
  vectorCount,
  ragMetrics,
  mcpServers,
  onNavigate,
}: QuickStatusPanelProps): JSX.Element {
  const indexHealthy = indexStatus?.status !== 'error';
  const connectedServers = mcpServers?.filter((s) => s.status === 'connected').length ?? 0;
  const totalServers = mcpServers?.length ?? 0;
  const totalTools = mcpServers?.reduce((sum, s) => sum + s.toolCount, 0) ?? 0;

  return (
    <div data-testid="quick-status-panel">
      <h2 style={{ fontSize: '16px', fontWeight: 600, marginBottom: '12px', color: '#374151' }}>
        Quick Status
      </h2>
      <div style={{ display: 'flex', gap: '16px', flexWrap: 'wrap' }}>
        <StatusCard
          title="Index"
          status={indexStatus ? (indexHealthy ? 'healthy' : 'error') : 'unknown'}
          metric={formatNumber(indexStatus?.chunksCreated ?? 0)}
          metricLabel="chunks"
          onClick={() => onNavigate?.('index')}
        />
        <StatusCard
          title="Vector DB"
          status={vectorCount !== undefined && vectorCount > 0 ? 'healthy' : 'unknown'}
          metric={formatNumber(vectorCount ?? 0)}
          metricLabel="vectors"
          onClick={() => onNavigate?.('vector-db')}
        />
        <StatusCard
          title="RAG"
          status={ragMetrics ? 'healthy' : 'unknown'}
          metric={ragMetrics ? `p95: ${ragMetrics.avgLatencyMs.toFixed(0)}ms` : 'N/A'}
          metricLabel="latency"
          onClick={() => onNavigate?.('rag')}
        />
        <StatusCard
          title="MCP"
          status={
            totalServers === 0
              ? 'unknown'
              : connectedServers === totalServers
                ? 'healthy'
                : connectedServers > 0
                  ? 'warning'
                  : 'error'
          }
          metric={`${connectedServers}/${totalServers}`}
          metricLabel={`${totalTools} tools`}
          onClick={() => onNavigate?.('mcp')}
        />
      </div>
    </div>
  );
}
