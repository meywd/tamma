/**
 * useAcceptanceRules (Story 39-5)
 *
 * Loads the resolved-per-type acceptance-rules snapshot once on mount and
 * exposes the mutation surface (upsert override, reset override, refresh) used
 * by the admin acceptance-rules page. Mirrors `useSystemPrompts` — read-mostly,
 * page-scoped, no Zustand store; mutation handlers re-fetch so the table
 * reflects the new provenance on the next render.
 */

import { useCallback, useEffect, useState } from 'react';
import {
  acceptanceRulesApi,
  type AcceptanceRules,
  type AcceptanceRulesUpsertRequest,
  type ResolvedAcceptanceRules,
} from '../../services/admin/acceptance-rules-api-client.js';

export interface UseAcceptanceRulesResult {
  /** Resolved rules for every document type — empty while the first load is in flight. */
  rows: ResolvedAcceptanceRules[];
  /** The shipped principal-base defaults — `null` until loaded. */
  defaults: AcceptanceRules | null;
  loading: boolean;
  error: string | null;
  /** Refetch the resolved snapshot (used after a mutation to refresh provenance). */
  reload: () => Promise<void>;
  /** Save an override for a document type (or the literal `base` dial row). */
  upsert: (
    documentTypeKey: string,
    body: AcceptanceRulesUpsertRequest,
  ) => Promise<ResolvedAcceptanceRules>;
  /** Delete an override → resolves back to the next tier. */
  reset: (documentTypeKey: string) => Promise<void>;
}

export function useAcceptanceRules(): UseAcceptanceRulesResult {
  const [rows, setRows] = useState<ResolvedAcceptanceRules[]>([]);
  const [defaults, setDefaults] = useState<AcceptanceRules | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [resolved, def] = await Promise.all([
        acceptanceRulesApi.listEffective(),
        acceptanceRulesApi.getDefaults(),
      ]);
      setRows(resolved);
      setDefaults(def);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load acceptance rules');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const upsert = useCallback(
    async (documentTypeKey: string, body: AcceptanceRulesUpsertRequest) => {
      const saved = await acceptanceRulesApi.upsert(documentTypeKey, body);
      void reload();
      return saved;
    },
    [reload],
  );

  const reset = useCallback(
    async (documentTypeKey: string) => {
      await acceptanceRulesApi.reset(documentTypeKey);
      void reload();
    },
    [reload],
  );

  return { rows, defaults, loading, error, reload, upsert, reset };
}
