/**
 * Tool List
 *
 * Displays MCP tools with search/filter and invocation triggers.
 */

import React, { useState, useMemo, type JSX } from 'react';
import type { MCPTool } from '@tamma/shared';

export interface ToolListProps {
  tools: MCPTool[];
  onInvoke?: (tool: MCPTool) => void;
}

export function ToolList({ tools, onInvoke }: ToolListProps): JSX.Element {
  const [searchQuery, setSearchQuery] = useState('');
  const [serverFilter, setServerFilter] = useState<string>('all');

  const servers = useMemo(() => {
    const set = new Set(tools.map((t) => t.serverName));
    return Array.from(set);
  }, [tools]);

  const filteredTools = useMemo(() => {
    return tools.filter((tool) => {
      const matchesSearch = !searchQuery ||
        tool.name.toLowerCase().includes(searchQuery.toLowerCase()) ||
        tool.description.toLowerCase().includes(searchQuery.toLowerCase());
      const matchesServer = serverFilter === 'all' || tool.serverName === serverFilter;
      return matchesSearch && matchesServer;
    });
  }, [tools, searchQuery, serverFilter]);

  return (
    <div data-testid="tool-list">
      <h3 style={{ margin: '0 0 12px 0', fontSize: '16px', fontWeight: 600 }}>Tools</h3>

      {/* Filters */}
      <div style={{ display: 'flex', gap: '8px', marginBottom: '12px' }}>
        <input
          data-testid="tool-search"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          placeholder="Search tools..."
          style={{
            flex: 1,
            padding: '6px 10px',
            border: '1px solid #d1d5db',
            borderRadius: '4px',
            fontSize: '13px',
          }}
        />
        <select
          data-testid="server-filter"
          value={serverFilter}
          onChange={(e) => setServerFilter(e.target.value)}
          style={{ padding: '6px 10px', border: '1px solid #d1d5db', borderRadius: '4px', fontSize: '13px' }}
        >
          <option value="all">All Servers</option>
          {servers.map((s) => (
            <option key={s} value={s}>{s}</option>
          ))}
        </select>
      </div>

      {/* Tool list */}
      {filteredTools.length === 0 ? (
        <div style={{ padding: '16px', textAlign: 'center', color: '#6b7280', fontSize: '13px' }}>
          No tools found
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
          {filteredTools.map((tool) => (
            <div
              key={`${tool.serverName}-${tool.name}`}
              data-testid="tool-item"
              style={{
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                padding: '10px 12px',
                backgroundColor: '#f9fafb',
                borderRadius: '6px',
                border: '1px solid #e5e7eb',
              }}
            >
              <div>
                <div style={{ fontWeight: 600, fontSize: '14px' }}>{tool.name}</div>
                <div style={{ fontSize: '12px', color: '#6b7280', marginTop: '2px' }}>{tool.description}</div>
                <div style={{ fontSize: '11px', color: '#9ca3af', marginTop: '2px' }}>Server: {tool.serverName}</div>
              </div>
              {onInvoke && (
                <button
                  data-testid="invoke-tool-btn"
                  onClick={() => onInvoke(tool)}
                  style={{
                    padding: '4px 10px',
                    border: '1px solid #3b82f6',
                    borderRadius: '4px',
                    backgroundColor: 'transparent',
                    color: '#3b82f6',
                    cursor: 'pointer',
                    fontSize: '12px',
                    flexShrink: 0,
                  }}
                >
                  Invoke
                </button>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
