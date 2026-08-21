/**
 * Actions Policy API Client (autonomy-dial governance)
 *
 * Typed wrapper for the `/api/actions/*` surface exposed by
 * `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs` and
 * `ActionAuthorizationEndpoints.cs`:
 *
 *   GET  /api/actions/dial                                — the server-owned dial range
 *   GET  /api/actions/catalog                             — the full action vocabulary
 *   GET  /api/actions/policy[?level=NN]                   — the resolved policy view
 *   POST /api/actions/policy/reset                        — clear overrides (all, or named)
 *   PUT/DELETE /api/actions/policy/groups/{group}/…       — per-group threshold rows
 *   PUT/DELETE /api/actions/policy/actions/{ns}/{key}/…   — per-action rows
 *   GET  /api/actions/authorizations                      — what is waiting on a person
 *   POST /api/actions/authorizations/{id}/decide          — grant or deny one of them
 *
 * The validated dial range is SERVER-OWNED (Tamma.Core AutonomyDial — its doc
 * forbids restating a bound as validation anywhere else). This client performs
 * NO range validation: it sends what it is given and surfaces the server's 400.
 * UI affordances (slider/input min-max) should mirror the values the `/dial`
 * endpoint returns, never a hardcoded constant.
 *
 * Provenance: each resolved action row carries `source` — which tier supplied
 * its effective threshold (system default, group/action override, platform
 * ceiling, or the legacy always-escalate floor).
 *
 * RBAC: reads are any authenticated member (AuthenticatedAny); policy writes
 * and the decide endpoint require `actions:manage` (tenant_owner/tenant_admin
 * — members get 403).
 *
 * Notable error codes surfaced by the API:
 *   ACTION_POLICY.INVALID                     (400) — bad level/threshold value
 *   ACTION_POLICY.MISSING_FIELD               (400) — single-field write body missing its field
 *   ACTION_POLICY.UNKNOWN_ACTION / _GROUP     (400) — a typo'd wire key
 *   ACTION_POLICY.MACHINERY_NOT_DIAL_GOVERNED (400) — threshold write on machinery
 *   ACTION_POLICY.NOT_ENFORCEABLE             (400) — threshold write on an informational row
 *   ACTION_POLICY.LEVEL_OWNED                 (409) — toggle on an already-automated action
 *   ACTION_AUTHORIZATION.NOT_PENDING          (409) — decide on a row no longer pending
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

/**
 * GET /api/actions/dial — the server-owned dial constants
 * (`AutonomyDial.Min/Max/AlwaysHuman` + the shipped default level). `default`
 * is the level a fresh deployment ships at, NOT `min`.
 */
export interface AutonomyDialInfo {
  min: number;
  max: number;
  /** Sentinel threshold above `max` meaning "a person decides at every level". */
  alwaysHuman: number;
  default: number;
}

/** One row of GET /api/actions/catalog. */
export interface CatalogAction {
  /** Full wire key, e.g. `"scw:pr.merge"`. */
  key: string;
  /** Namespace half of the key. */
  ns: string;
  group: string;
  risk: string;
  title: string;
  summary: string;
  reversible: boolean;
  defaultMinAutonomy: number;
  escalatableToHuman: boolean;
  enforceable: boolean;
  siteKey: string;
  /**
   * The concrete sites the RUNNING host has bound for this action. EMPTY means
   * the row governs nothing right now — render it as such, never as governed.
   */
  enforcementSites: string[];
}

/** Which tier supplied an action's effective threshold. */
export type ActionPolicySource =
  | 'system-default'
  | 'group-override'
  | 'action-override'
  | 'platform-ceiling'
  | 'always-escalate-legacy';

/** One resolved action row of GET /api/actions/policy. */
export interface PolicyAction {
  key: string;
  group: string;
  risk: string;
  title: string;
  summary: string;
  siteKey: string;
  /** The effective minimum autonomy after the whole ladder is applied. */
  minAutonomy: number;
  source: ActionPolicySource;
  enforce: boolean;
  enabled: boolean;
  allowedRoles: string[] | null;
  escalatableToHuman: boolean;
  enforceable: boolean;
  /** Machinery (deterministic plumbing) is never dial-governed. */
  isMachinery: boolean;
  /** The shipped catalog level — distinct from the resolved `minAutonomy`. */
  shippedLevel: number;
  /** The ladder resolution WITHOUT a per-action row — what a delete falls back to. */
  ladderWithoutRow: number;
  /** True when the view level automates this action (same comparison the gate applies). */
  automatedAtLevel: boolean;
  /** True when the dial alone already automates it — a per-action toggle would be redundant (409). */
  levelOwned: boolean;
  /** True when a per-action "force on" toggle is a legal write. */
  editable: boolean;
  /** Why: `level-owned` | `editable` | `machinery-not-dial-governed` | `not-enforceable`. */
  reason: string;
  /** An explicit per-action toggle standing above the current dial. */
  toggleAboveDial: boolean;
  enforcementSites: string[];
}

