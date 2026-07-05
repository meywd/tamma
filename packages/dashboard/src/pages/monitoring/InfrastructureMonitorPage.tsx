/**
 * Infrastructure Monitor — Story 23-8.
 *
 * Operator page for SYSTEM/PLATFORM-level infrastructure health: the API
 * process's live runtime / CPU / memory / disk / uptime, plus the coarse up/down
 * status of every backing dependency (PostgreSQL, RabbitMQ, ELSA engine,
 * ChromaDB, OpenSearch). All numbers come from a single read-only snapshot,
 * `GET /api/admin/monitoring/infrastructure`, which is PlatformOwnerAccess-gated
 * server-side (a member / tenant never reaches it) and carries NO connection
 * string, secret, or tenant data.
 *
 * This is a lightweight metrics tier built from what .NET / the container already
 * expose (GC, Process, DriveInfo, cgroup) — it deliberately does NOT stand up a
 * full metrics stack (Prometheus / node-exporter). The richer per-service panels
 * described in the story (Postgres query stats, RabbitMQ queue depth, Docker
 * per-container stats, …) would each need their own metrics source and are out of
 * scope here. The page is built on the Story 23-12 monitoring primitives; the
 * route is already `AdminGuard`-gated + lazy-mounted, so this module supplies only
 * the body.
 */

import { useEffect, type JSX } from 'react';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import { MetricGrid } from '../../components/monitoring/MetricGrid.js';
import { MetricCard } from '../../components/monitoring/MetricCard.js';
import { StatusBadge } from '../../components/monitoring/StatusBadge.js';
import { ProgressRing } from '../../components/monitoring/ProgressRing.js';
import { EmptyState } from '../../components/monitoring/EmptyState.js';
import { ErrorBanner } from '../../components/monitoring/ErrorBanner.js';
import { useInfrastructureMonitor } from '../../hooks/monitoring/useInfrastructureMonitor.js';
import { useAutoRefresh } from '../../hooks/monitoring/useAutoRefresh.js';
import {
  dependencyKind,
  formatBytes,
  formatUptime,
  usageTone,
} from './infra-monitor-utils.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/infrastructure');

/** Story AC: infrastructure metrics refresh at a 5s polling interval by default. */
const DEFAULT_REFRESH_MS = 5000;

