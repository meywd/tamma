/**
 * Run detail page (Story 21-4) — one workflow run's event/log timeline behind
 * `/runs/:runId`.
 *
 * Lazily loads the run's DCB event timeline, derived log stream, files changed,
 * PR link, and the tenant's OWN recorded total cost from
 * `GET /api/v1/runs/:runId`. A run owned by another tenant (or a null tenant)
 * comes back 404 and renders a friendly "not found" banner — never another
 * tenant's data. The cost shown is the tenant's own spend, never a platform
 * margin.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { Link, useParams } from 'react-router-dom';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { StatusBadge } from '../../components/monitoring/StatusBadge.js';
import { runsApi, type RunEvent, type WorkflowRunDetail } from '../../services/runs/runs-api-client.js';
import { formatCost, formatDuration, formatTimestamp, runStatusKind } from './run-format.js';

function eventKind(type: string): 'healthy' | 'down' | 'info' | 'unknown' {
  const upper = type.toUpperCase();
  if (upper.includes('SUCCESS') || upper.includes('COMPLETED')) return 'healthy';
  if (upper.includes('FAIL') || upper.includes('ERROR')) return 'down';
  if (upper.includes('STARTED') || upper.includes('STEP')) return 'info';
  return 'unknown';
}

function RunStat({ label, value }: { label: string; value: JSX.Element | string }): JSX.Element {
  return (
    <div className="rounded-lg border border-gray-200 bg-white p-3 shadow-sm dark:border-gray-700 dark:bg-gray-800">
      <div className="text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
        {label}
      </div>
      <div className="mt-1 text-sm font-semibold text-gray-900 dark:text-gray-100">{value}</div>
    </div>
  );
}

function TimelineRow({ event }: { event: RunEvent }): JSX.Element {
  const hasData = event.data != null && Object.keys(event.data).length > 0;
  return (
    <li className="flex gap-3 py-2">
      <div className="mt-1.5 shrink-0">
        <StatusBadge status={eventKind(event.type)} showDot label="" className="!px-1.5" />
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <code className="text-xs font-semibold text-gray-800 dark:text-gray-200">
            {event.type}
          </code>
          <span className="text-xs text-gray-400 dark:text-gray-500">
            {formatTimestamp(event.createdAt)}
          </span>
        </div>
        {hasData && (
          <details className="mt-1">
            <summary className="cursor-pointer text-xs text-blue-600 hover:underline dark:text-blue-400">
              details
            </summary>
            <pre className="mt-1 overflow-x-auto rounded bg-gray-50 p-2 text-[11px] text-gray-700 dark:bg-gray-900 dark:text-gray-300">
              {JSON.stringify(event.data, null, 2)}
            </pre>
          </details>
        )}
      </div>
    </li>
  );
}

export function RunDetailPage(): JSX.Element {
  const { runId } = useParams<{ runId: string }>();
  const [detail, setDetail] = useState<WorkflowRunDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (): Promise<void> => {
    if (!runId) return;
    setLoading(true);
    setError(null);
    try {
      setDetail(await runsApi.getDetail(runId));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load run');
    } finally {
      setLoading(false);
    }
  }, [runId]);

  useEffect(() => {
    void load();
  }, [load]);

  const notFound = error === 'run_not_found' || error === 'no_active_tenant';

  return (
    <div className="mx-auto max-w-5xl">
      <div className="mb-4">
        <Link to="/runs" className="text-sm text-blue-600 hover:underline dark:text-blue-400">
          ← Back to runs
        </Link>
      </div>

      {error && !notFound && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={() => void load()} />
        </div>
      )}

      {notFound && (
        <EmptyState
          title="Run not found"
          description="This run does not exist, or it belongs to a different account."
        />
      )}

      {loading && detail === null && !error && (
        <p className="text-sm text-gray-500 dark:text-gray-400">Loading run…</p>
      )}

      {detail && (
        <>
          <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
            <div>
              <h1 className="text-2xl font-bold text-gray-900 dark:text-gray-100">
                Run <code className="text-lg">{detail.id.slice(0, 8)}</code>
              </h1>
              <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
                {detail.repository ?? 'Workflow run'}
                {detail.issueNumber != null && ` · issue #${detail.issueNumber}`}
              </p>
            </div>
            <StatusBadge status={runStatusKind(detail.status)}>{detail.status}</StatusBadge>
          </div>

          <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
            <RunStat label="Provider" value={detail.provider ?? '—'} />
            <RunStat label="Started" value={formatTimestamp(detail.startedAt)} />
            <RunStat label="Duration" value={formatDuration(detail.durationMs)} />
            <RunStat label="Cost" value={formatCost(detail.totalCostUsd)} />
            <RunStat label="Events" value={String(detail.eventCount)} />
          </div>

          {(detail.prUrl || detail.filesChanged.length > 0) && (
            <div className="mb-6 rounded-lg border border-gray-200 bg-white p-4 shadow-sm dark:border-gray-700 dark:bg-gray-800">
              {detail.prUrl && (
                <div className="mb-2 text-sm">
                  <span className="font-medium text-gray-500 dark:text-gray-400">Pull request: </span>
                  <a
                    href={detail.prUrl}
                    target="_blank"
                    rel="noreferrer"
                    className="text-blue-600 hover:underline dark:text-blue-400"
                  >
                    {detail.prUrl}
                  </a>
                </div>
              )}
              {detail.filesChanged.length > 0 && (
                <div className="text-sm">
                  <span className="font-medium text-gray-500 dark:text-gray-400">
                    Files changed ({detail.filesChanged.length}):
                  </span>
                  <ul className="mt-1 list-inside list-disc text-gray-700 dark:text-gray-300">
                    {detail.filesChanged.map((f) => (
                      <li key={f}>
                        <code className="text-xs">{f}</code>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}

          <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
            <section>
              <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
                Event timeline
              </h2>
              {detail.events.length === 0 ? (
                <EmptyState title="No events" description="This run has not recorded any events." />
              ) : (
                <ul className="divide-y divide-gray-100 rounded-lg border border-gray-200 bg-white px-4 dark:divide-gray-800 dark:border-gray-700 dark:bg-gray-800">
                  {detail.events.map((e) => (
                    <TimelineRow key={e.id} event={e} />
                  ))}
                </ul>
              )}
            </section>

            <section>
              <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">Logs</h2>
              {detail.logs.length === 0 ? (
                <EmptyState title="No logs" description="This run produced no log output." />
              ) : (
                <pre
                  data-testid="run-logs"
                  className="max-h-[28rem] overflow-auto rounded-lg border border-gray-200 bg-gray-900 p-3 font-mono text-xs leading-relaxed text-gray-100 dark:border-gray-700"
                >
                  {detail.logs.map((line, i) => (
                    <div key={i}>{line}</div>
                  ))}
                </pre>
              )}
            </section>
          </div>
        </>
      )}
    </div>
  );
}
