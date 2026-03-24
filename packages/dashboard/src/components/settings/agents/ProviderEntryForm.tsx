import { useState, useEffect, useMemo } from 'react';
import type { FormEvent } from 'react';
import type { IProviderChainEntry } from '@tamma/shared';

interface ProviderEntryFormProps {
  onSave: (entry: IProviderChainEntry) => void;
  onCancel: () => void;
  initialValue?: IProviderChainEntry;
}

// Legacy compat
interface LegacyProviderEntryFormProps {
  onAdd: (entry: IProviderChainEntry) => void;
  onCancel: () => void;
  initialValue?: IProviderChainEntry;
}

/** Known providers and their available models (latest as of March 2026) */
const PROVIDER_MODELS: Record<string, string[]> = {
  'claude-code': [
    'claude-opus-4-6',
    'claude-sonnet-4-6',
    'claude-haiku-4-5',
    'claude-sonnet-4-5',
    'claude-opus-4-5',
    'claude-sonnet-4-20250514',
    'claude-opus-4-20250514',
  ],
  anthropic: [
    'claude-opus-4-6',
    'claude-sonnet-4-6',
    'claude-haiku-4-5',
    'claude-sonnet-4-5',
    'claude-opus-4-5',
    'claude-sonnet-4-20250514',
    'claude-opus-4-20250514',
    'claude-3-5-sonnet-20241022',
    'claude-3-5-haiku-20241022',
  ],
  openai: [
    'gpt-5.4',
    'gpt-5.4-mini',
    'gpt-5.4-nano',
    'gpt-5',
    'gpt-5-mini',
    'o4-mini',
    'o3',
    'o3-mini',
    'gpt-4.1',
    'gpt-4.1-mini',
    'gpt-4o',
    'gpt-4o-mini',
  ],
  gemini: [
    'gemini-3.1-pro-preview',
    'gemini-3.1-flash-lite-preview',
    'gemini-2.5-pro',
    'gemini-2.5-flash',
    'gemini-2.5-flash-lite',
  ],
  openrouter: [
    'anthropic/claude-4.6-opus-20260205',
    'anthropic/claude-4.6-sonnet-20260217',
    'openai/gpt-5.4',
    'google/gemini-3.1-pro-preview',
    'google/gemini-2.5-flash',
    'deepseek/deepseek-v3.2-20251201',
    'x-ai/grok-code-fast-1',
  ],
  opencode: [],
  'zen-mcp': [],
  'github-copilot': [],
};

const KNOWN_PROVIDERS = Object.keys(PROVIDER_MODELS);

/** Default API key env var name per provider */
const DEFAULT_API_KEY_REF: Record<string, string> = {
  anthropic: 'ANTHROPIC_API_KEY',
  openai: 'OPENAI_API_KEY',
  gemini: 'GOOGLE_API_KEY',
  openrouter: 'OPENROUTER_API_KEY',
  'github-copilot': 'GITHUB_TOKEN',
};

