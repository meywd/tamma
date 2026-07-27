/**
 * Config Diff Viewer
 *
 * Displays differences between two configuration versions.
 */

import { type JSX } from 'react';

export interface ConfigDiffViewerProps {
  oldConfig: Record<string, unknown>;
  newConfig: Record<string, unknown>;
  oldLabel?: string;
  newLabel?: string;
}

interface DiffLine {
  type: 'added' | 'removed' | 'unchanged';
  content: string;
}

function computeDiff(oldStr: string, newStr: string): DiffLine[] {
  const oldLines = oldStr.split('\n');
  const newLines = newStr.split('\n');
  const result: DiffLine[] = [];

  const maxLen = Math.max(oldLines.length, newLines.length);
  for (let i = 0; i < maxLen; i++) {
    const oldLine = oldLines[i];
    const newLine = newLines[i];

    if (oldLine === undefined && newLine !== undefined) {
      result.push({ type: 'added', content: newLine });
    } else if (newLine === undefined && oldLine !== undefined) {
      result.push({ type: 'removed', content: oldLine });
    } else if (oldLine !== newLine) {
      result.push({ type: 'removed', content: oldLine! });
      result.push({ type: 'added', content: newLine! });
    } else {
      result.push({ type: 'unchanged', content: oldLine! });
    }
  }

  return result;
}

const DIFF_STYLES: Record<string, { bg: string; color: string; prefix: string }> = {
  added: { bg: '#f0fdf4', color: '#166534', prefix: '+' },
  removed: { bg: '#fef2f2', color: '#991b1b', prefix: '-' },
  unchanged: { bg: 'transparent', color: '#e5e7eb', prefix: ' ' },
};

export function ConfigDiffViewer({ oldConfig, newConfig, oldLabel = 'Previous', newLabel = 'Current' }: ConfigDiffViewerProps): JSX.Element {
  const oldStr = JSON.stringify(oldConfig, null, 2);
  const newStr = JSON.stringify(newConfig, null, 2);
  const diffLines = computeDiff(oldStr, newStr);
  const hasChanges = diffLines.some((l) => l.type !== 'unchanged');

  return (
    <div data-testid="config-diff-viewer">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
        <h3 style={{ margin: 0, fontSize: '16px', fontWeight: 600 }}>Configuration Changes</h3>
        <div style={{ display: 'flex', gap: '12px', fontSize: '12px' }}>
          <span style={{ color: '#991b1b' }}>-- {oldLabel}</span>
          <span style={{ color: '#166534' }}>++ {newLabel}</span>
        </div>
      </div>

      {!hasChanges ? (
        <div style={{ padding: '16px', textAlign: 'center', color: '#6b7280', fontSize: '13px' }}>
          No changes detected
        </div>
      ) : (
        <div
          style={{
            border: '1px solid #e5e7eb',
            borderRadius: '8px',
            backgroundColor: '#1f2937',
            padding: '12px',
            maxHeight: '400px',
            overflow: 'auto',
            fontFamily: 'monospace',
            fontSize: '12px',
          }}
        >
          {diffLines.map((line, idx) => {
            const style = DIFF_STYLES[line.type]!;
            return (
              <div
                key={idx}
                data-testid={`diff-line-${line.type}`}
                style={{
                  backgroundColor: style.bg + (line.type === 'unchanged' ? '' : '30'),
                  color: style.color,
                  padding: '1px 8px',
                  lineHeight: '1.6',
                }}
              >
                <span style={{ color: '#6b7280', marginRight: '8px', userSelect: 'none' }}>{style.prefix}</span>
                {line.content}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
