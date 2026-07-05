/**
 * MonitoringLayout — shared page shell for every Epic-23 monitoring screen.
 * Story 23-12 (AC3).
 *
 * Renders the page header (title + description), a last-updated timestamp and
 * manual refresh button, an auto-refresh interval selector, a global time-range
 * selector, and an SSE connection-status indicator. The component is
 * presentational/controlled: pages own the data hooks (`useAutoRefresh`,
 * `useTimeRange`, `useMonitoringSSE`) and pass their state down.
 */

import type { JSX, ReactNode } from 'react';
import { LoadingSpinner } from '../common/LoadingSpinner.js';
import { StatusBadge, type StatusKind } from './StatusBadge.js';
import { TIME_RANGE_PRESETS, type TimeRangePreset } from '../../hooks/monitoring/useTimeRange.js';
import type { AutoRefreshInterval } from '../../hooks/monitoring/useAutoRefresh.js';
import type { SSEConnectionStatus } from '../../hooks/monitoring/useMonitoringSSE.js';

export interface AutoRefreshOption {
  value: AutoRefreshInterval;
  label: string;
}

export const AUTO_REFRESH_OPTIONS: readonly AutoRefreshOption[] = [
  { value: null, label: 'Off' },
  { value: 5000, label: '5s' },
  { value: 10000, label: '10s' },
  { value: 30000, label: '30s' },
  { value: 60000, label: '60s' },
];

const CONNECTION_META: Record<SSEConnectionStatus, { kind: StatusKind; label: string }> = {
  connected: { kind: 'healthy', label: 'Live' },
  connecting: { kind: 'info', label: 'Connecting…' },
  reconnecting: { kind: 'degraded', label: 'Reconnecting…' },
  disconnected: { kind: 'unknown', label: 'Offline' },
};

interface MonitoringLayoutProps {
  title: string;
  description?: string;
  children: ReactNode;
  lastUpdated?: Date | null;
  loading?: boolean;
  onRefresh?: () => void;
  autoRefreshInterval?: AutoRefreshInterval;
  onAutoRefreshChange?: (interval: AutoRefreshInterval) => void;
  timeRange?: TimeRangePreset;
  onTimeRangeChange?: (preset: TimeRangePreset) => void;
  /** Hide the time-range selector for views that don't need one. Defaults true. */
  showTimeRange?: boolean;
  connectionStatus?: SSEConnectionStatus;
  /** Extra header controls rendered before the refresh button. */
  actions?: ReactNode;
}

const SELECT_CLASS =
  'rounded-md border border-gray-300 bg-white px-2 py-1.5 text-sm text-gray-700 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-200';

export function MonitoringLayout({
  title,
  description,
  children,
  lastUpdated,
  loading = false,
  onRefresh,
  autoRefreshInterval,
  onAutoRefreshChange,
  timeRange,
  onTimeRangeChange,
  showTimeRange = true,
  connectionStatus,
  actions,
}: MonitoringLayoutProps): JSX.Element {
  const connection = connectionStatus ? CONNECTION_META[connectionStatus] : null;

  return (
    <div data-testid="monitoring-layout">
      <header className="mb-6 flex flex-col gap-3 border-b border-gray-200 pb-4 dark:border-gray-700 lg:flex-row lg:items-start lg:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-3">
            <h1 className="text-2xl font-bold text-gray-900 dark:text-gray-100">{title}</h1>
            {connection && (
              <StatusBadge status={connection.kind}>{connection.label}</StatusBadge>
            )}
          </div>
          {description && (
            <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">{description}</p>
          )}
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {actions}

          {showTimeRange && onTimeRangeChange && (
            <label className="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
              <span className="sr-only">Time range</span>
              <select
                aria-label="Time range"
                value={timeRange ?? '24h'}
                onChange={(e) => onTimeRangeChange(e.target.value as TimeRangePreset)}
                className={SELECT_CLASS}
              >
                {TIME_RANGE_PRESETS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </label>
          )}

          {onAutoRefreshChange && (
            <label className="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
              <span className="hidden sm:inline">Auto-refresh</span>
              <select
                aria-label="Auto-refresh interval"
                value={autoRefreshInterval == null ? 'off' : String(autoRefreshInterval)}
                onChange={(e) => {
                  const v = e.target.value;
                  onAutoRefreshChange(v === 'off' ? null : Number(v));
                }}
                className={SELECT_CLASS}
              >
                {AUTO_REFRESH_OPTIONS.map((opt) => (
                  <option key={opt.label} value={opt.value == null ? 'off' : String(opt.value)}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </label>
          )}

          {onRefresh && (
            <button
              type="button"
              onClick={onRefresh}
              disabled={loading}
              className="inline-flex items-center gap-2 rounded-md border border-gray-300 px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
            >
              {loading ? <LoadingSpinner size="sm" /> : <RefreshIcon />}
              Refresh
            </button>
          )}
        </div>
      </header>

      {lastUpdated && (
        <p className="mb-4 text-xs text-gray-400 dark:text-gray-500" data-testid="last-updated">
          Last updated {lastUpdated.toLocaleTimeString()}
        </p>
      )}

      <div>{children}</div>
    </div>
  );
}

function RefreshIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      className="h-4 w-4"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M4.5 12a7.5 7.5 0 0 1 12.8-5.3L20 9M20 4.5V9h-4.5M19.5 12a7.5 7.5 0 0 1-12.8 5.3L4 15M4 19.5V15h4.5"
      />
    </svg>
  );
}
