import { useCallback, useEffect, useState, type JSX } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  adminTenantsApi,
  AdminTenantApiError,
  type AdminTenantListItem,
  type ListTenantsFilters,
} from '../../../services/admin/admin-tenants-client.js';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import { TenantStatusBadge } from './components/TenantStatusBadge.js';

/**
 * Story 28-11 — platform-admin tenant roster. Replaces the previous
 * SSH-into-psql workflow for stuck-tenant investigation with a URL-synced
 * filterable list. Every filter ends up in `?status=…&plan=…&search=…`
 * so admins can paste links into incident tickets.
 */

const STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'All statuses' },
  { value: 'active', label: 'Active' },
  { value: 'provisioning', label: 'Provisioning' },
  { value: 'pending_verification', label: 'Pending verification' },
  { value: 'failed', label: 'Failed' },
  { value: 'deleting', label: 'Deleting' },
  { value: 'deleted', label: 'Deleted' },
];

const PLAN_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'All plans' },
  { value: 'free', label: 'Free' },
  { value: 'team', label: 'Team' },
  { value: 'enterprise', label: 'Enterprise' },
];

const PAGE_SIZE = 25;

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString();
}

function formatRelative(iso: string): string {
  const now = Date.now();
  const t = new Date(iso).getTime();
  const diffMs = now - t;
  const diffMins = Math.floor(diffMs / 60_000);
  if (diffMins < 1) return 'just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  const diffHours = Math.floor(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  const diffDays = Math.floor(diffHours / 24);
  if (diffDays < 30) return `${diffDays}d ago`;
  return formatDate(iso);
}

export function TenantsListPage(): JSX.Element {
  const [searchParams, setSearchParams] = useSearchParams();
  const [tenants, setTenants] = useState<AdminTenantListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const status = searchParams.get('status') ?? '';
  const plan = searchParams.get('plan') ?? '';
  const search = searchParams.get('search') ?? '';

  const load = useCallback(async (filters: ListTenantsFilters) => {
    setLoading(true);
    setError(null);
    try {
      const resp = await adminTenantsApi.list({ pageSize: PAGE_SIZE, ...filters });
      setTenants(resp.tenants);
      setTotal(resp.total);
      setPage(resp.page);
    } catch (e) {
      if (e instanceof AdminTenantApiError) {
        setError(`${e.message} (status ${e.status})`);
      } else {
        setError((e as Error).message);
      }
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const filters: ListTenantsFilters = {
      page: Number(searchParams.get('page') ?? 1),
      pageSize: PAGE_SIZE,
    };
    if (status) filters.status = status;
    if (plan) filters.plan = plan;
    if (search) filters.search = search;
    void load(filters);
  }, [load, status, plan, search, searchParams]);

  const updateFilter = (key: 'status' | 'plan' | 'search', value: string): void => {
    const next = new URLSearchParams(searchParams);
    if (value) next.set(key, value);
    else next.delete(key);
    next.delete('page'); // reset paging on any filter change
    setSearchParams(next);
  };

  const goToPage = (target: number): void => {
    const next = new URLSearchParams(searchParams);
    next.set('page', String(target));
    setSearchParams(next);
  };

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Tenants</h1>
        <div className="text-sm text-gray-500">
          {total.toLocaleString()} total
        </div>
      </div>

      {/* Filter bar */}
      <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-4 mb-6">
        <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <div>
            <label
              htmlFor="tenant-filter-search"
              className="block text-xs font-medium text-gray-600 mb-1"
            >
              Search (name or slug)
            </label>
            <input
              id="tenant-filter-search"
              type="search"
              value={search}
              onChange={(e) => updateFilter('search', e.target.value)}
              placeholder="acme, initech, …"
              className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label
              htmlFor="tenant-filter-status"
              className="block text-xs font-medium text-gray-600 mb-1"
            >
              Status
            </label>
            <select
              id="tenant-filter-status"
              value={status}
              onChange={(e) => updateFilter('status', e.target.value)}
              className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              {STATUS_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label
              htmlFor="tenant-filter-plan"
              className="block text-xs font-medium text-gray-600 mb-1"
            >
              Plan
            </label>
            <select
              id="tenant-filter-plan"
              value={plan}
              onChange={(e) => updateFilter('plan', e.target.value)}
              className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              {PLAN_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>
        </div>
      </div>

      {loading && tenants.length === 0 && (
        <div className="flex justify-center py-12">
          <LoadingSpinner size="lg" />
        </div>
      )}

      {error && (
        <div
          role="alert"
          className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-800 mb-4"
        >
          {error}
        </div>
      )}

      {!loading && tenants.length === 0 && !error && (
        <div className="text-center py-12 text-gray-500 bg-white rounded-lg border border-gray-200">
          <p className="text-lg mb-1">No tenants match these filters.</p>
          <p className="text-sm">Clear the filters or broaden the search.</p>
        </div>
      )}

      {tenants.length > 0 && (
        <>
          <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                    Tenant
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                    Status
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                    Plan
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                    Owner
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                    Last activity
                  </th>
                  <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                    Actions
                  </th>
                </tr>
              </thead>
              <tbody className="bg-white divide-y divide-gray-200">
                {tenants.map((t) => (
                  <tr key={t.id} className="hover:bg-gray-50">
                    <td className="px-4 py-3 whitespace-nowrap">
                      <div className="flex flex-col">
                        <span className="text-sm font-medium text-gray-900">
                          {t.name}
                        </span>
                        <code className="text-xs text-gray-400 font-mono">
                          {t.slug}
                        </code>
                      </div>
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap">
                      <TenantStatusBadge status={t.status} />
                      {t.failureReason && (
                        <div
                          title={t.failureReason}
                          className="text-xs text-red-600 mt-1 max-w-[16rem] truncate"
                        >
                          {t.failureReason}
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-700">
                      {t.planName ?? t.legacyPlan}
                      {t.planSlug && (
                        <span className="ml-1 text-xs text-gray-400 font-mono">
                          ({t.planSlug})
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-500">
                      {t.ownerEmail ?? '—'}
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-500">
                      {formatRelative(t.updatedAt)}
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap text-right">
                      <Link
                        to={`/admin/tenants/${t.id}`}
                        className="text-sm text-blue-600 hover:text-blue-800 font-medium"
                      >
                        View
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {totalPages > 1 && (
            <div className="flex items-center justify-between mt-4 text-sm text-gray-600">
              <span>
                Page {page} of {totalPages}
              </span>
              <div className="flex gap-2">
                <button
                  type="button"
                  disabled={page <= 1 || loading}
                  onClick={() => goToPage(page - 1)}
                  className="px-3 py-1 border border-gray-300 rounded-md bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  Previous
                </button>
                <button
                  type="button"
                  disabled={page >= totalPages || loading}
                  onClick={() => goToPage(page + 1)}
                  className="px-3 py-1 border border-gray-300 rounded-md bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  Next
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}
