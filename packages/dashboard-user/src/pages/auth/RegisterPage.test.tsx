/**
 * RegisterPage tests. On submit, posts display-name + email + password
 * to /api/v1/auth/register and navigates to /verify-email on success.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuthProvider } from '../../hooks/useAuth';
import { RegisterPage } from './RegisterPage';

function renderWithRouter() {
  return render(
    <MemoryRouter initialEntries={['/register']}>
      <AuthProvider>
        <Routes>
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/verify-email" element={<div>verify-email</div>} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('RegisterPage', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('renders the registration form', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('', { status: 401 }),
    );

    renderWithRouter();

    expect(await screen.findByLabelText(/name/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/email/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/password/i)).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: /sign up with github/i }),
    ).toHaveAttribute('href', '/api/auth/github');
  });

  it('posts registration payload and navigates to /verify-email', async () => {
    const fetchMock = vi.fn();
    fetchMock
      // initial /auth/me
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      // register → 201
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ok: true }), {
          status: 201,
          headers: { 'content-type': 'application/json' },
        }),
      )
      // follow-up /auth/me (still anon until verified)
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }));
    globalThis.fetch = fetchMock;

    renderWithRouter();
    await screen.findByLabelText(/name/i);

    await userEvent.type(screen.getByLabelText(/name/i), 'Alice');
    await userEvent.type(screen.getByLabelText(/email/i), 'a@b.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'secret123');
    await userEvent.click(screen.getByRole('button', { name: /create account/i }));

    await waitFor(() => {
      expect(screen.getByText('verify-email')).toBeInTheDocument();
    });

    const registerCall = fetchMock.mock.calls.find(
      (c) => typeof c[0] === 'string' && (c[0]).includes('/auth/register'),
    );
    expect(registerCall).toBeDefined();
    const body = JSON.parse((registerCall![1] as RequestInit).body as string);
    expect(body).toEqual({
      email: 'a@b.com',
      password: 'secret123',
      displayName: 'Alice',
    });
  });
});
