/**
 * Config Version History
 *
 * Displays version history of configuration changes with rollback capability.
 */

import React, { type JSX } from 'react';

export interface ConfigVersion {
  id: string;
  timestamp: string;
  author: string;
  description: string;
  config: Record<string, unknown>;
}

export interface ConfigVersionHistoryProps {
  versions: ConfigVersion[];
  currentVersionId?: string;
  onSelect: (version: ConfigVersion) => void;
  onRollback?: (version: ConfigVersion) => void;
  onExport?: (version: ConfigVersion) => void;
}

export function ConfigVersionHistory({
  versions,
  currentVersionId,
  onSelect,
  onRollback,
  onExport,
}: ConfigVersionHistoryProps): JSX.Element {
  return (
    <div data-testid="config-version-history">
      <h3 style={{ margin: '0 0 12px 0', fontSize: '16px', fontWeight: 600 }}>Version History</h3>

      {versions.length === 0 ? (
        <div style={{ padding: '16px', textAlign: 'center', color: '#6b7280', fontSize: '13px' }}>
          No version history available
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
          {versions.map((version) => {
            const isCurrent = version.id === currentVersionId;
            return (
              <div
                key={version.id}
                data-testid="version-entry"
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  padding: '10px 12px',
                  border: `1px solid ${isCurrent ? '#3b82f6' : '#e5e7eb'}`,
                  borderRadius: '6px',
                  backgroundColor: isCurrent ? '#eff6ff' : '#ffffff',
                  cursor: 'pointer',
                }}
                onClick={() => onSelect(version)}
              >
                <div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                    <span style={{ fontSize: '14px', fontWeight: 500 }}>{version.description}</span>
                    {isCurrent && (
                      <span style={{ padding: '1px 6px', borderRadius: '8px', backgroundColor: '#3b82f6', color: '#fff', fontSize: '10px', fontWeight: 600 }}>
                        CURRENT
                      </span>
                    )}
                  </div>
                  <div style={{ fontSize: '12px', color: '#6b7280', marginTop: '2px' }}>
                    {new Date(version.timestamp).toLocaleString()} by {version.author}
                  </div>
                </div>
                <div style={{ display: 'flex', gap: '6px' }} onClick={(e) => e.stopPropagation()}>
                  {onRollback && !isCurrent && (
                    <button
                      data-testid="rollback-btn"
                      onClick={() => onRollback(version)}
                      style={{
                        padding: '4px 10px',
                        border: '1px solid #f59e0b',
                        borderRadius: '4px',
                        backgroundColor: 'transparent',
                        color: '#f59e0b',
                        cursor: 'pointer',
                        fontSize: '12px',
                      }}
                    >
                      Rollback
                    </button>
                  )}
                  {onExport && (
                    <button
                      data-testid="export-btn"
                      onClick={() => onExport(version)}
                      style={{
                        padding: '4px 10px',
                        border: '1px solid #d1d5db',
                        borderRadius: '4px',
                        backgroundColor: 'transparent',
                        color: '#374151',
                        cursor: 'pointer',
                        fontSize: '12px',
                      }}
                    >
                      Export
                    </button>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
