/**
 * InviteAcceptPage tests (Story 45-3 AC5/AC6).
 *
 * The failure vocabulary is the endpoint's REAL one (OrgEndpoints.AcceptInvite):
 * three distinguishable 400 strings — "Invalid or expired invite token",
 * "Invite has already been accepted", "Invite has expired" — plus a generic
 * fallback. "Wrong account" and "revoked" are NOT distinguishable server-side
 * and are deliberately not faked (D6).
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { InviteAcceptPage } from './InviteAcceptPage';
import type { AuthUser } from '../../hooks/useAuth';

const { authState } = vi.hoisted(() => ({
  authState: {
    user: null as AuthUser | null,
    loading: false,
  },
}));

vi.mock('../../hooks/useAuth', () => ({
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

const MEMBER: AuthUser = {
  id: 'u-2',
  email: 'invitee@acme.dev',
  displayName: 'Invitee',
  tenantId: null,
  role: null,
};

function mockJson(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function renderPage(url = '/invites/accept?token=tok-1'): void {
  render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/invites/accept" element={<InviteAcceptPage />} />
        <Route path="/" element={<p>HOME</p>} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.restoreAllMocks();
  authState.user = null;
  authState.loading = false;
});
afterEach(() => cleanup());

describe('InviteAcceptPage — anonymous invitee', () => {
  it('offers sign-in and create-account, with the token preserved in the sign-in href (D2)', () => {
    const spy = vi.fn();
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();

    expect(screen.getByText(/you.ve been invited/i)).toBeInTheDocument();

    const signIn = screen.getByRole('link', { name: /^sign in$/i });
    expect(signIn).toHaveAttribute(
      'href',
      `/login?redirect=${encodeURIComponent('/invites/accept?token=tok-1')}`,
    );
    expect(screen.getByRole('link', { name: /create an account/i })).toHaveAttribute(
      'href',
      '/register',
    );

    // D3: registration crosses an email-verification the token cannot ride —
    // the page says, in explicit copy, to click the invite link again.
    expect(screen.getByText(/click the invite link in your email again/i)).toBeInTheDocument();

    // No accept POST is attempted while anonymous (the endpoint would 401).
    expect(spy).not.toHaveBeenCalled();
  });
});

describe('InviteAcceptPage — authenticated invitee', () => {
  it('accepts immediately (POST once) and navigates to /', async () => {
    authState.user = MEMBER;
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ tenantId: 'tnt-9', role: 'member', message: 'You have joined the organization' }),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();

    await waitFor(() => expect(screen.getByText('HOME')).toBeInTheDocument());
    expect(spy).toHaveBeenCalledTimes(1);
    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toContain('/api/v1/orgs/invites/accept');
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({ token: 'tok-1' });
  });

  it.each([
    ['Invite has expired', /invitation has expired/i],
    ['Invite has already been accepted', /already accepted/i],
    ['Invalid or expired invite token', /not valid/i],
  ])('renders a distinct state for "%s"', async (serverError, expected) => {
    authState.user = MEMBER;
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ error: serverError }, 400));
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(expected);
    // Did not navigate away.
    expect(screen.queryByText('HOME')).toBeNull();
  });

  it('renders a generic state for an unrecognized failure', async () => {
    authState.user = MEMBER;
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ error: 'weird' }, 500));
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not accept/i);
  });
});

describe('InviteAcceptPage — missing token', () => {
  it('explains the link is incomplete and never POSTs', () => {
    authState.user = MEMBER;
    const spy = vi.fn();
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage('/invites/accept');

    expect(screen.getByText(/link is incomplete/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });
});
