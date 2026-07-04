/**
 * Story 34-9 (AC1) — the platform-owner PRICING OVERVIEW panel.
 *
 * Consumes the single read-only rollup `GET /api/admin/pricing/overview`
 * (AdminPricingDashboardEndpoints.cs): headline totals, the full plan catalog
 * with live per-plan active-tenant counts, and the margin-config summary. This
 * surface reveals platform-internal economics (list prices + margin knobs) and
 * is gated `PlatformOwnerAccess` server-side — it is only ever rendered inside
 * the admin dashboard's AdminGuard chain.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import {
  adminPricingApi,
  type PricingOverviewResponse,
} from '../../../services/admin/admin-pricing-client.js';

function usd(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  return `$${value.toFixed(2)}`;
}

function StatCard({ label, value }: { label: string; value: number | string }): JSX.Element {
  return (
    <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-4 dark:bg-gray-800 dark:border-gray-700">
      <div className="text-2xl font-bold text-gray-900 dark:text-gray-100">{value}</div>
      <div className="text-xs uppercase tracking-wide text-gray-500 mt-1 dark:text-gray-400">
        {label}
      </div>
    </div>
  );
}

export function PricingOverviewPanel(): JSX.Element {
  const [data, setData] = useState<PricingOverviewResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await adminPricingApi.getOverview());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load pricing overview');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (loading) {
    return <p className="text-sm text-gray-500 dark:text-gray-400">Loading pricing overview…</p>;
  }

  if (error !== null) {
    return (
      <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
        {error}
      </div>
    );
  }

  if (data === null) {
    return <p className="text-sm text-gray-500 dark:text-gray-400">No pricing data.</p>;
  }

  const { plans, margins, totals } = data;

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
        <StatCard label="Active plans" value={totals.activePlanCount} />
        <StatCard label="Custom plans" value={totals.customPlanCount} />
        <StatCard label="Deprecated" value={totals.deprecatedPlanCount} />
        <StatCard label="Active assignments" value={totals.totalActiveAssignments} />
        <StatCard label="Plans in use" value={totals.plansWithActiveAssignments} />
      </div>

      <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-4 dark:bg-gray-800 dark:border-gray-700">
        <h3 className="text-sm font-semibold text-gray-900 mb-3 dark:text-gray-100">
          Margin configuration
        </h3>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
          <div>
            <div className="text-gray-500 dark:text-gray-400">Global markup ×</div>
            <div className="font-medium text-gray-900 dark:text-gray-100">
              {margins.globalMarkupMultiplier ?? '—'}
            </div>
          </div>
          <div>
            <div className="text-gray-500 dark:text-gray-400">Global fixed $/1M</div>
            <div className="font-medium text-gray-900 dark:text-gray-100">
              {usd(margins.globalFixedUsdPer1M)}
            </div>
          </div>
          <div>
            <div className="text-gray-500 dark:text-gray-400">Active policies</div>
            <div className="font-medium text-gray-900 dark:text-gray-100">
              {margins.activePolicyCount}
            </div>
          </div>
          <div>
            <div className="text-gray-500 dark:text-gray-400">Plan / provider scoped</div>
            <div className="font-medium text-gray-900 dark:text-gray-100">
              {margins.planScopedPolicyCount} / {margins.providerScopedPolicyCount}
            </div>
          </div>
        </div>
      </div>

      <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden dark:bg-gray-800 dark:border-gray-700">
        <div className="flex items-center justify-between px-4 py-3 border-b border-gray-200 dark:border-gray-700">
          <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">Plan catalog</h3>
          <button
            type="button"
            onClick={() => void load()}
            className="text-xs px-2 py-1 border border-gray-300 rounded hover:bg-gray-50 dark:border-gray-600"
          >
            Refresh
          </button>
        </div>
        {plans.length === 0 ? (
          <p className="p-6 text-sm text-gray-500 text-center dark:text-gray-400">
            No plans in the catalog yet. Create one from the Plans tab.
          </p>
        ) : (
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-600 dark:bg-gray-900 dark:text-gray-400">
              <tr>
                <th className="px-3 py-2 text-left">Plan</th>
                <th className="px-3 py-2 text-left">Slug</th>
                <th className="px-3 py-2 text-right">Version</th>
                <th className="px-3 py-2 text-left">Status</th>
                <th className="px-3 py-2 text-left">Interval</th>
                <th className="px-3 py-2 text-right">Recurring</th>
                <th className="px-3 py-2 text-right">Active tenants</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 dark:divide-gray-700">
              {plans.map((p) => (
                <tr key={p.planId} className="hover:bg-gray-50 dark:hover:bg-gray-700/40">
                  <td className="px-3 py-2 text-gray-900 dark:text-gray-100">
                    {p.displayName}
                    {p.isCustom && (
                      <span className="ml-2 inline-flex px-1.5 py-0.5 text-[10px] font-medium rounded bg-purple-100 text-purple-800">
                        custom
                      </span>
                    )}
                  </td>
                  <td className="px-3 py-2 font-mono text-xs text-gray-600 dark:text-gray-400">
                    {p.slug}
                  </td>
                  <td className="px-3 py-2 text-right text-gray-700 dark:text-gray-300">
                    v{p.version}
                  </td>
                  <td className="px-3 py-2">
                    <StatusPill status={p.status} />
                  </td>
                  <td className="px-3 py-2 text-gray-700 dark:text-gray-300">{p.billingInterval}</td>
                  <td className="px-3 py-2 text-right text-gray-700 dark:text-gray-300">
                    {usd(p.recurringUsd)}
                  </td>
                  <td className="px-3 py-2 text-right text-gray-900 font-medium dark:text-gray-100">
                    {p.activeTenantCount}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

export function StatusPill({ status }: { status: string }): JSX.Element {
  const colors: Record<string, string> = {
    active: 'bg-green-100 text-green-800',
    deprecated: 'bg-gray-200 text-gray-700',
    draft: 'bg-yellow-100 text-yellow-800',
  };
  return (
    <span
      className={`inline-flex px-2 py-0.5 text-xs font-medium rounded ${colors[status] ?? 'bg-gray-100 text-gray-700'}`}
    >
      {status}
    </span>
  );
}
