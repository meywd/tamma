/**
 * Config Editor
 *
 * JSON/YAML configuration editor with validation.
 * Allows editing all context layer configurations.
 */

import { useState, useCallback, type JSX } from 'react';

export interface ConfigEditorProps {
  config: Record<string, unknown>;
  onSave: (config: Record<string, unknown>) => void;
  onValidate?: (config: string) => string | null;
  format?: 'json' | 'yaml';
}

export function ConfigEditor({ config, onSave, onValidate, format = 'json' }: ConfigEditorProps): JSX.Element {
  const [content, setContent] = useState(JSON.stringify(config, null, 2));
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const handleValidate = useCallback(() => {
    try {
      JSON.parse(content);
      if (onValidate) {
        const validationError = onValidate(content);
        if (validationError) {
          setError(validationError);
          return false;
        }
      }
      setError(null);
      return true;
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Invalid JSON');
      return false;
    }
  }, [content, onValidate]);

  const handleSave = () => {
    if (handleValidate()) {
      try {
        const parsed = JSON.parse(content) as Record<string, unknown>;
        onSave(parsed);
        setSaved(true);
        setTimeout(() => setSaved(false), 2000);
      } catch {
        setError('Failed to parse configuration');
      }
    }
  };

  const handleReset = () => {
    setContent(JSON.stringify(config, null, 2));
    setError(null);
  };

  return (
    <div data-testid="config-editor">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
        <h3 style={{ margin: 0, fontSize: '16px', fontWeight: 600 }}>Configuration Editor</h3>
        <span style={{ fontSize: '12px', color: '#6b7280' }}>Format: {format.toUpperCase()}</span>
      </div>

      <textarea
        data-testid="config-textarea"
        value={content}
        onChange={(e) => { setContent(e.target.value); setError(null); setSaved(false); }}
        rows={20}
        style={{
          width: '100%',
          padding: '12px',
          border: `1px solid ${error ? '#ef4444' : '#d1d5db'}`,
          borderRadius: '6px',
          fontSize: '13px',
          fontFamily: 'monospace',
          lineHeight: '1.5',
          resize: 'vertical',
          backgroundColor: '#1f2937',
          color: '#e5e7eb',
        }}
      />

      {error && (
        <div data-testid="config-error" style={{ marginTop: '8px', padding: '8px 12px', backgroundColor: '#fef2f2', borderRadius: '4px', color: '#991b1b', fontSize: '13px' }}>
          {error}
        </div>
      )}

      {saved && (
        <div data-testid="config-saved" style={{ marginTop: '8px', padding: '8px 12px', backgroundColor: '#f0fdf4', borderRadius: '4px', color: '#166534', fontSize: '13px' }}>
          Configuration saved successfully
        </div>
      )}

      <div style={{ display: 'flex', gap: '8px', marginTop: '12px' }}>
        <button
          data-testid="validate-btn"
          onClick={handleValidate}
          style={{
            padding: '8px 16px',
            border: '1px solid #d1d5db',
            borderRadius: '6px',
            backgroundColor: '#ffffff',
            color: '#374151',
            cursor: 'pointer',
            fontSize: '14px',
          }}
        >
          Validate
        </button>
        <button
          data-testid="save-config-btn"
          onClick={handleSave}
          style={{
            padding: '8px 16px',
            border: 'none',
            borderRadius: '6px',
            backgroundColor: '#3b82f6',
            color: '#ffffff',
            cursor: 'pointer',
            fontSize: '14px',
            fontWeight: 500,
          }}
        >
          Save
        </button>
        <button
          data-testid="reset-config-btn"
          onClick={handleReset}
          style={{
            padding: '8px 16px',
            border: '1px solid #d1d5db',
            borderRadius: '6px',
            backgroundColor: '#ffffff',
            color: '#6b7280',
            cursor: 'pointer',
            fontSize: '14px',
          }}
        >
          Reset
        </button>
      </div>
    </div>
  );
}
