/**
 * Agent Monitor (Realtime) — Story 23-2.
 *
 * Operator page for REALTIME managed-agent activity:
 *   • an activity summary + ACTIVE-runs table derived from the `AGENT.*`
 *     managed-agent event family, loaded through the Story 4-7 keyset query API
 *     (`GET /api/engine/events/query?type=AGENT.&prefix=true`, tenant-scoped,
 *     null-tenant fail-closed) via {@link useEventQuery};
 *   • a recent-agent-activity table over the same set; and
 *   • the "realtime" part — a LIVE tail of a selected run's tool-loop via the
 *     Story 32-23 streaming run tap (`GET /api/v1/llm/runs/{id}/stream`, SSE,
 *     tenant-scoped, foreign run ⇒ 404) through {@link useRunStreamTail}, which
 *     composes the Story 23-12 {@link useMonitoringSSE} primitive.
 *
 * The non-live parts honour the shared time-range + auto-refresh controls. The
 * whole page is built on the Story 23-12 monitoring primitives + hooks; the route
 * is already `AdminGuard`-gated and lazy-mounted by Story 23-12, so this module
 * only supplies the page body. NO cost / margin is surfaced anywhere — agent
 * activity & status only.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import { MetricGrid } from '../../components/monitoring/MetricGrid.js';
import { MetricCard } from '../../components/monitoring/MetricCard.js';
import { StatusBadge } from '../../components/monitoring/StatusBadge.js';
import { DataTable, type DataTableColumn } from '../../components/monitoring/DataTable.js';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { useEventQuery, type DomainEventRow } from '../../hooks/monitoring/useEventQuery.js';
import { useAutoRefresh } from '../../hooks/monitoring/useAutoRefresh.js';
import { useTimeRange } from '../../hooks/monitoring/useTimeRange.js';
import {
  useRunStreamTail,
  type RunStreamFrame,
  type UseRunStreamTailOptions,
} from '../../hooks/monitoring/useRunStreamTail.js';
import type { EventSourceLike } from '../../hooks/monitoring/useMonitoringSSE.js';
import {
  AGENT_EVENT_PREFIX,
  agentEventTone,
  correlationOf,
  deriveActiveRuns,
  deriveSummary,
  frameTone,
  tagString,
  type ActiveRun,
} from './agent-monitor-utils.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/agents');

/** Max `AGENT.*` events fetched per query page. */
const PAGE_SIZE = 200;

function shortId(id: string): string {
  return id.length > 12 ? `${id.slice(0, 8)}…` : id;
}

function timeOf(iso: string): string {
  return new Date(iso).toLocaleTimeString();
}

function readString(payload: Record<string, unknown>, key: string): string | null {
  const v = payload[key];
  return typeof v === 'string' ? v : null;
}

function readNumber(payload: Record<string, unknown>, key: string): number | null {
  const v = payload[key];
  return typeof v === 'number' ? v : null;
}

function readBool(payload: Record<string, unknown>, key: string): boolean | null {
  const v = payload[key];
  return typeof v === 'boolean' ? v : null;
}

/** Human description for one run-stream frame. */
function describeFrame(frame: RunStreamFrame): string {
  const p = frame.payload;
  switch (frame.kind) {
    case 'tool_call': {
      const name = readString(p, 'toolName') ?? 'tool';
      const turn = readNumber(p, 'turn');
      return turn !== null ? `${name} (turn ${turn})` : name;
    }
    case 'tool_result': {
      const name = readString(p, 'toolName') ?? 'tool';
      const ok = readBool(p, 'success');
      const ms = readNumber(p, 'durationMs');
      const suffix = ms !== null ? ` · ${Math.round(ms)}ms` : '';
      return `${name} ${ok === false ? 'failed' : 'ok'}${suffix}`;
    }
    case 'token':
      return readString(p, 'delta') ?? '';
    case 'question':
      return readString(p, 'question') ?? '(question)';
    case 'answer':
      return readString(p, 'answer') ?? '(answer)';
    case 'final': {
      const ok = readBool(p, 'success');
      const turns = readNumber(p, 'totalTurns');
      const tokens = readNumber(p, 'totalTokens');
      const exhausted = readBool(p, 'exhausted');
      const parts = [ok === false ? 'failed' : 'succeeded'];
      if (turns !== null) parts.push(`${turns} turns`);
      if (tokens !== null) parts.push(`${tokens} tokens`);
      if (exhausted) parts.push('exhausted');
      return parts.join(' · ');
    }
    case 'end':
      return readString(p, 'reason') ?? 'closed';
    default:
      return frame.kind;
  }
}

