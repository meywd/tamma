/**
 * useSystemHealth — data hook for the System Health overview (Story 23-1).
 *
 * The Epic-23 monitoring LANDING page composes existing, read-only sources —
 * it adds NO new health infrastructure. Four sources are fetched in parallel
 * and merged into a single at-a-glance summary; each source is fetched
 * independently (`Promise.allSettled`) so one unavailable source degrades to a
 * red service card rather than blanking the whole page:
 *
 *   • `GET /api/health` — API process liveness (`{ status, version }`). Public.
 *   • `GET /api/providers/health` — per-provider circuit-breaker state
 *     (tenant-scoped, settings:view). Drives the provider status cards + the
 *     healthy/total roll-up.
 *   • `GET /api/providers/diagnostics/deep?from&to` — the Story 23-6 tenant
 *     aggregation. Only its `totalCalls` / `totalErrors` are read here for
 *     throughput + error-rate; cost/token economics are intentionally NOT
 *     surfaced on this overview (health/status only — no cost leak).
 *   • `GET /api/engine/events/query?from&to` — the Story 4-7 tenant-scoped DCB
 *     event query. Drives the recent-events/errors table, the active-run count
 *     (distinct correlation ids) and the recent-error count.
 *
 * Every source is tenant-scoped server-side; a null tenant on the tenant-scoped
 * routes fails closed there (404 / empty page) — this hook never fans out.
 * Responses arrive with ASP.NET Core's camelCase policy.
 */

import { useCallback, useState } from 'react';
import type { StatusKind } from '../../components/monitoring/StatusBadge.js';

/** Health of one composed data source, rendered as a service status card. */
export interface ServiceStatus {
  key: string;
  label: string;
  kind: StatusKind;
  detail: string;
}

/** A single provider's live circuit-breaker health. */
export interface ProviderHealthRow {
  providerKey: string;
  kind: StatusKind;
  label: string;
  failureCount: number;
  lastFailure: string | null;
}

/** A recent DCB event projected for the overview table. */
export interface RecentEventRow {
  id: string;
  type: string;
  isError: boolean;
  correlationId: string | null;
  issueNumber: number | null;
  createdAt: string;
}

export interface SystemHealthSummary {
  services: ServiceStatus[];
  providers: ProviderHealthRow[];
  providerHealthy: number;
  providerTotal: number;
  recentEvents: RecentEventRow[];
  recentEventTotal: number | null;
  recentErrorCount: number;
  activeRuns: number;
  totalCalls: number;
  totalErrors: number;
  errorRate: number;
  diagnosticsAvailable: boolean;
}

export interface UseSystemHealthResult {
  summary: SystemHealthSummary | null;
  loading: boolean;
  /** Set only when EVERY source failed (catastrophic). Per-source failures
   *  surface as a `down` service card, not a page-level error. */
  error: string | null;
  lastUpdated: Date | null;
  load: (range: { start: Date; end: Date }) => Promise<void>;
}

// ── Wire shapes (subset of each endpoint's response) ────────────────────────

interface HealthResponse {
  status?: string;
  version?: string;
}

interface ProviderHealthWire {
  providerKey: string;
  state?: string;
  status?: string;
  failureCount?: number;
  lastFailure?: string | null;
}

interface ProviderHealthResponse {
  providers?: ProviderHealthWire[];
}

interface DeepReportWire {
  totalCalls?: number;
  totalErrors?: number;
}

interface EventWire {
  id: string;
  type: string;
  tags: unknown;
  createdAt: string;
  issueNumber: number | null;
}

interface EventQueryResponse {
  events?: EventWire[];
  total?: number | null;
}

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

/** Events whose type marks a failure/rejection — drives the "error" flag. */
const ERROR_TYPE_PATTERN = /(FAIL|ERROR|REJECT|TIMEOUT|DENIED|EXHAUST)/i;

const RECENT_EVENT_LIMIT = 100;

