import { useEffect } from 'react';
import { useOrgStore } from '../../stores/orgs/org-store.js';

export function useOrgInvites() {
  const invites = useOrgStore((s) => s.invites);
  const loading = useOrgStore((s) => s.invitesLoading);
  const error = useOrgStore((s) => s.invitesError);
  const tenantId = useOrgStore((s) => s.activeTenantId);
  const load = useOrgStore((s) => s.loadInvites);
  const create = useOrgStore((s) => s.createInvite);
  const resend = useOrgStore((s) => s.resendInvite);
  const revoke = useOrgStore((s) => s.revokeInvite);

  useEffect(() => {
    if (tenantId) void load();
  }, [tenantId, load]);

  return { invites, loading, error, reload: load, create, resend, revoke };
}
