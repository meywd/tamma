import { useState, useEffect, useCallback, type JSX } from 'react';
import type { SecurityConfig } from '@tamma/shared';
import { useSecurityConfig } from '../../../hooks/settings/useSecurityConfig.js';
import { Card } from '../../common/Card.js';
import { Toggle } from '../../common/Toggle.js';
import { Slider } from '../../common/Slider.js';
import { LoadingSpinner } from '../../common/LoadingSpinner.js';
import { BlockedPatternsEditor } from './BlockedPatternsEditor.js';

export function SecuritySettingsPanel(): JSX.Element | null {
  const { config, loading, error, save } = useSecurityConfig();
  const [draft, setDraft] = useState<SecurityConfig | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    if (config && !draft) {
      setDraft({ ...config });
    }
  }, [config, draft]);

  const handleSave = useCallback(async () => {
    if (!draft) return;
    setSaving(true);
    setSaveError(null);
    try {
      await save(draft);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  }, [draft, save]);

  if (loading && !draft) {
    return <LoadingSpinner />;
  }

  if (error && !draft) {
    return <div className="text-red-600 text-sm">{error}</div>;
  }

  if (!draft) return null;

  const hasChanges = JSON.stringify(draft) !== JSON.stringify(config);

  return (
    <div className="space-y-6">
      <Card
        title="Content & Action Controls"
        actions={
          <button
            onClick={handleSave}
            disabled={!hasChanges || saving}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {saving ? 'Saving...' : 'Save Changes'}
          </button>
        }
      >
        {saveError && <div className="mb-4 text-sm text-red-600">{saveError}</div>}
        <Toggle
          label="Sanitize Content"
          description="Sanitize content before passing to agents"
          checked={draft.sanitizeContent ?? false}
          onChange={(v) => setDraft({ ...draft, sanitizeContent: v })}
        />
        <Toggle
          label="Validate URLs"
          description="Validate URLs before fetching"
          checked={draft.validateUrls ?? false}
          onChange={(v) => setDraft({ ...draft, validateUrls: v })}
        />
        <Toggle
          label="Gate Actions"
          description="Require approval for dangerous actions"
          checked={draft.gateActions ?? false}
          onChange={(v) => setDraft({ ...draft, gateActions: v })}
        />
        <Slider
          label="Max Fetch Size"
          value={draft.maxFetchSizeBytes ?? 10_485_760}
          min={0}
          max={104_857_600}
          step={1_048_576}
          onChange={(v) => setDraft({ ...draft, maxFetchSizeBytes: v })}
          formatValue={(v) => `${(v / 1_048_576).toFixed(0)} MB`}
        />
      </Card>

      <Card title="Blocked Command Patterns">
        <BlockedPatternsEditor
          patterns={draft.blockedCommandPatterns ?? []}
          onChange={(patterns) => setDraft({ ...draft, blockedCommandPatterns: patterns })}
        />
      </Card>
    </div>
  );
}
