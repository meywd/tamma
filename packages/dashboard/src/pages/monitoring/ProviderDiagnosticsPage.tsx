/**
 * Provider Diagnostics (Deep) — Story 23-6.
 *
 * Operator page for deep AI/LLM provider health & diagnostics: per-provider
 * latency percentiles (p50/p95/p99), error-class breakdown, token/cost
 * analytics, per-model usage and live circuit-breaker state.
 *
 * Data sources (both tenant-scoped, read-only):
 *   • `GET /api/providers/diagnostics/deep` — the Story 23-6 aggregation over
 *     the existing `provider_diagnostics` table (cost = the tenant's OWN spend,
 *     never a platform margin — Story 34-5 rule).
 *   • `GET /api/providers/health` — circuit-breaker state per provider.
 * Both are consumed through {@link useProviderDiagnostics}.
 *
 * The UI is built entirely on the Story 23-12 monitoring primitives
 * (MonitoringLayout, MetricGrid/MetricCard, LatencyBar, DataTable, StatusBadge,
 * EmptyState, ErrorBanner) and hooks (useTimeRange, useAutoRefresh). The route
 * is already `AdminGuard`-gated + lazy-mounted by Story 23-12; this module only
 * supplies the page body.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import { MetricGrid } from '../../components/monitoring/MetricGrid.js';
import { MetricCard } from '../../components/monitoring/MetricCard.js';
import { LatencyBar } from '../../components/monitoring/LatencyBar.js';
import { StatusBadge, type StatusKind } from '../../components/monitoring/StatusBadge.js';
import { DataTable, type DataTableColumn } from '../../components/monitoring/DataTable.js';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { useAutoRefresh } from '../../hooks/monitoring/useAutoRefresh.js';
import { useTimeRange } from '../../hooks/monitoring/useTimeRange.js';
import {
  useProviderDiagnostics,
  type ProviderDiagnosticSummary,
  type ProviderErrorClass,
  type ProviderModelUsage,
  type ProviderHealthEntry,
} from '../../hooks/monitoring/useProviderDiagnostics.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/providers');

const SELECT_CLASS =
  'rounded-md border border-gray-300 bg-white px-2 py-1.5 text-sm text-gray-700 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-200';

function formatUsd(value: number): string {
  if (value === 0) return '$0.00';
  return `$${value.toFixed(value < 1 ? 4 : 2)}`;
}

function formatInt(value: number): string {
  return value.toLocaleString();
}

function formatPct(fraction: number): string {
  return `${(fraction * 100).toFixed(1)}%`;
}

/**
 * Resolve the health badge kind for a provider. Prefers the live circuit-breaker
 * status; falls back to deriving from the observed error rate when the breaker
 * has never tracked the provider.
 */
function healthKind(
  health: ProviderHealthEntry | undefined,
  errorRate: number,
): { kind: StatusKind; label: string } {
  if (health) {
    switch (health.status) {
      case 'healthy':
        return { kind: 'healthy', label: 'Healthy' };
      case 'degraded':
        return { kind: 'degraded', label: 'Half-open' };
      case 'down':
        return { kind: 'down', label: 'Circuit open' };
      default:
        break;
    }
  }
  if (errorRate === 0) return { kind: 'healthy', label: 'Healthy' };
  if (errorRate < 0.5) return { kind: 'degraded', label: 'Degraded' };
  return { kind: 'down', label: 'Failing' };
}

const ERROR_COLUMNS: DataTableColumn<ProviderErrorClass>[] = [
  {
    key: 'errorClass',
    header: 'Error class',
    accessor: (r) => r.errorClass,
    render: (r) => <code className="text-xs">{r.errorClass}</code>,
    sortable: true,
  },
  { key: 'count', header: 'Count', accessor: (r) => r.count, align: 'right', sortable: true },
  {
    key: 'share',
    header: 'Share',
    accessor: (r) => r.share,
    render: (r) => formatPct(r.share),
    align: 'right',
    sortable: true,
  },
];

const MODEL_COLUMNS: DataTableColumn<ProviderModelUsage>[] = [
  { key: 'model', header: 'Model', accessor: (r) => r.model, sortable: true },
  { key: 'totalCalls', header: 'Calls', accessor: (r) => r.totalCalls, align: 'right', sortable: true },
  {
    key: 'successRate',
    header: 'Success',
    accessor: (r) => r.successRate,
    render: (r) => formatPct(r.successRate),
    align: 'right',
    sortable: true,
  },
  {
    key: 'avgLatencyMs',
    header: 'Avg ms',
    accessor: (r) => r.avgLatencyMs,
    render: (r) => Math.round(r.avgLatencyMs),
    align: 'right',
    sortable: true,
  },
  { key: 'totalTokens', header: 'Tokens', accessor: (r) => r.totalTokens, align: 'right', sortable: true },
  {
    key: 'totalCost',
    header: 'Cost',
    accessor: (r) => r.totalCost,
    render: (r) => formatUsd(r.totalCost),
    align: 'right',
    sortable: true,
  },
];

