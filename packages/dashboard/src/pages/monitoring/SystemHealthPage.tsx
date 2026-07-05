/**
 * System Health — Story 23-1.
 *
 * The Epic-23 monitoring LANDING page: an at-a-glance operator overview of
 * system health. It COMPOSES existing read-only sources (no new health
 * infrastructure) via {@link useSystemHealth}:
 *   • `GET /api/health` — API process liveness.
 *   • `GET /api/providers/health` — provider circuit-breaker state.
 *   • `GET /api/providers/diagnostics/deep` — throughput + error-rate (Story 23-6).
 *   • `GET /api/engine/events/query` — recent events, active runs, recent errors
 *     (Story 4-7).
 *
 * Deliberately health/status ONLY — no cost/token/margin figures are surfaced
 * here (those live behind Provider Diagnostics). The page links out to the
 * detail sections (Provider Diagnostics, Event Explorer) via the monitoring-nav
 * manifest.
 *
 * Built entirely on the Story 23-12 monitoring primitives (MonitoringLayout,
 * MetricGrid/MetricCard, StatusBadge, DataTable, EmptyState, ErrorBanner) and
 * hooks (useTimeRange, useAutoRefresh). The route is already `AdminGuard`-gated
 * + lazy-mounted by Story 23-12; this module only supplies the page body.
 */

import { useCallback, useEffect, useMemo, useRef, type JSX } from 'react';
import { Link } from 'react-router-dom';
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
  useSystemHealth,
  type ProviderHealthRow,
  type RecentEventRow,
  type ServiceStatus,
} from '../../hooks/monitoring/useSystemHealth.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/health');
const PROVIDERS_NAV = getMonitoringNavItem('/monitoring/providers');
const EVENTS_NAV = getMonitoringNavItem('/monitoring/events');

const DETAIL_LINK_CLASS =
  'text-xs font-medium text-blue-600 hover:text-blue-700 dark:text-blue-400 dark:hover:text-blue-300';

function formatInt(value: number): string {
  return value.toLocaleString();
}

