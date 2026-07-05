/**
 * useAutoRefresh — drives periodic re-fetching for a monitoring view.
 *
 * Story 23-12 (AC6). Calls the supplied fetcher at the configured interval,
 * pauses while the browser tab is backgrounded (`document.visibilityState`),
 * and optionally persists the chosen interval to localStorage so the
 * preference survives navigation/reload.
 */

import { useCallback, useEffect, useRef, useState } from 'react';

/** Refresh cadence in milliseconds; `null` disables auto-refresh. */
export type AutoRefreshInterval = number | null;

export interface UseAutoRefreshOptions {
  /** localStorage key used to persist the selected interval. */
  storageKey?: string;
  /** Interval used when nothing is persisted yet. Defaults to `null` (off). */
  defaultInterval?: AutoRefreshInterval;
}

export interface UseAutoRefreshResult {
  loading: boolean;
  error: Error | null;
  lastUpdated: Date | null;
  interval: AutoRefreshInterval;
  setInterval: (interval: AutoRefreshInterval) => void;
  refresh: () => Promise<void>;
}

function readStoredInterval(
  storageKey: string | undefined,
  fallback: AutoRefreshInterval,
): AutoRefreshInterval {
  if (!storageKey || typeof window === 'undefined') return fallback;
  try {
    const raw = window.localStorage.getItem(storageKey);
    if (raw === null) return fallback;
    if (raw === 'off') return null;
    const parsed = Number(raw);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
  } catch {
    return fallback;
  }
}

export function useAutoRefresh(
  fetcher: () => void | Promise<void>,
  options: UseAutoRefreshOptions = {},
): UseAutoRefreshResult {
  const { storageKey, defaultInterval = null } = options;

  const [interval, setIntervalState] = useState<AutoRefreshInterval>(() =>
    readStoredInterval(storageKey, defaultInterval),
  );
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<Error | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  // Track the latest fetcher without re-arming the interval on every render.
  const fetcherRef = useRef(fetcher);
  useEffect(() => {
    fetcherRef.current = fetcher;
  }, [fetcher]);

  const refresh = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      await fetcherRef.current();
      setLastUpdated(new Date());
    } catch (err) {
      setError(err instanceof Error ? err : new Error(String(err)));
    } finally {
      setLoading(false);
    }
  }, []);

  const setInterval = useCallback(
    (next: AutoRefreshInterval): void => {
      setIntervalState(next);
      if (storageKey && typeof window !== 'undefined') {
        try {
          window.localStorage.setItem(storageKey, next === null ? 'off' : String(next));
        } catch {
          /* ignore unavailable / full storage */
        }
      }
    },
    [storageKey],
  );

  useEffect(() => {
    if (interval === null) return;

    const tick = (): void => {
      // Skip work while the tab is hidden; the visibility handler refreshes on
      // return so the view is never stale for long.
      if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
      void refresh();
    };

    const timerId = globalThis.setInterval(tick, interval);

    const handleVisibility = (): void => {
      if (document.visibilityState === 'visible') void refresh();
    };
    document.addEventListener('visibilitychange', handleVisibility);

    return () => {
      globalThis.clearInterval(timerId);
      document.removeEventListener('visibilitychange', handleVisibility);
    };
  }, [interval, refresh]);

  return { loading, error, lastUpdated, interval, setInterval, refresh };
}