async function fetchJson<T>(url: string): Promise<T> {
  const res = await fetch(url, {
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
  return (await res.json()) as T;
}

/** Map the circuit-breaker `status` string to a badge kind + label. */
function providerKind(status: string | undefined): { kind: StatusKind; label: string } {
  switch (status) {
    case 'healthy':
      return { kind: 'healthy', label: 'Healthy' };
    case 'degraded':
      return { kind: 'degraded', label: 'Half-open' };
    case 'down':
      return { kind: 'down', label: 'Circuit open' };
    default:
      return { kind: 'unknown', label: 'Unknown' };
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Pull a string tag out of the (possibly null) parsed tag bag. */
function stringTag(tags: unknown, key: string): string | null {
  if (!isRecord(tags)) return null;
  const v = tags[key];
  return typeof v === 'string' && v.length > 0 ? v : null;
}

function isErrorEvent(e: EventWire): boolean {
  if (ERROR_TYPE_PATTERN.test(e.type)) return true;
  return stringTag(e.tags, 'status')?.toLowerCase() === 'failed';
}

export function useSystemHealth(): UseSystemHealthResult {
  const [summary, setSummary] = useState<SystemHealthSummary | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const load = useCallback(async (range: { start: Date; end: Date }): Promise<void> => {
    setLoading(true);
    setError(null);

    const params = new URLSearchParams({
      from: range.start.toISOString(),
      to: range.end.toISOString(),
    });
    const eventParams = new URLSearchParams(params);
    eventParams.set('limit', String(RECENT_EVENT_LIMIT));
    eventParams.set('includeTotal', 'true');

    const [healthRes, providersRes, deepRes, eventsRes] = await Promise.allSettled([
      fetchJson<HealthResponse>(`${API_BASE}/api/health`),
      fetchJson<ProviderHealthResponse>(`${API_BASE}/api/providers/health`),
      fetchJson<DeepReportWire>(
        `${API_BASE}/api/providers/diagnostics/deep?${params.toString()}`,
      ),
      fetchJson<EventQueryResponse>(
        `${API_BASE}/api/engine/events/query?${eventParams.toString()}`,
      ),
    ]);

    // Catastrophic: nothing responded (e.g. network down / logged out).
    if (
      healthRes.status === 'rejected' &&
      providersRes.status === 'rejected' &&
      deepRes.status === 'rejected' &&
      eventsRes.status === 'rejected'
    ) {
      setError(
        healthRes.reason instanceof Error
          ? healthRes.reason.message
          : 'Failed to load system health',
      );
      setLoading(false);
      return;
    }

    const services: ServiceStatus[] = [];

    // ── API process ──
    if (healthRes.status === 'fulfilled') {
      const ok = (healthRes.value.status ?? '').toLowerCase() === 'ok';
      services.push({
        key: 'api',
        label: 'API',
        kind: ok ? 'healthy' : 'degraded',
        detail: healthRes.value.version
          ? `v${healthRes.value.version}`
          : ok
            ? 'Operational'
            : 'Unexpected status',
      });
    } else {
      services.push({ key: 'api', label: 'API', kind: 'down', detail: 'Unreachable' });
    }

    // ── AI providers (circuit-breaker roll-up) ──
    let providers: ProviderHealthRow[] = [];
    let providerHealthy = 0;
    if (providersRes.status === 'fulfilled') {
      providers = (providersRes.value.providers ?? []).map((p) => {
        const { kind, label } = providerKind(p.status);
        return {
          providerKey: p.providerKey,
          kind,
          label,
          failureCount: p.failureCount ?? 0,
          lastFailure: p.lastFailure ?? null,
        };
      });
      providerHealthy = providers.filter((p) => p.kind === 'healthy').length;
      const anyDown = providers.some((p) => p.kind === 'down');
      const anyDegraded = providers.some((p) => p.kind === 'degraded');
      services.push({
        key: 'providers',
        label: 'AI providers',
        kind:
          providers.length === 0
            ? 'unknown'
            : anyDown
              ? 'down'
              : anyDegraded
                ? 'degraded'
                : 'healthy',
        detail:
          providers.length === 0
            ? 'None tracked'
            : `${providerHealthy}/${providers.length} healthy`,
      });
    } else {
      services.push({
        key: 'providers',
        label: 'AI providers',
        kind: 'down',
        detail: 'Health unavailable',
      });
    }

    // ── Provider diagnostics (throughput + error-rate source) ──
    let totalCalls = 0;
    let totalErrors = 0;
    const diagnosticsAvailable = deepRes.status === 'fulfilled';
    if (deepRes.status === 'fulfilled') {
      totalCalls = deepRes.value.totalCalls ?? 0;
      totalErrors = deepRes.value.totalErrors ?? 0;
      const errRate = totalCalls > 0 ? totalErrors / totalCalls : 0;
      services.push({
        key: 'diagnostics',
        label: 'Diagnostics',
        kind:
          totalCalls === 0
            ? 'unknown'
            : errRate === 0
              ? 'healthy'
              : errRate < 0.5
                ? 'degraded'
                : 'down',
        detail: totalCalls === 0 ? 'No calls in window' : `${totalCalls.toLocaleString()} calls`,
      });
    } else {
      services.push({
        key: 'diagnostics',
        label: 'Diagnostics',
        kind: 'down',
        detail: 'Report unavailable',
      });
    }

    // ── Event store (recent events / active runs / recent errors source) ──
    let recentEvents: RecentEventRow[] = [];
    let recentEventTotal: number | null = null;
    let recentErrorCount = 0;
    let activeRuns = 0;
    if (eventsRes.status === 'fulfilled') {
      const rows = eventsRes.value.events ?? [];
      const correlationIds = new Set<string>();
      recentEvents = rows.map((e) => {
        const correlationId = stringTag(e.tags, 'correlationId');
        if (correlationId) correlationIds.add(correlationId);
        const isError = isErrorEvent(e);
        if (isError) recentErrorCount += 1;
        return {
          id: e.id,
          type: e.type,
          isError,
          correlationId,
          issueNumber: e.issueNumber,
          createdAt: e.createdAt,
        };
      });
      activeRuns = correlationIds.size;
      recentEventTotal = eventsRes.value.total ?? null;
      services.push({
        key: 'events',
        label: 'Event store',
        kind: 'healthy',
        detail:
          recentEventTotal != null
            ? `${recentEventTotal.toLocaleString()} in window`
            : `${rows.length} recent`,
      });
    } else {
      services.push({
        key: 'events',
        label: 'Event store',
        kind: 'down',
        detail: 'Query unavailable',
      });
    }

    setSummary({
      services,
      providers,
      providerHealthy,
      providerTotal: providers.length,
      recentEvents,
      recentEventTotal,
      recentErrorCount,
      activeRuns,
      totalCalls,
      totalErrors,
      errorRate: totalCalls > 0 ? totalErrors / totalCalls : 0,
      diagnosticsAvailable,
    });
    setLastUpdated(new Date());
    setLoading(false);
  }, []);

  return { summary, loading, error, lastUpdated, load };
}
