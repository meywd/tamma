/**
 * Source Contribution Chart
 *
 * Visual bar chart showing token contributions from each source.
 */

import React, { type JSX } from 'react';
import type { UISourceContribution } from '@tamma/shared';

export interface SourceContributionChartProps {
  contributions: UISourceContribution[];
  totalTokens: number;
}

const SOURCE_COLORS: Record<string, string> = {
  vector_db: '#3b82f6',
  rag: '#8b5cf6',
  mcp: '#f59e0b',
  web_search: '#22c55e',
  live_api: '#ec4899',
};

export function SourceContributionChart({ contributions, totalTokens }: SourceContributionChartProps): JSX.Element {
  return (
    <div data-testid="source-contribution-chart">
      <h4 style={{ margin: '0 0 12px 0', fontSize: '14px', fontWeight: 600, color: '#374151' }}>
        Source Contributions
      </h4>
      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
        {contributions.map((src) => {
          const pct = totalTokens > 0 ? (src.tokensUsed / totalTokens) * 100 : 0;
          const color = SOURCE_COLORS[src.source] ?? '#6b7280';

          return (
            <div key={src.source} data-testid="contribution-bar">
              <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '4px', fontSize: '13px' }}>
                <span style={{ fontWeight: 500 }}>{src.source}</span>
                <span style={{ color: '#6b7280' }}>
                  {pct.toFixed(0)}% ({src.tokensUsed.toLocaleString()} tokens)
                </span>
              </div>
              <div style={{ width: '100%', height: '12px', backgroundColor: '#e5e7eb', borderRadius: '6px', overflow: 'hidden' }}>
                <div
                  style={{
                    width: `${pct}%`,
                    height: '100%',
                    backgroundColor: color,
                    borderRadius: '6px',
                    transition: 'width 0.3s ease',
                  }}
                />
              </div>
              <div style={{ display: 'flex', gap: '16px', marginTop: '4px', fontSize: '11px', color: '#9ca3af' }}>
                <span>{src.chunksProvided} chunks</span>
                <span>{src.latencyMs}ms</span>
                {src.cacheHit && <span>cache hit</span>}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
