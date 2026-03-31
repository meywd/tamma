import { useEffect } from 'react';
import { useAdminStore } from '../../stores/admin/store.js';

export function useCurrentUser() {
  const currentUser = useAdminStore((s) => s.currentUser);
  const loading = useAdminStore((s) => s.currentUserLoading);
  const error = useAdminStore((s) => s.currentUserError);
  const load = useAdminStore((s) => s.loadCurrentUser);

  useEffect(() => {
    if (!currentUser && !loading) {
      void load();
    }
  }, [currentUser, loading, load]);

  const isAdmin = currentUser?.role === 'admin' || currentUser?.role === 'owner';
  const isOwner = currentUser?.role === 'owner';

  return { user: currentUser, loading, error, isAdmin, isOwner, reload: load };
}
