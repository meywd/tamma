/**
 * Tests for the useAuth hook.
 *
 * The hook models four states: loading (initial fetch), authenticated
 * (user present), anonymous (401 on /auth/me), and erroring. It exposes
 * `login`, `register`, `logout`, and `refresh` mutation helpers.
 */

import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { useAuth, AuthProvider } from './useAuth';
import type { ReactNode } from 'react';

describe('useAuth', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  const wrapper = ({ children }: { children: ReactNode }) => (
    <AuthProvider>{children}</AuthProvider>
  );

  it('starts in loading state then transitions to authenticated when /auth/me returns a user', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          user: { id: 'u1', email: 'a@b.com', displayName: 'Alice' },
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    );

    const { result } = renderHook(() => useAuth(), { wrapper });

    expect(result.current.loading).toBe(true);

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.user).toEqual({
      id: 'u1',
      email: 'a@b.com',
      displayName: 'Alice',
    });
  });

  it('sets user=null when /auth/me returns 401', async () => {
    // One fetch for /auth/me (401), one for /auth/refresh (also 401) — the
    // client throws UnauthorizedError which the hook converts to user=null.
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }));

    const { result } = renderHook(() => useAuth(), { wrapper });

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.user).toBeNull();
  });

  it('login() posts credentials and refreshes current user', async () => {
    // Initial /auth/me — 401 (then refresh 401 → anon).
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      // login → 200
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      )
      // follow-up /auth/me → authenticated
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            user: { id: 'u2', email: 'x@y.com', displayName: 'X' },
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      );

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await result.current.login('x@y.com', 'pw');
    });

    expect(result.current.user).toEqual({
      id: 'u2',
      email: 'x@y.com',
      displayName: 'X',
    });

    const loginCall = fetchMock.mock.calls.find(
      (c) => typeof c[0] === 'string' && (c[0] as string).includes('/auth/login'),
    );
    expect(loginCall).toBeDefined();
    expect((loginCall![1] as RequestInit).method).toBe('POST');
  });

  it('logout() posts to /auth/logout and clears user', async () => {
    // Initial /auth/me → authenticated.
    fetchMock
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            user: { id: 'u1', email: 'a@b.com', displayName: 'A' },
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      )
      .mockResolvedValueOnce(new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })); // logout

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.user).not.toBeNull());

    await act(async () => {
      await result.current.logout();
    });

    expect(result.current.user).toBeNull();
    const logoutCall = fetchMock.mock.calls.find(
      (c) => typeof c[0] === 'string' && (c[0] as string).includes('/auth/logout'),
    );
    expect(logoutCall).toBeDefined();
  });
});