const CONNECTION_LABEL: Record<string, { kind: 'healthy' | 'info' | 'degraded' | 'unknown'; label: string }> = {
  connected: { kind: 'healthy', label: 'Live' },
  connecting: { kind: 'info', label: 'Connecting…' },
  reconnecting: { kind: 'degraded', label: 'Reconnecting…' },
  disconnected: { kind: 'unknown', label: 'Offline' },
};

interface RunStreamPanelProps {
  correlationId: string;
  onClose: () => void;
  eventSourceFactory?: UseRunStreamTailOptions['eventSourceFactory'];
}

/**
 * The live tool-loop tail for one selected run. Mounted with
 * `key={correlationId}` so switching runs starts a completely fresh
 * subscription (no stale-frame bleed).
 */
function RunStreamPanel({ correlationId, onClose, eventSourceFactory }: RunStreamPanelProps): JSX.Element {
  const tail = useRunStreamTail(
    correlationId,
    eventSourceFactory ? { eventSourceFactory } : {},
  );
  const conn = tail.done
    ? { kind: 'unknown' as const, label: 'Completed' }
    : (CONNECTION_LABEL[tail.status] ?? CONNECTION_LABEL['disconnected']!);

  // Newest frame first so the latest activity is always visible without scroll.
  const ordered = useMemo(() => [...tail.frames].reverse(), [tail.frames]);

  return (
    <section
      data-testid="run-stream-panel"
      aria-label="Live run tail"
      className="mb-6 rounded-lg border border-blue-200 bg-white shadow-sm dark:border-blue-900/50 dark:bg-gray-800"
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-gray-200 px-4 py-3 dark:border-gray-700">
        <div className="flex items-center gap-3">
          <h2 className="text-sm font-semibold text-gray-900 dark:text-gray-100">
            Live tail ·{' '}
            <code className="text-xs" title={correlationId}>
              {shortId(correlationId)}
            </code>
          </h2>
          <StatusBadge status={conn.kind}>{conn.label}</StatusBadge>
          <span className="text-xs text-gray-400 dark:text-gray-500">
            {tail.frames.length} frame{tail.frames.length === 1 ? '' : 's'}
          </span>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded-md border border-gray-300 px-3 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
        >
          Stop tail
        </button>
      </div>

      {tail.error && (
        <p className="px-4 py-2 text-xs text-red-600 dark:text-red-400" role="alert">
          Stream error: {tail.error.message}
        </p>
      )}

      <ol className="max-h-80 divide-y divide-gray-100 overflow-y-auto dark:divide-gray-800">
        {ordered.length === 0 ? (
          <li className="px-4 py-6">
            <EmptyState
              title={tail.done ? 'Run finished' : 'Waiting for activity…'}
              description={
                tail.done
                  ? 'This run produced no further live frames.'
                  : 'Tool calls and results will appear here as the run executes.'
              }
            />
          </li>
        ) : (
          ordered.map((frame) => (
            <li
              key={`${frame.kind}:${frame.seq ?? frame.receivedAt}`}
              data-testid="run-stream-frame"
              className="flex items-start gap-3 px-4 py-2 text-sm"
            >
              <span className="mt-0.5 shrink-0">
                <StatusBadge
                  status={frameTone(frame.kind, readBool(frame.payload, 'success'))}
                  showDot={false}
                >
                  {frame.kind}
                </StatusBadge>
              </span>
              <span className="min-w-0 flex-1 break-words text-gray-700 dark:text-gray-300">
                {describeFrame(frame)}
              </span>
              <span className="shrink-0 text-xs tabular-nums text-gray-400 dark:text-gray-500">
                {new Date(frame.receivedAt).toLocaleTimeString()}
              </span>
            </li>
          ))
        )}
      </ol>
    </section>
  );
}

