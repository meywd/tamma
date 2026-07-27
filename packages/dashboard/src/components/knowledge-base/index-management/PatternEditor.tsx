/**
 * Pattern Editor
 *
 * Inline editor for glob include/exclude patterns with add/remove controls.
 */

import { useState, type JSX } from 'react';

export interface PatternEditorProps {
  label: string;
  patterns: string[];
  onChange: (patterns: string[]) => void;
}

export function PatternEditor({ label, patterns, onChange }: PatternEditorProps): JSX.Element {
  const [newPattern, setNewPattern] = useState('');

  const handleAdd = () => {
    const trimmed = newPattern.trim();
    if (trimmed && !patterns.includes(trimmed)) {
      onChange([...patterns, trimmed]);
      setNewPattern('');
    }
  };

  const handleRemove = (index: number) => {
    onChange(patterns.filter((_, i) => i !== index));
  };

  return (
    <div data-testid="pattern-editor">
      <label style={{ display: 'block', fontSize: '13px', fontWeight: 500, color: '#374151', marginBottom: '8px' }}>
        {label}
      </label>

      {/* Existing patterns */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px', marginBottom: '8px' }}>
        {patterns.map((pattern, idx) => (
          <span
            key={`${pattern}-${idx}`}
            data-testid="pattern-tag"
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: '4px',
              padding: '4px 10px',
              backgroundColor: '#f3f4f6',
              borderRadius: '12px',
              fontSize: '13px',
              fontFamily: 'monospace',
            }}
          >
            {pattern}
            <button
              data-testid="remove-pattern"
              onClick={() => handleRemove(idx)}
              style={{
                border: 'none',
                background: 'none',
                cursor: 'pointer',
                color: '#9ca3af',
                fontSize: '14px',
                padding: '0 2px',
                lineHeight: 1,
              }}
              aria-label={`Remove ${pattern}`}
            >
              x
            </button>
          </span>
        ))}
      </div>

      {/* Add new pattern */}
      <div style={{ display: 'flex', gap: '6px' }}>
        <input
          data-testid="new-pattern-input"
          value={newPattern}
          onChange={(e) => setNewPattern(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); handleAdd(); } }}
          placeholder="e.g., **/*.ts"
          style={{
            flex: 1,
            padding: '6px 10px',
            border: '1px solid #d1d5db',
            borderRadius: '4px',
            fontSize: '13px',
            fontFamily: 'monospace',
          }}
        />
        <button
          data-testid="add-pattern"
          onClick={handleAdd}
          style={{
            padding: '6px 12px',
            border: 'none',
            borderRadius: '4px',
            backgroundColor: '#3b82f6',
            color: '#fff',
            cursor: 'pointer',
            fontSize: '13px',
          }}
        >
          Add
        </button>
      </div>
    </div>
  );
}
