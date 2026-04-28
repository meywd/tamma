/**
 * Tool Invoke Panel
 *
 * Panel for invoking an MCP tool with arguments and viewing results.
 */

import React, { useState, type JSX } from 'react';
import type { MCPTool, MCPToolInvokeResult } from '@tamma/shared';

export interface ToolInvokePanelProps {
  tool: MCPTool;
  onInvoke: (serverName: string, toolName: string, args: Record<string, unknown>) => Promise<void>;
  result: MCPToolInvokeResult | null;
  loading?: boolean;
}

export function ToolInvokePanel({ tool, onInvoke, result, loading }: ToolInvokePanelProps): JSX.Element {
  const [argsJson, setArgsJson] = useState('{}');
  const [parseError, setParseError] = useState<string | null>(null);

  const handleInvoke = () => {
    try {
      const args = JSON.parse(argsJson) as Record<string, unknown>;
      setParseError(null);
      void onInvoke(tool.serverName, tool.name, args);
    } catch {
      setParseError('Invalid JSON');
    }
  };

  return (
    <div data-testid="tool-invoke-panel" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '16px', backgroundColor: '#fff' }}>
      <h4 style={{ margin: '0 0 12px 0', fontSize: '15px', fontWeight: 600 }}>
        Invoke: {tool.name}
      </h4>
      <div style={{ fontSize: '12px', color: '#6b7280', marginBottom: '12px' }}>
        {tool.description} (Server: {tool.serverName})
      </div>

      {/* Schema preview */}
      {tool.inputSchema && (
        <div style={{ marginBottom: '12px' }}>
          <div style={{ fontSize: '12px', fontWeight: 500, color: '#374151', marginBottom: '4px' }}>Input Schema</div>
          <pre style={{
            padding: '8px',
            backgroundColor: '#f9fafb',
            borderRadius: '4px',
            fontSize: '11px',
            overflow: 'auto',
            maxHeight: '80px',
            margin: 0,
          }}>
            {JSON.stringify(tool.inputSchema, null, 2)}
          </pre>
        </div>
      )}

      {/* Arguments input */}
      <div style={{ marginBottom: '12px' }}>
        <div style={{ fontSize: '12px', fontWeight: 500, color: '#374151', marginBottom: '4px' }}>Arguments (JSON)</div>
        <textarea
          data-testid="tool-args-input"
          value={argsJson}
          onChange={(e) => setArgsJson(e.target.value)}
          rows={4}
          style={{
            width: '100%',
            padding: '8px',
            border: '1px solid #d1d5db',
            borderRadius: '4px',
            fontSize: '13px',
            fontFamily: 'monospace',
            resize: 'vertical',
          }}
        />
        {parseError && (
          <div style={{ color: '#ef4444', fontSize: '12px', marginTop: '4px' }}>{parseError}</div>
        )}
      </div>

      <button
        data-testid="execute-invoke"
        onClick={handleInvoke}
        disabled={loading}
        style={{
          padding: '8px 16px',
          border: 'none',
          borderRadius: '6px',
          backgroundColor: '#3b82f6',
          color: '#fff',
          cursor: loading ? 'not-allowed' : 'pointer',
          opacity: loading ? 0.6 : 1,
          fontSize: '14px',
          marginBottom: '12px',
        }}
      >
        {loading ? 'Invoking...' : 'Invoke Tool'}
      </button>

      {/* Result */}
      {result && (
        <div data-testid="invoke-result" style={{ marginTop: '12px' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '8px' }}>
            <span style={{
              padding: '2px 8px',
              borderRadius: '10px',
              fontSize: '12px',
              fontWeight: 600,
              backgroundColor: result.success ? '#dcfce7' : '#fee2e2',
              color: result.success ? '#166534' : '#991b1b',
            }}>
              {result.success ? 'Success' : 'Failed'}
            </span>
            <span style={{ fontSize: '12px', color: '#6b7280' }}>{result.durationMs}ms</span>
          </div>
          {result.error && (
            <div style={{ padding: '8px', backgroundColor: '#fef2f2', borderRadius: '4px', fontSize: '12px', color: '#991b1b', marginBottom: '8px' }}>
              {result.error}
            </div>
          )}
          <pre style={{
            padding: '8px',
            backgroundColor: '#f9fafb',
            borderRadius: '4px',
            fontSize: '12px',
            overflow: 'auto',
            maxHeight: '200px',
            margin: 0,
          }}>
            {JSON.stringify(result.content, null, 2)}
          </pre>
        </div>
      )}
    </div>
  );
}
