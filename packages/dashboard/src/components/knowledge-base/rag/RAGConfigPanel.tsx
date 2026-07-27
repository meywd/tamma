/**
 * RAG Config Panel
 *
 * Configuration editor for the RAG pipeline.
 */

import { useState, useEffect, type JSX } from 'react';
import type { RAGConfigInfo } from '@tamma/shared';

export interface RAGConfigPanelProps {
  config: RAGConfigInfo;
  onSave: (config: Partial<RAGConfigInfo>) => void;
}

export function RAGConfigPanel({ config, onSave }: RAGConfigPanelProps): JSX.Element {
  const [maxTokens, setMaxTokens] = useState(config.assembly.maxTokens);
  const [format, setFormat] = useState(config.assembly.format);
  const [fusionMethod, setFusionMethod] = useState(config.ranking.fusionMethod);
  const [mmrLambda, setMmrLambda] = useState(config.ranking.mmrLambda);
  const [cachingEnabled, setCachingEnabled] = useState(config.caching.enabled);
  const [ttlSeconds, setTtlSeconds] = useState(config.caching.ttlSeconds);

  useEffect(() => {
    setMaxTokens(config.assembly.maxTokens);
    setFormat(config.assembly.format);
    setFusionMethod(config.ranking.fusionMethod);
    setMmrLambda(config.ranking.mmrLambda);
    setCachingEnabled(config.caching.enabled);
    setTtlSeconds(config.caching.ttlSeconds);
  }, [config]);

  const handleSave = () => {
    onSave({
      assembly: { ...config.assembly, maxTokens, format: format as 'xml' | 'markdown' | 'plain' },
      ranking: { ...config.ranking, fusionMethod: fusionMethod as 'rrf' | 'linear' | 'learned', mmrLambda },
      caching: { ...config.caching, enabled: cachingEnabled, ttlSeconds },
    });
  };

  const sectionStyle = { marginBottom: '20px' };
  const labelStyle = { display: 'block', fontSize: '13px', fontWeight: 500 as const, color: '#374151', marginBottom: '4px' };
  const inputStyle = { padding: '8px 12px', border: '1px solid #d1d5db', borderRadius: '6px', fontSize: '14px', width: '100%' };

  return (
    <div data-testid="rag-config-panel">
      <h3 style={{ margin: '0 0 20px 0', fontSize: '16px', fontWeight: 600 }}>RAG Pipeline Configuration</h3>

      {/* Source Weights */}
      <div style={sectionStyle}>
        <h4 style={{ fontSize: '14px', fontWeight: 600, marginBottom: '12px' }}>Source Weights</h4>
        {Object.entries(config.sources).map(([key, src]) => (
          <div key={key} style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: '8px' }}>
            <span style={{ width: '80px', fontSize: '13px', fontWeight: 500 }}>{key}</span>
            <span style={{ fontSize: '12px', color: src.enabled ? '#22c55e' : '#9ca3af' }}>
              {src.enabled ? 'ON' : 'OFF'}
            </span>
            <span style={{ fontSize: '13px', color: '#6b7280' }}>
              weight: {src.weight} | topK: {src.topK}
            </span>
          </div>
        ))}
      </div>

      {/* Ranking */}
      <div style={sectionStyle}>
        <h4 style={{ fontSize: '14px', fontWeight: 600, marginBottom: '12px' }}>Ranking</h4>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
          <div>
            <label style={labelStyle}>Fusion Method</label>
            <select
              data-testid="fusion-method"
              value={fusionMethod}
              onChange={(e) => setFusionMethod(e.target.value as 'rrf' | 'linear' | 'learned')}
              style={inputStyle}
            >
              <option value="rrf">Reciprocal Rank Fusion</option>
              <option value="linear">Linear</option>
              <option value="learned">Learned</option>
            </select>
          </div>
          <div>
            <label style={labelStyle}>MMR Lambda ({mmrLambda})</label>
            <input
              type="range"
              min="0"
              max="1"
              step="0.05"
              value={mmrLambda}
              onChange={(e) => setMmrLambda(parseFloat(e.target.value))}
              style={{ width: '100%' }}
            />
          </div>
        </div>
      </div>

      {/* Assembly */}
      <div style={sectionStyle}>
        <h4 style={{ fontSize: '14px', fontWeight: 600, marginBottom: '12px' }}>Assembly</h4>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
          <div>
            <label style={labelStyle}>Max Tokens</label>
            <input
              data-testid="assembly-max-tokens"
              type="number"
              value={maxTokens}
              onChange={(e) => setMaxTokens(parseInt(e.target.value, 10))}
              style={inputStyle}
            />
          </div>
          <div>
            <label style={labelStyle}>Output Format</label>
            <select
              value={format}
              onChange={(e) => setFormat(e.target.value as typeof format)}
              style={inputStyle}
            >
              <option value="xml">XML</option>
              <option value="markdown">Markdown</option>
              <option value="plain">Plain Text</option>
            </select>
          </div>
        </div>
      </div>

      {/* Caching */}
      <div style={sectionStyle}>
        <h4 style={{ fontSize: '14px', fontWeight: 600, marginBottom: '12px' }}>Caching</h4>
        <div style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: '12px' }}>
          <input
            type="checkbox"
            checked={cachingEnabled}
            onChange={(e) => setCachingEnabled(e.target.checked)}
            id="caching-enabled"
          />
          <label htmlFor="caching-enabled" style={{ fontSize: '14px' }}>Enable caching</label>
        </div>
        {cachingEnabled && (
          <div>
            <label style={labelStyle}>TTL (seconds)</label>
            <input
              type="number"
              value={ttlSeconds}
              onChange={(e) => setTtlSeconds(parseInt(e.target.value, 10))}
              style={{ ...inputStyle, width: '120px' }}
            />
          </div>
        )}
      </div>

      <button
        data-testid="save-rag-config"
        onClick={handleSave}
        style={{
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
