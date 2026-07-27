/**
 * RAG Test Interface
 *
 * Interactive component for testing RAG pipeline queries.
 */

import { useState, type JSX } from 'react';
import type { RAGTestResult } from '@tamma/shared';

export interface RAGTestInterfaceProps {
  onTest: (query: string, options?: { sources?: string[]; maxTokens?: number; topK?: number }) => Promise<void>;
  result: RAGTestResult | null;
  loading?: boolean;
}

export function RAGTestInterface({ onTest, result, loading }: RAGTestInterfaceProps): JSX.Element {
  const [query, setQuery] = useState('');
  const [topK, setTopK] = useState(10);

  const handleTest = () => {
    if (query.trim()) {
      void onTest(query, { topK });
    }
  };

  return (
    <div data-testid="rag-test-interface">
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>Test RAG Query</h3>

      <div style={{ display: 'flex', gap: '8px', marginBottom: '16px' }}>
        <input
          data-testid="rag-query-input"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Enter a test query..."
          onKeyDown={(e) => { if (e.key === 'Enter') handleTest(); }}
          style={{ flex: 1, padding: '8px 12px', border: '1px solid #d1d5db', borderRadius: '6px', fontSize: '14px' }}
        />
        <input
          type="number"
          value={topK}
          onChange={(e) => setTopK(parseInt(e.target.value, 10))}
          min={1}
          max={50}
          style={{ width: '60px', padding: '8px', border: '1px solid #d1d5db', borderRadius: '6px' }}
        />
        <button
          data-testid="rag-test-button"
          onClick={handleTest}
          disabled={loading}
          style={{
            padding: '8px 16px',
            border: 'none',
            borderRadius: '6px',
            backgroundColor: '#3b82f6',
            color: '#fff',
            cursor: loading ? 'not-allowed' : 'pointer',
            opacity: loading ? 0.6 : 1,
          }}
        >
          {loading ? 'Testing...' : 'Test'}
        </button>
      </div>

      {result && (
        <div data-testid="rag-test-result">
          <div style={{ display: 'flex', gap: '16px', marginBottom: '16px', fontSize: '13px', color: '#6b7280' }}>
            <span>{result.chunks.length} chunks</span>
            <span>{result.tokenCount} tokens</span>
            <span>{result.latencyMs}ms</span>
          </div>

          {/* Source Attribution */}
          <div style={{ marginBottom: '16px' }}>
            {result.sources.map((src) => (
              <div key={src.source} style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '4px' }}>
                <span style={{ fontSize: '13px', fontWeight: 500, width: '80px' }}>{src.source}</span>
                <div style={{ flex: 1, height: '8px', backgroundColor: '#e5e7eb', borderRadius: '4px', overflow: 'hidden' }}>
                  <div
                    style={{
                      width: `${(src.tokensUsed / result.tokenCount) * 100}%`,
                      height: '100%',
                      backgroundColor: '#3b82f6',
                      borderRadius: '4px',
                    }}
                  />
                </div>
                <span style={{ fontSize: '12px', color: '#6b7280' }}>{src.count} chunks</span>
              </div>
            ))}
          </div>

          {/* Chunks */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
            {result.chunks.map((chunk, idx) => (
              <div
                key={chunk.id}
                style={{
                  border: '1px solid #e5e7eb',
                  borderRadius: '8px',
                  padding: '12px',
                  backgroundColor: '#fff',
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '8px' }}>
                  <span style={{ fontSize: '13px', color: '#6b7280' }}>
                    #{idx + 1} | {chunk.metadata.filePath ?? chunk.source}
                  </span>
                  <span style={{ fontSize: '12px', fontWeight: 600, color: '#3b82f6' }}>
                    {chunk.score.toFixed(3)}
                  </span>
                </div>
                <pre style={{
                  margin: 0,
                  padding: '8px',
                  backgroundColor: '#f9fafb',
                  borderRadius: '4px',
                  fontSize: '12px',
                  overflow: 'auto',
                  maxHeight: '100px',
                }}>
                  {chunk.content}
                </pre>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
