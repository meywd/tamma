/**
 * Workflow Monitor (Story 23-5).
 *
 * Operator page over the tenant's WORKFLOW instances: a run-instance table
 * (id, definition, status, started, duration), per-status + per-definition
 * counts, a recent-failures highlight, status/definition filters, and the
 * global time-range + auto-refresh controls.
 *
 * Data sources are EXISTING tenant-scoped, fail-closed reads (no new lib):
 *   • `GET /api/v1/runs`          — the Story 21-4 (#258) run list (table).
 *   • `GET /api/v1/runs/summary`  — the Story 23-5 windowed count aggregate
 *                                   (metric cards / filters). Counts only, no
 *                                   cost / economics.
 * Both resolve the tenant from the session and fail closed (404) on a null /
 * cross-tenant read. The route is already `AdminGuard`-gated + lazy-mounted by
 * Story 23-12; this module only supplies the page body.
 *
 * A row links to the run detail (`/runs/:runId`) — the DCB event timeline for
 * that correlationId.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { useNavigate } from 'react-router-dom';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import { MetricGrid } from '../../components/monitoring/MetricGrid.js';
import { MetricCard } from '../../components/monitoring/MetricCard.js';
import { DataTable, type DataTableColumn } from '../../components/monitoring/DataTable.js';
import { StatusBadge, type StatusTone } from '../../components/monitoring/StatusBadge.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { useWorkflowMonitor } from '../../hooks/monitoring/useWorkflowMonitor.js';
import { useAutoRefresh } from '../../hooks/monitoring/useAutoRefresh.js';
import { useTimeRange } from '../../hooks/monitoring/useTimeRange.js';
import type { WorkflowRunSummary } from '../../services/runs/runs-api-client.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/workflows');

const INPUT_CLASS =
  'rounded-md border border-gray-300 px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100';

/** Map a raw workflow status onto the shared monitoring colour vocabulary. */
function statusTone(status: string): StatusTone {
  switch (status.toLowerCase()) {
    case 'completed':
    case 'succeeded':
    case 'success':
      return 'green';
    case 'failed':
    case 'error':
    case 'faulted':
      return 'red';
    case 'running':
      return 'blue';
    case 'paused':
    case 'awaiting_approval':
    case 'suspended':
      return 'yellow';
    default:
      return 'gray';
  }
}

function isFailed(status: string): boolean {
  const s = status.toLowerCase();
  return s === 'failed' || s === 'error' || s === 'faulted';
}

function isInProgress(status: string): boolean {
  const s = status.toLowerCase();
  return (
    s === 'running' ||
    s === 'pending' ||
    s === 'paused' ||
    s === 'awaiting_approval' ||
    s === 'suspended'
  );
}

function isCompleted(status: string): boolean {
  const s = status.toLowerCase();
  return s === 'completed' || s === 'succeeded' || s === 'success';
}

/** Format an elapsed millisecond span as "Xh Ym" / "Xm Ys" / "Xs". */
function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '—';
  const totalSeconds = Math.floor(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const totalMinutes = Math.floor(totalSeconds / 60);
  if (totalMinutes < 60) return `${totalMinutes}m ${totalSeconds % 60}s`;
  const hours = Math.floor(totalMinutes / 60);
  return `${hours}h ${totalMinutes % 60}m`;
}

/** Live duration for a run: recorded span, else elapsed-since-start if active. */
function runDurationMs(run: WorkflowRunSummary, nowMs: number): number | null {
  if (run.durationMs != null) return run.durationMs;
  if (run.startedAt && isInProgress(run.status)) {
    return nowMs - new Date(run.startedAt).getTime();
  }
  return null;
}

function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}

