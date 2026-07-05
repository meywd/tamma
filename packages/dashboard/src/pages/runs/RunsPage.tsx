/**
 * Runs page (Story 21-4) — the tenant's workflow-run history behind the SPA's
 * `/runs` destination.
 *
 * Read-only list from `GET /api/v1/runs` (tenant-scoped WorkflowInstance rows).
 * Reuses the shared monitoring `DataTable` for client-side sort/filter/paging
 * (AC6/AC7) and adds a status filter. Clicking a row opens the run detail
 * (`/runs/:runId`). The route is behind `AuthGuard`; the API scopes rows to the
 * caller's tenant.
 */

import { useCallback, useEffect, useMemo, useState, type JSX } from 'react';
import { useNavigate } from 'react-router-dom';
import { DataTable, type DataTableColumn } from '../../components/monitoring/DataTable.js';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { StatusBadge } from '../../components/monitoring/StatusBadge.js';
import { runsApi, type WorkflowRunSummary } from '../../services/runs/runs-api-client.js';
import { formatDuration, formatTimestamp, runStatusKind, shortId } from './run-format.js';

const SELECT_CLASS =
  'rounded-md border border-gray-300 bg-white px-2 py-1.5 text-sm text-gray-700 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-200';

export function RunsPage(): JSX.Element {
  const navigate = useNavigate();
  const [runs, setRuns] = useState<WorkflowRunSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState('all');

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const res = await runsApi.list({ limit: 100 });
      setRuns(res.runs);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load workflow runs');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const statuses = useMemo(
    () => Array.from(new Set(runs.map((r) => r.status))).sort(),
    [runs],
  );

  const visibleRuns = useMemo(
    () => (statusFilter === 'all' ? runs : runs.filter((r) => r.status === statusFilter)),
    [runs, statusFilter],
  );

  const columns = useMemo<DataTableColumn<WorkflowRunSummary>[]>(
    () => [
      {
        key: 'id',
        header: 'Run',
        accessor: (r) => r.id,
        render: (r) => <code className="text-xs">{shortId(r.id)}</code>,
      },
      {
        key: 'status',
        header: 'Status',
        accessor: (r) => r.status,
        render: (r) => <StatusBadge status={runStatusKind(r.status)}>{r.status}</StatusBadge>,
        sortable: true,
      },
      {
        key: 'activity',
        header: 'Current activity',
        accessor: (r) => r.currentActivity ?? '',
        render: (r) => r.currentActivity ?? '—',
      },
      {
        key: 'startedAt',
        header: 'Started',
        accessor: (r) => r.startedAt ?? r.createdAt,
        render: (r) => formatTimestamp(r.startedAt ?? r.createdAt),
        sortable: true,
      },
      {
        key: 'duration',
        header: 'Duration',
        accessor: (r) => r.durationMs ?? -1,
        render: (r) => formatDuration(r.durationMs),
        sortable: true,
        align: 'right',
      },
    ],
    [],
  );

  return (
    <div className="mx-auto max-w-6xl">
      <div className="mb-6 flex items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 dark:text-gray-100">Workflow Runs</h1>
          <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
            History of what Tamma has run on your behalf.
          </p>
        </div>
        <div className="flex items-center gap-2">
          {statuses.length > 0 && (
            <label className="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
              <span className="sr-only">Status</span>
              <select
                aria-label="Status filter"
                value={statusFilter}
                onChange={(e) => setStatusFilter(e.target.value)}
                className={SELECT_CLASS}
              >
                <option value="all">All statuses</option>
                {statuses.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </select>
            </label>
          )}
          <button
            type="button"
            onClick={() => void load()}
            className="rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={() => void load()} />
        </div>
      )}

      {loading && runs.length === 0 && !error && (
        <p className="text-sm text-gray-500 dark:text-gray-400">Loading workflow runs…</p>
      )}

      {!loading && runs.length === 0 && !error && (
        <EmptyState
          title="No workflow runs yet"
          description="Once Tamma picks up an issue on one of your repositories, its runs will appear here."
        />
      )}

      {runs.length > 0 && (
        <DataTable
          columns={columns}
          rows={visibleRuns}
          getRowId={(r) => r.id}
          pageSize={20}
          initialSort={{ key: 'startedAt', direction: 'desc' }}
          filterPlaceholder="Filter runs…"
          onRowClick={(r) => navigate(`/runs/${r.id}`)}
          emptyTitle="No matching runs"
          emptyMessage="No runs match the current filters."
        />
      )}
    </div>
  );
}
