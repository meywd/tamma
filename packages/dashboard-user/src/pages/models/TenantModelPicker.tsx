/**
 * Story 46-3 — per-provider model picker for the tenant model-settings page.
 *
 * Mirrors the admin app's picker behaviours (46-2's ModelPicker in
 * packages/dashboard/src/pages/admin/providers/ — deliberately NOT shared
 * code, epic-45 sanctioned divergence / 46-3 D1; a future extraction story
 * should find both via this cross-link):
 *   - fetch-on-open: this component fetches when mounted, and the page only
 *     mounts it when a row is expanded (46-2 D1 restated);
 *   - client-side search over id + displayName;
 *   - current model pinned first, with a "no longer listed" marker when the
 *     envelope synthesized it (the server's `delisted` flag — a plain read);
 *   - deprecated entries marked and sorted last;
 *   - stale-cache and list-unavailable banners straight from the fail-soft
 *     envelope (epic D6 — the page is NEVER unusable);
 *   - free-text model id path for `modelsSupported: false` providers and as
 *     the fallback whenever the list is empty/unavailable;
 *   - save (PUT) surfacing the non-blocking pricingKnown warning (epic D3b);
 *   - "Use platform default" (DELETE) behind an inline confirm that names
 *     the fallback model from the roster row's server-computed
 *     `fallbackModel` (generic only when the server reports none);
 *   - 403 on either mutation downgrades the whole page via onForbidden —
 *     the SERVER is the RBAC enforcement, canEdit is cosmetic (D2).
 */

import { useEffect, useMemo, useState, type JSX } from 'react';
import {
  providerModelsApi,
  type ProviderModelEntry,
  type ProviderModelsResponse,
  type PutTenantProviderModelResponse,
} from '../../api/provider-models';
import { ApiError } from '../../api/client';

export interface TenantModelPickerProps {
  provider: string;
  displayName: string;
  modelsSupported: boolean;
  /** The row's currently-effective model (override or platform default). */
  effectiveModel: string | null;
  hasOverride: boolean;
  /**
   * The platform-default model this provider falls back to — the roster
   * row's server-computed `fallbackModel` (the skip-principal resolution;
   * available even while an override is active). null → nothing below the
   * override names a model, so the reset confirm stays generic.
   */
  platformDefaultModel: string | null;
  canEdit: boolean;
  onSaved: (resp: PutTenantProviderModelResponse) => void;
  onResetDone: () => void;
  onForbidden: () => void;
}

