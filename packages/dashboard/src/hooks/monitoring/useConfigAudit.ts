/**
 * useConfigAudit — data hook for the Configuration Audit page (Story 23-4).
 *
 * Like the System Health overview (Story 23-1) this page adds NO new backend
 * surface: it COMPOSES existing, read-only, tenant-scoped endpoints entirely on
 * the client via `Promise.allSettled`, so one unavailable source degrades that
 * section rather than blanking the whole page. Two halves:
 *
 *   Effective configuration (the "what is configured right now" summary):
 *     • `GET /api/providers/health`        — configured AI providers + circuit
 *                                            state (metadata only — never keys).
 *     • `GET /api/prompts`                 — the tenant/user prompt OVERRIDES
 *                                            (source = user|tenant), i.e. every
 *                                            prompt diffing from the system
 *                                            default. Count + list.
 *     • `GET /api/conventions`             — resolved conventions with an
 *                                            `isOverride` flag (tenant vs system).
 *     • `GET /api/pricing/entitlements`    — the tenant's OWN resolved plan +
 *                                            entitlement limits / live headroom.
 *                                            Carries NO price/margin fields.
 *
 *   Change history (the "who changed what, when" audit):
 *     • `GET /api/v1/orgs/{tenantId}/audit` — the Epic-37 curated, redacted,
 *                                            tenant-scoped audit read-model,
 *                                            filtered client-side to the
 *                                            configuration-relevant categories
 *                                            (config / persona / byok / billing).
 *                                            The tenant id comes from `/auth/me`;
 *                                            with no active tenant the audit call
 *                                            is skipped entirely (fail-closed —
 *                                            never fans out cross-tenant).
 *
 * SECURITY: the page NEVER renders a secret value. `/api/config/providers`
 * (which returns the raw user-settings blob that can hold plaintext keys) is
 * deliberately NOT read; the curated audit rows are already redacted and this
 * hook never surfaces their `payload`. No cost / margin / sell-price figure is
 * read from any source (entitlements carries none; the platform price-book lives
 * behind platform-owner-only `/api/admin/pricing/*` and is never touched here).
 *
 * Responses arrive with ASP.NET Core's camelCase policy.
 */

import { useCallback, useState } from 'react';
import type { StatusKind, StatusTone } from '../../components/monitoring/StatusBadge.js';

/** The configuration-relevant audit categories surfaced on this page. Security /
 *  auth / rbac / tenant-lifecycle categories are the Security Audit page's scope
 *  (Story 23-10), NOT this one. */
export const CONFIG_AUDIT_CATEGORIES = ['config', 'persona', 'byok', 'billing'] as const;
export type ConfigAuditCategory = (typeof CONFIG_AUDIT_CATEGORIES)[number];

const CONFIG_CATEGORY_SET = new Set<string>(CONFIG_AUDIT_CATEGORIES);

/** Max curated audit rows pulled per window (the endpoint's own MaxLimit). */
const AUDIT_LIMIT = 200;

// ── Public row/summary shapes ───────────────────────────────────────────────

/** A configured AI provider (circuit-breaker roll-up — metadata only). */
export interface ProviderConfigRow {
  providerKey: string;
  kind: StatusKind;
  label: string;
}

/** A prompt or convention override diffing from the shipped system default. */
export interface OverrideRow {
  id: string;
  scope: 'prompt' | 'convention';
  role: string;
  action: string;
  /** `user` / `tenant` (an override) — system rows are excluded from this list. */
  source: string;
}

/** One resolved entitlement limit (no monetary fields — never a price/margin). */
export interface EntitlementRow {
  metricKey: string;
  limitValue: number | null;
  period: string | null;
  currentUsage: number | null;
  remaining: number | null;
  isOver: boolean;
}

/** The tenant's OWN plan identity (never a price). */
export interface PlanSummary {
  planId: string | null;
  planVersion: number | null;
  isCustom: boolean;
}

/** A single configuration-change audit record (who / what / when). */
export interface ConfigChangeRow {
  id: string;
  occurredAt: string;
  /** Point-in-time actor email snapshot, or `System` for system actions. */
  actor: string;
  actionCode: string;
  category: string;
  /** `type:id` target, or `—`. */
  target: string;
  severity: string;
  outcome: string;
}

