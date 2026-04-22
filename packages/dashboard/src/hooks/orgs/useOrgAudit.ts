import { useEffect } from 'react';
import { useOrgStore } from '../../stores/orgs/org-store.js';

export function useOrgAudit(options?: { type?: string; limit?: number; offset?: number }) {
  const events = useOrgStore((s) => s.auditEvents);
  const total = useOrgStore((s) => s.auditTotal);
  const loading = useOrgStore((s) => s.auditLoading);
  const error = useOrgStore((s) => s.auditError);
  const tenantId = useOrgStore((s) => s.activeTenantId);
  const load = useOrgStore((s) => s.loadAudit);

  useEffect(() => {
    if (tenantId) void load(options);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId, options?.type, options?.limit, options?.offset, load]);

  return { events, total, loading, error, reload: load };
}
