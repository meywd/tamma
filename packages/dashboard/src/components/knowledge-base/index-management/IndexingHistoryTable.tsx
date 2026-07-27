/**
 * Indexing History Table
 *
 * Shows previous indexing runs with key metrics.
 */

import { type JSX } from 'react';
import type { IndexHistoryEntry } from '@tamma/shared';

export interface IndexingHistoryTableProps {
  history: IndexHistoryEntry[];
  onSelectEntry?: (entry: IndexHistoryEntry) => void;
}

const STATUS_COLORS: Record<string, string> = {
  success: '#22c55e',
  partial: '#f59e0b',
  failed: '#ef4444',
};

export function IndexingHistoryTable({ history, onSelectEntry }: IndexingHistoryTableProps): JSX.Element {
  if (history.length === 0) {
    return (
      <div data-testid="indexing-history-empty" style={{ padding: '24px', textAlign: 'center', color: '#6b7280' }}>
        No indexing history available
      </div>
    );
  }

  return (
    <div data-testid="indexing-history-table" style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '14px' }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e5e7eb' }}>
            <th style={{ textAlign: 'left', padding: '8px 12px', color: '#6b7280', fontWeight: 500 }}>Date</th>
            <th style={{ textAlign: 'left', padding: '8px 12px', color: '#6b7280', fontWeight: 500 }}>Status</th>
            <th style={{ textAlign: 'right', padding: '8px 12px', color: '#6b7280', fontWeight: 500 }}>Files</th>
            <th style={{ textAlign: 'right', padding: '8px 12px', color: '#6b7280', fontWeight: 500 }}>Chunks</th>
            <th style={{ textAlign: 'right', padding: '8px 12px', color: '#6b7280', fontWeight: 500 }}>Duration</th>
            <th style={{ textAlign: 'right', padding: '8px 12px', color: '#6b7280', fontWeight: 500 }}>Cost</th>
          </tr>
        </thead>
        <tbody>
          {history.map((entry) => (
            <tr
              key={entry.id}
              data-testid="history-row"
              onClick={() => onSelectEntry?.(entry)}
              style={{
                borderBottom: '1px solid #f3f4f6',
                cursor: onSelectEntry ? 'pointer' : 'default',
              }}
            >
              <td style={{ padding: '8px 12px' }}>
                {new Date(entry.startTime).toLocaleString()}
              </td>
              <td style={{ padding: '8px 12px' }}>
                <span style={{ color: STATUS_COLORS[entry.status] ?? '#6b7280', fontWeight: 500 }}>
                  {entry.status}
                </span>
              </td>
              <td style={{ padding: '8px 12px', textAlign: 'right' }}>{entry.filesProcessed}</td>
              <td style={{ padding: '8px 12px', textAlign: 'right' }}>{entry.chunksCreated}</td>
              <td style={{ padding: '8px 12px', textAlign: 'right' }}>
                {entry.durationMs >= 1000
                  ? `${(entry.durationMs / 1000).toFixed(1)}s`
                  : `${entry.durationMs}ms`}
              </td>
              <td style={{ padding: '8px 12px', textAlign: 'right' }}>
                ${entry.embeddingCost.toFixed(4)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
