import { useEffect } from 'react';
import { useAdminStore } from '../../stores/admin/store.js';

export function useUsers(options?: { limit?: number; offset?: number }) {
  const users = useAdminStore((s) => s.users);
  const total = useAdminStore((s) => s.usersTotal);
  const loading = useAdminStore((s) => s.usersLoading);
  const error = useAdminStore((s) => s.usersError);
  const load = useAdminStore((s) => s.loadUsers);
  const updateRole = useAdminStore((s) => s.updateUserRole);
  const remove = useAdminStore((s) => s.removeUser);
  const invite = useAdminStore((s) => s.createInvite);

  useEffect(() => {
    void load(options);
  }, [load, options?.limit, options?.offset]);

  return { users, total, loading, error, reload: load, updateRole, remove, invite };
}