export interface AgentMonitorPageProps {
  /** Injected in tests so the live tail can be driven by a fake EventSource. */
  eventSourceFactory?: (url: string) => EventSourceLike;
}

export function AgentMonitorPage({ eventSourceFactory }: AgentMonitorPageProps = {}): JSX.Element {
  const { preset, range, setPreset } = useTimeRange('24h');
  const { events, total, hasMore, loading, error, lastUpdated, runQuery, loadMore } = useEventQuery();
  const [selected, setSelected] = useState<string | null>(null);

  // Stable trigger that always reads the latest time-range window.
  const searchRef = useRef<() => void>(() => {});
  useEffect(() => {
    searchRef.current = () => {
      void runQuery({
        type: AGENT_EVENT_PREFIX,
        typeMatch: 'prefix',
        from: range.start,
        to: range.end,
        limit: PAGE_SIZE,
      });
    };
  }, [runQuery, range]);
  const trigger = useCallback(() => searchRef.current(), []);

  const autoRefresh = useAutoRefresh(trigger, {
    storageKey: NAV.storageKey,
    defaultInterval: null,
  });

  // Run on mount and whenever the time-range preset changes.
  useEffect(() => {
    trigger();
  }, [trigger, preset]);

  const summary = useMemo(() => deriveSummary(events), [events]);
  const activeRuns = useMemo(() => deriveActiveRuns(events), [events]);

  const activeColumns: DataTableColumn<ActiveRun>[] = useMemo(
    () => [
      {
        key: 'startedAt',
        header: 'Started',
        accessor: (r) => r.startedAt,
        render: (r) => (
          <span title={r.startedAt} className="whitespace-nowrap text-gray-600 dark:text-gray-300">
            {timeOf(r.startedAt)}
          </span>
        ),
        sortable: true,
      },
      { key: 'agentId', header: 'Agent', accessor: (r) => r.agentId ?? '—', sortable: true },
      { key: 'role', header: 'Role', accessor: (r) => r.role ?? '—', sortable: true },
      {
        key: 'provider',
        header: 'Provider / model',
        accessor: (r) => r.provider ?? '',
        render: (r) => (
          <span className="text-gray-600 dark:text-gray-300">
            {r.provider ?? '—'}
            {r.model ? <span className="text-gray-400 dark:text-gray-500"> · {r.model}</span> : null}
          </span>
        ),
      },
      { key: 'toolCalls', header: 'Tools', accessor: (r) => r.toolCalls, align: 'right', sortable: true },
      {
        key: 'action',
        header: '',
        align: 'right',
        hideable: false,
        render: (r) => (
          <button
            type="button"
            onClick={() => setSelected(r.correlationId)}
            title={`Tap live: ${r.correlationId}`}
            className={`rounded-md px-3 py-1 text-xs font-medium ${
              selected === r.correlationId
                ? 'bg-blue-600 text-white'
                : 'border border-gray-300 text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700'
            }`}
          >
            {selected === r.correlationId ? 'Watching' : 'Tap live'}
          </button>
        ),
      },
    ],
    [selected],
  );

  const activityColumns: DataTableColumn<DomainEventRow>[] = useMemo(
    () => [
      {
        key: 'createdAt',
        header: 'Time',
        accessor: (r) => r.createdAt,
        render: (r) => (
          <span title={r.createdAt} className="whitespace-nowrap text-gray-600 dark:text-gray-300">
            {timeOf(r.createdAt)}
          </span>
        ),
        sortable: true,
      },
      {
        key: 'type',
        header: 'Event',
        accessor: (r) => r.type,
        render: (r) => (
          <StatusBadge status={agentEventTone(r.type)} showDot={false}>
            {r.type}
          </StatusBadge>
        ),
        sortable: true,
      },
      { key: 'agentId', header: 'Agent', accessor: (r) => tagString(r, 'agentId') ?? '—', sortable: true },
      { key: 'role', header: 'Role', accessor: (r) => tagString(r, 'role') ?? '—', sortable: true },
      { key: 'provider', header: 'Provider', accessor: (r) => tagString(r, 'provider') ?? '—', sortable: true },
      {
        key: 'run',
        header: 'Run',
        accessor: (r) => correlationOf(r) ?? '',
        render: (r) => {
          const cid = correlationOf(r);
          if (cid === null) return <span className="text-gray-400">—</span>;
          return (
            <button
              type="button"
              onClick={() => setSelected(cid)}
              aria-label={`Tap live run ${cid}`}
              className="rounded font-mono text-xs text-blue-600 hover:underline dark:text-blue-400"
              title={`Tap live: ${cid}`}
            >
              {shortId(cid)}
            </button>
          );
        },
      },
    ],
    [],
  );

  const hasEvents = events.length > 0;

  return (
    <MonitoringLayout
      title={NAV.label}
      description="Realtime managed-agent activity — active runs, recent agent events, and a live tail of a selected run's tool-loop."
      loading={loading}
      lastUpdated={lastUpdated}
      onRefresh={trigger}
      autoRefreshInterval={autoRefresh.interval}
      onAutoRefreshChange={autoRefresh.setInterval}
      timeRange={preset}
      onTimeRangeChange={setPreset}
      showTimeRange
    >
      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={trigger} />
        </div>
      )}

      <MetricGrid columns={4} className="mb-6">
        <MetricCard label="Active runs" value={summary.active} tone={summary.active > 0 ? 'blue' : 'gray'} />
        <MetricCard label="Runs started" value={summary.started} hint="in the selected window" />
        <MetricCard
          label="Succeeded / failed"
          value={`${summary.succeeded} / ${summary.failed}`}
          tone={summary.failed > 0 ? 'red' : 'green'}
        />
        <MetricCard label="Tool calls" value={summary.toolCalls} />
      </MetricGrid>

      {selected && (
        <RunStreamPanel
          key={selected}
          correlationId={selected}
          onClose={() => setSelected(null)}
          {...(eventSourceFactory ? { eventSourceFactory } : {})}
        />
      )}

      <section aria-label="Active runs" className="mb-8">
        <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
          Active runs
          {activeRuns.length > 0 && (
            <span className="ml-2 text-xs font-normal text-gray-400 dark:text-gray-500">
              {activeRuns.length} in flight
            </span>
          )}
        </h2>
        {activeRuns.length === 0 ? (
          <EmptyState
            title="No active runs"
            description="No managed-agent run is currently in flight in the selected window. Tap a run below to watch its tool-loop live."
          />
        ) : (
          <DataTable
            columns={activeColumns}
            rows={activeRuns}
            getRowId={(r) => r.correlationId}
            pageSize={10}
            filterable={false}
            initialSort={{ key: 'startedAt', direction: 'desc' }}
            emptyTitle="No active runs"
          />
        )}
      </section>

      <section aria-label="Recent agent activity">
        <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">Recent agent activity</h2>
        {!hasEvents && !loading ? (
          <EmptyState
            title="No agent activity"
            description="No AGENT.* events were recorded in the selected time range."
          />
        ) : (
          <>
            <DataTable
              columns={activityColumns}
              rows={events}
              getRowId={(r) => r.id}
              pageSize={25}
              filterPlaceholder="Quick-filter loaded events…"
              initialSort={{ key: 'createdAt', direction: 'desc' }}
              emptyTitle="No agent activity"
              emptyMessage="No AGENT.* events match the current filters and time range."
            />
            <div
              className="mt-4 flex items-center justify-between text-sm text-gray-500 dark:text-gray-400"
              data-testid="agent-activity-footer"
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
          </>
        )}
      </section>
    </MonitoringLayout>
  );
}