export function TenantModelPicker(props: TenantModelPickerProps): JSX.Element {
  const {
    provider,
    displayName,
    modelsSupported,
    effectiveModel,
    hasOverride,
    platformDefaultModel,
    canEdit,
    onSaved,
    onResetDone,
    onForbidden,
  } = props;

  const [envelope, setEnvelope] = useState<ProviderModelsResponse | null>(null);
  const [loading, setLoading] = useState(modelsSupported);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [selection, setSelection] = useState('');
  const [saving, setSaving] = useState(false);
  const [savedModel, setSavedModel] = useState<string | null>(null);
  const [saveWarning, setSaveWarning] = useState<string | null>(null);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const [confirmingReset, setConfirmingReset] = useState(false);
  const [resetting, setResetting] = useState(false);

  // Fetch-on-open: the page mounts this component only when the row is
  // expanded, so a mount IS an open (46-2 D1 restated for this app).
  useEffect(() => {
    if (!modelsSupported) return undefined;
    let cancelled = false;
    void (async () => {
      try {
        const res = await providerModelsApi.listProviderModels(provider);
        if (!cancelled) {
          setEnvelope(res);
          setLoading(false);
        }
      } catch (err) {
        // The server contract is always-200 (epic D6); this branch is for
        // network/auth failures. Fail soft: keep the free-text path usable.
        if (!cancelled) {
          setLoadError(err instanceof Error ? err.message : 'Failed to load the model list');
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [provider, modelsSupported]);

  const currentEntry = useMemo(
    () => envelope?.models.find((m) => m.current) ?? null,
    [envelope],
  );
  // The envelope states synthesis directly (`delisted: true` only on the
  // server-injected entry; absent/false = genuinely listed) — a plain read.
  const delisted = currentEntry?.delisted === true;

  // Non-current entries, search-filtered, deprecated sorted last (each half
  // keeps the server's order).
  const listed = useMemo(() => {
    if (envelope === null) return [];
    const q = search.trim().toLowerCase();
    const rest = envelope.models.filter((m) => !m.current);
    const filtered =
      q === ''
        ? rest
        : rest.filter(
            (m) =>
              m.id.toLowerCase().includes(q) ||
              (m.displayName !== null && m.displayName.toLowerCase().includes(q)),
          );
    return [...filtered.filter((m) => !m.deprecated), ...filtered.filter((m) => m.deprecated)];
  }, [envelope, search]);

  const listUnavailable =
    envelope !== null && !envelope.stale && envelope.errorCode !== null;
  const hasSelectableList = modelsSupported && loadError === null && listed.length > 0;

  const handleSave = async (): Promise<void> => {
    const model = selection.trim();
    if (model === '' || saving) return;
    setSaving(true);
    setMutationError(null);
    setSaveWarning(null);
    setSavedModel(null);
    try {
      const resp = await providerModelsApi.putProviderModel(provider, model);
      // Epic D3b — pricingKnown:false warns, never blocks.
      setSaveWarning(resp.pricingKnown ? null : resp.warning);
      setSavedModel(resp.model);
      onSaved(resp);
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        setMutationError('Your role can view models but not change them.');
        onForbidden();
      } else if (err instanceof ApiError && err.status === 409) {
        setMutationError('This provider is disabled by the platform.');
      } else {
        setMutationError(err instanceof Error ? err.message : 'Failed to save the model');
      }
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async (): Promise<void> => {
    if (resetting) return;
    setResetting(true);
    setMutationError(null);
    setSaveWarning(null);
    setSavedModel(null);
    try {
      await providerModelsApi.deleteProviderModel(provider);
      setConfirmingReset(false);
      onResetDone();
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        setConfirmingReset(false);
        setMutationError('Your role can view models but not change them.');
        onForbidden();
      } else if (err instanceof ApiError && err.status === 404) {
        // Already gone — treat as reset.
        setConfirmingReset(false);
        onResetDone();
      } else {
        setMutationError(err instanceof Error ? err.message : 'Failed to remove the override');
      }
    } finally {
      setResetting(false);
    }
  };

  const entryLabel = (m: ProviderModelEntry): string =>
    m.displayName !== null && m.displayName !== '' && m.displayName !== m.id
      ? `${m.displayName} (${m.id})`
      : m.id;

  return (
    <div className="mt-3 border-t border-gray-100 pt-3 space-y-3">
      {/* ── Envelope banners (fail-soft: never an unusable panel) ── */}
      {loading && (
        <p role="status" className="text-sm text-gray-500">
          Loading model list…
        </p>
      )}
      {loadError !== null && (
        <div role="alert" className="p-2 text-sm text-amber-800 bg-amber-50 rounded">
          Couldn&apos;t load the live model list ({loadError}).
          {canEdit && ' You can still enter a model id below.'}
        </div>
      )}
      {envelope?.stale === true && (
        <div role="status" className="p-2 text-sm text-amber-800 bg-amber-50 rounded">
          Showing a cached model list — the live fetch failed
          {envelope.errorCode !== null && (
            <>
              {' '}
              (<code>{envelope.errorCode}</code>)
            </>
          )}
          .
        </div>
      )}
      {listUnavailable && (
        <div role="status" className="p-2 text-sm text-amber-800 bg-amber-50 rounded">
          The provider&apos;s model list is unavailable
          {envelope !== null && envelope.errorCode !== null && (
            <>
              {' '}
              (<code>{envelope.errorCode}</code>)
            </>
          )}
          .{canEdit && ' Enter a model id below.'}
        </div>
      )}
      {!modelsSupported && (
        <p className="text-sm text-gray-500">
          This provider does not publish a model list.
          {canEdit && ' Enter the model id to use.'}
        </p>
      )}

      {/* ── Current model, pinned first ── */}
      {(currentEntry !== null || effectiveModel !== null) && (
        <div className="flex items-center gap-2 text-sm">
          <span className="inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-blue-100 text-blue-800">
            current
          </span>
          <code className="text-gray-900">{currentEntry?.id ?? effectiveModel}</code>
          {delisted && (
            <span className="inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-amber-100 text-amber-800">
              no longer listed by the provider
            </span>
          )}
        </div>
      )}

      {/* ── Live list (search + entries) ── */}
      {modelsSupported && loadError === null && !loading && (
        <>
          <input
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search models…"
            aria-label={`Search ${displayName} models`}
            className="w-full px-2 py-1.5 text-sm border border-gray-300 rounded-md"
          />
          {hasSelectableList ? (
            <ul className="max-h-64 overflow-y-auto divide-y divide-gray-100 border border-gray-200 rounded-md">
              {listed.map((m) => (
                <li key={m.id}>
                  {canEdit ? (
                    <button
                      type="button"
                      onClick={() => setSelection(m.id)}
                      className={`w-full text-left px-3 py-1.5 text-sm hover:bg-gray-50 ${
                        selection === m.id ? 'bg-blue-50 text-blue-900' : 'text-gray-700'
                      }`}
                    >
                      {entryLabel(m)}
                      {m.deprecated && (
                        <span className="ml-2 inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-gray-200 text-gray-600">
                          deprecated
                        </span>
                      )}
                    </button>
                  ) : (
                    <span className="block px-3 py-1.5 text-sm text-gray-700">
                      {entryLabel(m)}
                      {m.deprecated && (
                        <span className="ml-2 inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-gray-200 text-gray-600">
                          deprecated
                        </span>
                      )}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          ) : (
            search.trim() !== '' && (
              <p className="text-sm text-gray-500">No models match your search.</p>
            )
          )}
        </>
      )}

      {/* ── Editor: model id input + save / reset (admins only — AC4) ── */}
      {canEdit ? (
        <div className="space-y-2">
          <label className="block text-sm">
            <span className="text-gray-700">Model id</span>
            <input
              type="text"
              value={selection}
              onChange={(e) => setSelection(e.target.value)}
              placeholder={effectiveModel ?? 'model id'}
              aria-label={`${displayName} model id`}
              className="mt-1 w-full px-2 py-1.5 text-sm border border-gray-300 rounded-md font-mono"
            />
          </label>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => {
                void handleSave();
              }}
              disabled={selection.trim() === '' || saving}
              className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
            >
              {saving ? 'Saving…' : 'Save override'}
            </button>
            {hasOverride && !confirmingReset && (
              <button
                type="button"
                onClick={() => setConfirmingReset(true)}
                className="px-3 py-1.5 text-sm font-medium text-gray-700 border border-gray-300 rounded-md hover:bg-gray-50"
              >
                Use platform default
              </button>
            )}
          </div>
          {confirmingReset && (
            <div className="p-3 bg-gray-50 border border-gray-200 rounded-md text-sm space-y-2">
              <p className="text-gray-700">
                Remove your override? {displayName} will fall back to the platform default
                {platformDefaultModel !== null && (
                  <>
                    {' '}
                    (<code>{platformDefaultModel}</code>)
                  </>
                )}
                .
              </p>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => {
                    void handleReset();
                  }}
                  disabled={resetting}
                  className="px-3 py-1.5 text-sm font-medium text-white bg-red-600 hover:bg-red-700 rounded-md disabled:opacity-50"
                >
                  {resetting ? 'Removing…' : 'Confirm'}
                </button>
                <button
                  type="button"
                  onClick={() => setConfirmingReset(false)}
                  className="px-3 py-1.5 text-sm text-gray-700 hover:text-gray-900"
                >
                  Cancel
                </button>
              </div>
            </div>
          )}
        </div>
      ) : (
        <p className="text-sm text-gray-500">
          Read-only — ask a tenant admin to change this model.
        </p>
      )}

      {/* ── Outcome messages ── */}
      {savedModel !== null && (
        <p role="status" className="text-sm text-green-700">
          Saved <code>{savedModel}</code> as your override.
        </p>
      )}
      {saveWarning !== null && (
        <div role="status" className="p-2 text-sm text-amber-800 bg-amber-50 rounded">
          {saveWarning}
        </div>
      )}
      {mutationError !== null && (
        <div role="alert" className="p-2 text-sm text-red-700 bg-red-50 rounded">
          {mutationError}
        </div>
      )}
    </div>
  );
}