export interface ConfigAuditSummary {
  providers: ProviderConfigRow[];
  providerHealthy: number;
  promptOverrideCount: number;
  overrides: OverrideRow[];
  conventionOverrideCount: number;
  conventionTotal: number;
  plan: PlanSummary | null;
  entitlements: EntitlementRow[];
  changes: ConfigChangeRow[];
  /** True when the audit read 403'd (caller is not a tenant admin). */
  changesForbidden: boolean;
  /** True when there is no active tenant, so the audit read was skipped. */
  changesNoTenant: boolean;
  /** Per-source availability so the UI can show a degraded note per section. */
  sources: {
    providers: boolean;
    prompts: boolean;
    conventions: boolean;
    entitlements: boolean;
    changes: boolean;
  };
}

export interface UseConfigAuditResult {
  summary: ConfigAuditSummary | null;
  loading: boolean;
  /** Set only when EVERY source failed (catastrophic). Per-source failures
   *  degrade their own section instead. */
  error: string | null;
  lastUpdated: Date | null;
  load: (range: { start: Date; end: Date }) => Promise<void>;
}

// ── Wire shapes (subset of each endpoint's camelCase response) ──────────────

interface MeResponse {
  user?: { tenantId?: string | null };
}

interface ProviderHealthWire {
  providerKey: string;
  status?: string;
}

interface ProviderHealthResponse {
  providers?: ProviderHealthWire[];
}

interface PromptOverrideWire {
  role: string | null;
  action: string | null;
  source?: string;
}

interface ConventionWire {
  id?: string;
  role: string;
  action: string;
  source?: string;
  isOverride?: boolean;
}

interface EntitlementLimitWire {
  metricKey: string;
  limitValue?: number | null;
  period?: string | null;
  currentUsage?: number | null;
  remaining?: number | null;
  isOver?: boolean;
}

interface EntitlementsResponse {
  planId?: string | null;
  planVersion?: number | null;
  isCustom?: boolean;
  limits?: EntitlementLimitWire[];
}

interface AuditRecordWire {
  id: string;
  actionCategory: string;
  actionCode: string;
  actorLabel?: string | null;
  targetType?: string | null;
  targetId?: string | null;
  severity: string;
  outcome: string;
  occurredAt: string;
}

interface AuditQueryResponse {
  records?: AuditRecordWire[];
}

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

/** Sentinel: no active tenant, so the curated audit read is skipped. */
const NO_TENANT = Symbol('no-active-tenant');

interface HttpError extends Error {
  status?: number;
}

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
    const err = new Error(message) as HttpError;
    err.status = res.status;
    throw err;
  }
  return (await res.json()) as T;
}

/** Map a provider circuit-breaker `status` to a badge kind + label. */
function providerKind(status: string | undefined): { kind: StatusKind; label: string } {
  switch (status) {
    case 'healthy':
      return { kind: 'healthy', label: 'Healthy' };
    case 'degraded':
      return { kind: 'degraded', label: 'Half-open' };
    case 'down':
      return { kind: 'down', label: 'Circuit open' };
    default:
      return { kind: 'unknown', label: 'Configured' };
  }
}

/** Severity → badge tone for the change-history table. */
export function severityTone(severity: string): StatusTone {
  switch (severity.toLowerCase()) {
    case 'critical':
      return 'red';
    case 'warning':
      return 'yellow';
    case 'notice':
      return 'blue';
    default:
      return 'gray';
  }
}

