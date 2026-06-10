import { useCallback, useEffect, useState, type JSX } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  adminTenantsApi,
  AdminTenantApiError,
  type AdminTenantDetailResponse,
} from '../../../services/admin/admin-tenants-client.js';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import { TenantStatusBadge } from './components/TenantStatusBadge.js';
import { EventsTimeline } from './components/EventsTimeline.js';
import { DestructiveActions } from './components/DestructiveActions.js';

/**
 * Story 28-11 — platform-admin tenant detail page. Surfaces the Epic-28
 * shadow columns (Status, PlanId, KekVersion, FailureReason,
 * DeleteRequestedAt) as a read-only summary, renders the recent
 * platform_events feed, and composes the destructive-actions controls
 * gated by the server-computed action gate.
 *
 * Plan change is handled inline here (small form + save) rather than
 * in a separate modal because the tenant can only be on one plan at a
 * time and the list of plans is tiny.
 */

export function TenantDetailPage(): JSX.Element {
  const { tenantId } = useParams<{ tenantId: string }>();
  const [data, setData] = useState<AdminTenantDetailResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [planDraft, setPlanDraft] = useState<string>('');
  const [planSaving, setPlanSaving] = useState(false);
  const [planMessage, setPlanMessage] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!tenantId) return;
    setLoading(true);
    setError(null);
    try {
      const resp = await adminTenantsApi.getDetail(tenantId);
      setData(resp);
      setPlanDraft(resp.tenant.planSlug ?? '');
    } catch (e) {
      if (e instanceof AdminTenantApiError) {
        setError(`${e.message} (status ${e.status})`);
      } else {
        setError((e as Error).message);
      }
    } finally {
      setLoading(false);
    }
  }, [tenantId]);

  useEffect(() => {
    void load();
  }, [load]);

  const savePlan = async (): Promise<void> => {
    if (!data || !tenantId) return;
    // Map slug → PlanId by looking up the current plan list. Keep the
    // lookup hard-coded here because the plan catalogue is small + stable;
    // a future spike can hydrate this from /api/admin/plans.
    const PLAN_ID_BY_SLUG: Record<string, string> = {
      free: 'aaaaaaaa-0000-0000-0000-000000000001',
      team: 'aaaaaaaa-0000-0000-0000-000000000002',
      enterprise: 'aaaaaaaa-0000-0000-0000-000000000003',
    };
    const planId = PLAN_ID_BY_SLUG[planDraft];
    if (!planId) {
      setPlanMessage(`Unknown plan slug "${planDraft}"`);
      return;
    }
    setPlanSaving(true);
    setPlanMessage(null);
    try {
      const resp = await adminTenantsApi.updatePlan(tenantId, planId);
      setPlanMessage(resp.message);
      await load();
    } catch (e) {
      if (e instanceof AdminTenantApiError) {
        setPlanMessage(`${e.message} (status ${e.status})`);
      } else {
        setPlanMessage((e as Error).message);
      }
    } finally {
      setPlanSaving(false);
    }
  };

  if (loading && !data) {
    return (
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="space-y-4">
        <Link
          to="/admin/tenants"
          className="text-sm text-blue-600 hover:text-blue-800 dark:text-blue-400"
        >
          ← Back to tenants
        </Link>
        <div
          role="alert"
          className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-800 dark:bg-red-950 dark:border-red-800"
        >
          {error}
        </div>
      </div>
    );
  }

  if (!data) return <></>;

  const { tenant, recentEvents, actions } = data;

  return (
    <div className="space-y-6">
      <div>
        <Link
          to="/admin/tenants"
          className="text-sm text-blue-600 hover:text-blue-800 dark:text-blue-400"
        >
          ← Back to tenants
        </Link>
      </div>

      {/* Header */}
      <div className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 dark:bg-gray-800 dark:border-gray-700">
        <div className="flex items-start justify-between">
          <div>
            <h1 className="text-2xl font-bold text-gray-900 dark:text-gray-100">{tenant.name}</h1>
            <p className="text-sm text-gray-500 mt-1 dark:text-gray-400">
              <code className="font-mono">{tenant.slug}</code> ·{' '}
              <code className="font-mono text-xs">{tenant.id}</code>
            </p>
          </div>
          <TenantStatusBadge status={tenant.status} />
        </div>
        {tenant.failureReason && (
          <div className="mt-4 bg-red-50 border border-red-200 rounded-md p-3 text-sm text-red-800 dark:bg-red-950 dark:border-red-800">
            <strong>Last failure reason:</strong> {tenant.failureReason}
          </div>
        )}
      </div>

      {/* Metadata grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <dl className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 space-y-3 dark:bg-gray-800 dark:border-gray-700">
          <div className="flex justify-between">
            <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">Type</dt>
            <dd className="text-sm text-gray-900 dark:text-gray-100">{tenant.type}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">Created</dt>
            <dd className="text-sm text-gray-900 dark:text-gray-100">
              {new Date(tenant.createdAt).toLocaleString()}
            </dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">Last activity</dt>
            <dd className="text-sm text-gray-900 dark:text-gray-100">
              {new Date(tenant.updatedAt).toLocaleString()}
            </dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">Owner email</dt>
            <dd className="text-sm text-gray-900 dark:text-gray-100">
              {tenant.ownerEmail ?? (
                <span className="text-gray-400 italic dark:text-gray-500">Unknown</span>
              )}
            </dd>
          </div>
          {tenant.deleteRequestedAt && (
            <div className="flex justify-between">
              <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">
                Delete requested at
              </dt>
              <dd className="text-sm text-gray-900 dark:text-gray-100">
                {new Date(tenant.deleteRequestedAt).toLocaleString()}
              </dd>
            </div>
          )}
        </dl>

        <dl className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 space-y-3 dark:bg-gray-800 dark:border-gray-700">
          <div className="flex justify-between">
            <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">Plan</dt>
            <dd className="text-sm text-gray-900 dark:text-gray-100">
              {tenant.planName ?? tenant.legacyPlan}
              {tenant.planSlug && (
                <span className="ml-1 text-xs text-gray-400 font-mono dark:text-gray-500">
                  ({tenant.planSlug})
                </span>
              )}
            </dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">
              Encrypted connection string
            </dt>
            <dd className="text-sm text-gray-900 dark:text-gray-100">
              {tenant.hasEncryptedConnectionString ? (
                <span className="text-green-700 dark:text-green-300">Present</span>
              ) : (
                <span className="text-gray-400 italic dark:text-gray-500">None</span>
              )}
            </dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-sm font-medium text-gray-500 dark:text-gray-400">KEK version</dt>
            <dd className="text-sm text-gray-900 font-mono dark:text-gray-100">
              {tenant.kekVersion ?? '—'}
            </dd>
          </div>
        </dl>
      </div>

      {/* Plan change */}
      <section
        aria-labelledby="plan-change-heading"
        className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 dark:bg-gray-800 dark:border-gray-700"
      >
        <h2 id="plan-change-heading" className="text-lg font-semibold text-gray-900 mb-3 dark:text-gray-100">
          Change plan
        </h2>
        <p className="text-sm text-gray-600 mb-4 dark:text-gray-400">
          Changes the billable plan immediately. Emits <code>PLAN.UPDATED</code>{' '}
          to the audit log.
        </p>
        <div className="flex items-end gap-3">
          <div className="flex-1 max-w-xs">
            <label
              htmlFor="plan-select"
              className="block text-xs font-medium text-gray-600 mb-1 dark:text-gray-400"
            >
              Target plan
            </label>
            <select
              id="plan-select"
              value={planDraft}
              onChange={(e) => setPlanDraft(e.target.value)}
              disabled={!actions.canChangePlan || planSaving}
              className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-gray-100 disabled:cursor-not-allowed dark:bg-gray-800 dark:border-gray-600"
            >
              <option value="free">Free</option>
              <option value="team">Team</option>
              <option value="enterprise">Enterprise</option>
            </select>
          </div>
          <button
            type="button"
            disabled={
              !actions.canChangePlan
              || planSaving
              || planDraft === tenant.planSlug
            }
            onClick={() => void savePlan()}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {planSaving ? 'Saving…' : 'Save plan'}
          </button>
        </div>
        {!actions.canChangePlan && (
          <p className="text-xs text-gray-500 mt-2 italic dark:text-gray-400">
            Plan changes are gated when the tenant is deleting/deleted.
          </p>
        )}
        {planMessage && (
          <div className="mt-3 bg-blue-50 border border-blue-200 rounded-md text-sm text-blue-800 px-3 py-2 dark:bg-blue-950 dark:text-blue-200 dark:border-blue-800">
            {planMessage}
          </div>
        )}
      </section>

      {/* Destructive actions */}
      <section
        aria-labelledby="tenant-actions-heading"
        className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 dark:bg-gray-800 dark:border-gray-700"
      >
        <h2
          id="tenant-actions-heading"
          className="text-lg font-semibold text-gray-900 mb-3 dark:text-gray-100"
        >
          Tenant actions
        </h2>
        <DestructiveActions
          tenant={tenant}
          actions={actions}
          onActionComplete={() => void load()}
        />
      </section>

      {/* Events timeline */}
      <section
        aria-labelledby="tenant-events-heading"
        className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 dark:bg-gray-800 dark:border-gray-700"
      >
        <div className="flex items-center justify-between mb-3">
          <h2
            id="tenant-events-heading"
            className="text-lg font-semibold text-gray-900 dark:text-gray-100"
          >
            Recent platform events
          </h2>
          <button
            type="button"
            onClick={() => void load()}
            disabled={loading}
            className="text-sm text-blue-600 hover:text-blue-800 font-medium disabled:opacity-50 dark:text-blue-400"
          >
            Refresh
          </button>
        </div>
        <EventsTimeline events={recentEvents} />
      </section>
    </div>
  );
}
