/**
 * Configuration Audit — Story 23-4.
 *
 * An operator page that audits the tenant's effective CONFIGURATION (providers,
 * prompt/convention overrides, plan/entitlements) alongside a config-change
 * history (who changed what, when). Like System Health (Story 23-1) it adds NO
 * backend surface — {@link useConfigAudit} COMPOSES existing read-only,
 * tenant-scoped endpoints on the client:
 *   • `GET /api/providers/health`         — configured providers (metadata only)
 *   • `GET /api/prompts`                  — prompt overrides (diff vs defaults)
 *   • `GET /api/conventions`              — conventions + `isOverride`
 *   • `GET /api/pricing/entitlements`     — own plan + entitlement limits
 *   • `GET /api/v1/orgs/{tenantId}/audit` — Epic-37 curated, redacted audit
 *                                           (config-relevant categories only)
 *
 * SECURITY: never renders a secret value (the raw-settings `/api/config/providers`
 * blob is deliberately not read; audit rows are redacted server-side and their
 * payload is never surfaced) and never surfaces a cost / margin / sell-price
 * figure (entitlements carries none; the platform price-book is not touched).
 *
 * The route is already `AdminGuard`-gated + lazy-mounted by Story 23-12; the
 * curated audit read additionally requires tenant-admin server-side, and with no
 * active tenant it is skipped entirely (fail-closed — never cross-tenant).
 * This module only supplies the page body, on the Story 23-12 primitives.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import { MetricGrid } from '../../components/monitoring/MetricGrid.js';
import { MetricCard } from '../../components/monitoring/MetricCard.js';
import { StatusBadge } from '../../components/monitoring/StatusBadge.js';
import { DataTable, type DataTableColumn } from '../../components/monitoring/DataTable.js';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { useAutoRefresh } from '../../hooks/monitoring/useAutoRefresh.js';
import { useTimeRange } from '../../hooks/monitoring/useTimeRange.js';
import {
  useConfigAudit,
  severityTone,
  CONFIG_AUDIT_CATEGORIES,
  type ProviderConfigRow,
  type OverrideRow,
  type EntitlementRow,
  type ConfigChangeRow,
} from '../../hooks/monitoring/useConfigAudit.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/config');

const SELECT_CLASS =
  'rounded-md border border-gray-300 bg-white px-2 py-1.5 text-sm text-gray-700 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-200';

function formatInt(value: number): string {
  return value.toLocaleString();
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

function formatLimit(value: number | null): string {
  if (value == null) return '—';
  if (value < 0) return 'Unlimited';
  return formatInt(value);
}

// ── Column definitions for the effective-config + change-history tables ─────

const PROVIDER_COLUMNS: DataTableColumn<ProviderConfigRow>[] = [
  { key: 'providerKey', header: 'Provider', accessor: (r) => r.providerKey, sortable: true },
  {
    key: 'status',
    header: 'Status',
    accessor: (r) => r.label,
    render: (r) => <StatusBadge status={r.kind}>{r.label}</StatusBadge>,
    sortable: true,
  },
];

const OVERRIDE_COLUMNS: DataTableColumn<OverrideRow>[] = [
  {
    key: 'scope',
    header: 'Kind',
    accessor: (r) => r.scope,
    render: (r) => (
      <StatusBadge status={r.scope === 'prompt' ? 'blue' : 'gray'} showDot={false}>
        {r.scope}
      </StatusBadge>
    ),
    sortable: true,
  },
  { key: 'role', header: 'Role', accessor: (r) => r.role, sortable: true },
  { key: 'action', header: 'Action', accessor: (r) => r.action, sortable: true },
  {
    key: 'source',
    header: 'Source',
    accessor: (r) => r.source,
    render: (r) => <code className="text-xs">{r.source}</code>,
    sortable: true,
  },
];

const ENTITLEMENT_COLUMNS: DataTableColumn<EntitlementRow>[] = [
  {
    key: 'metricKey',
    header: 'Entitlement',
    accessor: (r) => r.metricKey,
    render: (r) => <code className="text-xs">{r.metricKey}</code>,
    sortable: true,
  },
  {
    key: 'limitValue',
    header: 'Limit',
    accessor: (r) => r.limitValue ?? -1,
    render: (r) => formatLimit(r.limitValue),
    align: 'right',
    sortable: true,
  },
  {
    key: 'currentUsage',
    header: 'Used',
    accessor: (r) => r.currentUsage ?? -1,
    render: (r) => (r.currentUsage == null ? '—' : formatInt(r.currentUsage)),
    align: 'right',
    sortable: true,
  },
  {
    key: 'remaining',
    header: 'Remaining',
    accessor: (r) => r.remaining ?? -1,
    render: (r) =>
      r.remaining == null ? (
        '—'
      ) : (
        <span className={r.isOver ? 'font-medium text-red-600 dark:text-red-400' : undefined}>
          {formatInt(r.remaining)}
        </span>
      ),
    align: 'right',
    sortable: true,
  },
  { key: 'period', header: 'Period', accessor: (r) => r.period ?? '—', sortable: true },
];

const CHANGE_COLUMNS: DataTableColumn<ConfigChangeRow>[] = [
  {
    key: 'occurredAt',
    header: 'When',
    accessor: (r) => r.occurredAt,
    render: (r) => <span className="whitespace-nowrap">{formatTime(r.occurredAt)}</span>,
    sortable: true,
  },
  { key: 'actor', header: 'Who', accessor: (r) => r.actor, sortable: true },
  {
    key: 'actionCode',
    header: 'What',
    accessor: (r) => r.actionCode,
    render: (r) => <code className="text-xs">{r.actionCode}</code>,
    sortable: true,
  },
  {
    key: 'category',
    header: 'Category',
    accessor: (r) => r.category,
    render: (r) => (
      <StatusBadge status="gray" showDot={false}>
        {r.category}
      </StatusBadge>
    ),
    sortable: true,
  },
  { key: 'target', header: 'Target', accessor: (r) => r.target, sortable: true },
  {
    key: 'severity',
    header: 'Severity',
    accessor: (r) => r.severity,
    render: (r) => <StatusBadge status={severityTone(r.severity)}>{r.severity}</StatusBadge>,
    sortable: true,
  },
  {
    key: 'outcome',
    header: 'Outcome',
    accessor: (r) => r.outcome,
    render: (r) => (
      <StatusBadge status={r.outcome === 'success' ? 'green' : 'red'} showDot={false}>
        {r.outcome}
      </StatusBadge>
    ),
    sortable: true,
  },
];

function SectionHeading({ children }: { children: string }): JSX.Element {
  return (
    <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">{children}</h2>
  );
}

export function ConfigAuditPage(): JSX.Element {
  const { preset, range, setPreset } = useTimeRange('24h');
  const { summary, loading, error, lastUpdated, load } = useConfigAudit();
  const [categoryFilter, setCategoryFilter] = useState<string>('all');

  // Stable trigger that always reads the latest time-range window, so the
  // interval timer / manual refresh / preset change reuse it without re-arming.
  const loadRef = useRef<() => void>(() => {});
  useEffect(() => {
    loadRef.current = () => {
      void load({ start: range.start, end: range.end });
    };
  }, [load, range]);
  const trigger = useCallback(() => loadRef.current(), []);

  const autoRefresh = useAutoRefresh(trigger, {
    storageKey: NAV.storageKey,
    defaultInterval: null,
  });

  // Run on mount and whenever the time-range preset changes.
  useEffect(() => {
    trigger();
  }, [trigger, preset]);

  const providers = summary?.providers ?? [];
  const overrides = summary?.overrides ?? [];
  const entitlements = summary?.entitlements ?? [];
  const changes = summary?.changes ?? [];

  const visibleChanges = useMemo(
    () =>
      categoryFilter === 'all'
        ? changes
        : changes.filter((c) => c.category.toLowerCase() === categoryFilter),
    [changes, categoryFilter],
  );

  const planLabel = useMemo(() => {
    if (!summary?.plan) return '—';
    if (summary.plan.isCustom) return 'Custom';
    return summary.plan.planVersion != null ? `v${summary.plan.planVersion}` : 'Assigned';
  }, [summary]);

  return (
    <MonitoringLayout
      title={NAV.label}
      description="Audit the effective tenant configuration — providers, prompt & convention overrides, plan/entitlements — and the who/what/when history of configuration changes."
      loading={loading}
      lastUpdated={lastUpdated}
      onRefresh={trigger}
      autoRefreshInterval={autoRefresh.interval}
      onAutoRefreshChange={autoRefresh.setInterval}
      timeRange={preset}
      onTimeRangeChange={setPreset}
      showTimeRange
      actions={
        <label className="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
          <span className="sr-only">Change category</span>
          <select
            aria-label="Change category filter"
            value={categoryFilter}
            onChange={(e) => setCategoryFilter(e.target.value)}
            className={SELECT_CLASS}
          >
            <option value="all">All config changes</option>
            {CONFIG_AUDIT_CATEGORIES.map((c) => (
              <option key={c} value={c}>
                {c}
              </option>
            ))}
          </select>
        </label>
      }
    >
      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={trigger} />
        </div>
      )}

      {/* Effective config summary */}
      {summary && (
        <MetricGrid columns={4} className="mb-6">
          <MetricCard
            label="Providers configured"
            value={formatInt(providers.length)}
            hint={`${formatInt(summary.providerHealthy)} healthy`}
          />
          <MetricCard
            label="Prompt overrides"
            value={formatInt(summary.promptOverrideCount)}
            hint="vs system defaults"
            tone={summary.promptOverrideCount > 0 ? 'blue' : 'gray'}
          />
          <MetricCard
            label="Convention overrides"
            value={formatInt(summary.conventionOverrideCount)}
            hint={`${formatInt(summary.conventionTotal)} resolved`}
            tone={summary.conventionOverrideCount > 0 ? 'blue' : 'gray'}
          />
          <MetricCard
            label="Plan"
            value={planLabel}
            hint={`${formatInt(entitlements.length)} entitlement limits`}
          />
        </MetricGrid>
      )}

      {/* Effective configuration detail */}
      <div className="mb-6 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <section aria-label="Providers configured">
          <SectionHeading>Providers configured</SectionHeading>
          {summary && !summary.sources.providers ? (
            <EmptyState title="Unavailable" description="Provider configuration could not be read." />
          ) : providers.length === 0 ? (
            <EmptyState
              title="No providers configured"
              description="No AI providers are currently configured for this tenant."
            />
          ) : (
            <DataTable
              columns={PROVIDER_COLUMNS}
              rows={providers}
              getRowId={(r) => r.providerKey}
              pageSize={10}
              filterable={false}
              initialSort={{ key: 'providerKey', direction: 'asc' }}
              emptyTitle="No providers configured"
            />
          )}
        </section>

        <section aria-label="Plan entitlements">
          <SectionHeading>Plan &amp; entitlement limits</SectionHeading>
          {summary && !summary.sources.entitlements ? (
            <EmptyState title="Unavailable" description="Plan / entitlements could not be read." />
          ) : entitlements.length === 0 ? (
            <EmptyState
              title="No entitlement limits"
              description="No resolved plan entitlements for this tenant."
            />
          ) : (
            <DataTable
              columns={ENTITLEMENT_COLUMNS}
              rows={entitlements}
              getRowId={(r) => r.metricKey}
              pageSize={10}
              filterable={false}
              initialSort={{ key: 'metricKey', direction: 'asc' }}
              emptyTitle="No entitlement limits"
            />
          )}
        </section>
      </div>

      {/* Overrides diffing from the shipped defaults */}
      <section className="mb-6" aria-label="Configuration overrides">
        <SectionHeading>Overrides (diff from system defaults)</SectionHeading>
        {summary && !summary.sources.prompts && !summary.sources.conventions ? (
          <EmptyState title="Unavailable" description="Overrides could not be read." />
        ) : overrides.length === 0 ? (
          <EmptyState
            title="No overrides"
            description="Every prompt and convention resolves to its shipped system default."
          />
        ) : (
          <DataTable
            columns={OVERRIDE_COLUMNS}
            rows={overrides}
            getRowId={(r) => r.id}
            pageSize={10}
            filterPlaceholder="Filter overrides…"
            initialSort={{ key: 'scope', direction: 'asc' }}
            emptyTitle="No overrides"
          />
        )}
      </section>

      {/* Config change history */}
      <section aria-label="Configuration change history">
        <SectionHeading>Configuration change history</SectionHeading>
        {summary?.changesNoTenant ? (
          <EmptyState
            title="No active tenant"
            description="Change history is scoped to a tenant; no active tenant is available."
          />
        ) : summary?.changesForbidden ? (
          <EmptyState
            title="Change history restricted"
            description="Viewing the configuration change history requires a tenant admin role."
          />
        ) : summary && !summary.sources.changes ? (
          <EmptyState title="Unavailable" description="The audit history could not be read." />
        ) : visibleChanges.length === 0 ? (
          <EmptyState
            title="No configuration changes"
            description="No configuration changes were recorded in the selected time range."
          />
        ) : (
          <DataTable
            columns={CHANGE_COLUMNS}
            rows={visibleChanges}
            getRowId={(r) => r.id}
            pageSize={15}
            filterPlaceholder="Filter changes…"
            initialSort={{ key: 'occurredAt', direction: 'desc' }}
            emptyTitle="No configuration changes"
          />
        )}
      </section>
    </MonitoringLayout>
  );
}
