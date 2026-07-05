/**
 * Event Store Explorer (Story 23-3).
 *
 * Operator page to browse / search / inspect the DCB `domain_events` stream.
 * Data source is the Story 4-7 keyset query API
 * (`GET /api/engine/events/query`, tenant-scoped, WorkflowsView) via
 * {@link useEventQuery}; the UI is built entirely on the Story 23-12 monitoring
 * primitives (MonitoringLayout, DataTable, TimeSeriesChart, StatusBadge,
 * EmptyState, ErrorBanner) and hooks (useTimeRange, useAutoRefresh).
 *
 * The route is already `AdminGuard`-gated and lazy-mounted by Story 23-12
 * (`pages/monitoring/index.tsx`); this module only supplies the page body.
 *
 * Frequency/summary aggregation and CSV/JSON export are computed client-side
 * over the loaded result set (no extra backend endpoint required).
 */

import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import {
  DataTable,
  type DataTableColumn,
} from '../../components/monitoring/DataTable.js';
import { StatusBadge } from '../../components/monitoring/StatusBadge.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { TimeSeriesChart } from '../../components/monitoring/TimeSeriesChart.js';
import { EventDetailPanel } from '../../components/monitoring/events/EventDetailPanel.js';
import {
  bucketOverTime,
  eventTone,
  eventsToCsv,
  eventsToJson,
  exportFilename,
  formatTagsPreview,
  groupByType,
  tagValue,
  triggerDownload,
} from '../../components/monitoring/events/event-explorer-utils.js';
import { useEventQuery, type DomainEventRow } from '../../hooks/monitoring/useEventQuery.js';
import { useAutoRefresh } from '../../hooks/monitoring/useAutoRefresh.js';
import { useTimeRange } from '../../hooks/monitoring/useTimeRange.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/events');
const PAGE_SIZES = [25, 50, 100] as const;

const INPUT_CLASS =
  'rounded-md border border-gray-300 px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100';

