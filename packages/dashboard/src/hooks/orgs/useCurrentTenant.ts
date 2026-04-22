/**
 * Resolves the caller's active tenant + role-in-tenant by hitting
 * `GET /auth/me` and caching the result in module-local memory. Used by
 * the tenant-admin UI to gate routes and pre-fill the org store with
 * `activeTenantId`.
 */

import { useEffect, useState } from 'react';
import {
  meApi,
  resolveActiveTenantRole,
  type MeUserFull,
} from '../../services/orgs/me-api-client.js';
import { useOrgStore } from '../../stores/orgs/org-store.js';

interface CurrentTenantState {
  loading: boolean;
  error: string | null;
  me: MeUserFull | null;
  tenantId: string | null;
  role: 'owner' | 'admin' | 'member' | null;
}

const initialState: CurrentTenantState = {
  loading: true,
  error: null,
  me: null,
  tenantId: null,
  role: null,
};

let cache: CurrentTenantState | null = null;

export function useCurrentTenant(): CurrentTenantState & { reload: () => Promise<void> } {
  const [state, setState] = useState<CurrentTenantState>(cache ?? initialState);
  const setActive = useOrgStore((s) => s.setActiveTenant);

  const load = async () => {
    setState((s) => ({ ...s, loading: true, error: null }));
    try {
      const me = await meApi.getFull();
      const active = resolveActiveTenantRole(me);
      const next: CurrentTenantState = {
        loading: false,
        error: null,
        me,
        tenantId: active?.tenantId ?? null,
        role: active?.role ?? null,
      };
      cache = next;
      setState(next);
      // Mirror the active tenant into the org store so member/invite/audit
      // hooks can fire without ad-hoc threading of the tenant id.
      setActive(active?.tenantId ?? null);
    } catch (err) {
      const next: CurrentTenantState = {
        loading: false,
        error: err instanceof Error ? err.message : 'Failed to load identity',
        me: null,
        tenantId: null,
        role: null,
      };
      cache = next;
      setState(next);
    }
  };

  useEffect(() => {
    if (!cache) void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return { ...state, reload: load };
}

/** Test seam — clears the module-local cache so a fresh fetch fires. */
export function _resetCurrentTenantCache(): void {
  cache = null;
}
