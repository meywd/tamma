/**
 * useSystemPrompts (Story 27-4)
 *
 * Loads the bulk system-defaults snapshot once on mount and exposes the
 * mutation surface (upsert override, reset override, refresh) used by
 * the admin prompts page.
 *
 * The hook keeps state local — no Zustand store — because the data is
 * essentially read-mostly and scoped to one page; the mutation handlers
 * locally re-fetch the snapshot so the table reflects the new override
 * source on the next render.
 */

import { useCallback, useEffect, useState } from 'react';
import {
  promptsApi,
  type PromptResponse,
  type SystemDefaultsResponse,
  type UpsertPromptRequest,
} from '../../services/admin/prompts-api-client.js';

export interface UseSystemPromptsResult {
  /** The full system-defaults snapshot — `null` while the first load is in flight. */
  data: SystemDefaultsResponse | null;
  loading: boolean;
  error: string | null;
  /** Refetch the bulk snapshot (used after a mutation to refresh provenance). */
  reload: () => Promise<void>;
  /** Resolve the prompt for the calling user — used in the edit drawer. */
  getResolved: (role: string, action: string) => Promise<PromptResponse>;
  /** Save the caller's user-override for a role+action cell. */
  upsertOverride: (
    role: string,
    action: string,
    body: UpsertPromptRequest,
  ) => Promise<PromptResponse>;
  /** Delete the caller's user-override; resolves back to the system default. */
  resetOverride: (role: string, action: string) => Promise<void>;
  /** Save a role-system preamble override (no action axis). */
  upsertSystemPromptOverride: (
    role: string,
    body: UpsertPromptRequest,
  ) => Promise<void>;
  /** Delete the role-system preamble override. */
  resetSystemPromptOverride: (role: string) => Promise<void>;
}

export function useSystemPrompts(): UseSystemPromptsResult {
  const [data, setData] = useState<SystemDefaultsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const snapshot = await promptsApi.listSystemDefaults();
      setData(snapshot);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load prompts');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const getResolved = useCallback(
    (role: string, action: string) => promptsApi.getResolved(role, action),
    [],
  );

  const upsertOverride = useCallback(
    async (role: string, action: string, body: UpsertPromptRequest) => {
      const saved = await promptsApi.upsertOverride(role, action, body);
      // Refresh in the background so the table reflects the new "user"
      // provenance without blocking the dialog close.
      void reload();
      return saved;
    },
    [reload],
  );

  const resetOverride = useCallback(
    async (role: string, action: string) => {
      await promptsApi.deleteOverride(role, action);
      void reload();
    },
    [reload],
  );

  const upsertSystemPromptOverride = useCallback(
    async (role: string, body: UpsertPromptRequest) => {
      await promptsApi.upsertSystemPromptOverride(role, body);
      void reload();
    },
    [reload],
  );

  const resetSystemPromptOverride = useCallback(
    async (role: string) => {
      await promptsApi.deleteSystemPromptOverride(role);
      void reload();
    },
    [reload],
  );

  return {
    data,
    loading,
    error,
    reload,
    getResolved,
    upsertOverride,
    resetOverride,
    upsertSystemPromptOverride,
    resetSystemPromptOverride,
  };
}
