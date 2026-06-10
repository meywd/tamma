/**
 * LoginPage tests. The form submits email+password to /api/v1/auth/login
 * via useAuth.login() and on success redirects to `?redirect=...` or `/`.
 * A "Sign in with GitHub" link anchors to the GitHub OAuth start endpoint.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuthProvider } from '../../hooks/useAuth';
import { LoginPage } from './LoginPage';

function renderWithRouter(initialPath = '/login') {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/" element={<div>home</div>} />
          <Route path="/repos" element={<div>repos</div>} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('LoginPage', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('renders the email / password form and GitHub link', async () => {
    // Initial /auth/me returns 401 — page renders in anonymous state.
    globalThis.fetch = vi
      .fn()
      .mockResolvedValue(new Response('', { status: 401 }));

    renderWithRouter();

    expect(await screen.findByLabelText(/email/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/password/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /sign in with github/i })).toHaveAttribute(
      'href',
      '/oauth2/start?rd=%2F',
    );
  });

  it('submits credentials and redirects to "/" on success', async () => {
    const fetchMock = vi.fn();
    fetchMock
      // initial /auth/me — anonymous
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      // login → ok
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      )
      // post-login /auth/me
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            user: { id: 'u1', email: 'a@b.com', displayName: 'Alice' },
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      );
    globalThis.fetch = fetchMock;

    renderWithRouter();

    await screen.findByLabelText(/email/i);
    await userEvent.type(screen.getByLabelText(/email/i), 'a@b.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'secret');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => {
      expect(screen.getByText('home')).toBeInTheDocument();
    });

    const loginCall = fetchMock.mock.calls.find(
      (c) => typeof c[0] === 'string' && (c[0]).includes('/auth/login'),
    );
    expect(loginCall).toBeDefined();
    expect(JSON.parse((loginCall![1] as RequestInit).body as string)).toEqual({
      email: 'a@b.com',
      password: 'secret',
    });
  });

  it('shows error message on failed login', async () => {
    const fetchMock = vi.fn();
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ error: 'Invalid credentials' }), {
          status: 401,
          headers: { 'content-type': 'application/json' },
        }),
      )
      .mockResolvedValueOnce(new Response('', { status: 401 }));
    globalThis.fetch = fetchMock;

    renderWithRouter();

    await screen.findByLabelText(/email/i);
    await userEvent.type(screen.getByLabelText(/email/i), 'a@b.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'bad');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
    });
  });

  it('redirects to the ?redirect target if present', async () => {
    const fetchMock = vi.fn();
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            user: { id: 'u1', email: 'a@b.com', displayName: 'Alice' },
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      );
    globalThis.fetch = fetchMock;

    renderWithRouter('/login?redirect=%2Frepos');

    await screen.findByLabelText(/email/i);
    await userEvent.type(screen.getByLabelText(/email/i), 'a@b.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'secret');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => {
      expect(screen.getByText('repos')).toBeInTheDocument();
    });
  });
});
