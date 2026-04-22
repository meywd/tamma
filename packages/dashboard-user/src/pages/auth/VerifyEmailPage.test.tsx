/**
 * VerifyEmailPage auto-submits the `?token=` query param to
 * POST /api/v1/auth/verify-email on mount. On success, shows a
 * "verified — continue" link; on failure, an error message.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuthProvider } from '../../hooks/useAuth';
import { VerifyEmailPage } from './VerifyEmailPage';

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthProvider>
        <Routes>
          <Route path="/verify-email" element={<VerifyEmailPage />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('VerifyEmailPage', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('shows waiting state when no token is in the query', async () => {
    globalThis.fetch = vi
      .fn()
      .mockResolvedValue(new Response('', { status: 401 })) as unknown as typeof fetch;

    renderAt('/verify-email');

    expect(
      await screen.findByText(/check your email/i),
    ).toBeInTheDocument();
  });

  it('POSTs token on mount and shows success state', async () => {
    // URL-aware mock — order of effects is non-deterministic across
    // parent/child renders, so match on URL instead of call order.
    const fetchMock = vi.fn((url: string) => {
      if (url.includes('/auth/verify-email')) {
        return Promise.resolve(
          new Response(JSON.stringify({ ok: true }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          }),
        );
      }
      // /auth/me and /auth/refresh both return 401 so the useAuth hook
      // settles into the anonymous state without interfering.
      return Promise.resolve(new Response('', { status: 401 }));
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    renderAt('/verify-email?token=abc123');

    await waitFor(() => {
      expect(screen.getByText(/email verified/i)).toBeInTheDocument();
    });

    const verifyCall = (fetchMock.mock.calls as unknown as Array<[string, RequestInit]>).find(
      (c) => typeof c[0] === 'string' && c[0].includes('/auth/verify-email'),
    );
    expect(verifyCall).toBeDefined();
    const body = JSON.parse(verifyCall![1].body as string);
    expect(body).toEqual({ token: 'abc123' });
  });

  it('shows error when token is rejected', async () => {
    const fetchMock = vi.fn((url: string) => {
      if (url.includes('/auth/verify-email')) {
        return Promise.resolve(
          new Response(JSON.stringify({ error: 'expired_token' }), {
            status: 400,
            headers: { 'content-type': 'application/json' },
          }),
        );
      }
      return Promise.resolve(new Response('', { status: 401 }));
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    renderAt('/verify-email?token=bad');

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
    });
  });
});
