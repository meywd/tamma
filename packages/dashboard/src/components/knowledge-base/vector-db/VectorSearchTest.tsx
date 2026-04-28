/**
 * Vector Search Test
 *
 * Interactive component for testing vector similarity searches.
 */

import React, { useState, type JSX } from 'react';
import type { VectorSearchResult } from '@tamma/shared';

export interface VectorSearchTestProps {
  collections: string[];
  onSearch: (query: string, collection: string, topK: number) => Promise<void>;
  results: VectorSearchResult[];
  loading?: boolean;
}

export function VectorSearchTest({ collections, onSearch, results, loading }: VectorSearchTestProps): JSX.Element {
  const [query, setQuery] = useState('');
  const [collection, setCollection] = useState(collections[0] ?? '');
  const [topK, setTopK] = useState(10);

  const handleSearch = () => {
    if (query.trim() && collection) {
      void onSearch(query, collection, topK);
    }
  };

  return (
    <div data-testid="vector-search-test">
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>Similarity Search</h3>

      <div style={{ display: 'flex', gap: '8px', marginBottom: '16px', flexWrap: 'wrap' }}>
        <input
          data-testid="search-query"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Enter search query..."
          onKeyDown={(e) => { if (e.key === 'Enter') handleSearch(); }}
          style={{ flex: 1, minWidth: '200px', padding: '8px 12px', border: '1px solid #d1d5db', borderRadius: '6px', fontSize: '14px' }}
        />
        <select
          data-testid="search-collection"
          value={collection}
          onChange={(e) => setCollection(e.target.value)}
          style={{ padding: '8px 12px', border: '1px solid #d1d5db', borderRadius: '6px', fontSize: '14px' }}
        >
          {collections.map((c) => (
            <option key={c} value={c}>{c}</option>
          ))}
        </select>
        <input
          type="number"
          value={topK}
          onChange={(e) => setTopK(parseInt(e.target.value, 10))}
          min={1}
          max={100}
          style={{ width: '60px', padding: '8px', border: '1px solid #d1d5db', borderRadius: '6px', fontSize: '14px' }}
        />
        <button
          data-testid="search-button"
          onClick={handleSearch}
          disabled={loading}
          style={{
            padding: '8px 16px',
            border: 'none',
            borderRadius: '6px',
            backgroundColor: '#3b82f6',
            color: '#ffffff',
            cursor: loading ? 'not-allowed' : 'pointer',
            opacity: loading ? 0.6 : 1,
            fontSize: '14px',
          }}
        >
          {loading ? 'Searching...' : 'Search'}
        </button>
      </div>

      {results.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
          {results.map((result, idx) => (
            <div
              key={result.id}
              data-testid="search-result"
              style={{
                border: '1px solid #e5e7eb',
                borderRadius: '8px',
                padding: '12px',
                backgroundColor: '#ffffff',
              }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '8px' }}>
                <span style={{ fontSize: '13px', color: '#6b7280' }}>
                  #{idx + 1} | {(result.metadata as Record<string, unknown>)['filePath'] as string ?? 'unknown'}
                </span>
                <span
                  data-testid="score-badge"
                  style={{
                    padding: '2px 8px',
                    borderRadius: '10px',
                    backgroundColor: result.score > 0.8 ? '#dcfce7' : result.score > 0.6 ? '#fef9c3' : '#fee2e2',
                    color: result.score > 0.8 ? '#166534' : result.score > 0.6 ? '#854d0e' : '#991b1b',
                    fontSize: '12px',
                    fontWeight: 600,
                  }}
                >
                  {result.score.toFixed(3)}
                </span>
              </div>
              <pre style={{
                margin: 0,
                padding: '8px',
                backgroundColor: '#f9fafb',
                borderRadius: '4px',
                fontSize: '12px',
                overflow: 'auto',
                maxHeight: '120px',
              }}>
                {result.content}
              </pre>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