export function WorkflowMonitorPage(): JSX.Element {
  const navigate = useNavigate();
  const { preset, range, setPreset } = useTimeRange('24h');
  const { runs, total, summary, loading, error, lastUpdated, load } = useWorkflowMonitor();

  const [statusFilter, setStatusFilter] = useState('all');
  const [definitionFilter, setDefinitionFilter] = useState('all');
  const [failuresOnly, setFailuresOnly] = useState(false);

  // Stable trigger that always reads the latest window, so the auto-refresh
  // interval is not re-armed on every render (mirrors the Event Explorer).
  const loadRef = useRef<() => void>(() => {});
  useEffect(() => {
    loadRef.current = () => {
      void load({ from: range.start, to: range.end });
    };
  }, [load, range]);

  const triggerLoad = useCallback(() => loadRef.current(), []);

  const autoRefresh = useAutoRefresh(triggerLoad, {
    storageKey: NAV.storageKey,
    defaultInterval: null,
  });

  useEffect(() => {
    triggerLoad();
  }, [triggerLoad, preset]);

  // definitionId → friendly name, resolved from the windowed summary.
  const definitionNameById = useMemo(() => {
    const map = new Map<string, string>();
    for (const d of summary?.byDefinition ?? []) map.set(d.definitionId, d.definitionName);
    return map;
  }, [summary]);

  // Windowed metric counts (authoritative, from the summary endpoint).
  const counts = useMemo(() => {
    let inProgress = 0;
    let completed = 0;
    let failed = 0;
    for (const s of summary?.byStatus ?? []) {
      if (isFailed(s.status)) failed += s.count;
      else if (isCompleted(s.status)) completed += s.count;
      else if (isInProgress(s.status)) inProgress += s.count;
    }
    return { total: summary?.total ?? 0, inProgress, completed, failed };
  }, [summary]);

  const statusOptions = useMemo(
    () => (summary?.byStatus ?? []).map((s) => s.status).sort((a, b) => a.localeCompare(b)),
    [summary],
  );

  const definitionOptions = useMemo(
    () =>
      (summary?.byDefinition ?? [])
        .map((d) => ({ id: d.definitionId, name: d.definitionName }))
        .sort((a, b) => a.name.localeCompare(b.name)),
    [summary],
  );

  // Client-side filtering over the loaded run list: time window (the /runs list
  // is not server-windowed) + status + definition + failures-only.
  const visibleRuns = useMemo(() => {
    const startMs = range.start.getTime();
    const endMs = range.end.getTime();
    return runs.filter((r) => {
      const created = new Date(r.createdAt).getTime();
      if (Number.isFinite(created) && (created < startMs || created > endMs)) return false;
      if (failuresOnly && !isFailed(r.status)) return false;
      if (statusFilter !== 'all' && r.status !== statusFilter) return false;
      if (definitionFilter !== 'all' && r.definitionId !== definitionFilter) return false;
      return true;
    });
  }, [runs, range, statusFilter, definitionFilter, failuresOnly]);

  const nowMs = lastUpdated?.getTime() ?? Date.now();

  const columns: DataTableColumn<WorkflowRunSummary>[] = useMemo(
    () => [
      {
        key: 'id',
        header: 'Run',
        accessor: (r) => r.id,
        render: (r) => (
          <code
            className="text-xs text-blue-600 underline-offset-2 hover:underline dark:text-blue-400"
            title={r.id}
          >
            {shortId(r.id)}
          </code>
        ),
        sortable: true,
      },
      {
        key: 'definition',
        header: 'Definition',
        accessor: (r) => definitionNameById.get(r.definitionId) ?? r.definitionId,
        render: (r) => (
          <span className="text-gray-700 dark:text-gray-300">
            {definitionNameById.get(r.definitionId) ?? shortId(r.definitionId)}
          </span>
        ),
        sortable: true,
      },
      {
        key: 'status',
        header: 'Status',
        accessor: (r) => r.status,
        render: (r) => <StatusBadge status={statusTone(r.status)}>{r.status}</StatusBadge>,
        sortable: true,
      },
      {
        key: 'currentActivity',
        header: 'Activity',
        accessor: (r) => r.currentActivity,
        render: (r) => (
          <span className="text-xs text-gray-500 dark:text-gray-400">
            {r.currentActivity ?? '—'}
          </span>
        ),
      },
      {
        key: 'startedAt',
        header: 'Started',
        accessor: (r) => r.startedAt ?? r.createdAt,
        render: (r) => {
          const at = r.startedAt ?? r.createdAt;
          return (
            <span className="whitespace-nowrap text-gray-600 dark:text-gray-300" title={at}>
              {new Date(at).toLocaleString()}
            </span>
          );
        },
        sortable: true,
      },
      {
        key: 'duration',
        header: 'Duration',
        accessor: (r) => runDurationMs(r, nowMs) ?? -1,
        render: (r) => {
          const ms = runDurationMs(r, nowMs);
          return (
            <span className="whitespace-nowrap text-gray-600 dark:text-gray-300">
              {ms == null ? '—' : formatDuration(ms)}
            </span>
          );
        },
        sortable: true,
        align: 'right',
      },
    ],
    [definitionNameById, nowMs],
  );

  return (
    <MonitoringLayout
      title={NAV.label}
      description="Workflow instances by status and definition — durations, recent failures and per-run event trail."
      loading={loading}
      lastUpdated={lastUpdated}
      onRefresh={triggerLoad}
      autoRefreshInterval={autoRefresh.interval}
      onAutoRefreshChange={autoRefresh.setInterval}
      timeRange={preset}
      onTimeRangeChange={setPreset}
      showTimeRange
    >
      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={triggerLoad} />
        </div>
      )}

      {/* Windowed count metrics */}
      <MetricGrid columns={4} className="mb-4">
        <MetricCard
          label="Total (window)"
          value={counts.total}
          tone="blue"
          hint="Instances started in range"
        />
        <MetricCard label="In progress" value={counts.inProgress} tone="blue" />
        <MetricCard label="Completed" value={counts.completed} tone="green" />
        <MetricCard
          label="Failed"
          value={counts.failed}
          tone="red"
          {...(counts.failed > 0 ? { hint: 'Click to filter failures' } : {})}
          onClick={() => {
            setFailuresOnly(true);
            setStatusFilter('all');
          }}
        />
      </MetricGrid>

      {/* Per-status + per-definition breakdown chips */}
      {(statusOptions.length > 0 || definitionOptions.length > 0) && (
        <section
          data-testid="workflow-breakdown"
          className="mb-4 grid grid-cols-1 gap-4 rounded-lg border border-gray-200 bg-white p-4 dark:border-gray-700 dark:bg-gray-900 lg:grid-cols-2"
        >
          <div>
            <h3 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">By status</h3>
            <ul className="flex flex-wrap gap-2">
              {(summary?.byStatus ?? []).map((s) => (
                <li key={s.status}>
                  <StatusBadge status={statusTone(s.status)}>
                    {s.status} · {s.count}
                  </StatusBadge>
                </li>
              ))}
              {statusOptions.length === 0 && (
                <li className="text-sm text-gray-500 dark:text-gray-400">No instances in range.</li>
              )}
            </ul>
          </div>
          <div>
            <h3 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
              By definition
            </h3>
            <ul className="flex flex-wrap gap-2">
              {(summary?.byDefinition ?? []).map((d) => (
                <li key={d.definitionId}>
                  <StatusBadge status="blue" showDot={false}>
                    {d.definitionName} · {d.count}
                  </StatusBadge>
                </li>
              ))}
              {definitionOptions.length === 0 && (
                <li className="text-sm text-gray-500 dark:text-gray-400">No definitions in range.</li>
              )}
            </ul>
          </div>
        </section>
      )}

      {/* Filter bar */}
      <div className="mb-4 flex flex-wrap items-end gap-3 rounded-lg border border-gray-200 bg-gray-50 p-3 dark:border-gray-700 dark:bg-gray-800/50">
        <label className="flex flex-col gap-1 text-xs text-gray-500 dark:text-gray-400">
          Status
          <select
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value)}
            aria-label="Status filter"
            className={INPUT_CLASS}
          >
            <option value="all">All statuses</option>
            {statusOptions.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </label>
        <label className="flex flex-col gap-1 text-xs text-gray-500 dark:text-gray-400">
          Definition
          <select
            value={definitionFilter}
            onChange={(e) => setDefinitionFilter(e.target.value)}
            aria-label="Definition filter"
            className={INPUT_CLASS}
          >
            <option value="all">All definitions</option>
            {definitionOptions.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2 text-xs text-gray-600 dark:text-gray-300">
          <input
            type="checkbox"
            checked={failuresOnly}
            onChange={(e) => setFailuresOnly(e.target.checked)}
            aria-label="Failures only"
          />
          Failures only
        </label>
        {(statusFilter !== 'all' || definitionFilter !== 'all' || failuresOnly) && (
          <button
            type="button"
            onClick={() => {
              setStatusFilter('all');
              setDefinitionFilter('all');
              setFailuresOnly(false);
            }}
            className="rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            Clear filters
          </button>
        )}
      </div>

      <DataTable
        columns={columns}
        rows={visibleRuns}
        getRowId={(row) => row.id}
        pageSize={25}
        initialSort={{ key: 'startedAt', direction: 'desc' }}
        filterPlaceholder="Quick-filter loaded runs…"
        emptyTitle="No workflow instances"
        emptyMessage="No workflow runs match the current filters and time range."
        onRowClick={(row) => navigate(`/runs/${encodeURIComponent(row.id)}`)}
      />

      <div className="mt-4 text-sm text-gray-500 dark:text-gray-400" data-testid="workflow-footer">
        Showing {visibleRuns.length} of {runs.length} loaded
        {total != null ? ` (tenant total ${total})` : ''}
      </div>
    </MonitoringLayout>
  );
}
