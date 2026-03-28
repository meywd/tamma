import { useEffect } from 'react';
import { useAdminStore } from '../../stores/admin/store.js';

export function useApiKeys() {
  const apiKeys = useAdminStore((s) => s.apiKeys);
  const loading = useAdminStore((s) => s.apiKeysLoading);
  const error = useAdminStore((s) => s.apiKeysError);
  const loadAll = useAdminStore((s) => s.loadAllApiKeys);
  const create = useAdminStore((s) => s.createApiKey);
  const revoke = useAdminStore((s) => s.revokeApiKey);
  const users = useAdminStore((s) => s.users);

  useEffect(() => {
    // Only load if we have users (keys are per-user)
    if (users.length > 0) {
      void loadAll();
    }
  }, [loadAll, users.length]);

  return { apiKeys, loading, error, reload: loadAll, create, revoke };
}
