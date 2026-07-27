/**
 * ResetPasswordPage tests (Story 45-3 AC3/AC4). Failure shapes mirror the
 * real endpoint (AuthEndpoints.PasswordResetConfirm): 400 "Password too
 * weak" + details[], 400 "Invalid or expired reset token", 200 success.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { ResetPasswordPage } from './ResetPasswordPage';

function mockJson(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function renderPage(url = '/reset-password?token=tok-1'): void {
  render(
    <MemoryRouter initialEntries={[url]}>
      <ResetPasswordPage />
    </MemoryRouter>,
  );
}

function fill(password: string, confirm: string = password): void {
  fireEvent.change(screen.getByLabelText('New password'), { target: { value: password } });
  fireEvent.change(screen.getByLabelText('Confirm new password'), {
    target: { value: confirm },
  });
  fireEvent.click(screen.getByRole('button', { name: /reset password/i }));
}

beforeEach(() => vi.restoreAllMocks());
afterEach(() => cleanup());

describe('ResetPasswordPage', () => {
  it('renders an explanatory state (and never POSTs) when the token is missing', () => {
    const spy = vi.fn();
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage('/reset-password');

    expect(screen.getByText(/link is incomplete/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /request a new reset link/i })).toHaveAttribute(
      'href',
      '/forgot-password',
    );
    expect(spy).not.toHaveBeenCalled();
  });

  it('blocks a mismatched confirmation without issuing a POST', () => {
    const spy = vi.fn();
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    fill('Str0ngPassw0rd', 'DifferentPassw0rd');

    expect(screen.getByRole('alert')).toHaveTextContent(/do not match/i);
    expect(spy).not.toHaveBeenCalled();
  });

  it('pre-flights the server password rules without issuing a POST', () => {
    const spy = vi.fn();
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    fill('short');

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent(/at least 8 characters/i);
    expect(alert).toHaveTextContent(/uppercase/i);
    expect(alert).toHaveTextContent(/digit/i);
    expect(spy).not.toHaveBeenCalled();
  });

  it('POSTs token + new password to /api/v1/auth/password-reset/confirm and links to /login on success', async () => {
    const spy = vi
      .fn()
      .mockResolvedValueOnce(mockJson({ message: 'Password reset successfully' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    fill('Str0ngPassw0rd');

    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toContain('/api/v1/auth/password-reset/confirm');
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({
      token: 'tok-1',
      newPassword: 'Str0ngPassw0rd',
    });

    expect(await screen.findByText(/password reset successfully/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /sign in/i })).toHaveAttribute('href', '/login');
  });

  it('renders a distinct invalid-or-expired-token state', async () => {
    const spy = vi
      .fn()
      .mockResolvedValueOnce(mockJson({ error: 'Invalid or expired reset token' }, 400));
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    fill('Str0ngPassw0rd');

    expect(await screen.findByText(/invalid or has expired/i)).toBeInTheDocument();
    // Distinct from a validation failure: the form is gone, the re-request link is offered.
    expect(screen.queryByLabelText('New password')).toBeNull();
    expect(screen.getByRole('link', { name: /request a new reset link/i })).toBeInTheDocument();
  });

  it('surfaces the server "Password too weak" details verbatim', async () => {
    // e.g. the common-password list, which the client pre-flight does not mirror.
    const spy = vi.fn().mockResolvedValueOnce(
      mockJson({ error: 'Password too weak', details: ['Password is too common'] }, 400),
    );
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    fill('Passw0rdPassw0rd');

    expect(await screen.findByRole('alert')).toHaveTextContent(/password is too common/i);
    // Still on the form — the user can try again.
    expect(screen.getByLabelText('New password')).toBeInTheDocument();
  });
});
