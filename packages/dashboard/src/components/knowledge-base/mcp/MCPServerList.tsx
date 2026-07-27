/**
 * MCP Server List
 *
 * Grid of MCP server cards.
 */

import { type JSX } from 'react';
import type { MCPServerInfo } from '@tamma/shared';
import { MCPServerCard } from './MCPServerCard.js';

export interface MCPServerListProps {
  servers: MCPServerInfo[];
  onStart: (name: string) => void;
  onStop: (name: string) => void;
  onRestart: (name: string) => void;
  onViewTools: (name: string) => void;
  onViewLogs: (name: string) => void;
}

export function MCPServerList({
  servers,
  onStart,
  onStop,
  onRestart,
  onViewTools,
  onViewLogs,
}: MCPServerListProps): JSX.Element {
  if (servers.length === 0) {
    return (
      <div data-testid="mcp-server-list-empty" style={{ padding: '24px', textAlign: 'center', color: '#6b7280' }}>
        No MCP servers configured
      </div>
    );
  }

  return (
    <div data-testid="mcp-server-list" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))', gap: '16px' }}>
      {servers.map((server) => (
        <MCPServerCard
          key={server.name}
          server={server}
          onStart={() => onStart(server.name)}
          onStop={() => onStop(server.name)}
          onRestart={() => onRestart(server.name)}
          onViewTools={() => onViewTools(server.name)}
          onViewLogs={() => onViewLogs(server.name)}
        />
      ))}
    </div>
  );
}
