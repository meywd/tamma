/**
 * useInfrastructureMonitor — data hook for the Infrastructure Monitor (Story 23-8).
 *
 * Fetches a single, read-only, live snapshot from the platform-owner endpoint
 * `GET /api/admin/monitoring/infrastructure` (PlatformOwnerAccess-gated server-
 * side; the route is already behind the dashboard's AdminGuard). The snapshot
 * carries ONLY system statistics — .NET runtime / process / memory / disk / uptime
 * — plus the coarse up/down status of every backing dependency. It contains NO
 * connection string, secret, or tenant data.
 *
 * The endpoint is a snapshot, not a windowed query, so there is no time range —
 * the page drives re-fetching via the shared `useAutoRefresh` primitive.
 */

import { useCallback, useState } from 'react';

/** .NET runtime + host identity and live CPU / uptime. */
export interface RuntimeMetrics {
  frameworkDescription: string;
  osDescription: string;
  processArchitecture: string;
  processorCount: number;
  cpuUsagePercent: number;
  uptimeSeconds: number;
  startedAt: string;
}

/** Thread + GC counters for the process. */
export interface ProcessMetrics {
  threadCount: number;
  threadPoolThreadCount: number;
  threadPoolPendingWorkItems: number;
  threadPoolCompletedWorkItems: number;
  gen0Collections: number;
  gen1Collections: number;
  gen2Collections: number;
}

/** Process memory footprint against the effective (cgroup or GC) limit. */
export interface MemoryMetrics {
  workingSetBytes: number;
  privateMemoryBytes: number;
  managedHeapBytes: number;
  gcHeapSizeBytes: number;
  memoryLimitBytes: number;
  memoryUsedBytes: number;
  memoryUsagePercent: number;
  memoryLimitSource: string;
}

/** One mounted volume. */
export interface DiskMetrics {
  name: string;
  driveFormat: string;
  totalBytes: number;
  freeBytes: number;
  usedBytes: number;
  usedPercent: number;
}

/** Coarse connectivity of one backing service (no secrets in `detail`). */
export interface DependencyStatus {
  name: string;
  status: string;
  responseTimeMs: number;
  detail: string | null;
}

export interface InfrastructureMetrics {
  runtime: RuntimeMetrics;
  process: ProcessMetrics;
  memory: MemoryMetrics;
  disks: DiskMetrics[];
  dependencies: DependencyStatus[];
  collectedAt: string;
}

export interface UseInfrastructureMonitorResult {
  metrics: InfrastructureMetrics | null;
  loading: boolean;
  error: string | null;
  lastUpdated: Date | null;
  load: () => Promise<void>;
}

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

export function useInfrastructureMonitor(): UseInfrastructureMonitorResult {
  const [metrics, setMetrics] = useState<InfrastructureMetrics | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`${API_BASE}/api/admin/monitoring/infrastructure`, {
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
      });
      if (!res.ok) {
        let message = `HTTP ${res.status}`;
        try {
          const body = (await res.json()) as { error?: string };
          if (body?.error) message = body.error;
        } catch {
          // keep the default status message
        }
        throw new Error(message);
      }
      setMetrics((await res.json()) as InfrastructureMetrics);
      setLastUpdated(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load infrastructure metrics');
    } finally {
      setLoading(false);
    }
  }, []);

  return { metrics, loading, error, lastUpdated, load };
}
