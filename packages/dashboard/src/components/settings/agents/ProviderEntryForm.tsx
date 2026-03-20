import { useState, useEffect } from 'react';
import type { FormEvent } from 'react';
import type { IProviderChainEntry } from '@tamma/shared';

interface ProviderEntryFormProps {
  /** Called when saving (both add and edit). */
  onSave: (entry: IProviderChainEntry) => void;
  onCancel: () => void;
  /** If provided, the form is in edit mode with pre-filled values. */
  initialValue?: IProviderChainEntry;
}

// Keep backward compat with old onAdd prop
interface LegacyProviderEntryFormProps {
  onAdd: (entry: IProviderChainEntry) => void;
  onCancel: () => void;
  initialValue?: IProviderChainEntry;
}

const KNOWN_PROVIDERS = [
  'claude-code',
  'opencode',
  'openrouter',
  'zen-mcp',
  'anthropic',
  'openai',
  'gemini',
  'github-copilot',
];

export function ProviderEntryForm(
  props: ProviderEntryFormProps | LegacyProviderEntryFormProps,
): JSX.Element {
  const onSave = 'onSave' in props ? props.onSave : props.onAdd;
  const { onCancel, initialValue } = props;
  const isEdit = initialValue !== undefined;

  const [provider, setProvider] = useState(initialValue?.provider ?? '');
  const [model, setModel] = useState(initialValue?.model ?? '');
  const [apiKeyRef, setApiKeyRef] = useState(initialValue?.apiKeyRef ?? '');

  // Reset form when initialValue changes (switching between entries)
  useEffect(() => {
    setProvider(initialValue?.provider ?? '');
    setModel(initialValue?.model ?? '');
    setApiKeyRef(initialValue?.apiKeyRef ?? '');
  }, [initialValue]);

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
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Provider</label>
        <input
          type="text"
          value={provider}
          onChange={(e) => setProvider(e.target.value)}
          list="known-providers"
          placeholder="e.g., claude-code"
          className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          required
        />
        <datalist id="known-providers">
          {KNOWN_PROVIDERS.map((p) => (
            <option key={p} value={p} />
          ))}
        </datalist>
      </div>
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Model (optional)</label>
        <input
          type="text"
          value={model}
          onChange={(e) => setModel(e.target.value)}
          placeholder="e.g., claude-sonnet-4-5"
          className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
        />
      </div>
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">API Key Ref (optional)</label>
        <input
          type="text"
          value={apiKeyRef}
          onChange={(e) => setApiKeyRef(e.target.value)}
          placeholder="e.g., ANTHROPIC_API_KEY"
          className="w-full px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
        />
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
