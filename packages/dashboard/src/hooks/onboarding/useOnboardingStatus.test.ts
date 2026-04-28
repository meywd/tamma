import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { useOnboardingStatus } from './useOnboardingStatus.js';
import type { OnboardingStatus } from '../../services/onboarding/onboarding-api-client.js';

// We deliberately use *real* timers + a short interval and `waitFor` to
// observe the polling. Fake timers + waitFor don't compose: waitFor has
// its own polling that needs real time to advance.
//
// Trade-off: tests take ~50–200 ms each. Acceptable; under 1 s total.

const PENDING_STATUS: OnboardingStatus = {
  emailVerified: true,
  hasOrg: true,
  tenantId: 't-1',
  hasInstallation: false,
  installationCount: 0,
  installations: [],
};

const COMPLETE_STATUS: OnboardingStatus = {
  emailVerified: true,
  hasOrg: true,
  tenantId: 't-1',
  hasInstallation: true,
  installationCount: 1,
  installations: [
    {
      installationId: 1,
      accountLogin: 'acme',
      accountType: 'Organization',
      suspended: false,
      repoCount: 1,
      repos: [{ repoId: 10, fullName: 'acme/api' }],
    },
  ],
};

describe('useOnboardingStatus', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function stubFetchSequence(payloads: OnboardingStatus[]): { calls: () => number } {
    let i = 0;
    const fetchMock = vi.fn(async () => {
      const payload = payloads[Math.min(i, payloads.length - 1)];
      i += 1;
      return new Response(JSON.stringify(payload), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    });
    vi.stubGlobal('fetch', fetchMock);
    return { calls: () => fetchMock.mock.calls.length };
  }

  it('fetches once on mount and exposes the status', async () => {
    const { calls } = stubFetchSequence([PENDING_STATUS]);
    const { result } = renderHook(() => useOnboardingStatus({ intervalMs: 5000 }));

    expect(result.current.loading).toBe(true);

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.status).toEqual(PENDING_STATUS);
    expect(result.current.error).toBeNull();
    expect(calls()).toBe(1);
  });

  it('keeps polling while install is pending and stops once linked', async () => {
    // 50ms interval keeps the test snappy.
    const { calls } = stubFetchSequence([
      PENDING_STATUS,
      PENDING_STATUS,
      COMPLETE_STATUS,
    ]);
    const { result } = renderHook(() =>
      useOnboardingStatus({ intervalMs: 30 }),
    );

    // Wait for the third call to land (means polling fired twice after mount).
    await waitFor(() => expect(calls()).toBeGreaterThanOrEqual(3), { timeout: 2000 });
    await waitFor(() => expect(result.current.status?.hasInstallation).toBe(true));

    // Once linked, polling should stop. Verify call count is stable for
    // ~5x the interval.
    const before = calls();
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(calls()).toBe(before);
  });

  it('does not poll when pollWhilePending is false', async () => {
    const { calls } = stubFetchSequence([PENDING_STATUS]);
    renderHook(() =>
      useOnboardingStatus({ intervalMs: 30, pollWhilePending: false }),
    );

    await waitFor(() => expect(calls()).toBe(1));
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(calls()).toBe(1);
  });

  it('exposes a friendly error on 401', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 401 }));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOnboardingStatus({ intervalMs: 5000 }));

    await waitFor(() => expect(result.current.error).not.toBeNull());
    expect(result.current.error).toMatch(/session expired/i);
  });

  it('refresh() forces a re-fetch even after polling stopped', async () => {
    const { calls } = stubFetchSequence([COMPLETE_STATUS]); // immediately complete
    const { result } = renderHook(() => useOnboardingStatus({ intervalMs: 30 }));

    await waitFor(() => expect(calls()).toBe(1));
    // Polling should have stopped because hasInstallation=true. Verify
    // by waiting longer than the interval and seeing calls stay at 1.
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(calls()).toBe(1);

    await act(async () => {
      await result.current.refresh();
    });
    expect(calls()).toBe(2);
  });
});
