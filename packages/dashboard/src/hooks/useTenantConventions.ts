/**
 * useTenantConventions (Story 27-12)
 *
 * Data-fetching + mutation hook for the tenant convention UI.
 * Fetches the resolved convention list (GET /api/conventions) which includes
 * the `isOverride` flag. Exposes upsert/delete (tenant override), and a
 * helper to fetch the system default for a given (role, action) for diff view.
 */

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  conventionsApi,
  adminConventionsApi,
  type ConventionResponse,
  type UpsertConventionRequest,
} from '../services/admin/conventions-api-client.js';

export interface UseTenantConventionsReturn {
  conventions: ConventionResponse[];
  loading: boolean;
  error: string | null;
  overrideCount: number;
  fetchConventions: () => Promise<void>;
  get: (role: string, action: string) => Promise<ConventionResponse | null>;
  upsertOverride: (
    role: string,
    action: string,
    req: UpsertConventionRequest,
  ) => Promise<ConventionResponse>;
  deleteOverride: (role: string, action: string) => Promise<boolean>;
  getSystemDefault: (role: string, action: string) => Promise<ConventionResponse | null>;
}

export function useTenantConventions(): UseTenantConventionsReturn {
  const [conventions, setConventions] = useState<ConventionResponse[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchConventions = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await conventionsApi.list();
      setConventions(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load conventions');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchConventions();
  }, [fetchConventions]);

  const get = useCallback(async (role: string, action: string) => {
    try {
      return await conventionsApi.get(role, action);
    } catch {
      return null;
    }
  }, []);

  const upsertOverride = useCallback(
    async (role: string, action: string, req: UpsertConventionRequest) => {
      const saved = await conventionsApi.upsert(role, action, req);
      void fetchConventions();
      return saved;
    },
    [fetchConventions],
  );

  const deleteOverride = useCallback(
    async (role: string, action: string) => {
      try {
        await conventionsApi.delete(role, action);
        void fetchConventions();
        return true;
      } catch {
        return false;
      }
    },
    [fetchConventions],
  );

  const getSystemDefault = useCallback(async (role: string, action: string) => {
    try {
      return await adminConventionsApi.getDefault(role, action);
    } catch {
      return null;
    }
  }, []);

  const overrideCount = useMemo(
    () => conventions.filter((c) => c.isOverride === true).length,
    [conventions],
  );

  return {
    conventions,
    loading,
    error,
    overrideCount,
    fetchConventions,
    get,
    upsertOverride,
    deleteOverride,
    getSystemDefault,
  };
}
