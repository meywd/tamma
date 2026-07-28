/**
 * ModelPicker (Story 46-2)
 *
 * The per-provider settings panel opened from a roster row:
 *
 * - Providers with `modelsSupported: true` get a searchable listbox fed by
 *   `GET /api/admin/providers/:key/models`, fetched when the panel opens
 *   (plan D1 — never for all rows on page load). The entry flagged
 *   `current: true` is pre-selected and pinned at the top (even when the
 *   search filter would exclude it); deprecated entries are marked and sorted
 *   after non-deprecated ones; a stale response renders a cache banner; an
 *   empty list degrades to the banner plus a free-text input (epic D6 — the
 *   admin is never dead-ended).
 * - Providers with `modelsSupported: false` get a plain text input pre-filled
 *   with the current model (epic D4).
 *
 * Save PUTs `{defaultModel}` and surfaces the D3b `pricingKnown: false`
 * warning non-blockingly; Reset DELETEs the platform row behind an inline
 * confirm step. Search is client-side filtering of the fetched list (plan D3).
 */

import { useEffect, useMemo, useState, type JSX } from 'react';
import {
  providersAdminApi,
  type ProviderModelEntry,
  type ProviderModelsResponse,
  type ProviderStatusRow,
  type PutProviderSettingsResponse,
} from '../../../services/admin/providers-api-client.js';
import { ResetConfirm } from './ResetConfirm.js';

export interface ModelPickerProps {
  row: ProviderStatusRow;
  /** PUTs `{defaultModel}` and re-fetches the roster; resolves to the PUT response. */
  onSave: (key: string, model: string) => Promise<PutProviderSettingsResponse>;
  /** DELETEs the platform settings row and re-fetches the roster. */
  onReset: (key: string) => Promise<void>;
}

