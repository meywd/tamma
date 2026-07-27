/**
 * Router-tree tests (Story 45-2 AC8) — the pin that would have caught all six
 * missing entry points: every declared route must render SOMETHING, an
 * unknown path must render a real 404 (in the shell when signed in,
 * standalone with NO login redirect when anonymous), and the /verify alias
 * must preserve the ?token= query the API emails.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import type { ReactNode } from 'react';
import { App, ROUTE_PATHS } from './App';
import type { AuthUser } from './hooks/useAuth';

const { authState } = vi.hoisted(() => ({
  authState: {
    user: null as AuthUser | null,
    loading: false,
  },
}));

vi.mock('./hooks/useAuth', () => ({
  AuthProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
  useAuth: () => ({
    user: authState.user,
    loading: authState.loading,
    error: null,
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
    refresh: vi.fn(),
  }),
}));

const OWNER: AuthUser = {
  id: 'u-1',
  email: 'owner@acme.dev',
  displayName: 'Owner',
  tenantId: 'tnt-1',
  role: 'owner',
};

function stubFetch(): void {
  // Generic body carrying the empty-collection fields the data pages read
  // (items/limits/plans/…), so every route renders its empty state rather
  // than throwing on `undefined.map` — this test asserts routing, not data.
  const body = {
    items: [],
    count: 0,
    limit: 0,
    plans: [],
    limits: [],
    installations: [],
    runs: [],
    stats: null,
    tenantId: 'tnt-1',
    planId: 'p-1',
    planVersion: 1,
    isCustom: false,
  };
  globalThis.fetch = vi.fn(
    async () =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
  ) as unknown as typeof fetch;
}

function renderAt(path: string): ReturnType<typeof render> {
  window.history.pushState({}, '', path);
  return render(<App />);
}

beforeEach(() => {
  authState.user = OWNER;
  authState.loading = false;
  stubFetch();
});

afterEach(() => {
  cleanup();
  window.history.pushState({}, '', '/');
  vi.restoreAllMocks();
});

describe('route table — every declared route renders something', () => {
  it.each(ROUTE_PATHS)('%s renders a non-empty page (authenticated owner)', (path) => {
    const { container } = renderAt(path);
    expect(container.textContent?.trim().length ?? 0).toBeGreaterThan(0);
  });
});

describe('the six API-emailed entry points resolve', () => {
  it('/verify (the path the API emails) mounts VerifyEmailPage and preserves ?token=', async () => {
    authState.user = null;
    renderAt('/verify?token=tok-abc');

    await waitFor(() => {
      const calls = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls;
      const verifyCall = calls.find((c) => String(c[0]).includes('/api/v1/auth/verify-email'));
      expect(verifyCall).toBeDefined();
      const body = JSON.parse((verifyCall?.[1] as RequestInit).body as string);
      expect(body.token).toBe('tok-abc');
    });
  });

  it('/verify-email (the historical path) still mounts the same page', () => {
    authState.user = null;
    renderAt('/verify-email');
    expect(screen.getByText(/check your email/i)).toBeInTheDocument();
  });

  it('/reset-password renders the reset page', () => {
    authState.user = null;
    renderAt('/reset-password?token=tok');
    expect(screen.getByLabelText('New password')).toBeInTheDocument();
  });

  it('/invites/accept renders for an anonymous invitee (no login redirect)', () => {
    authState.user = null;
    renderAt('/invites/accept?token=tok');
    expect(screen.getByText(/you.ve been invited/i)).toBeInTheDocument();
    expect(window.location.pathname).toBe('/invites/accept');
  });

  it('/invites/pending renders the informational page', () => {
    authState.user = null;
    renderAt('/invites/pending?inviteId=inv-1');
    expect(screen.getByText(/pending invitation/i)).toBeInTheDocument();
  });

  it('/onboarding/success and /onboarding/error render for a signed-in user', () => {
    renderAt('/onboarding/success');
    expect(screen.getByText(/github app installed/i)).toBeInTheDocument();
    cleanup();
    renderAt('/onboarding/error?reason=tenant_mismatch');
    expect(screen.getByText(/install failed/i)).toBeInTheDocument();
    expect(screen.getByText('tenant_mismatch')).toBeInTheDocument();
  });
});

describe('catch-all 404 (45-2 AC5)', () => {
  it('renders the 404 inside the shell for a signed-in user', () => {
    renderAt('/no-such-page');
    expect(screen.getByText(/page not found/i)).toBeInTheDocument();
    // The shell drew too: sidebar nav + signed-in header.
    expect(screen.getByRole('button', { name: /sign out/i })).toBeInTheDocument();
  });

  it('renders the 404 standalone for an anonymous user — no /login redirect', () => {
    authState.user = null;
    renderAt('/no-such-page');
    expect(screen.getByText(/page not found/i)).toBeInTheDocument();
    // Deliberately NOT bounced through /login?redirect=%2Fno-such-page.
    expect(window.location.pathname).toBe('/no-such-page');
    // Anonymous users get a sign-in link instead.
    expect(screen.getByRole('link', { name: /sign in/i })).toBeInTheDocument();
  });

  it('echoes the unknown path', () => {
    renderAt('/typo/deep/path');
    expect(screen.getByText('/typo/deep/path')).toBeInTheDocument();
  });
});

describe('/onboarding redirect (45-2 AC4)', () => {
  it('redirects /onboarding to /onboarding/platforms', async () => {
    renderAt('/onboarding');
    await waitFor(() => expect(window.location.pathname).toBe('/onboarding/platforms'));
  });
});
