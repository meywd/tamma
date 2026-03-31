import { useEffect, useRef } from 'react';
import { useAdminStore } from '../../stores/admin/store.js';

const POLL_INTERVAL_MS = 30_000;

export function useSystemHealth() {
  const services = useAdminStore((s) => s.services);
  const loading = useAdminStore((s) => s.healthLoading);
  const error = useAdminStore((s) => s.healthError);
  const load = useAdminStore((s) => s.loadHealth);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    void load();

    intervalRef.current = setInterval(() => {
      void load();
    }, POLL_INTERVAL_MS);

    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
      }
    };
  }, [load]);

  return { services, loading, error, reload: load };
}
