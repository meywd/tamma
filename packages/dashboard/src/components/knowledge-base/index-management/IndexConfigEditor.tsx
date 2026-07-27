/**
 * Index Config Editor
 *
 * Form for editing index configuration including patterns,
 * chunking settings, and trigger configuration.
 */

import { useState, useEffect, type JSX } from 'react';
import type { IndexConfig } from '@tamma/shared';

export interface IndexConfigEditorProps {
  config: IndexConfig;
  onSave: (config: Partial<IndexConfig>) => void;
}

export function IndexConfigEditor({ config, onSave }: IndexConfigEditorProps): JSX.Element {
  const [includePatterns, setIncludePatterns] = useState(config.includePatterns.join('\n'));
  const [excludePatterns, setExcludePatterns] = useState(config.excludePatterns.join('\n'));
  const [maxTokens, setMaxTokens] = useState(config.chunkingConfig.maxTokens);
  const [overlapTokens, setOverlapTokens] = useState(config.chunkingConfig.overlapTokens);
  const [schedule, setSchedule] = useState(config.triggerConfig.schedule ?? '');
  const [gitHooks, setGitHooks] = useState(config.triggerConfig.gitHooks);

  useEffect(() => {
    setIncludePatterns(config.includePatterns.join('\n'));
    setExcludePatterns(config.excludePatterns.join('\n'));
    setMaxTokens(config.chunkingConfig.maxTokens);
    setOverlapTokens(config.chunkingConfig.overlapTokens);
    setSchedule(config.triggerConfig.schedule ?? '');
    setGitHooks(config.triggerConfig.gitHooks);
  }, [config]);

  const handleSave = () => {
    onSave({
      includePatterns: includePatterns.split('\n').map((s) => s.trim()).filter(Boolean),
      excludePatterns: excludePatterns.split('\n').map((s) => s.trim()).filter(Boolean),
      chunkingConfig: { ...config.chunkingConfig, maxTokens, overlapTokens },
      triggerConfig: { ...config.triggerConfig, gitHooks, schedule: schedule || null },
    });
  };

  const inputStyle = {
    width: '100%',
    padding: '8px 12px',
    border: '1px solid #d1d5db',
    borderRadius: '6px',
    fontSize: '14px',
    fontFamily: 'monospace',
  };

  const labelStyle = { display: 'block', fontSize: '13px', fontWeight: 500 as const, color: '#374151', marginBottom: '4px' };

  return (
    <div data-testid="index-config-editor" style={{ display: 'flex', flexDirection: 'column', gap: '20px' }}>
      <h3 style={{ margin: 0, fontSize: '16px', fontWeight: 600 }}>Index Configuration</h3>

      <div>
        <label style={labelStyle}>Include Patterns (one per line)</label>
        <textarea
          data-testid="include-patterns"
          value={includePatterns}
          onChange={(e) => setIncludePatterns(e.target.value)}
          rows={4}
          style={inputStyle}
        />
      </div>

      <div>
        <label style={labelStyle}>Exclude Patterns (one per line)</label>
        <textarea
          data-testid="exclude-patterns"
          value={excludePatterns}
          onChange={(e) => setExcludePatterns(e.target.value)}
          rows={4}
          style={inputStyle}
        />
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '16px' }}>
        <div>
          <label style={labelStyle}>Max Tokens per Chunk</label>
          <input
            data-testid="max-tokens"
            type="number"
            value={maxTokens}
            onChange={(e) => setMaxTokens(parseInt(e.target.value, 10))}
            style={inputStyle}
          />
        </div>
        <div>
          <label style={labelStyle}>Overlap Tokens</label>
          <input
            data-testid="overlap-tokens"
            type="number"
            value={overlapTokens}
            onChange={(e) => setOverlapTokens(parseInt(e.target.value, 10))}
            style={inputStyle}
          />
        </div>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
        <input
          type="checkbox"
          checked={gitHooks}
          onChange={(e) => setGitHooks(e.target.checked)}
          id="git-hooks"
        />
        <label htmlFor="git-hooks" style={{ fontSize: '14px' }}>Enable git hook triggers</label>
      </div>

      <div>
        <label style={labelStyle}>Schedule (cron expression, optional)</label>
        <input
          data-testid="schedule"
          type="text"
          value={schedule}
          onChange={(e) => setSchedule(e.target.value)}
          placeholder="e.g., 0 2 * * *"
          style={inputStyle}
        />
      </div>

      <button
        data-testid="save-config"
        onClick={handleSave}
        style={{
          alignSelf: 'flex-start',
          padding: '8px 20px',
          border: 'none',
          borderRadius: '6px',
          backgroundColor: '#3b82f6',
          color: '#ffffff',
          cursor: 'pointer',
          fontSize: '14px',
          fontWeight: 500,
        }}
      >
        Save Configuration
      </button>
    </div>
  );
}