function formatPct(fraction: number): string {
  return `${(fraction * 100).toFixed(1)}%`;
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** A single composed-source health tile. */
function ServiceCard({ service }: { service: ServiceStatus }): JSX.Element {
  return (
    <div
      data-testid="service-card"
      className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm dark:border-gray-700 dark:bg-gray-800"
    >
      <div className="flex items-center justify-between gap-2">
        <span className="text-sm font-semibold text-gray-900 dark:text-gray-100">
          {service.label}
        </span>
        <StatusBadge status={service.kind}>{service.kind}</StatusBadge>
      </div>
      <p className="mt-2 text-xs text-gray-500 dark:text-gray-400">{service.detail}</p>
    </div>
  );
}

const PROVIDER_COLUMNS: DataTableColumn<ProviderHealthRow>[] = [
  {
    key: 'providerKey',
    header: 'Provider',
    accessor: (r) => r.providerKey,
    sortable: true,
  },
  {
    key: 'status',
    header: 'Status',
    accessor: (r) => r.label,
    render: (r) => <StatusBadge status={r.kind}>{r.label}</StatusBadge>,
    sortable: true,
  },
  {
    key: 'failureCount',
    header: 'Failures',
    accessor: (r) => r.failureCount,
    align: 'right',
    sortable: true,
  },
  {
    key: 'lastFailure',
    header: 'Last failure',
    accessor: (r) => r.lastFailure ?? '',
    render: (r) => (r.lastFailure ? formatTime(r.lastFailure) : '—'),
    sortable: true,
  },
];

const EVENT_COLUMNS: DataTableColumn<RecentEventRow>[] = [
  {
    key: 'createdAt',
    header: 'Time',
    accessor: (r) => r.createdAt,
    render: (r) => <span className="whitespace-nowrap">{formatTime(r.createdAt)}</span>,
    sortable: true,
  },
  {
    key: 'type',
    header: 'Event',
    accessor: (r) => r.type,
    render: (r) => (
      <span className="inline-flex items-center gap-2">
        {r.isError && <StatusBadge status="down" showDot={false}>error</StatusBadge>}
        <code className="text-xs">{r.type}</code>
      </span>
    ),
    sortable: true,
  },
  {
    key: 'correlationId',
    header: 'Correlation',
    accessor: (r) => r.correlationId ?? '',
    render: (r) =>
      r.correlationId ? <code className="text-xs">{r.correlationId}</code> : '—',
    sortable: true,
  },
  {
    key: 'issueNumber',
    header: 'Issue',
    accessor: (r) => r.issueNumber ?? '',
    render: (r) => (r.issueNumber != null ? `#${r.issueNumber}` : '—'),
    align: 'right',
    sortable: true,
  },
];

export function SystemHealthPage(): JSX.Element {
  const { preset, range, setPreset } = useTimeRange('24h');
  const { summary, loading, error, lastUpdated, load } = useSystemHealth();

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

  const services = summary?.services ?? [];
  const providers = summary?.providers ?? [];
  const recentEvents = summary?.recentEvents ?? [];

  const errorRateTone = useMemo(() => {
    if (!summary || summary.totalCalls === 0) return undefined;
    return summary.totalErrors > 0 ? ('red' as const) : ('green' as const);
  }, [summary]);

  return (
    <MonitoringLayout
      title={NAV.label}
      description="At-a-glance operator overview — service status, AI-provider health, active runs, error rate and recent events for your tenant."
      loading={loading}
      lastUpdated={lastUpdated}
      onRefresh={trigger}
      autoRefreshInterval={autoRefresh.interval}
      onAutoRefreshChange={autoRefresh.setInterval}
      timeRange={preset}
      onTimeRangeChange={setPreset}
      showTimeRange
      actions={
        <div className="flex items-center gap-3">
          <Link to={PROVIDERS_NAV.to} className={DETAIL_LINK_CLASS}>
            Provider diagnostics →
          </Link>
          <Link to={EVENTS_NAV.to} className={DETAIL_LINK_CLASS}>
            Event explorer →
          </Link>
        </div>
      }
    >
      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={trigger} />
        </div>
      )}

      {/* Services / provider status roll-up */}
      <section className="mb-6" aria-label="Service status">
        <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">Services</h2>
        {services.length === 0 && !loading ? (
          <EmptyState title="No status yet" description="Health sources have not reported." />
        ) : (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {services.map((service) => (
              <ServiceCard key={service.key} service={service} />
            ))}
          </div>
        )}
      </section>

      {/* Key metrics */}
      {summary && (
        <MetricGrid columns={4} className="mb-6">
          <MetricCard
            label="Active runs"
            value={formatInt(summary.activeRuns)}
            hint="distinct correlation IDs (window)"
          />
          <MetricCard
            label="Error rate"
            value={summary.diagnosticsAvailable ? formatPct(summary.errorRate) : '—'}
            hint={
              summary.diagnosticsAvailable
                ? `${formatInt(summary.totalErrors)} of ${formatInt(summary.totalCalls)} provider calls`
                : 'diagnostics unavailable'
            }
            {...(errorRateTone ? { tone: errorRateTone } : {})}
          />
          <MetricCard
            label="Throughput"
            value={summary.diagnosticsAvailable ? formatInt(summary.totalCalls) : '—'}
            hint="provider calls in window"
            {...(summary.diagnosticsAvailable ? { unit: 'calls' } : {})}
          />
          <MetricCard
            label="Recent errors"
            value={formatInt(summary.recentErrorCount)}
            tone={summary.recentErrorCount > 0 ? 'red' : 'green'}
            hint={
              summary.recentEventTotal != null
                ? `${formatInt(summary.recentEventTotal)} events in window`
                : `in ${formatInt(recentEvents.length)} recent events`
            }
          />
        </MetricGrid>
      )}

      {/* Provider health detail */}
      <section className="mb-6" aria-label="Provider health">
        <div className="mb-2 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-gray-700 dark:text-gray-200">
            AI providers
            {summary && summary.providerTotal > 0 && (
              <span className="ml-2 text-xs font-normal text-gray-400 dark:text-gray-500">
                {summary.providerHealthy}/{summary.providerTotal} healthy
              </span>
            )}
          </h2>
          <Link to={PROVIDERS_NAV.to} className={DETAIL_LINK_CLASS}>
            View diagnostics →
          </Link>
        </div>
        {providers.length === 0 ? (
          <EmptyState
            title="No providers tracked"
            description="No AI-provider circuit-breaker state has been recorded for this tenant."
          />
        ) : (
          <DataTable
            columns={PROVIDER_COLUMNS}
            rows={providers}
            getRowId={(r) => r.providerKey}
            pageSize={10}
            filterable={false}
            initialSort={{ key: 'providerKey', direction: 'asc' }}
            emptyTitle="No providers tracked"
          />
        )}
      </section>

      {/* Recent events / errors */}
      <section aria-label="Recent events">
        <div className="mb-2 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-gray-700 dark:text-gray-200">Recent events</h2>
          <Link to={EVENTS_NAV.to} className={DETAIL_LINK_CLASS}>
            Open event explorer →
          </Link>
        </div>
        {recentEvents.length === 0 ? (
          <EmptyState
            title="No recent events"
            description="No audit events were recorded in the selected time range."
          />
        ) : (
          <DataTable
            columns={EVENT_COLUMNS}
            rows={recentEvents}
            getRowId={(r) => r.id}
            pageSize={10}
            filterPlaceholder="Filter events…"
            initialSort={{ key: 'createdAt', direction: 'desc' }}
            emptyTitle="No recent events"
          />
        )}
      </section>
    </MonitoringLayout>
  );
}