export function EventExplorerPage(): JSX.Element {
  const { preset, range, setPreset } = useTimeRange('24h');
  const eventQuery = useEventQuery();
  const { events, total, hasMore, loading, error, lastUpdated, runQuery, loadMore } = eventQuery;

  // Draft filter state (applied on Search / time-range change / auto-refresh).
  const [typeFilter, setTypeFilter] = useState('');
  const [typeMatch, setTypeMatch] = useState<'exact' | 'prefix'>('prefix');
  const [correlationId, setCorrelationId] = useState('');
  const [actor, setActor] = useState('');
  const [pageSize, setPageSize] = useState<number>(50);

  const [selected, setSelected] = useState<DomainEventRow | null>(null);
  const [showSummary, setShowSummary] = useState(false);

  // Build the current filter snapshot from the live form + time range.
  const buildFilters = useCallback(
    () => ({
      ...(typeFilter.trim() ? { type: typeFilter.trim(), typeMatch } : {}),
      ...(correlationId.trim() ? { correlationId: correlationId.trim() } : {}),
      ...(actor.trim() ? { actor: actor.trim() } : {}),
      from: range.start,
      to: range.end,
      limit: pageSize,
    }),
    [typeFilter, typeMatch, correlationId, actor, range, pageSize],
  );

  // Keep a stable trigger that always reads the latest filters, so it can be
  // used by the interval timer, the manual refresh, and the search button
  // without re-arming the auto-refresh interval on every keystroke.
  const searchRef = useRef<() => void>(() => {});
  useEffect(() => {
    searchRef.current = () => {
      void runQuery(buildFilters());
    };
  }, [runQuery, buildFilters]);

  const triggerSearch = useCallback(() => searchRef.current(), []);

  const autoRefresh = useAutoRefresh(triggerSearch, {
    storageKey: NAV.storageKey,
    defaultInterval: null,
  });

  // Run on mount and whenever the time-range preset or server page size changes.
  useEffect(() => {
    triggerSearch();
  }, [triggerSearch, preset, pageSize]);

  const summaryByType = useMemo(() => groupByType(events), [events]);
  const frequencySeries = useMemo(() => bucketOverTime(events), [events]);

  const handleExport = useCallback(
    (format: 'json' | 'csv') => {
      if (events.length === 0) return;
      const typeContext = typeFilter.trim() || undefined;
      if (format === 'json') {
        triggerDownload(exportFilename('json', typeContext), 'application/json', eventsToJson(events));
      } else {
        triggerDownload(exportFilename('csv', typeContext), 'text/csv', eventsToCsv(events));
      }
    },
    [events, typeFilter],
  );

  const columns: DataTableColumn<DomainEventRow>[] = useMemo(
    () => [
      {
        key: 'createdAt',
        header: 'Timestamp',
        accessor: (r) => r.createdAt,
        render: (r) => (
          <span title={r.createdAt} className="whitespace-nowrap text-gray-600 dark:text-gray-300">
            {new Date(r.createdAt).toLocaleString()}
          </span>
        ),
        sortable: true,
      },
      {
        key: 'type',
        header: 'Type',
        accessor: (r) => r.type,
        render: (r) => (
          <StatusBadge status={eventTone(r.type)} showDot={false}>
            {r.type}
          </StatusBadge>
        ),
        sortable: true,
      },
      {
        key: 'issueNumber',
        header: 'Issue #',
        accessor: (r) => r.issueNumber,
        render: (r) => (r.issueNumber != null ? `#${r.issueNumber}` : '—'),
        sortable: true,
        align: 'right',
      },
      {
        key: 'correlationId',
        header: 'Correlation',
        accessor: (r) => tagValue(r.tags, 'correlationId'),
        render: (r) => {
          const c = tagValue(r.tags, 'correlationId');
          return c ? <code className="text-xs">{c}</code> : '—';
        },
      },
      {
        key: 'tags',
        header: 'Tags',
        accessor: (r) => JSON.stringify(r.tags ?? {}),
        render: (r) => (
          <span className="text-xs text-gray-500 dark:text-gray-400">
            {formatTagsPreview(r.tags) || '—'}
          </span>
        ),
      },
    ],
    [],
  );

  return (
    <MonitoringLayout
      title={NAV.label}
      description="Search, filter and inspect the DCB event store — the primary workflow-execution debugging tool."
      loading={loading}
      lastUpdated={lastUpdated}
      onRefresh={triggerSearch}
      autoRefreshInterval={autoRefresh.interval}
      onAutoRefreshChange={autoRefresh.setInterval}
      timeRange={preset}
      onTimeRangeChange={setPreset}
      showTimeRange
      actions={
        <>
          <button
            type="button"
            onClick={() => setShowSummary((s) => !s)}
            aria-pressed={showSummary}
            className="rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            {showSummary ? 'Hide summary' : 'Summary'}
          </button>
          <button
            type="button"
            onClick={() => handleExport('json')}
            disabled={events.length === 0}
            className="rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-40 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            Export JSON
          </button>
          <button
            type="button"
            onClick={() => handleExport('csv')}
            disabled={events.length === 0}
            className="rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-40 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            Export CSV
          </button>
        </>
      }
    >
      {/* Filter bar */}
      <form
        aria-label="Event filters"
        className="mb-4 flex flex-wrap items-end gap-3 rounded-lg border border-gray-200 bg-gray-50 p-3 dark:border-gray-700 dark:bg-gray-800/50"
        onSubmit={(e) => {
          e.preventDefault();
          triggerSearch();
        }}
      >
        <label className="flex flex-col gap-1 text-xs text-gray-500 dark:text-gray-400">
          Event type
          <input
            type="text"
            value={typeFilter}
            onChange={(e) => setTypeFilter(e.target.value)}
            placeholder="e.g. AGENT.TASK"
            aria-label="Event type"
            className={`${INPUT_CLASS} w-52`}
          />
        </label>
        <label className="flex flex-col gap-1 text-xs text-gray-500 dark:text-gray-400">
          Match
          <select
            value={typeMatch}
            onChange={(e) => setTypeMatch(e.target.value as 'exact' | 'prefix')}
            aria-label="Type match mode"
            className={INPUT_CLASS}
          >
            <option value="prefix">Prefix</option>
            <option value="exact">Exact</option>
          </select>
        </label>
        <label className="flex flex-col gap-1 text-xs text-gray-500 dark:text-gray-400">
          Correlation ID
          <input
            type="text"
            value={correlationId}
            onChange={(e) => setCorrelationId(e.target.value)}
            placeholder="run / workflow id"
            aria-label="Correlation ID"
            className={`${INPUT_CLASS} w-52`}
          />
        </label>
        <label className="flex flex-col gap-1 text-xs text-gray-500 dark:text-gray-400">
          Actor
          <input
            type="text"
            value={actor}
            onChange={(e) => setActor(e.target.value)}
            placeholder="userId"
            aria-label="Actor"
            className={`${INPUT_CLASS} w-40`}
          />
        </label>
        <label className="flex flex-col gap-1 text-xs text-gray-500 dark:text-gray-400">
          Page size
          <select
            value={pageSize}
            onChange={(e) => setPageSize(Number(e.target.value))}
            aria-label="Page size"
            className={INPUT_CLASS}
          >
            {PAGE_SIZES.map((n) => (
              <option key={n} value={n}>
                {n}
              </option>
            ))}
          </select>
        </label>
        <button
          type="submit"
          className="rounded-md bg-blue-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
        >
          Search
        </button>
      </form>

      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={triggerSearch} />
        </div>
      )}

      {showSummary && (
        <section
          data-testid="event-summary"
          aria-label="Event summary"
          className="mb-4 grid grid-cols-1 gap-4 rounded-lg border border-gray-200 bg-white p-4 dark:border-gray-700 dark:bg-gray-900 lg:grid-cols-2"
        >
          <div>
            <h3 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
              Events over time ({events.length} loaded)
            </h3>
            <TimeSeriesChart
              data={frequencySeries}
              variant="area"
              ariaLabel="Event frequency over time"
              emptyMessage="No events in the loaded set."
            />
          </div>
          <div>
            <h3 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
              By type
            </h3>
            <ul className="flex flex-wrap gap-2">
              {summaryByType.map((s) => (
                <li key={s.type}>
                  <StatusBadge status={eventTone(s.type)} showDot={false}>
                    {s.type} · {s.count}
                  </StatusBadge>
                </li>
              ))}
              {summaryByType.length === 0 && (
                <li className="text-sm text-gray-500 dark:text-gray-400">No events loaded.</li>
              )}
            </ul>
          </div>
        </section>
      )}

      {selected && <EventDetailPanel event={selected} onClose={() => setSelected(null)} />}

      <DataTable
        columns={columns}
        rows={events}
        getRowId={(row) => row.id}
        pageSize={100000}
        initialSort={{ key: 'createdAt', direction: 'desc' }}
        filterPlaceholder="Quick-filter loaded events…"
        emptyTitle="No events"
        emptyMessage="No events match the current filters and time range."
        onRowClick={(row) => setSelected(row)}
      />

      <div
        className="mt-4 flex items-center justify-between text-sm text-gray-500 dark:text-gray-400"
        data-testid="event-footer"
      >
        <span>
          Showing {events.length}
          {total != null ? ` of ${total}` : ''} event{events.length === 1 ? '' : 's'}
        </span>
        {hasMore && (
          <button
            type="button"
            onClick={() => void loadMore()}
            disabled={loading}
            className="rounded-md border border-gray-300 px-3 py-1.5 font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-40 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            Load more
          </button>
        )}
      </div>
    </MonitoringLayout>
  );
}
