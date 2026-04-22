/**
 * useTenantPrompts — data hook for the tenant-member Prompt Store UI
 * (Story 27-5).
 *
 * The C# API exposes:
 *   - `GET /api/prompts`                   — user overrides (array)
 *   - `GET /api/prompts/system`            — system defaults (bundle)
 *   - `GET /api/prompts/:role/:action`     — resolved prompt
 *   - `PUT /api/prompts/:role/:action`     — upsert override
 *   - `DELETE /api/prompts/:role/:action`  — delete override
 *   - `POST /api/prompts/:role/:action/render` — render with variables
 *
 * The dashboard needs a merged view: every system role+action template with
 * any tenant override layered on top. The API does not yet expose a merged
 * projection (Story 28-1 splits account/tenant scopes properly), so this hook
 * fetches both endpoints and merges client-side — AC #2, AC #3, AC #12.
 */

import { useCallback, useEffect, useMemo, useState } from 'react';

export type PromptSource = 'system' | 'user';

export interface ResolvedPrompt {
  role: string;
  action: string;
  template: string;
  systemPrompt: string;
  variables: string[];
  enableTools: boolean;
  maxTokens: number;
  source: PromptSource;
}

export interface PromptDetail extends ResolvedPrompt {
  /** Currently unused but reserved for a future wire-version bump. */
  version?: number;
}

export interface RenderedResult {
  role: string;
  action: string;
  version: number;
  renderedTemplate: string;
  renderedSystemPrompt: string;
  enableTools: boolean;
  maxTokens: number;
  unresolvedVariables: string[];
}

export interface UpsertPromptInput {
  template: string;
  systemPrompt?: string;
  variables?: string[];
  enableTools?: boolean;
  maxTokens?: number;
}

export interface UseTenantPromptsReturn {
  prompts: ResolvedPrompt[];
  loading: boolean;
  error: string | null;
  overrideCount: number;
  fetchPrompts: () => Promise<void>;
  getPrompt: (role: string, action: string) => Promise<PromptDetail | null>;
  upsertOverride: (
    role: string,
    action: string,
    input: UpsertPromptInput,
  ) => Promise<PromptDetail>;
  deleteOverride: (role: string, action: string) => Promise<boolean>;
  renderPreview: (
    role: string,
    action: string,
    variables: Record<string, string>,
  ) => Promise<RenderedResult | null>;
}

// -----------------------------------------------------------------------
// Wire types — these match Tamma.Api.Dtos.Prompts.PromptResponse /
// SystemDefaultsResponse / RenderedPromptResponse (PascalCase on the wire
// because System.Text.Json defaults to the C# property casing).
// -----------------------------------------------------------------------

interface WirePromptResponse {
  Role?: string | null;
  Action?: string | null;
  Template: string;
  SystemPrompt?: string | null;
  Variables?: string[] | null;
  EnableTools: boolean;
  MaxTokens: number;
  Source: string;
}

interface WireSystemDefaults {
  RoleActionTemplates: WirePromptResponse[];
  SystemPrompts: Record<string, string>;
  ActionDefaults: Record<string, WirePromptResponse>;
}

interface WireRenderedPromptResponse {
  Role: string;
  Action: string;
  Version: number;
  RenderedTemplate: string;
  RenderedSystemPrompt: string;
  EnableTools: boolean;
  MaxTokens: number;
  UnresolvedVariables: string[];
}

function toResolvedPrompt(w: WirePromptResponse): ResolvedPrompt {
  const source: PromptSource = w.Source === 'user' ? 'user' : 'system';
  return {
    role: w.Role ?? '',
    action: w.Action ?? '',
    template: w.Template,
    systemPrompt: w.SystemPrompt ?? '',
    variables: w.Variables ?? [],
    enableTools: w.EnableTools,
    maxTokens: w.MaxTokens,
    source,
  };
}

function keyOf(role: string, action: string): string {
  return `${role}::${action}`;
}

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    credentials: 'include',
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  });
  if (!res.ok) {
    let err = `HTTP ${res.status}`;
    try {
      const body = (await res.json()) as { error?: string };
      if (body?.error) err = body.error;
    } catch {
      // keep default
    }
    throw new Error(err);
  }
  return (await res.json()) as T;
}

