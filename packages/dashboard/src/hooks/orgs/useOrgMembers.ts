import { useEffect } from 'react';
import { useOrgStore } from '../../stores/orgs/org-store.js';

/**
 * Loads + subscribes to the active-tenant member list. Triggers a refetch
 * whenever `activeTenantId` flips (e.g. user switches tenant via the
 * existing `switch-org` flow).
 */
export function useOrgMembers() {
  const members = useOrgStore((s) => s.members);
  const total = useOrgStore((s) => s.membersTotal);
  const loading = useOrgStore((s) => s.membersLoading);
  const error = useOrgStore((s) => s.membersError);
  const tenantId = useOrgStore((s) => s.activeTenantId);
  const load = useOrgStore((s) => s.loadMembers);
  const updateRole = useOrgStore((s) => s.updateMemberRole);
  const remove = useOrgStore((s) => s.removeMember);

  useEffect(() => {
    if (tenantId) void load();
  }, [tenantId, load]);

  return { members, total, loading, error, reload: load, updateRole, remove };
}