/** A stored assignment row as echoed inside the policy group view. */
export interface PolicyAssignmentRow {
  minAutonomy: number | null;
  enforce: boolean | null;
  enabled: boolean | null;
  allowedRoles: string[] | null;
}

export interface PolicyGroup {
  group: string;
  description: string;
  members: number;
  /** The principal's (tenant/user) stored group row, if any. */
  principalRow: PolicyAssignmentRow | null;
  /** The platform ceiling's stored group row, if any. */
  platformRow: PolicyAssignmentRow | null;
}

export interface PolicyDial extends AutonomyDialInfo {
  /** The principal's current dial position. */
  current: number;
  /** The level this policy view was computed at (`?level=` or the current dial). */
  viewLevel: number;
}

/** GET /api/actions/policy response. */
export interface ActionPolicyResponse {
  dial: PolicyDial;
  groups: PolicyGroup[];
  actions: PolicyAction[];
}

/** PUT …/actions/{ns}/{key}/threshold response. */
export interface SetActionThresholdResult {
  key: string;
  minAutonomy: number;
  dialAtMint: number;
}

/** PUT …/actions/{ns}/{key}/enforce | /enabled | /roles response. */
export interface SetActionFieldResult {
  key: string;
  field: string;
  value: unknown;
}

/** DELETE …/actions/{ns}/{key} response — names what now applies. */
export interface DeleteActionResult {
  message: string;
  nowResolvesTo: number;
  source: string;
  reason: string;
}

/** PUT …/groups/{group}/threshold response. */
export interface SetGroupThresholdResult {
  group: string;
  minAutonomy: number;
}

export interface DeleteGroupResult {
  message: string;
}

/**
 * POST /api/actions/policy/reset response. Without targets: `removed` counts
 * every deleted principal row. With targets (bulk revoke of named per-action
 * rows): the per-wire breakdown is included.
 */
export interface ResetPolicyResult {
  removed: number;
  deleted?: string[];
  missing?: string[];
  unknown?: string[];
}

export type AuthorizationState = 'pending' | 'granted' | 'denied' | 'expired';

/** One row of GET /api/actions/authorizations. */
export interface ActionAuthorization {
  id: string;
  correlationId: string;
  targetKind: string;
  targetKey: string;
  state: AuthorizationState;
  requestedAtUtc: string;
  decidedAtUtc: string | null;
  decidedByUserId: string | null;
  expiresAtUtc: string | null;
  consumedAtUtc: string | null;
  autonomyLevelAtRequest: number | null;
  reason: string | null;
  /**
   * A row can be past its expiry while still saying `pending` (expiry is
   * enforced at the transition, not by a sweeper) — when true, a decide
   * would 409, so hide/disable the decision buttons.
   */
  expired: boolean;
}

export interface AuthorizationListResponse {
  state: string;
  count: number;
  authorizations: ActionAuthorization[];
}

/** POST …/authorizations/{id}/decide response. */
export interface DecideAuthorizationResult {
  id: string;
  state: string;
  correlationId: string;
  targetKind: string;
  targetKey: string;
  decidedAtUtc: string | null;
  decidedByUserId: string | null;
  expiresAtUtc: string | null;
  reason: string | null;
}

export type AuthorizationDecision = 'granted' | 'denied';

// === Client ==================================================================

/** `"ns:rest.of.key"` → the `/policy/actions/{ns}/{key}` path segments. */
function actionPath(wireKey: string): string {
  const idx = wireKey.indexOf(':');
  const ns = idx === -1 ? wireKey : wireKey.slice(0, idx);
  const key = idx === -1 ? '' : wireKey.slice(idx + 1);
  return `/actions/policy/actions/${encodeURIComponent(ns)}/${encodeURIComponent(key)}`;
}

