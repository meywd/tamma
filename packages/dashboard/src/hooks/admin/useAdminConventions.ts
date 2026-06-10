/**
 * useAdminConventions (Story 27-11)
 *
 * Data-fetching + mutation hook for the platform-owner convention admin page.
 * Loads the full system-defaults list from `GET /api/conventions/defaults` and
 * exposes upsert/reset/delete mutations, each of which re-fetches the list on
 * success so the table reflects the new state.
 *
 * Registry (roles, actions, eligible pairs) is fetched once and cached in
 * component state — it's immutable for the lifetime of the process.
 */

import { useCallback, useEffect, useState } from 'react';
import {
  adminConventionsApi,
  conventionRegistryApi,
  type ConventionResponse,
  type UpsertConventionRequest,
} from '../../services/admin/conventions-api-client.js';

export interface EligiblePair {
  role: string;
  action: string;
}

export interface UseAdminConventionsResult {
  conventions: ConventionResponse[];
  roles: string[];
  actions: string[];
  eligiblePairs: EligiblePair[];
  loading: boolean;
  error: string | null;
  reload: () => Promise<void>;
  getDefault: (role: string, action: string) => Promise<ConventionResponse | null>;
  upsert: (role: string, action: string, req: UpsertConventionRequest) => Promise<ConventionResponse>;
  reset: (role: string, action: string) => Promise<ConventionResponse>;
  remove: (role: string, action: string) => Promise<void>;
}

export function useAdminConventions(): UseAdminConventionsResult {
  const [conventions, setConventions] = useState<ConventionResponse[]>([]);
  const [roles, setRoles] = useState<string[]>([]);
  const [actions, setActions] = useState<string[]>([]);
  const [eligiblePairs, setEligiblePairs] = useState<EligiblePair[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await adminConventionsApi.listDefaults();
      setConventions(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load conventions');
    } finally {
      setLoading(false);
    }
  }, []);

  // Load registry (roles, actions, eligible pairs) once on mount.
  useEffect(() => {
    void (async () => {
      try {
        const pairs = await conventionRegistryApi.getRoleActions();
        const roleSet = Array.from(new Set(pairs.map((p) => p.role))).sort();
        const actionSet = Array.from(new Set(pairs.map((p) => p.action))).sort();
        setRoles(roleSet);
        setActions(actionSet);
        setEligiblePairs(pairs);
      } catch {
        // Registry failure is non-fatal — dropdowns will just be empty.
      }
    })();
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const getDefault = useCallback(async (role: string, action: string) => {
    try {
      return await adminConventionsApi.getDefault(role, action);
    } catch {
      return null;
    }
  }, []);

  const upsert = useCallback(
    async (role: string, action: string, req: UpsertConventionRequest) => {
      const saved = await adminConventionsApi.upsert(role, action, req);
      void reload();
      return saved;
    },
    [reload],
  );

  const reset = useCallback(
    async (role: string, action: string) => {
      const result = await adminConventionsApi.reset(role, action);
      void reload();
      return result;
    },
    [reload],
  );

  const remove = useCallback(
    async (role: string, action: string) => {
      await adminConventionsApi.delete(role, action);
      void reload();
    },
    [reload],
  );

  return {
    conventions,
    roles,
    actions,
    eligiblePairs,
    loading,
    error,
    reload,
    getDefault,
    upsert,
    reset,
    remove,
  };
}