export function InfrastructureMonitorPage(): JSX.Element {
  const { metrics, loading, error, lastUpdated, load } = useInfrastructureMonitor();

  const autoRefresh = useAutoRefresh(load, {
    storageKey: NAV.storageKey,
    defaultInterval: DEFAULT_REFRESH_MS,
  });

  useEffect(() => {
    void load();
  }, [load]);

  const runtime = metrics?.runtime ?? null;
  const process = metrics?.process ?? null;
  const memory = metrics?.memory ?? null;
  const disks = metrics?.disks ?? [];
  const dependencies = metrics?.dependencies ?? [];
  const primaryDisk = disks[0] ?? null;

  return (
    <MonitoringLayout
      title={NAV.label}
      description="Live system health of the API process and its backing services — runtime, CPU, memory, disk, uptime and dependency connectivity."
      loading={loading}
      lastUpdated={lastUpdated}
      onRefresh={load}
      autoRefreshInterval={autoRefresh.interval}
      onAutoRefreshChange={autoRefresh.setInterval}
      showTimeRange={false}
    >
      {error && (
        <div className="mb-4">
          <ErrorBanner message={error} onRetry={load} />
        </div>
      )}

      {!metrics && !loading && !error ? (
        <EmptyState
          title="No infrastructure data"
          description="The infrastructure snapshot could not be loaded. Try refreshing."
        />
      ) : null}

      {metrics && runtime && memory && process && (
        <>
          {/* ── Headline metrics ── */}
          <MetricGrid columns={4} className="mb-6">
            <MetricCard
              label="CPU usage"
              value={runtime.cpuUsagePercent}
              unit="%"
              tone={usageTone(runtime.cpuUsagePercent)}
              hint={`${runtime.processorCount} core${runtime.processorCount === 1 ? '' : 's'}`}
            />
            <MetricCard
              label="Memory"
              value={formatBytes(memory.memoryUsedBytes)}
              unit={`/ ${formatBytes(memory.memoryLimitBytes)}`}
              tone={usageTone(memory.memoryUsagePercent)}
              hint={`${memory.memoryUsagePercent}% · limit via ${memory.memoryLimitSource}`}
            />
            <MetricCard
              label="Uptime"
              value={formatUptime(runtime.uptimeSeconds)}
              hint={`since ${new Date(runtime.startedAt).toLocaleString()}`}
            />
            <MetricCard
              label="Threads"
              value={process.threadCount}
              hint={`${process.threadPoolThreadCount} pooled · ${process.threadPoolPendingWorkItems} pending`}
            />
          </MetricGrid>

          {/* ── Utilisation rings ── */}
          <section aria-label="Utilisation" className="mb-8 flex flex-wrap gap-8">
            <div className="flex flex-col items-center">
              <ProgressRing
                value={memory.memoryUsagePercent}
                label="Memory"
                tone={usageTone(memory.memoryUsagePercent)}
              />
              <span className="mt-1 text-xs text-gray-500 dark:text-gray-400">
                {formatBytes(memory.memoryUsedBytes)} / {formatBytes(memory.memoryLimitBytes)}
              </span>
            </div>
            {primaryDisk && (
              <div className="flex flex-col items-center">
                <ProgressRing
                  value={primaryDisk.usedPercent}
                  label={`Disk (${primaryDisk.name})`}
                  tone={usageTone(primaryDisk.usedPercent)}
                />
                <span className="mt-1 text-xs text-gray-500 dark:text-gray-400">
                  {formatBytes(primaryDisk.usedBytes)} / {formatBytes(primaryDisk.totalBytes)}
                </span>
              </div>
            )}
            <div className="flex flex-col items-center">
              <ProgressRing
                value={runtime.cpuUsagePercent}
                label="CPU"
                tone={usageTone(runtime.cpuUsagePercent)}
              />
              <span className="mt-1 text-xs text-gray-500 dark:text-gray-400">
                {runtime.cpuUsagePercent}% of {runtime.processorCount} core
                {runtime.processorCount === 1 ? '' : 's'}
              </span>
            </div>
          </section>

          {/* ── Dependency connectivity ── */}
          <section aria-label="Dependencies" className="mb-8">
            <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">
              Dependencies
            </h2>
            {dependencies.length === 0 ? (
              <EmptyState title="No dependency probes" description="No backing services were probed." />
            ) : (
              <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
                {dependencies.map((dep) => (
                  <li
                    key={dep.name}
                    data-testid="dependency-row"
                    className="flex items-center justify-between rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800"
                  >
                    <span className="font-medium text-gray-800 dark:text-gray-100">{dep.name}</span>
                    <span className="flex items-center gap-2">
                      {dep.status === 'healthy' && (
                        <span className="text-xs tabular-nums text-gray-400 dark:text-gray-500">
                          {dep.responseTimeMs}ms
                        </span>
                      )}
                      <StatusBadge status={dependencyKind(dep.status)}>
                        {dep.detail && dep.status !== 'healthy' ? dep.detail : dep.status}
                      </StatusBadge>
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {/* ── Disks ── */}
          {disks.length > 0 && (
            <section aria-label="Disks" className="mb-8">
              <h2 className="mb-2 text-sm font-semibold text-gray-700 dark:text-gray-200">Disks</h2>
              <div className="overflow-x-auto">
                <table className="min-w-full text-sm">
                  <thead>
                    <tr className="text-left text-xs uppercase tracking-wide text-gray-500 dark:text-gray-400">
                      <th className="px-3 py-2">Mount</th>
                      <th className="px-3 py-2">Format</th>
                      <th className="px-3 py-2 text-right">Used</th>
                      <th className="px-3 py-2 text-right">Free</th>
                      <th className="px-3 py-2 text-right">Total</th>
                      <th className="px-3 py-2 text-right">Usage</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100 dark:divide-gray-800">
                    {disks.map((disk) => (
                      <tr key={disk.name} data-testid="disk-row">
                        <td className="px-3 py-2 font-mono text-gray-700 dark:text-gray-300">{disk.name}</td>
                        <td className="px-3 py-2 text-gray-500 dark:text-gray-400">{disk.driveFormat}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{formatBytes(disk.usedBytes)}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{formatBytes(disk.freeBytes)}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{formatBytes(disk.totalBytes)}</td>
                        <td className="px-3 py-2 text-right">
                          <StatusBadge status={usageTone(disk.usedPercent)} showDot={false}>
                            {disk.usedPercent}%
                          </StatusBadge>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}

          {/* ── Runtime footer ── */}
          <section aria-label="Runtime" className="text-xs text-gray-500 dark:text-gray-400">
            <span className="mr-4">{runtime.frameworkDescription}</span>
            <span className="mr-4">{runtime.osDescription}</span>
            <span className="mr-4">{runtime.processArchitecture}</span>
            <span>
              GC gen0/1/2: {process.gen0Collections}/{process.gen1Collections}/{process.gen2Collections}
            </span>
          </section>
        </>
      )}
    </MonitoringLayout>
  );
}
