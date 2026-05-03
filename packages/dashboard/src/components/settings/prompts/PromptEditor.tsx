import { useState, useEffect, useCallback, type JSX } from 'react';
import type { PromptTemplateEntry } from '../../../services/settings/settings-api-client.js';
import { Card } from '../../common/Card.js';

interface PromptEditorProps {
  role: string;
  template: PromptTemplateEntry;
  onSave: (role: string, template: PromptTemplateEntry) => Promise<void>;
}

const ROLE_LABELS: Record<string, string> = {
  defaults: 'Defaults',
  scrum_master: 'Scrum Master',
  architect: 'Architect',
  researcher: 'Researcher',
  analyst: 'Analyst',
  planner: 'Planner',
  implementer: 'Implementer',
  reviewer: 'Reviewer',
  tester: 'Tester',
  documenter: 'Documenter',
};

export function PromptEditor({ role, template, onSave }: PromptEditorProps): JSX.Element {
  const [systemPrompt, setSystemPrompt] = useState(template.systemPrompt ?? '');
  const [providerPromptsText, setProviderPromptsText] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    setSystemPrompt(template.systemPrompt ?? '');
    setProviderPromptsText(
      template.providerPrompts
        ? Object.entries(template.providerPrompts)
            .map(([k, v]) => `${k}: ${v}`)
            .join('\n')
        : '',
    );
    setSaveError(null);
  }, [role, template]);

  const handleSave = useCallback(async () => {
    setSaving(true);
    setSaveError(null);
    try {
      const providerPrompts: Record<string, string> = {};
      if (providerPromptsText.trim()) {
        for (const line of providerPromptsText.split('\n')) {
          const colonIdx = line.indexOf(':');
          if (colonIdx > 0) {
            const key = line.slice(0, colonIdx).trim();
            const value = line.slice(colonIdx + 1).trim();
            if (key && value) {
              providerPrompts[key] = value;
            }
          }
        }
      }

      // Always send systemPrompt so the server can clear it when empty.
      // Use empty string to signal "clear", server handles empty → undefined.
      const entry: PromptTemplateEntry = {};
      entry.systemPrompt = systemPrompt;
      if (Object.keys(providerPrompts).length > 0) {
        entry.providerPrompts = providerPrompts;
      }
      await onSave(role, entry);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  }, [role, systemPrompt, providerPromptsText, onSave]);

  return (
    <Card
      title={`Prompt: ${ROLE_LABELS[role] ?? role}`}
      actions={
        <button
          onClick={handleSave}
          disabled={saving}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
        >
          {saving ? 'Saving...' : 'Save'}
        </button>
      }
    >
      {saveError && <div className="mb-4 text-sm text-red-600 dark:text-red-400">{saveError}</div>}

      <div className="space-y-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1 dark:text-gray-300">System Prompt</label>
          <textarea
            value={systemPrompt}
            onChange={(e) => setSystemPrompt(e.target.value)}
            rows={8}
            placeholder="Enter system prompt for this role..."
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 font-mono dark:border-gray-600"
          />
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1 dark:text-gray-300">
            Provider-Specific Prompts
          </label>
          <p className="text-xs text-gray-500 mb-2 dark:text-gray-400">
            One per line, format: <code>provider-name: prompt text</code>
          </p>
          <textarea
            value={providerPromptsText}
            onChange={(e) => setProviderPromptsText(e.target.value)}
            rows={4}
            placeholder="claude-code: You are a coding assistant..."
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 font-mono dark:border-gray-600"
          />
        </div>
      </div>
    </Card>
  );
}
