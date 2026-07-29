/**
 * Acceptance Rules API Client (Story 39-5)
 *
 * Typed wrapper for `/api/acceptance-rules/*` exposed by
 * `apps/tamma-elsa/src/Tamma.Api/Endpoints/AcceptanceRulesEndpoints.cs`.
 *
 * Resolution + provenance:
 *   Each resolved row carries `source` — one of:
 *     "system-default"    — the shipped static default (no override)
 *     "principal-default" — the principal base override (the dial)
 *     "type-override"     — a per-document-type override
 *
 * RBAC: reads are any authenticated tenant member (AuthenticatedAny); writes
 * require `acceptance-rules:manage` (tenant_owner / tenant_admin) — members get
 * 403 on PUT/DELETE.
 *
 * Error codes surfaced by the API:
 *   ACCEPTANCE_RULES.INVALID  (400) — out-of-range knob (autonomy, bounds, threshold)
 *   DOCUMENT.TYPE.UNKNOWN     (400) — a typo'd document type key
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

export interface ApiError extends Error {
  status?: number;
  code?: string;
}

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });

  if (!response.ok) {
    const body = (await response
      .json()
      .catch(() => ({ error: response.statusText }))) as Record<string, string>;
    const err = new Error(body['error'] ?? `HTTP ${response.status}`) as ApiError;
    err.status = response.status;
    if (body['code'] !== undefined) err.code = body['code'];
    throw err;
  }

  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

// === Types ===================================================================

export type AcceptanceRulesSource =
  | 'system-default'
  | 'principal-default'
  | 'type-override';

export type EscalationClassKind = 'document-type' | 'agent-action';
export type ReviewerMode = 'single-reviewer' | 'panel';
export type ReviewDecisionRule = 'unanimous' | 'majority';

/**
 * WHO may answer the acceptance decision for a document type — the per-type
 * autonomy floor (Story 39-13 D4). Mirrors the two-member C# enum at
 * `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs`
 * (`[Wire("any")] Any`, `[Wire("human")] Human`).
 *
 *   `any`   — the autonomy dial alone decides who accepts.
 *   `human` — a person must accept, no matter how high the dial is set.
 *
 * `design`, `sprint-plan` and `threat-model` SHIP `human`. Story 43-0: this field
 * was missing from this interface, so the dialog's PUT body omitted it and every
 * admin save silently reset those types to `any`.
 */
export type AcceptorRequirement = 'any' | 'human';

export interface EscalationClass {
  kind: EscalationClassKind;
  key: string;
}

export interface ReviewerSelection {
  mode: ReviewerMode;
  reviewerRole: string | null;
  panelRoles: string[];
  quorum: number | null;
  decisionRule: ReviewDecisionRule;
}

/**
 * The full acceptance-rules body. This interface is the PUT contract — it must
 * carry every wire field of `AcceptanceRulesUpsertRequest`
 * (`apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs`),
 * because a field missing here is a field `tsc` cannot ask the dialog to send.
 * The C# side pins the field set in `AcceptanceRulesUpsertRequestFieldSetTests`
 * and that pin names this file.
 */
export interface AcceptanceRules {
  autonomyLevel: number;
  maxRevisionRounds: number;
  maxValidationRepairAttempts: number;
  ambiguityEscalationThreshold: number;
  alwaysEscalate: EscalationClass[];
  reviewerSelection: ReviewerSelection;
  acceptorRequirement: AcceptorRequirement;
  decisionGuidance: string;
  routingGuidance: string;
}

export interface ResolvedAcceptanceRules {
  rules: AcceptanceRules;
  source: AcceptanceRulesSource;
  version: number;
  documentTypeKey: string;
  resolvedAt: string;
}

export type AcceptanceRulesUpsertRequest = AcceptanceRules;

// === Client ==================================================================

export const acceptanceRulesApi = {
  /** GET /api/acceptance-rules — resolved rules for every document type + provenance. */
  listEffective(): Promise<ResolvedAcceptanceRules[]> {
    return fetchJSON<ResolvedAcceptanceRules[]>('/acceptance-rules');
  },

  /** GET /api/acceptance-rules/defaults — the shipped principal-base rules row. */
  getDefaults(): Promise<AcceptanceRules> {
    return fetchJSON<AcceptanceRules>('/acceptance-rules/defaults');
  },

  /**
   * GET /api/acceptance-rules/{documentTypeKey} — resolved rules for one type,
   * or the literal `base` dial row.
   */
  getResolved(documentTypeKey: string): Promise<ResolvedAcceptanceRules> {
    return fetchJSON<ResolvedAcceptanceRules>(
      `/acceptance-rules/${encodeURIComponent(documentTypeKey)}`,
    );
  },

  /** PUT /api/acceptance-rules/{documentTypeKey} — create/update an override. */
  upsert(
    documentTypeKey: string,
    body: AcceptanceRulesUpsertRequest,
  ): Promise<ResolvedAcceptanceRules> {
    return fetchJSON<ResolvedAcceptanceRules>(
      `/acceptance-rules/${encodeURIComponent(documentTypeKey)}`,
      { method: 'PUT', body: JSON.stringify(body) },
    );
  },

  /** DELETE /api/acceptance-rules/{documentTypeKey} — reset to the next tier. */
  reset(documentTypeKey: string): Promise<void> {
    return fetchJSON<void>(
      `/acceptance-rules/${encodeURIComponent(documentTypeKey)}`,
      { method: 'DELETE' },
    );
  },
};