function ProviderCard({
  provider,
  health,
}: {
  provider: ProviderDiagnosticSummary;
  health: ProviderHealthEntry | undefined;
}): JSX.Element {
  const badge = healthKind(health, provider.errorRate);
  return (
    <section
      data-testid="provider-card"
      className="rounded-lg border border-gray-200 bg-white p-5 shadow-sm dark:border-gray-700 dark:bg-gray-800"
    >
      <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-3">
          <h2 className="text-lg font-semibold text-gray-900 dark:text-gray-100">
            {provider.providerKey}
          </h2>
          <StatusBadge status={badge.kind}>{badge.label}</StatusBadge>
        </div>
        <div className="flex items-center gap-4 text-sm text-gray-600 dark:text-gray-300">
          <span>
            <span className="font-medium">{formatInt(provider.totalCalls)}</span> calls
          </span>
          <span>
            <span className="font-medium">{formatPct(provider.successRate)}</span> ok
          </span>
          <span>
            <span className="font-medium">{formatUsd(provider.totalCost)}</span> spend
          </span>
        </div>
      </div>

      <div className="mb-4">
        <div className="mb-1 text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
          Latency (ms)
        </div>
        <LatencyBar
          p50={Math.round(provider.latency.p50)}
          p95={Math.round(provider.latency.p95)}
          p99={Math.round(provider.latency.p99)}
        />
      </div>

      <MetricGrid columns={4} className="mb-4">
        <MetricCard
          label="Errors"
          value={formatInt(provider.failureCount)}
          hint={formatPct(provider.errorRate)}
          tone={provider.failureCount > 0 ? 'red' : 'green'}
        />
        <MetricCard label="Avg latency" value={Math.round(provider.latency.avg)} unit="ms" />
        <MetricCard
          label="Tokens"
          value={formatInt(provider.totalTokens)}
          hint={`${formatInt(provider.inputTokens)} in / ${formatInt(provider.outputTokens)} out`}
        />
        <MetricCard label="Spend" value={formatUsd(provider.totalCost)} tone="blue" />
      </MetricGrid>

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
        <div>
          <h3 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
            Error classification
          </h3>
          {provider.errors.length === 0 ? (
            <EmptyState title="No errors" description="Every call in this window succeeded." />
          ) : (
            <DataTable
              columns={ERROR_COLUMNS}
              rows={provider.errors}
              getRowId={(r) => r.errorClass}
              pageSize={100}
              initialSort={{ key: 'count', direction: 'desc' }}
              filterable={false}
              emptyTitle="No errors"
            />
          )}
        </div>
        <div>
          <h3 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
            Model usage &amp; cost
          </h3>
          <DataTable
            columns={MODEL_COLUMNS}
            rows={provider.models}
            getRowId={(r) => r.model}
            pageSize={100}
            initialSort={{ key: 'totalCalls', direction: 'desc' }}
            filterable={false}
            emptyTitle="No model usage"
          />
        </div>
      </div>
    </section>
  );
}

export function ProviderDiagnosticsPage(): JSX.Element {
  const { preset, range, setPreset } = useTimeRange('24h');
  const { report, healthByKey, loading, error, lastUpdated, load } = useProviderDiagnostics();
  const [providerFilter, setProviderFilter] = useState('all');

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

  const providers = report?.providers ?? [];
  const providerKeys = useMemo(() => providers.map((p) => p.providerKey), [providers]);
  const visibleProviders = useMemo(
    () =>
      providerFilter === 'all'
        ? providers
        : providers.filter((p) => p.providerKey === providerFilter),
    [providers, providerFilter],
  );

  const overallErrorRate =
    report && report.totalCalls > 0 ? report.totalErrors / report.totalCalls : 0;
  const hasData = report !== null && providers.length > 0;

  return (
    <MonitoringLayout
      title={NAV.label}
      description="Deep AI-provider diagnostics — latency percentiles, error classification, token & cost analytics and circuit-breaker health for your tenant."
      loading={loading}
      lastUpdated={lastUpdated}
      onRefresh={trigger}
      autoRefreshInterval={autoRefresh.interval}
      onAutoRefreshChange={autoRefresh.setInterval}
      timeRange={preset}
      onTimeRangeChange={setPreset}
      showTimeRange
      actions={
        providerKeys.length > 0 ? (
          <label className="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
            <span className="sr-only">Provider</span>
            <select
              aria-label="Provider filter"
              value={providerFilter}
              onChange={(e) => setProviderFilter(e.target.value)}
              className={SELECT_CLASS}
            >
              <option value="all">All providers</option>
              {providerKeys.map((key) => (
                <option key={key} value={key}>
                  {key}
                </option>
              ))}
            </select>
          </label>
        ) : undefined
      }
    >
      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={trigger} />
        </div>
      )}

      {report && (
        <MetricGrid columns={4} className="mb-6">
          <MetricCard label="Total calls" value={formatInt(report.totalCalls)} />
          <MetricCard
            label="Error rate"
            value={formatPct(overallErrorRate)}
            tone={report.totalErrors > 0 ? 'red' : 'green'}
            hint={`${formatInt(report.totalErrors)} failed`}
          />
          <MetricCard label="Total tokens" value={formatInt(report.totalTokens)} />
          <MetricCard label="Total spend" value={formatUsd(report.totalCost)} tone="blue" />
        </MetricGrid>
      )}

      {!hasData && !loading && !error && (
        <EmptyState
          title="No provider activity"
          description="No AI-provider calls were recorded in the selected time range."
        />
      )}

      <div className="flex flex-col gap-6">
        {visibleProviders.map((provider) => (
          <ProviderCard
            key={provider.providerKey}
            provider={provider}
            health={healthByKey[provider.providerKey]}
          />
        ))}
      </div>
    </MonitoringLayout>
  );
}
