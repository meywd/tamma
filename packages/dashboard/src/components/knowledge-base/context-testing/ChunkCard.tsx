/**
 * Chunk Card
 *
 * Displays a single retrieved context chunk with metadata and feedback controls.
 */

import React, { type JSX } from 'react';
import type { UIContextChunk } from '@tamma/shared';

export interface ChunkCardProps {
  chunk: UIContextChunk;
  index: number;
  onFeedback?: (chunkId: string, rating: 'relevant' | 'irrelevant' | 'partially_relevant') => void;
}

const SOURCE_COLORS: Record<string, string> = {
  vector_db: '#3b82f6',
  rag: '#8b5cf6',
  mcp: '#f59e0b',
  web_search: '#22c55e',
  live_api: '#ec4899',
};

export function ChunkCard({ chunk, index, onFeedback }: ChunkCardProps): JSX.Element {
  const sourceColor = SOURCE_COLORS[chunk.source] ?? '#6b7280';

  return (
    <div
      data-testid="chunk-card"
      style={{
        border: '1px solid #e5e7eb',
        borderRadius: '8px',
        padding: '12px',
        backgroundColor: '#ffffff',
      }}
    >
      {/* Header */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '8px' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
          <span style={{ fontSize: '13px', color: '#6b7280' }}>
            #{index + 1}
            {chunk.metadata.filePath && ` | ${chunk.metadata.filePath}`}
            {chunk.metadata.startLine && `:${chunk.metadata.startLine}-${chunk.metadata.endLine}`}
          </span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
          <span
            data-testid="source-badge"
            style={{
              padding: '2px 8px',
              borderRadius: '10px',
              fontSize: '11px',
              fontWeight: 600,
              backgroundColor: `${sourceColor}20`,
              color: sourceColor,
            }}
          >
            {chunk.source}
          </span>
          <span style={{ fontSize: '12px', fontWeight: 600, color: '#3b82f6' }}>
            {chunk.relevance.toFixed(2)}
          </span>
        </div>
      </div>

      {/* Content */}
      <pre
        data-testid="chunk-content"
        style={{
          margin: '0 0 8px 0',
          padding: '8px',
          backgroundColor: '#f9fafb',
          borderRadius: '4px',
          fontSize: '12px',
          overflow: 'auto',
          maxHeight: '150px',
          lineHeight: '1.5',
        }}
      >
        {chunk.content}
      </pre>

      {/* Feedback controls */}
      {onFeedback && (
        <div data-testid="feedback-controls" style={{ display: 'flex', gap: '6px', justifyContent: 'flex-end' }}>
          <button
            data-testid="feedback-relevant"
            onClick={() => onFeedback(chunk.id, 'relevant')}
            title="Relevant"
            style={{ padding: '4px 10px', border: '1px solid #22c55e', borderRadius: '4px', backgroundColor: 'transparent', color: '#22c55e', cursor: 'pointer', fontSize: '12px' }}
          >
            Relevant
          </button>
          <button
            data-testid="feedback-partial"
            onClick={() => onFeedback(chunk.id, 'partially_relevant')}
            title="Partially Relevant"
            style={{ padding: '4px 10px', border: '1px solid #f59e0b', borderRadius: '4px', backgroundColor: 'transparent', color: '#f59e0b', cursor: 'pointer', fontSize: '12px' }}
          >
            Partial
          </button>
          <button
            data-testid="feedback-irrelevant"
            onClick={() => onFeedback(chunk.id, 'irrelevant')}
            title="Irrelevant"
            style={{ padding: '4px 10px', border: '1px solid #ef4444', borderRadius: '4px', backgroundColor: 'transparent', color: '#ef4444', cursor: 'pointer', fontSize: '12px' }}
          >
            Irrelevant
          </button>
        </div>
      )}
    </div>
  );
}
