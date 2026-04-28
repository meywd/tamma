/**
 * Context Test Interface
 *
 * Main interface for interactive context retrieval testing.
 * Combines query input, source selection, and results display.
 */

import React, { useState, type JSX } from 'react';
import type { ContextTestResult, UIContextSource, UITaskType } from '@tamma/shared';
import { ContextViewer } from './ContextViewer.js';

export interface ContextTestInterfaceProps {
  onTest: (
    query: string,
    taskType: UITaskType,
    maxTokens: number,
    sources?: UIContextSource[],
    options?: { deduplicate?: boolean; includeMetadata?: boolean },
  ) => Promise<void>;
  onFeedback?: (requestId: string, chunkId: string, rating: 'relevant' | 'irrelevant' | 'partially_relevant') => void;
  result: ContextTestResult | null;
  loading?: boolean;
}

const TASK_TYPES: UITaskType[] = ['analysis', 'planning', 'implementation', 'review', 'testing', 'documentation'];
const SOURCE_OPTIONS: UIContextSource[] = ['vector_db', 'rag', 'mcp', 'web_search'];

export function ContextTestInterface({ onTest, onFeedback, result, loading }: ContextTestInterfaceProps): JSX.Element {
  const [query, setQuery] = useState('');
  const [taskType, setTaskType] = useState<UITaskType>('implementation');
  const [maxTokens, setMaxTokens] = useState(4000);
  const [selectedSources, setSelectedSources] = useState<Set<UIContextSource>>(new Set(['vector_db', 'rag']));
  const [deduplicate, setDeduplicate] = useState(true);

  const handleTest = () => {
    if (query.trim()) {
      void onTest(
        query,
        taskType,
        maxTokens,
        Array.from(selectedSources),
        { deduplicate, includeMetadata: true },
      );
    }
  };

  const toggleSource = (source: UIContextSource) => {
    setSelectedSources((prev) => {
      const next = new Set(prev);
      if (next.has(source)) {
        next.delete(source);
      } else {
        next.add(source);
      }
      return next;
    });
  };

  return (
    <div data-testid="context-test-interface">
      <h3 style={{ margin: '0 0 16px 0', fontSize: '18px', fontWeight: 600 }}>Context Test</h3>

      {/* Query input */}
      <div style={{ marginBottom: '16px' }}>
        <div style={{ display: 'flex', gap: '8px' }}>
          <input
            data-testid="query-input"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="How does authentication work in this codebase?"
            onKeyDown={(e) => { if (e.key === 'Enter') handleTest(); }}
            style={{
              flex: 1,
              padding: '10px 14px',
              border: '1px solid #d1d5db',
              borderRadius: '6px',
              fontSize: '14px',
            }}
          />
          <button
            data-testid="test-button"
            onClick={handleTest}
            disabled={loading || !query.trim()}
            style={{
              padding: '10px 20px',
              border: 'none',
              borderRadius: '6px',
              backgroundColor: '#3b82f6',
              color: '#fff',
              cursor: loading || !query.trim() ? 'not-allowed' : 'pointer',
              opacity: loading || !query.trim() ? 0.6 : 1,
              fontSize: '14px',
              fontWeight: 500,
            }}
          >
            {loading ? 'Testing...' : 'Test Query'}
          </button>
        </div>
      </div>

      {/* Options */}
      <div style={{ display: 'flex', gap: '24px', flexWrap: 'wrap', marginBottom: '20px', padding: '12px 16px', backgroundColor: '#f9fafb', borderRadius: '8px' }}>
        {/* Sources */}
        <div>
          <div style={{ fontSize: '12px', fontWeight: 500, color: '#6b7280', marginBottom: '6px' }}>Sources</div>
          <div style={{ display: 'flex', gap: '8px' }}>
            {SOURCE_OPTIONS.map((source) => (
              <label
                key={source}
                data-testid={`source-${source}`}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: '4px',
                  fontSize: '13px',
                  cursor: 'pointer',
                }}
              >
                <input
                  type="checkbox"
                  checked={selectedSources.has(source)}
                  onChange={() => toggleSource(source)}
                />
                {source}
              </label>
            ))}
          </div>
        </div>

        {/* Task type */}
        <div>
          <div style={{ fontSize: '12px', fontWeight: 500, color: '#6b7280', marginBottom: '6px' }}>Task Type</div>
          <select
            value={taskType}
            onChange={(e) => setTaskType(e.target.value as UITaskType)}
            style={{ padding: '4px 8px', border: '1px solid #d1d5db', borderRadius: '4px', fontSize: '13px' }}
          >
            {TASK_TYPES.map((t) => (
              <option key={t} value={t}>{t}</option>
            ))}
          </select>
        </div>

        {/* Max tokens */}
        <div>
          <div style={{ fontSize: '12px', fontWeight: 500, color: '#6b7280', marginBottom: '6px' }}>Max Tokens</div>
          <input
            type="number"
            value={maxTokens}
            onChange={(e) => setMaxTokens(parseInt(e.target.value, 10))}
            style={{ width: '80px', padding: '4px 8px', border: '1px solid #d1d5db', borderRadius: '4px', fontSize: '13px' }}
          />
        </div>

        {/* Deduplicate */}
        <div style={{ display: 'flex', alignItems: 'flex-end' }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: '4px', fontSize: '13px', cursor: 'pointer' }}>
            <input
              type="checkbox"
              checked={deduplicate}
              onChange={(e) => setDeduplicate(e.target.checked)}
            />
            Deduplicate
          </label>
        </div>
      </div>

      {/* Results */}
      {result && (
        <ContextViewer
          result={result}
          onFeedback={onFeedback
            ? (chunkId, rating) => onFeedback(result.requestId, chunkId, rating)
            : undefined
          }
        />
      )}
    </div>
  );
}
