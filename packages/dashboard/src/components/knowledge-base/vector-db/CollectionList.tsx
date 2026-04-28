/**
 * Collection List
 *
 * Displays all vector database collections with actions.
 */

import React, { useState, type JSX } from 'react';
import type { CollectionInfo } from '@tamma/shared';

export interface CollectionListProps {
  collections: CollectionInfo[];
  onSelect: (name: string) => void;
  onCreate: (name: string, dimensions?: number) => void;
  onDelete: (name: string) => void;
}

function formatBytes(bytes: number): string {
  if (bytes >= 1073741824) return `${(bytes / 1073741824).toFixed(1)} GB`;
  if (bytes >= 1048576) return `${(bytes / 1048576).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

export function CollectionList({ collections, onSelect, onCreate, onDelete }: CollectionListProps): JSX.Element {
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState('');
  const [newDimensions, setNewDimensions] = useState(1536);

  const handleCreate = () => {
    if (newName.trim()) {
      onCreate(newName.trim(), newDimensions);
      setNewName('');
      setShowCreate(false);
    }
  };

  return (
    <div data-testid="collection-list">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
        <h3 style={{ margin: 0, fontSize: '16px', fontWeight: 600 }}>Collections</h3>
        <button
          data-testid="create-collection-btn"
          onClick={() => setShowCreate(!showCreate)}
          style={{
            padding: '6px 14px',
            border: 'none',
            borderRadius: '6px',
            backgroundColor: '#3b82f6',
            color: '#ffffff',
            cursor: 'pointer',
            fontSize: '13px',
          }}
        >
          New Collection
        </button>
      </div>

      {showCreate && (
        <div data-testid="create-form" style={{ padding: '16px', backgroundColor: '#f9fafb', borderRadius: '8px', marginBottom: '16px', display: 'flex', gap: '8px', alignItems: 'flex-end' }}>
          <div>
            <label style={{ display: 'block', fontSize: '12px', marginBottom: '4px' }}>Name</label>
            <input
              data-testid="collection-name-input"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="collection-name"
              style={{ padding: '6px 10px', border: '1px solid #d1d5db', borderRadius: '4px', fontSize: '14px' }}
            />
          </div>
          <div>
            <label style={{ display: 'block', fontSize: '12px', marginBottom: '4px' }}>Dimensions</label>
            <input
              type="number"
              value={newDimensions}
              onChange={(e) => setNewDimensions(parseInt(e.target.value, 10))}
              style={{ padding: '6px 10px', border: '1px solid #d1d5db', borderRadius: '4px', fontSize: '14px', width: '80px' }}
            />
          </div>
          <button
            data-testid="confirm-create"
            onClick={handleCreate}
            style={{ padding: '6px 14px', border: 'none', borderRadius: '4px', backgroundColor: '#22c55e', color: '#fff', cursor: 'pointer' }}
          >
            Create
          </button>
        </div>
      )}

      {collections.length === 0 ? (
        <div style={{ padding: '24px', textAlign: 'center', color: '#6b7280' }}>
          No collections found
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
          {collections.map((col) => (
            <div
              key={col.name}
              data-testid="collection-item"
              style={{
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                padding: '12px 16px',
                backgroundColor: '#ffffff',
                border: '1px solid #e5e7eb',
                borderRadius: '8px',
                cursor: 'pointer',
              }}
              onClick={() => onSelect(col.name)}
            >
              <div>
                <div style={{ fontWeight: 600, fontSize: '14px' }}>{col.name}</div>
                <div style={{ fontSize: '12px', color: '#6b7280', marginTop: '2px' }}>
                  {col.vectorCount.toLocaleString()} vectors | {col.dimensions}d | {formatBytes(col.storageBytes)}
                </div>
              </div>
              <button
                data-testid={`delete-${col.name}`}
                onClick={(e) => { e.stopPropagation(); onDelete(col.name); }}
                style={{
                  padding: '4px 10px',
                  border: '1px solid #dc2626',
                  borderRadius: '4px',
                  backgroundColor: 'transparent',
                  color: '#dc2626',
                  cursor: 'pointer',
                  fontSize: '12px',
                }}
              >
                Delete
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
