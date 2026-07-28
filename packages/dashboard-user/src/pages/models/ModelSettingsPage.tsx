/**
 * Story 46-3 — tenant model-settings page (/settings/models).
 *
 * One card per platform-ENABLED provider from
 * `GET /api/v1/agents/providers/models` (disabled providers are absent by
 * server contract — the tenant never sees the platform's off switch), each
 * showing: display name, a "Your key" indicator when the tenant holds a BYOK
 * key (presence metadata only), the effective model, and a two-state
 * provenance line — "Your override" vs "Platform default" (D3: tenants do
 * NOT see the platform's config/descriptor internals; the mapping lives in
 * ./provenance.ts, the page's sole text map).
 *
 * RBAC (D2): the SERVER's AgentManage gate is the enforcement — member PUT/
 * DELETE 403s. The client's canEdit is cosmetic: derived from the auth
 * payload's role when present ('owner'/'admin' edit, 'member' read-only; the
 * single-user sole user carries no membership role → '' → optimistic edit),
 * and downgraded page-wide when any mutation actually 403s.
 *
 * The picker (TenantModelPicker) mounts only when a row is expanded —
 * fetch-on-open, so 15 provider lists are never fetched up front (46-2 D1).
 *
 * Platform default (AC3): every roster row carries the server-computed
 * `fallbackModel` — what resolution would answer if the tenant override were
 * removed (skip-principal, computed through the 46-1 resolver) — so the
 * reset confirm always names it, even for rows that already had an override
 * when the page loaded. Generic wording only when the server reports null
 * (nothing below the override names a model).
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { useAuth } from '../../hooks/useAuth';
import {
  providerModelsApi,
  type PutTenantProviderModelResponse,
  type TenantProviderRosterRow,
} from '../../api/provider-models';
import { provenanceLabel } from './provenance';
import { TenantModelPicker } from './TenantModelPicker';

export function ModelSettingsPage(): JSX.Element {
  const { user } = useAuth();
  const role = user?.role ?? '';
  // Mirrors the server's AgentManage policy (owner/admin write; member 403).
  // '' = single-user sole user (no membership role) → optimistic edit; the
  // server stays authoritative and a real 403 flips `forbidden` below.
  const roleAllowsEdit = role === '' || role === 'owner' || role === 'admin';
  const [forbidden, setForbidden] = useState(false);
  const canEdit = roleAllowsEdit && !forbidden;

  const [rows, setRows] = useState<TenantProviderRosterRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const resp = await providerModelsApi.listProviderModelSettings();
      setRows(resp?.providers ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load provider model settings');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const handleSaved = useCallback((provider: string, resp: PutTenantProviderModelResponse) => {
    setRows((prev) =>
      prev.map((row) =>
        row.provider === provider
          ? { ...row, model: resp.model, source: resp.source, hasOverride: true }
          : row,
      ),
    );
  }, []);

  const handleResetDone = useCallback(
    async (provider: string) => {
      // Re-resolve just this row so the provenance line and effective model
      // reflect the platform default the tenant fell back to.
      try {
        const resolved = await providerModelsApi.getProviderModel(provider);
        setRows((prev) =>
          prev.map((row) =>
            row.provider === provider
              ? {
                  ...row,
                  model: resolved.model,
                  source: resolved.source,
                  hasOverride: resolved.override !== null,
                  fallbackModel: resolved.fallbackModel,
                }
              : row,
          ),
        );
      } catch {
        // Single-row refresh failed — fall back to a full reload.
        void load();
      }
    },
    [load],
  );

  const handleForbidden = useCallback(() => {
    setForbidden(true);
  }, []);

  return (
    <div className="space-y-6 max-w-3xl">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Model settings</h1>
        <p className="mt-1 text-sm text-gray-500">
          Choose the model your organisation uses for each provider. Model lists come live
          from the provider — using your own key where you have one.
        </p>
        {!canEdit && (
          <p role="status" className="mt-2 text-sm text-amber-700">
            {forbidden
              ? 'Your role can view models but not change them.'
              : 'You have read-only access; ask a tenant admin to change models.'}
          </p>
        )}
      </div>

      {error !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          <p>{error}</p>
          <button
            type="button"
            onClick={() => {
              void load();
            }}
            className="mt-2 px-3 py-1.5 text-sm font-medium text-red-700 border border-red-300 rounded-md hover:bg-red-100"
          >
            Retry
          </button>
        </div>
      )}

      {loading ? (
        <p role="status" className="text-sm text-gray-500">
          Loading providers…
        </p>
      ) : error === null && rows.length === 0 ? (
        <div className="p-6 bg-gray-50 border border-dashed border-gray-300 rounded text-sm text-gray-600">
          No providers are enabled for your organisation.
        </div>
      ) : (
        <ul className="space-y-3">
          {rows.map((row) => {
            const isOpen = expanded === row.provider;
            return (
              <li key={row.provider} className="bg-white border border-gray-200 rounded-md p-4">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="flex items-center gap-2">
                      <span className="text-sm font-semibold text-gray-900">
                        {row.displayName}
                      </span>
                      {row.byokKeyPresent && (
                        <span
                          className="inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-green-100 text-green-800"
                          title="Model lists and calls for this provider use your own API key"
                        >
                          Your key
                        </span>
                      )}
                    </div>
                    <div className="mt-1 text-sm text-gray-700">
                      {row.model !== null ? (
                        <code>{row.model}</code>
                      ) : (
                        <span className="text-gray-400">No model set</span>
                      )}
                    </div>
                    <div className="mt-0.5 text-xs text-gray-500">
                      {provenanceLabel(row.source)}
                    </div>
                  </div>
                  <button
                    type="button"
                    onClick={() => setExpanded(isOpen ? null : row.provider)}
                    aria-expanded={isOpen}
                    className="px-3 py-1.5 text-sm font-medium text-gray-700 border border-gray-300 rounded-md hover:bg-gray-50 shrink-0"
                  >
                    {isOpen ? 'Close' : canEdit ? 'Change model' : 'View models'}
                  </button>
                </div>

                {isOpen && (
                  <TenantModelPicker
                    provider={row.provider}
                    displayName={row.displayName}
                    modelsSupported={row.modelsSupported}
                    effectiveModel={row.model}
                    hasOverride={row.hasOverride}
                    platformDefaultModel={row.fallbackModel}
                    canEdit={canEdit}
                    onSaved={(resp) => handleSaved(row.provider, resp)}
                    onResetDone={() => {
                      void handleResetDone(row.provider);
                    }}
                    onForbidden={handleForbidden}
                  />
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