export function ModelPicker({ row, onSave, onReset }: ModelPickerProps): JSX.Element {
  const [models, setModels] = useState<ProviderModelsResponse | null>(null);
  const [loadingModels, setLoadingModels] = useState(row.modelsSupported);
  const [fetchError, setFetchError] = useState<string | null>(null);

  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState(row.currentModel ?? '');
  const [freeText, setFreeText] = useState(row.currentModel ?? '');

  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [pricingWarning, setPricingWarning] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const [confirmingReset, setConfirmingReset] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [resetError, setResetError] = useState<string | null>(null);

  // Fetch-on-open (plan D1): this component mounts only when a row is
  // expanded, so the effect below IS the on-open fetch.
  useEffect(() => {
    if (!row.modelsSupported) return;
    let cancelled = false;
    void (async () => {
      setLoadingModels(true);
      setFetchError(null);
      try {
        const response = await providersAdminApi.listProviderModels(row.key);
        if (!cancelled) setModels(response);
      } catch (err) {
        // The endpoint is fail-soft (always 200 for a known key — epic D6);
        // this catch covers transport failures. Degrade to free text below.
        if (!cancelled) {
          setFetchError(err instanceof Error ? err.message : 'Failed to load the model list');
        }
      } finally {
        if (!cancelled) setLoadingModels(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [row.key, row.modelsSupported]);

  const currentEntry = useMemo(
    () => models?.models.find((m) => m.current) ?? null,
    [models],
  );

  // "No longer listed by the provider": the envelope states the fact —
  // BuildModelsResponse flags the entry it synthesized (`delisted: true`;
  // absent/false on genuinely-listed entries), so this is a plain flag read.
  const currentDelisted = currentEntry?.delisted === true;

  // Pinned-current + filter + deprecated-last ordering. The current entry is
  // always visible (pinned) regardless of the search filter.
  const orderedEntries = useMemo(() => {
    if (models == null) return [];
    const query = search.trim().toLowerCase();
    const matches = (entry: ProviderModelEntry): boolean =>
      query === '' ||
      entry.id.toLowerCase().includes(query) ||
      (entry.displayName ?? '').toLowerCase().includes(query);
    const others = models.models.filter((m) => !m.current);
    const fresh = others.filter((m) => !m.deprecated && matches(m));
    const deprecated = others.filter((m) => m.deprecated && matches(m));
    return [...(currentEntry ? [currentEntry] : []), ...fresh, ...deprecated];
  }, [models, search, currentEntry]);

  const listedOthers = useMemo(
    () => (models?.models ?? []).filter((m) => !m.current).length,
    [models],
  );

  // Free-text applies to providers without a models endpoint, to a transport
  // failure, and to an empty live list — never a dead end (epic D6/D4).
  const useFreeText =
    !row.modelsSupported ||
    fetchError != null ||
    (models != null && listedOthers === 0);

  const modelToSave = useFreeText ? freeText.trim() : selected;

  const handleSave = async (): Promise<void> => {
    setSaving(true);
    setSaveError(null);
    setPricingWarning(null);
    setSaved(false);
    try {
      const response = await onSave(row.key, modelToSave);
      setSaved(true);
      if (!response.pricingKnown && response.warning != null) {
        setPricingWarning(response.warning);
      }
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'Failed to save provider settings');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async (): Promise<void> => {
    setResetting(true);
    setResetError(null);
    try {
      await onReset(row.key);
      setConfirmingReset(false);
    } catch (err) {
      setResetError(err instanceof Error ? err.message : 'Failed to reset provider settings');
    } finally {
      setResetting(false);
    }
  };

  const entryLabel = (entry: ProviderModelEntry): string => {
    let label = entry.displayName != null ? `${entry.displayName} — ${entry.id}` : entry.id;
    if (entry.current) {
      label += ' (current)';
      if (currentDelisted) label += ' — no longer listed by the provider';
    }
    if (entry.deprecated) label += ' (deprecated)';
    return label;
  };

  const staleOrEmptyBanner = (): JSX.Element | null => {
    if (models != null && models.stale) {
      return (
        <div
          data-testid="models-stale-banner"
          className="mb-3 border border-amber-300 bg-amber-50 rounded-md p-2 text-xs text-amber-900 dark:bg-amber-950 dark:border-amber-800 dark:text-amber-200"
        >
          shown from cache — the provider could not be reached
          {models.errorCode != null && ` (${models.errorCode})`}
        </div>
      );
    }
    if (models != null && listedOthers === 0) {
      return (
        <div
          data-testid="models-empty-banner"
          className="mb-3 border border-amber-300 bg-amber-50 rounded-md p-2 text-xs text-amber-900 dark:bg-amber-950 dark:border-amber-800 dark:text-amber-200"
        >
          the provider&apos;s model list could not be fetched
          {models.errorCode != null && ` (${models.errorCode})`} — enter a model id below
        </div>
      );
    }
    if (fetchError != null) {
      return (
        <div
          data-testid="models-fetch-error"
          className="mb-3 border border-red-300 bg-red-50 rounded-md p-2 text-xs text-red-700 dark:bg-red-950 dark:border-red-800 dark:text-red-300"
        >
          could not load the model list ({fetchError}) — enter a model id below
        </div>
      );
    }
    return null;
  };

  return (
    <div
      data-testid={`model-picker-${row.key}`}
      className="bg-gray-50 border border-gray-200 rounded-md p-4 dark:bg-gray-900 dark:border-gray-700"
    >
      <h3 className="text-sm font-semibold text-gray-900 mb-3 dark:text-gray-100">
        Default model — {row.displayName}
      </h3>

      {loadingModels ? (
        <div data-testid="models-loading" className="text-sm text-gray-500 py-4 dark:text-gray-400">
          Loading model list…
        </div>
      ) : (
        <>
          {staleOrEmptyBanner()}

          {!useFreeText && models != null && (
            <div className="mb-3">
              <input
                type="text"
                aria-label="Filter models"
                placeholder="Filter models…"
                data-testid="model-search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                className="w-full mb-2 px-3 py-1.5 text-sm border border-gray-300 rounded-md dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
              />
              <select
                size={10}
                aria-label="Model list"
                data-testid="model-listbox"
                value={selected}
                onChange={(e) => setSelected(e.target.value)}
                className="w-full text-sm font-mono border border-gray-300 rounded-md dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
              >
                {orderedEntries.map((entry) => (
                  <option key={entry.id} value={entry.id} data-testid={`model-option-${entry.id}`}>
                    {entryLabel(entry)}
                  </option>
                ))}
              </select>
            </div>
          )}

          {useFreeText && (
            <div className="mb-3">
              <label
                htmlFor={`model-free-text-${row.key}`}
                className="block text-xs font-medium text-gray-600 mb-1 dark:text-gray-400"
              >
                Model
              </label>
              <input
                id={`model-free-text-${row.key}`}
                type="text"
                data-testid="model-free-text"
                value={freeText}
                onChange={(e) => setFreeText(e.target.value)}
                className="w-full px-3 py-1.5 text-sm font-mono border border-gray-300 rounded-md dark:bg-gray-800 dark:border-gray-600 dark:text-gray-100"
              />
              {!row.modelsSupported && (
                <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
                  This provider does not expose a model list — enter the model id to use.
                </p>
              )}
            </div>
          )}

          {pricingWarning != null && (
            <div
              data-testid="pricing-warning"
              className="mb-3 border border-amber-300 bg-amber-50 rounded-md p-2 text-xs text-amber-900 dark:bg-amber-950 dark:border-amber-800 dark:text-amber-200"
            >
              {pricingWarning}
            </div>
          )}

          {saveError != null && (
            <div
              data-testid="save-error"
              className="mb-3 border border-red-300 bg-red-50 rounded-md p-2 text-xs text-red-700 dark:bg-red-950 dark:border-red-800 dark:text-red-300"
            >
              {saveError}
            </div>
          )}

          {saved && saveError == null && (
            <div
              data-testid="save-success"
              className="mb-3 text-xs text-green-700 dark:text-green-400"
            >
              Saved.
            </div>
          )}

          <div className="flex gap-2">
            <button
              type="button"
              data-testid="model-save"
              disabled={saving || modelToSave === ''}
              onClick={() => void handleSave()}
              className="px-3 py-1.5 text-xs font-medium text-white bg-blue-600 rounded-md hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
            <button
              type="button"
              data-testid="model-reset"
              disabled={saving || resetting}
              onClick={() => {
                setResetError(null);
                setConfirmingReset(true);
              }}
              className="px-3 py-1.5 text-xs font-medium text-gray-700 border border-gray-300 bg-white rounded-md hover:bg-gray-50 dark:bg-gray-800 dark:text-gray-300 dark:border-gray-600"
            >
              Reset to default
            </button>
          </div>

          {confirmingReset && (
            <ResetConfirm
              providerDisplayName={row.displayName}
              busy={resetting}
              error={resetError}
              onConfirm={() => void handleReset()}
              onCancel={() => setConfirmingReset(false)}
            />
          )}
        </>
      )}
    </div>
  );
}