export function useConfigAudit(): UseConfigAuditResult {
  const [summary, setSummary] = useState<ConfigAuditSummary | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const load = useCallback(async (range: { start: Date; end: Date }): Promise<void> => {
    setLoading(true);
    setError(null);

    // Resolve the active tenant id — needed ONLY to address the curated tenant
    // audit URL. Best-effort + fail-closed: no tenant → the audit read is
    // skipped (the hook never fans out cross-tenant).
    let tenantId: string | null = null;
    try {
      const me = await fetchJson<MeResponse>(`${API_BASE}/api/auth/me`);
      tenantId = me.user?.tenantId ?? null;
    } catch {
      tenantId = null;
    }

    const auditParams = new URLSearchParams({
      from: range.start.toISOString(),
      to: range.end.toISOString(),
      limit: String(AUDIT_LIMIT),
    });
    const auditUrl = tenantId
      ? `${API_BASE}/api/v1/orgs/${tenantId}/audit?${auditParams.toString()}`
      : null;

    const [healthR, promptsR, conventionsR, entitlementsR, auditR] = await Promise.allSettled([
      fetchJson<ProviderHealthResponse>(`${API_BASE}/api/providers/health`),
      fetchJson<PromptOverrideWire[]>(`${API_BASE}/api/prompts`),
      fetchJson<ConventionWire[]>(`${API_BASE}/api/conventions`),
      fetchJson<EntitlementsResponse>(`${API_BASE}/api/pricing/entitlements`),
      auditUrl ? fetchJson<AuditQueryResponse>(auditUrl) : Promise.reject(NO_TENANT),
    ]);

    // Catastrophic: every configuration source failed (e.g. logged out).
    if (
      healthR.status === 'rejected' &&
      promptsR.status === 'rejected' &&
      conventionsR.status === 'rejected' &&
      entitlementsR.status === 'rejected'
    ) {
      setError(
        healthR.reason instanceof Error
          ? healthR.reason.message
          : 'Failed to load configuration',
      );
      setLoading(false);
      return;
    }

    // ── Providers ──
    let providers: ProviderConfigRow[] = [];
    if (healthR.status === 'fulfilled') {
      providers = (healthR.value.providers ?? []).map((p) => {
        const { kind, label } = providerKind(p.status);
        return { providerKey: p.providerKey, kind, label };
      });
    }
    const providerHealthy = providers.filter((p) => p.kind === 'healthy').length;

    // ── Prompt overrides ── (the list IS the overrides — system defaults excluded)
    const overrides: OverrideRow[] = [];
    let promptOverrideCount = 0;
    if (promptsR.status === 'fulfilled') {
      for (const p of promptsR.value ?? []) {
        if ((p.source ?? '') === 'system') continue;
        promptOverrideCount += 1;
        overrides.push({
          id: `prompt:${p.role ?? '*'}:${p.action ?? '*'}`,
          scope: 'prompt',
          role: p.role ?? '—',
          action: p.action ?? '—',
          source: p.source ?? 'override',
        });
      }
    }

    // ── Convention overrides ── (list resolves every eligible pair; count the
    // tenant-overridden ones and add them to the overrides table).
    let conventionOverrideCount = 0;
    let conventionTotal = 0;
    if (conventionsR.status === 'fulfilled') {
      const rows = conventionsR.value ?? [];
      conventionTotal = rows.length;
      for (const c of rows) {
        const isOverride = c.isOverride === true || c.source === 'tenant';
        if (!isOverride) continue;
        conventionOverrideCount += 1;
        overrides.push({
          id: `convention:${c.id ?? `${c.role}:${c.action}`}`,
          scope: 'convention',
          role: c.role,
          action: c.action,
          source: c.source ?? 'tenant',
        });
      }
    }

    // ── Plan + entitlements ── (own plan/limits only — NEVER a price/margin)
    let plan: PlanSummary | null = null;
    let entitlements: EntitlementRow[] = [];
    if (entitlementsR.status === 'fulfilled') {
      const e = entitlementsR.value;
      plan = {
        planId: e.planId ?? null,
        planVersion: e.planVersion ?? null,
        isCustom: e.isCustom ?? false,
      };
      entitlements = (e.limits ?? []).map((l) => ({
        metricKey: l.metricKey,
        limitValue: l.limitValue ?? null,
        period: l.period ?? null,
        currentUsage: l.currentUsage ?? null,
        remaining: l.remaining ?? null,
        isOver: l.isOver ?? false,
      }));
    }

    // ── Change history ── (curated, redacted; config-relevant categories only)
    let changes: ConfigChangeRow[] = [];
    let changesForbidden = false;
    const changesNoTenant = auditUrl === null;
    if (auditR.status === 'fulfilled') {
      changes = (auditR.value.records ?? [])
        .filter((r) => CONFIG_CATEGORY_SET.has(r.actionCategory.toLowerCase()))
        .map((r) => ({
          id: r.id,
          occurredAt: r.occurredAt,
          actor: r.actorLabel && r.actorLabel.length > 0 ? r.actorLabel : 'System',
          actionCode: r.actionCode,
          category: r.actionCategory,
          target:
            r.targetType && r.targetType.length > 0
              ? r.targetId && r.targetId.length > 0
                ? `${r.targetType}:${r.targetId}`
                : r.targetType
              : '—',
          severity: r.severity,
          outcome: r.outcome,
        }));
    } else if (auditR.reason !== NO_TENANT) {
      const status = (auditR.reason as HttpError | undefined)?.status;
      changesForbidden = status === 403;
    }

    setSummary({
      providers,
      providerHealthy,
      promptOverrideCount,
      overrides,
      conventionOverrideCount,
      conventionTotal,
      plan,
      entitlements,
      changes,
      changesForbidden,
      changesNoTenant,
      sources: {
        providers: healthR.status === 'fulfilled',
        prompts: promptsR.status === 'fulfilled',
        conventions: conventionsR.status === 'fulfilled',
        entitlements: entitlementsR.status === 'fulfilled',
        changes: auditR.status === 'fulfilled',
      },
    });
    setLastUpdated(new Date());
    setLoading(false);
  }, []);

  return { summary, loading, error, lastUpdated, load };
}
