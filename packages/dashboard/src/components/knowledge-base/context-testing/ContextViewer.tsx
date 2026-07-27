/**
 * Context Viewer
 *
 * Displays assembled context with chunk cards and source contributions.
 */

import { type JSX } from 'react';
import type { ContextTestResult } from '@tamma/shared';
import { ChunkCard } from './ChunkCard.js';
import { SourceContributionChart } from './SourceContributionChart.js';

export interface ContextViewerProps {
  result: ContextTestResult;
  onFeedback?:
    | ((chunkId: string, rating: 'relevant' | 'irrelevant' | 'partially_relevant') => void)
    | undefined;
}

export function ContextViewer({ result, onFeedback }: ContextViewerProps): JSX.Element {
  return (
    <div data-testid="context-viewer">
      {/* Metrics summary */}
      <div style={{ display: 'flex', gap: '24px', marginBottom: '20px', padding: '12px 16px', backgroundColor: '#f9fafb', borderRadius: '8px', fontSize: '13px' }}>
        <div>
          <span style={{ color: '#6b7280' }}>Chunks: </span>
          <span style={{ fontWeight: 600 }}>{result.context.chunks.length}</span>
        </div>
        <div>
          <span style={{ color: '#6b7280' }}>Tokens: </span>
          <span style={{ fontWeight: 600 }}>{result.context.tokenCount.toLocaleString()}</span>
        </div>
        <div>
          <span style={{ color: '#6b7280' }}>Latency: </span>
          <span style={{ fontWeight: 600 }}>{result.metrics.totalLatencyMs}ms</span>
        </div>
        <div>
          <span style={{ color: '#6b7280' }}>Budget: </span>
          <span style={{ fontWeight: 600 }}>{(result.metrics.budgetUtilization * 100).toFixed(0)}%</span>
        </div>
        <div>
          <span style={{ color: '#6b7280' }}>Dedup: </span>
          <span style={{ fontWeight: 600 }}>{(result.metrics.deduplicationRate * 100).toFixed(0)}%</span>
        </div>
      </div>

      {/* Source contributions */}
      <div style={{ marginBottom: '20px' }}>
        <SourceContributionChart
          contributions={result.sources}
          totalTokens={result.context.tokenCount}
        />
      </div>

      {/* Chunks */}
      <h4 style={{ margin: '0 0 12px 0', fontSize: '14px', fontWeight: 600, color: '#374151' }}>
        Retrieved Chunks ({result.context.chunks.length})
      </h4>
      <div data-testid="context-results" style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
        {result.context.chunks.map((chunk, idx) => (
          <ChunkCard
            key={chunk.id}
            chunk={chunk}
            index={idx}
            onFeedback={onFeedback}
          />
        ))}
      </div>
    </div>
  );
}