export function ProviderEntryForm(
  props: ProviderEntryFormProps | LegacyProviderEntryFormProps,
): JSX.Element {
  const onSave = 'onSave' in props ? props.onSave : props.onAdd;
  const { onCancel, initialValue } = props;
  const isEdit = initialValue !== undefined;

  const [provider, setProvider] = useState(initialValue?.provider ?? '');
  const [model, setModel] = useState(initialValue?.model ?? '');
  const [apiKeyRef, setApiKeyRef] = useState(initialValue?.apiKeyRef ?? '');

  useEffect(() => {
    setProvider(initialValue?.provider ?? '');
    setModel(initialValue?.model ?? '');
    setApiKeyRef(initialValue?.apiKeyRef ?? '');
  }, [initialValue]);

  // Models available for the selected provider
  const availableModels = useMemo(() => {
    const normalized = provider.trim().toLowerCase();
    return PROVIDER_MODELS[normalized] ?? [];
  }, [provider]);

  // Auto-fill API key ref when provider changes (only if empty)
  const handleProviderChange = (newProvider: string) => {
    setProvider(newProvider);
    const ref = DEFAULT_API_KEY_REF[newProvider.trim().toLowerCase()];
    if (ref && !apiKeyRef) {
      setApiKeyRef(ref);
    }
  };

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (!provider.trim()) return;

    const entry: IProviderChainEntry = { provider: provider.trim() };
    if (model.trim()) {
      entry.model = model.trim();
    }
    if (apiKeyRef.trim()) {
      entry.apiKeyRef = apiKeyRef.trim();
    }
    onSave(entry);
  };

  return (
    <form onSubmit={handleSubmit} className={`p-3 rounded-md border space-y-3 ${isEdit ? 'bg-yellow-50 border-yellow-200' : 'bg-blue-50 border-blue-200'}`}>
      {/* Provider — select dropdown */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Provider</label>
        <select
          value={KNOWN_PROVIDERS.includes(provider) ? provider : '__custom__'}
          onChange={(e) => {
            if (e.target.value === '__custom__') return;
            handleProviderChange(e.target.value);
          }}
          className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 bg-white"
        >
          <option value="" disabled>Select a provider...</option>
          {KNOWN_PROVIDERS.map((p) => (
            <option key={p} value={p}>{p}</option>
          ))}
          {!KNOWN_PROVIDERS.includes(provider) && provider && (
            <option value="__custom__">{provider} (custom)</option>
          )}
        </select>
        {/* Allow custom provider name if not in list */}
        {!KNOWN_PROVIDERS.includes(provider) && (
          <input
            type="text"
            value={provider}
            onChange={(e) => setProvider(e.target.value)}
            placeholder="Custom provider name"
            className="w-full mt-1 px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        )}
      </div>

      {/* Model — dropdown with available models + custom option */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Model</label>
        {availableModels.length > 0 ? (
          <>
            <select
              value={availableModels.includes(model) ? model : model ? '__custom__' : ''}
              onChange={(e) => {
                if (e.target.value === '__custom__') return;
                setModel(e.target.value);
              }}
              className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 bg-white"
            >
              <option value="">Default model</option>
              {availableModels.map((m) => (
                <option key={m} value={m}>{m}</option>
              ))}
              {model && !availableModels.includes(model) && (
                <option value="__custom__">{model} (custom)</option>
              )}
            </select>
            {model && !availableModels.includes(model) && (
              <input
                type="text"
                value={model}
                onChange={(e) => setModel(e.target.value)}
                placeholder="Custom model name"
                className="w-full mt-1 px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            )}
          </>
        ) : (
          <input
            type="text"
            value={model}
            onChange={(e) => setModel(e.target.value)}
            placeholder="e.g., claude-sonnet-4-5"
            className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        )}
      </div>

      {/* API Key Ref */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">API Key Env Var</label>
        <input
          type="text"
          value={apiKeyRef}
          onChange={(e) => setApiKeyRef(e.target.value)}
          placeholder="e.g., ANTHROPIC_API_KEY"
          className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
        />
        {provider && DEFAULT_API_KEY_REF[provider.toLowerCase()] && !apiKeyRef && (
          <p className="text-xs text-gray-400 mt-0.5">
            Default: {DEFAULT_API_KEY_REF[provider.toLowerCase()]}
          </p>
        )}
      </div>

      <div className="flex gap-2">
        <button
          type="submit"
          className={`px-3 py-1.5 text-sm font-medium text-white rounded-md ${isEdit ? 'bg-yellow-600 hover:bg-yellow-700' : 'bg-blue-600 hover:bg-blue-700'}`}
        >
          {isEdit ? 'Save Changes' : 'Add'}
        </button>
        <button
          type="button"
          onClick={onCancel}
          className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
        >
          Cancel
        </button>
      </div>
    </form>
  );
}
