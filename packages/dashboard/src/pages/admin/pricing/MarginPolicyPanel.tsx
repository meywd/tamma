/**
 * Story 34-9 (AC3) — the admin MARGIN-POLICY panel.
 *
 * Reads `GET /api/admin/pricing/margins` and versions a policy via
 * `PUT /api/admin/pricing/margins` (AdminPricingEndpoints.cs). Margin writes are
 * immutable-versioned server-side (a PUT supersedes the prior active row for the
 * same scope/refKey); the response surfaces the new policy + supersededPolicyId
 * (the `PRICING.MARGIN.UPDATED` DCB result). Client-side the form validates that
 * at least one of markup-multiplier / fixed-$-per-1M is set before POST — the
 * server remains authoritative.
 *
 * This is platform-internal economics (the markup knobs) — only ever rendered on
 * the platform-owner admin surface, never on the tenant `/api/pricing/*` routes.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import {
  adminPricingApi,
  MARGIN_SCOPES,
  type MarginPolicyDto,
} from '../../../services/admin/admin-pricing-client.js';

export function MarginPolicyPanel(): JSX.Element {
  const [policies, setPolicies] = useState<MarginPolicyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [scope, setScope] = useState<string>('global');
  const [refKey, setRefKey] = useState('');
  const [markup, setMarkup] = useState('');
  const [fixed, setFixed] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const [result, setResult] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const resp = await adminPricingApi.listMargins();
      setPolicies(resp.policies);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load margin policies');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const submit = async (): Promise<void> => {
    setFormError(null);
    setResult(null);
    // AC3 — at least one knob must be set (mirrors the server's has-knob guard).
    if (markup.trim() === '' && fixed.trim() === '') {
      setFormError('Set at least one of markup multiplier or fixed $/1M.');
      return;
    }
    if (scope !== 'global' && refKey.trim() === '') {
      setFormError('A plan/provider-scoped policy requires a ref key.');
      return;
    }
    setSaving(true);
    try {
      const resp = await adminPricingApi.versionMargin({
        scope,
        refKey: scope === 'global' ? null : refKey.trim(),
        markupMultiplier: markup.trim() === '' ? null : Number(markup),
        fixedUsdPer1M: fixed.trim() === '' ? null : Number(fixed),
      });
      setResult(
        resp.supersededPolicyId
          ? `Policy updated (superseded prior ${resp.supersededPolicyId.slice(0, 8)}…).`
          : 'Policy created.',
      );
      setMarkup('');
      setFixed('');
      setRefKey('');
      await load();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-4">
      <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-4 space-y-3 dark:bg-gray-800 dark:border-gray-700">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">
          Add / update margin policy
        </h3>
        {formError !== null && (
          <div role="alert" className="p-2 text-sm text-red-700 bg-red-50 rounded">
            {formError}
          </div>
        )}
        {result !== null && (
          <div role="status" className="p-2 text-sm text-green-800 bg-green-50 rounded">
            {result}
          </div>
        )}
        <div className="grid grid-cols-2 md:grid-cols-5 gap-2 items-end">
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Scope
            <select
              aria-label="Margin scope"
              value={scope}
              onChange={(e) => setScope(e.target.value)}
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            >
              {MARGIN_SCOPES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </label>
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Ref key {scope === 'global' ? '(n/a)' : ''}
            <input
              aria-label="Margin ref key"
              value={refKey}
              disabled={scope === 'global'}
              onChange={(e) => setRefKey(e.target.value)}
              placeholder={scope === 'provider' ? 'anthropic' : scope === 'plan' ? 'pro' : ''}
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm disabled:bg-gray-100 dark:bg-gray-900 dark:border-gray-600 dark:disabled:bg-gray-700"
            />
          </label>
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Markup ×
            <input
              aria-label="Markup multiplier"
              value={markup}
              inputMode="decimal"
              onChange={(e) => setMarkup(e.target.value)}
              placeholder="1.5"
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            />
          </label>
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Fixed $/1M
            <input
              aria-label="Fixed usd per 1m"
              value={fixed}
              inputMode="decimal"
              onChange={(e) => setFixed(e.target.value)}
              placeholder="0.50"
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            />
          </label>
          <button
            type="button"
            disabled={saving}
            onClick={() => void submit()}
            className="px-3 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Save policy'}
          </button>
        </div>
      </div>

      {error !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          {error}
        </div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">Loading margin policies…</p>
      ) : policies.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">
          No margin policies yet. The global policy is the pricing safety net — add it above.
        </p>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden dark:bg-gray-800 dark:border-gray-700">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-600 dark:bg-gray-900 dark:text-gray-400">
              <tr>
                <th className="px-3 py-2 text-left">Scope</th>
                <th className="px-3 py-2 text-left">Ref key</th>
                <th className="px-3 py-2 text-right">Markup ×</th>
                <th className="px-3 py-2 text-right">Fixed $/1M</th>
                <th className="px-3 py-2 text-left">Effective from</th>
                <th className="px-3 py-2 text-left">Status</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 dark:divide-gray-700">
              {policies.map((p) => (
                <tr key={p.id} className="hover:bg-gray-50 dark:hover:bg-gray-700/40">
                  <td className="px-3 py-2 text-gray-900 dark:text-gray-100">{p.scope}</td>
                  <td className="px-3 py-2 font-mono text-xs text-gray-600 dark:text-gray-400">
                    {p.refKey ?? '—'}
                  </td>
                  <td className="px-3 py-2 text-right text-gray-700 dark:text-gray-300">
                    {p.markupMultiplier ?? '—'}
                  </td>
                  <td className="px-3 py-2 text-right text-gray-700 dark:text-gray-300">
                    {p.fixedUsdPer1M ?? '—'}
                  </td>
                  <td className="px-3 py-2 text-gray-500 text-xs dark:text-gray-400">
                    {new Date(p.effectiveFrom).toLocaleDateString()}
                  </td>
                  <td className="px-3 py-2">
                    <span
                      className={`inline-flex px-2 py-0.5 text-xs font-medium rounded ${
                        p.status === 'active'
                          ? 'bg-green-100 text-green-800'
                          : 'bg-gray-200 text-gray-600'
                      }`}
                    >
                      {p.status}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
