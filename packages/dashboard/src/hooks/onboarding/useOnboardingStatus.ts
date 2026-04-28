/**
 * Onboarding status hook (Story 18-4)
 *
 * Polls `GET /api/v1/onboarding/status` so the wizard observes the
 * GitHub-App install webhook landing without a manual refresh.
 *
 * Polling cadence:
 * - Default `intervalMs = 4000` matches the typical 1–4s gap between
 *   GitHub's user-redirect-back and webhook arrival; users tolerate it
 *   without the 1s-CPU drain of a hot polling loop.
 * - When the install step is satisfied (`hasInstallation === true`) the
 *   polling auto-stops — there's nothing else to wait for and we don't
 *   need to keep the API warm.
 * - The hook also pauses while the document is hidden (tab in background)
 *   so swap-back doesn't race the user.
 *
 * Tests live alongside in `useOnboardingStatus.test.ts`.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { onboardingApi, type OnboardingStatus } from '../../services/onboarding/onboarding-api-client.js';

interface State {
  loading: boolean;
  status: OnboardingStatus | null;
  error: string | null;
}

interface UseOnboardingStatusOptions {
  /** Polling interval while the install step is unsatisfied. Default: 4 s. */
  intervalMs?: number;
  /** Disable polling (still fetches once on mount). */
  pollWhilePending?: boolean;
}

interface UseOnboardingStatusResult extends State {
  /** Force a refetch (used by the "Check now" button). */
  refresh: () => Promise<void>;
}

export function useOnboardingStatus(
  options: UseOnboardingStatusOptions = {},
): UseOnboardingStatusResult {
  const { intervalMs = 4000, pollWhilePending = true } = options;
  const [state, setState] = useState<State>({
    loading: true,
    status: null,
    error: null,
  });
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mountedRef = useRef(true);

  const fetchOnce = useCallback(async () => {
    try {
      const status = await onboardingApi.getStatus();
      if (!mountedRef.current) return;
      setState({ loading: false, status, error: null });
    } catch (err) {
      if (!mountedRef.current) return;
      const status = (err as Error & { status?: number }).status;
      // 401 means the session expired — surface a friendly message; the
      // AuthGuard will redirect on the next render. Other errors get the
      // raw message for diagnosis.
      const msg =
        status === 401
          ? 'Your session expired. Please sign in again.'
          : err instanceof Error
            ? err.message
            : 'Failed to load onboarding status';
      setState((prev) => ({ ...prev, loading: false, error: msg }));
    }
  }, []);

  // Schedule the next poll only when:
  //  - polling is enabled, AND
  //  - install step is still unsatisfied, AND
  //  - tab is visible (avoid waking on background tabs).
  const scheduleNext = useCallback(
    (status: OnboardingStatus | null) => {
      if (timerRef.current) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
      if (!pollWhilePending) return;
      if (status?.hasInstallation) return;
      if (typeof document !== 'undefined' && document.hidden) return;
      timerRef.current = setTimeout(() => {
        void fetchOnce();
      }, intervalMs);
    },
    [fetchOnce, intervalMs, pollWhilePending],
  );

  useEffect(() => {
    mountedRef.current = true;
    void fetchOnce();
    return () => {
      mountedRef.current = false;
      if (timerRef.current) clearTimeout(timerRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Re-schedule whenever the status snapshot changes.
  useEffect(() => {
    scheduleNext(state.status);
    return () => {
      if (timerRef.current) clearTimeout(timerRef.current);
    };
  }, [state.status, scheduleNext]);

  // Refetch when the tab regains focus — covers the "user opened GitHub
  // in a new tab and tabs back here" path.
  useEffect(() => {
    if (typeof document === 'undefined') return;
    function onVisibility(): void {
      if (!document.hidden) {
        void fetchOnce();
      }
    }
    document.addEventListener('visibilitychange', onVisibility);
    return () => document.removeEventListener('visibilitychange', onVisibility);
  }, [fetchOnce]);

  const refresh = useCallback(async () => {
    setState((prev) => ({ ...prev, loading: true }));
    await fetchOnce();
  }, [fetchOnce]);

  return { ...state, refresh };
}