export function useTenantPrompts(): UseTenantPromptsReturn {
  const [prompts, setPrompts] = useState<ResolvedPrompt[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchPrompts = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      // Pull both the shipped system registry (80 role+action templates)
      // and the current user's overrides. Merge so every row in the table
      // carries a `source` discriminator.
      const [systemBundle, userOverrides] = await Promise.all([
        fetchJson<WireSystemDefaults>('/api/prompts/system'),
        fetchJson<WirePromptResponse[]>('/api/prompts').catch(() => [] as WirePromptResponse[]),
      ]);

      const overrideMap = new Map<string, WirePromptResponse>();
      for (const o of userOverrides) {
        if (o.Role && o.Action) {
          overrideMap.set(keyOf(o.Role, o.Action), o);
        }
      }

      const merged: ResolvedPrompt[] = systemBundle.RoleActionTemplates.map((sys) => {
        const k = keyOf(sys.Role ?? '', sys.Action ?? '');
        const override = overrideMap.get(k);
        return override ? toResolvedPrompt(override) : toResolvedPrompt(sys);
      });

      // Surface overrides that are not backed by a shipped default (defensive —
      // shouldn't happen with the current 8×10 catalogue but guards against
      // drift once more roles ship).
      for (const [k, o] of overrideMap) {
        if (!merged.some((m) => keyOf(m.role, m.action) === k)) {
          merged.push(toResolvedPrompt(o));
        }
      }

      setPrompts(merged);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch prompts');
    } finally {
      setLoading(false);
    }
  }, []);

  const getPrompt = useCallback(
    async (role: string, action: string): Promise<PromptDetail | null> => {
      try {
        const w = await fetchJson<WirePromptResponse>(
          `/api/prompts/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
        );
        return toResolvedPrompt(w);
      } catch {
        return null;
      }
    },
    [],
  );

  const upsertOverride = useCallback(
    async (
      role: string,
      action: string,
      input: UpsertPromptInput,
    ): Promise<PromptDetail> => {
      const body: Record<string, unknown> = { Template: input.template };
      if (input.systemPrompt !== undefined) body['SystemPrompt'] = input.systemPrompt;
      if (input.variables !== undefined) body['Variables'] = input.variables;
      if (input.enableTools !== undefined) body['EnableTools'] = input.enableTools;
      if (input.maxTokens !== undefined) body['MaxTokens'] = input.maxTokens;

      const saved = await fetchJson<WirePromptResponse>(
        `/api/prompts/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
        {
          method: 'PUT',
          body: JSON.stringify(body),
        },
      );
      return toResolvedPrompt(saved);
    },
    [],
  );

  const deleteOverride = useCallback(
    async (role: string, action: string): Promise<boolean> => {
      try {
        await fetchJson<unknown>(
          `/api/prompts/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
          { method: 'DELETE' },
        );
        return true;
      } catch {
        return false;
      }
    },
    [],
  );

  const renderPreview = useCallback(
    async (
      role: string,
      action: string,
      variables: Record<string, string>,
    ): Promise<RenderedResult | null> => {
      try {
        const w = await fetchJson<WireRenderedPromptResponse>(
          `/api/prompts/${encodeURIComponent(role)}/${encodeURIComponent(action)}/render`,
          { method: 'POST', body: JSON.stringify({ Variables: variables }) },
        );
        return {
          role: w.Role,
          action: w.Action,
          version: w.Version,
          renderedTemplate: w.RenderedTemplate,
          renderedSystemPrompt: w.RenderedSystemPrompt,
          enableTools: w.EnableTools,
          maxTokens: w.MaxTokens,
          unresolvedVariables: w.UnresolvedVariables ?? [],
        };
      } catch {
        return null;
      }
    },
    [],
  );

  useEffect(() => {
    void fetchPrompts();
  }, [fetchPrompts]);

  const overrideCount = useMemo(
    () => prompts.filter((p) => p.source === 'user').length,
    [prompts],
  );

  return {
    prompts,
    loading,
    error,
    overrideCount,
    fetchPrompts,
    getPrompt,
    upsertOverride,
    deleteOverride,
    renderPreview,
  };
}
