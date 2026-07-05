/**
 * useProviderDiagnostics — data hook for the Provider Diagnostics page
 * (Story 23-6).
 *
 * Fetches two tenant-scoped, read-only sources in parallel and merges them:
 *   • `GET /api/providers/diagnostics/deep` (Story 23-6 aggregation) — per
 *     provider latency percentiles, error-class breakdown, token/cost analytics
 *     and per-model usage over a time range.
 *   • `GET /api/providers/health` (circuit-breaker state) — the live health /
 *     circuit state per provider, keyed by provider key.
 *
 * Both endpoints scope to the caller's tenant (SettingsView). The deep report's
 * `cost` figures are the tenant's OWN recorded spend — never a platform margin.
 *
 * Responses arrive with ASP.NET Core's camelCase policy.
 */

import { useCallback, useState } from 'react';

export interface LatencyPercentiles {
  p50: number;
  p95: number;
  p99: number;
  max: number;
  avg: number;
}

export interface ProviderErrorClass {
  errorClass: string;
  count: number;
  share: number;
}

export interface ProviderModelUsage {
  model: string;
  totalCalls: number;
  successCount: number;
  successRate: number;
  totalCost: number;
  totalTokens: number;
  avgLatencyMs: number;
}

export interface ProviderDiagnosticSummary {
  providerKey: string;
  totalCalls: number;
  successCount: number;
  failureCount: number;
  successRate: number;
  errorRate: number;
  latency: LatencyPercentiles;
  totalTokens: number;
  inputTokens: number;
  outputTokens: number;
  totalCost: number;
  errors: ProviderErrorClass[];
  models: ProviderModelUsage[];
}

export interface DeepReport {
  from: string;
  to: string;
  providers: ProviderDiagnosticSummary[];
  totalCalls: number;
  totalErrors: number;
  totalTokens: number;
  totalCost: number;
}

/** Circuit-breaker health for a single provider (subset of the API shape). */
export interface ProviderHealthEntry {
  providerKey: string;
  state: string;
  status: string;
  failureCount: number;
  lastSuccess: string | null;
  lastFailure: string | null;
  circuitOpenUntil: string | null;
  healthy: boolean;
  circuitOpen: boolean;
  halfOpen: boolean;
}

export interface UseProviderDiagnosticsResult {
  report: DeepReport | null;
  /** Circuit-breaker state keyed by provider key. */
  healthByKey: Record<string, ProviderHealthEntry>;
  loading: boolean;
  error: string | null;
  lastUpdated: Date | null;
  /** Fetch the deep report + health for the supplied window. */
  load: (range: { start: Date; end: Date }) => Promise<void>;
}

interface HealthResponse {
  providers?: ProviderHealthEntry[];
}

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

const EMPTY_REPORT: DeepReport = {
  from: '',
  to: '',
  providers: [],
  totalCalls: 0,
  totalErrors: 0,
  totalTokens: 0,
  totalCost: 0,
};

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

export function useProviderDiagnostics(): UseProviderDiagnosticsResult {
  const [report, setReport] = useState<DeepReport | null>(null);
  const [healthByKey, setHealthByKey] = useState<Record<string, ProviderHealthEntry>>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const load = useCallback(async (range: { start: Date; end: Date }): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams({
        from: range.start.toISOString(),
        to: range.end.toISOString(),
      });
      const [deep, health] = await Promise.all([
        fetchJson<DeepReport>(`${API_BASE}/api/providers/diagnostics/deep?${params.toString()}`),
        fetchJson<HealthResponse>(`${API_BASE}/api/providers/health`),
      ]);

      setReport(deep ?? EMPTY_REPORT);

      const map: Record<string, ProviderHealthEntry> = {};
      for (const entry of health?.providers ?? []) {
        map[entry.providerKey] = entry;
      }
      setHealthByKey(map);
      setLastUpdated(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load provider diagnostics');
    } finally {
      setLoading(false);
    }
  }, []);

  return { report, healthByKey, loading, error, lastUpdated, load };
}
