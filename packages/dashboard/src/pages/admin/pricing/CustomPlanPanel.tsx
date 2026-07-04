/**
 * Story 34-9 (AC5) — the admin CUSTOM-PLAN panel.
 *
 * Mints an `IsCustom` plan bound to exactly one tenant via
 * `POST /api/admin/pricing/plans/custom` and assigns it to that tenant via
 * `adminTenantsApi.updatePlan(tenantId, planId)` (34-4 — assignment is NOT
 * duplicated here). Custom plans are visually flagged and are excluded from the
 * public catalog by construction (the server rejects a public custom plan with
 * 400 — the `makePublic` guard is never set true by this UI). The panel lists
 * existing custom plans via `GET /api/admin/pricing/plans?isCustom=true`.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import {
  adminPricingApi,
  METRIC_KEYS,
  type PlanSnapshot,
  type PlanEntitlementBody,
} from '../../../services/admin/admin-pricing-client.js';
import { adminTenantsApi } from '../../../services/admin/admin-tenants-client.js';

interface EntitlementRow {
  metricKey: string;
  limit: string;
}

export function CustomPlanPanel(): JSX.Element {
  const [plans, setPlans] = useState<PlanSnapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [tenantId, setTenantId] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [billingInterval, setBillingInterval] = useState('monthly');
  const [recurringUsd, setRecurringUsd] = useState('0');
  const [entitlements, setEntitlements] = useState<EntitlementRow[]>([
    { metricKey: METRIC_KEYS[0], limit: '' },
  ]);
  const [assignAfterMint, setAssignAfterMint] = useState(true);
  const [formError, setFormError] = useState<string | null>(null);
  const [result, setResult] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const resp = await adminPricingApi.listPlans({ isCustom: true });
      setPlans(resp.plans);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load custom plans');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const toEntitlementBodies = (): PlanEntitlementBody[] =>
    entitlements.map((e) => ({
      metricKey: e.metricKey,
      limitValue: e.limit.trim() === '' ? null : Number(e.limit),
      period: 'monthly',
      overageMode: 'block',
    }));

  const submit = async (): Promise<void> => {
    setFormError(null);
    setResult(null);
    if (tenantId.trim() === '') {
      setFormError('Tenant ID is required to bind a custom plan.');
      return;
    }
    if (displayName.trim() === '') {
      setFormError('Display name is required.');
      return;
    }
    // Fix 3: a non-empty limit MUST parse to a finite number. Only a blank means
    // "unlimited" (null). A typo like "10O0" → NaN must be a VALIDATION ERROR,
    // never silently coerced to null/unlimited.
    const invalidLimit = entitlements.find(
      (e) => e.limit.trim() !== '' && !Number.isFinite(Number(e.limit)),
    );
    if (invalidLimit) {
      setFormError(
        `Entitlement limit "${invalidLimit.limit}" for ${invalidLimit.metricKey} is not a number. Leave blank for unlimited.`,
      );
      return;
    }
    setSaving(true);
    try {
      const plan = await adminPricingApi.mintCustomPlan({
        tenantId: tenantId.trim(),
        displayName: displayName.trim(),
        billingInterval,
        entitlements: toEntitlementBodies(),
        prices: [
          {
            pricingMode: 'platform_provided',
            recurringUsd: Number(recurringUsd) || 0,
            seatUsd: 0,
          },
        ],
        // Never public — a custom plan must never surface in the public catalog.
      });

      let message = `Minted custom plan ${plan.slug} (v${plan.version}).`;
      if (assignAfterMint) {
        await adminTenantsApi.updatePlan(tenantId.trim(), plan.planId);
        message += ` Assigned to tenant ${tenantId.trim().slice(0, 8)}…`;
      }
      setResult(message);
      setDisplayName('');
      setRecurringUsd('0');
      await load();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Mint failed');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-4">
      <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-4 space-y-3 dark:bg-gray-800 dark:border-gray-700">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">
          Mint bespoke enterprise plan
        </h3>
        <p className="text-xs text-gray-500 dark:text-gray-400">
          A custom plan is bound to one tenant and never appears in the public catalog.
        </p>
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

        <div className="grid grid-cols-1 md:grid-cols-4 gap-2 items-end">
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Tenant ID
            <input
              aria-label="Tenant ID"
              value={tenantId}
              onChange={(e) => setTenantId(e.target.value)}
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            />
          </label>
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Display name
            <input
              aria-label="Custom plan display name"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            />
          </label>
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Billing interval
            <select
              aria-label="Custom billing interval"
              value={billingInterval}
              onChange={(e) => setBillingInterval(e.target.value)}
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            >
              <option value="monthly">monthly</option>
              <option value="annual">annual</option>
            </select>
          </label>
          <label className="flex flex-col text-xs text-gray-600 dark:text-gray-400">
            Recurring $
            <input
              aria-label="Custom recurring usd"
              value={recurringUsd}
              inputMode="decimal"
              onChange={(e) => setRecurringUsd(e.target.value)}
              className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
            />
          </label>
        </div>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-semibold uppercase text-gray-500 dark:text-gray-400">
              Entitlements
            </span>
            <button
              type="button"
              onClick={() => setEntitlements([...entitlements, { metricKey: METRIC_KEYS[0], limit: '' }])}
              className="text-xs px-2 py-0.5 border border-gray-300 rounded hover:bg-gray-50 dark:border-gray-600"
            >
              + Add
            </button>
          </div>
          {entitlements.map((r, i) => (
            <div key={i} className="grid grid-cols-3 gap-2 items-end">
              <label className="flex flex-col text-[10px] text-gray-500">
                Metric
                <select
                  aria-label={`Custom entitlement metric ${i}`}
                  value={r.metricKey}
                  onChange={(e) =>
                    setEntitlements(
                      entitlements.map((x, idx) =>
                        idx === i ? { ...x, metricKey: e.target.value } : x,
                      ),
                    )
                  }
                  className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
                >
                  {METRIC_KEYS.map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </select>
              </label>
              <label className="flex flex-col text-[10px] text-gray-500">
                Limit (blank=∞)
                <input
                  aria-label={`Custom entitlement limit ${i}`}
                  value={r.limit}
                  inputMode="numeric"
                  onChange={(e) =>
                    setEntitlements(
                      entitlements.map((x, idx) => (idx === i ? { ...x, limit: e.target.value } : x)),
                    )
                  }
                  className="mt-0.5 px-2 py-1 border border-gray-300 rounded text-sm dark:bg-gray-900 dark:border-gray-600"
                />
              </label>
              <button
                type="button"
                onClick={() => setEntitlements(entitlements.filter((_, idx) => idx !== i))}
                className="px-2 py-1 text-xs text-red-600 border border-red-200 rounded hover:bg-red-50"
              >
                Remove
              </button>
            </div>
          ))}
        </div>

        <div className="flex items-center justify-between">
          <label className="flex items-center gap-2 text-xs text-gray-600 dark:text-gray-400">
            <input
              type="checkbox"
              checked={assignAfterMint}
              onChange={(e) => setAssignAfterMint(e.target.checked)}
            />
            Assign to the tenant immediately after minting
          </label>
          <button
            type="button"
            disabled={saving}
            onClick={() => void submit()}
            className="px-3 py-1.5 text-sm bg-purple-600 text-white rounded hover:bg-purple-700 disabled:opacity-50"
          >
            {saving ? 'Minting…' : 'Mint custom plan'}
          </button>
        </div>
      </div>

      {error !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          {error}
        </div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">Loading custom plans…</p>
      ) : plans.length === 0 ? (
        <p className="text-sm text-gray-500 dark:text-gray-400">
          No custom plans yet. Mint one above to bind a bespoke price-book to a single tenant.
        </p>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden dark:bg-gray-800 dark:border-gray-700">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-600 dark:bg-gray-900 dark:text-gray-400">
              <tr>
                <th className="px-3 py-2 text-left">Plan</th>
                <th className="px-3 py-2 text-left">Slug</th>
                <th className="px-3 py-2 text-right">Version</th>
                <th className="px-3 py-2 text-left">Status</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 dark:divide-gray-700">
              {plans.map((p) => (
                <tr key={p.planId} className="hover:bg-gray-50 dark:hover:bg-gray-700/40">
                  <td className="px-3 py-2 text-gray-900 dark:text-gray-100">
                    {p.displayName}
                    <span className="ml-2 inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-purple-100 text-purple-800">
                      custom
                    </span>
                  </td>
                  <td className="px-3 py-2 font-mono text-xs text-gray-600 dark:text-gray-400">
                    {p.slug}
                  </td>
                  <td className="px-3 py-2 text-right text-gray-700 dark:text-gray-300">
                    v{p.version}
                  </td>
                  <td className="px-3 py-2 text-gray-700 dark:text-gray-300">{p.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
