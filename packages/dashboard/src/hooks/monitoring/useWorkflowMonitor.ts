/**
 * useWorkflowMonitor — data hook for the Workflow Monitor (Story 23-5).
 *
 * Composes two EXISTING tenant-scoped, fail-closed read endpoints (Story 21-4 /
 * Story 23-5) into one refresh:
 *   • `GET /api/v1/runs?limit=…`     — the newest workflow-instance rows for the
 *                                      table (id, definition, status, timings).
 *   • `GET /api/v1/runs/summary?from&to` — windowed per-status / per-definition
 *                                      instance counts for the metric cards.
 *
 * Both resolve the tenant from the caller's session (no tenant id sent from the
 * browser); a null / cross-tenant read fails closed with 404. The summary is a
 * pure count projection — it carries no cost / economics figure.
 *
 * The `/runs` list is newest-first and has no server-side time filter, so the
 * page applies the selected time window client-side over the loaded rows; the
 * windowed authoritative counts come from the summary endpoint.
 */

import { useCallback, useRef, useState } from 'react';
import {
  runsApi,
  type WorkflowRunSummary,
  type WorkflowRunsSummary,
} from '../../services/runs/runs-api-client.js';

/** The largest run page the table loads in one shot. */
export const WORKFLOW_MONITOR_RUN_LIMIT = 100;

export interface WorkflowMonitorWindow {
  from?: Date;
  to?: Date;
}

export interface UseWorkflowMonitorResult {
  runs: WorkflowRunSummary[];
  /** Total instances the tenant has (from the `/runs` list), or null. */
  total: number | null;
  summary: WorkflowRunsSummary | null;
  loading: boolean;
  error: string | null;
  lastUpdated: Date | null;
  /** Reload both the run list and the windowed summary for `window`. */
  load: (window: WorkflowMonitorWindow) => Promise<void>;
}

export function useWorkflowMonitor(): UseWorkflowMonitorResult {
  const [runs, setRuns] = useState<WorkflowRunSummary[]>([]);
  const [total, setTotal] = useState<number | null>(null);
  const [summary, setSummary] = useState<WorkflowRunsSummary | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  // Guards against a slow earlier request overwriting a newer one's result.
  const requestSeq = useRef(0);

  const load = useCallback(async (window: WorkflowMonitorWindow): Promise<void> => {
    const seq = ++requestSeq.current;
    setLoading(true);
    setError(null);
    try {
      const [list, sum] = await Promise.all([
        runsApi.list({ limit: WORKFLOW_MONITOR_RUN_LIMIT }),
        runsApi.summary({
          ...(window.from ? { from: window.from.toISOString() } : {}),
          ...(window.to ? { to: window.to.toISOString() } : {}),
        }),
      ]);
      if (seq !== requestSeq.current) return; // superseded
      setRuns(list.runs);
      setTotal(list.total);
      setSummary(sum);
      setLastUpdated(new Date());
    } catch (err) {
      if (seq !== requestSeq.current) return;
      setError(err instanceof Error ? err.message : 'Failed to load workflow instances');
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, []);

  return { runs, total, summary, loading, error, lastUpdated, load };
}
