/**
 * Prompts API Client (Story 27-4)
 *
 * Typed wrapper for `/api/prompts/*` endpoints exposed by
 * `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs`.
 *
 * Backend semantics (see CLAUDE.md "Prompt Store Architecture"):
 *
 *   - `GET    /api/prompts/system`               — bulk read of every layer
 *                                                  of the system-shipped
 *                                                  registry (read-only).
 *   - `GET    /api/prompts/system/:role/:action` — single role+action default.
 *   - `GET    /api/prompts/defaults/:action`     — single action default
 *                                                  (the layer-4 safety net).
 *   - `PUT    /api/prompts/:role/:action`        — upsert a USER OVERRIDE
 *                                                  for the calling user.
 *                                                  Mutating a system default
 *                                                  for everyone is intentionally
 *                                                  not exposed; admins
 *                                                  customise via overrides.
 *   - `DELETE /api/prompts/:role/:action`        — remove the caller's
 *                                                  override (resolves back
 *                                                  to system default).
 *   - `PUT    /api/prompts/system/:role`         — upsert the caller's
 *                                                  role-system preamble
 *                                                  override (no action axis).
 *   - `DELETE /api/prompts/system/:role`         — remove that override.
 *
 * The Story 27-4 admin UI presents the system grid and lets owners save
 * overrides on top of any cell; the read APIs let it surface "system" vs
 * "user" provenance via the `source` field on every PromptResponse.
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });

  if (!response.ok) {
    const error = await response
      .json()
      .catch(() => ({ error: response.statusText }));
    const err = new Error(
      (error as Record<string, string>).error ?? `HTTP ${response.status}`,
    );
    (err as Error & { status?: number }).status = response.status;
    throw err;
  }

  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

// === Types matching PromptDtos.cs ==========================================

/** Provenance of a resolved prompt — `"system"` (shipped) or `"user"` (override). */
export type PromptSource = 'system' | 'user';

export interface PromptResponse {
  role: string | null;
  action: string | null;
  template: string;
  systemPrompt: string | null;
  variables: string[] | null;
  enableTools: boolean;
  maxTokens: number;
  source: PromptSource;
}

/** Bulk payload returned by `GET /api/prompts/system`. */
export interface SystemDefaultsResponse {
  roleActionTemplates: PromptResponse[];
  /** `{ [role]: identityPrompt }` — the 8 role preambles. */
  systemPrompts: Record<string, string>;
  /** `{ [action]: ActionDefault }` — the 10 layer-4 safety-net templates. */
  actionDefaults: Record<string, PromptResponse>;
}

export interface UpsertPromptRequest {
  template: string;
  systemPrompt?: string | null;
  variables?: string[] | null;
  enableTools?: boolean | null;
  maxTokens?: number | null;
}

export interface ConventionTemplateSummary {
  key: string;
  name: string;
  description: string;
}

export interface ConventionTemplate extends ConventionTemplateSummary {
  /** Field name from `ConventionTemplates.All` C# record — the full body. */
  conventions: string;
}

// === API ===================================================================

export const promptsApi = {
  /**
   * Fetch every system-shipped prompt layer in a single call. The response
   * matches `SystemDefaultsResponse` from `PromptDtos.cs`.
   */
  listSystemDefaults: () =>
    fetchJSON<SystemDefaultsResponse>('/prompts/system'),

  /** Fetch one role+action system default (read-only). */
  getSystemDefault: (role: string, action: string) =>
    fetchJSON<PromptResponse>(
      `/prompts/system/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
    ),

  /**
   * Fetch the resolved prompt for the calling user. This applies the
   * 4-layer resolution order — user override > system default for the
   * (role, action) > user action default > system action default — so
   * the response represents "what would actually be sent to the LLM".
   */
  getResolved: (role: string, action: string) =>
    fetchJSON<PromptResponse>(
      `/prompts/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
    ),

  /**
   * Save (insert or update) the caller's user-override for a role+action.
   * The PromptStoreService scopes overrides by `userId` automatically.
   */
  upsertOverride: (
    role: string,
    action: string,
    body: UpsertPromptRequest,
  ) =>
    fetchJSON<PromptResponse>(
      `/prompts/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
      { method: 'PUT', body: JSON.stringify(body) },
    ),

  /**
   * Delete the caller's user-override. After this returns, the resolved
   * prompt falls back to the system default.
   */
  deleteOverride: (role: string, action: string) =>
    fetchJSON<{ message: string }>(
      `/prompts/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
      { method: 'DELETE' },
    ),

  /** Save the caller's role-system preamble override. */
  upsertSystemPromptOverride: (role: string, body: UpsertPromptRequest) =>
    fetchJSON<{ message: string; scope: string; role: string }>(
      `/prompts/system/${encodeURIComponent(role)}`,
      { method: 'PUT', body: JSON.stringify(body) },
    ),

  /** Delete the caller's role-system preamble override. */
  deleteSystemPromptOverride: (role: string) =>
    fetchJSON<{ message: string }>(
      `/prompts/system/${encodeURIComponent(role)}`,
      { method: 'DELETE' },
    ),
};

export const conventionTemplatesApi = {
  list: () => fetchJSON<ConventionTemplateSummary[]>('/convention-templates'),

  /** Returns the full template incl. `conventions` body — the heavy field. */
  get: (key: string) =>
    fetchJSON<ConventionTemplate>(
      `/convention-templates/${encodeURIComponent(key)}`,
    ),
};