export const actionsPolicyApi = {
  /** GET /api/actions/dial — the server-owned dial range + shipped default. */
  getDial(): Promise<AutonomyDialInfo> {
    return fetchJSON<AutonomyDialInfo>('/actions/dial');
  },

  /** GET /api/actions/catalog — the full code-resident action vocabulary. */
  getCatalog(): Promise<CatalogAction[]> {
    return fetchJSON<CatalogAction[]>('/actions/catalog');
  },

  /** GET /api/actions/policy — the resolved view, optionally at a what-if level. */
  getPolicy(level?: number): Promise<ActionPolicyResponse> {
    const query = level !== undefined ? `?level=${encodeURIComponent(level)}` : '';
    return fetchJSON<ActionPolicyResponse>(`/actions/policy${query}`);
  },

  /**
   * PUT /api/actions/policy/actions/{ns}/{key}/threshold — the per-action
   * "force on" toggle. The server accepts exactly one value (its own dial
   * minimum, from GET /dial); anything else is a 400, and an already
   * dial-automated action is a 409 (ACTION_POLICY.LEVEL_OWNED).
   */
  setActionThreshold(wireKey: string, minAutonomy: number): Promise<SetActionThresholdResult> {
    return fetchJSON<SetActionThresholdResult>(`${actionPath(wireKey)}/threshold`, {
      method: 'PUT',
      body: JSON.stringify({ minAutonomy }),
    });
  },

  /** PUT /api/actions/policy/actions/{ns}/{key}/enforce */
  setActionEnforce(wireKey: string, enforce: boolean): Promise<SetActionFieldResult> {
    return fetchJSON<SetActionFieldResult>(`${actionPath(wireKey)}/enforce`, {
      method: 'PUT',
      body: JSON.stringify({ enforce }),
    });
  },

  /** PUT /api/actions/policy/actions/{ns}/{key}/enabled */
  setActionEnabled(wireKey: string, enabled: boolean): Promise<SetActionFieldResult> {
    return fetchJSON<SetActionFieldResult>(`${actionPath(wireKey)}/enabled`, {
      method: 'PUT',
      body: JSON.stringify({ enabled }),
    });
  },

  /** PUT /api/actions/policy/actions/{ns}/{key}/roles — empty array clears the restriction. */
  setActionRoles(wireKey: string, allowedRoles: string[]): Promise<SetActionFieldResult> {
    return fetchJSON<SetActionFieldResult>(`${actionPath(wireKey)}/roles`, {
      method: 'PUT',
      body: JSON.stringify({ allowedRoles }),
    });
  },

  /** DELETE /api/actions/policy/actions/{ns}/{key} — remove the per-action row. */
  deleteActionOverride(wireKey: string): Promise<DeleteActionResult> {
    return fetchJSON<DeleteActionResult>(actionPath(wireKey), { method: 'DELETE' });
  },

  /** PUT /api/actions/policy/groups/{group}/threshold */
  setGroupThreshold(group: string, minAutonomy: number): Promise<SetGroupThresholdResult> {
    return fetchJSON<SetGroupThresholdResult>(
      `/actions/policy/groups/${encodeURIComponent(group)}/threshold`,
      { method: 'PUT', body: JSON.stringify({ minAutonomy }) },
    );
  },

  /** DELETE /api/actions/policy/groups/{group} — remove the per-group row. */
  deleteGroupOverride(group: string): Promise<DeleteGroupResult> {
    return fetchJSON<DeleteGroupResult>(
      `/actions/policy/groups/${encodeURIComponent(group)}`,
      { method: 'DELETE' },
    );
  },

  /**
   * POST /api/actions/policy/reset — with no targets, delete every override
   * this principal has stored; with targets, delete exactly those per-action
   * rows (bulk revoke).
   */
  resetPolicy(targets?: string[]): Promise<ResetPolicyResult> {
    return fetchJSON<ResetPolicyResult>('/actions/policy/reset', {
      method: 'POST',
      body: JSON.stringify(targets !== undefined ? { targets } : {}),
    });
  },

  /** GET /api/actions/authorizations — what is waiting on a person (default: pending). */
  listAuthorizations(
    state?: AuthorizationState | 'all',
  ): Promise<AuthorizationListResponse> {
    const query = state !== undefined ? `?state=${encodeURIComponent(state)}` : '';
    return fetchJSON<AuthorizationListResponse>(`/actions/authorizations${query}`);
  },

  /**
   * POST /api/actions/authorizations/{id}/decide — grant or deny one pending
   * authorization. A row already decided / expired / unknown answers 409.
   */
  decideAuthorization(
    id: string,
    decision: AuthorizationDecision,
    reason?: string,
  ): Promise<DecideAuthorizationResult> {
    return fetchJSON<DecideAuthorizationResult>(
      `/actions/authorizations/${encodeURIComponent(id)}/decide`,
      { method: 'POST', body: JSON.stringify({ decision, reason: reason ?? null }) },
    );
  },
};
