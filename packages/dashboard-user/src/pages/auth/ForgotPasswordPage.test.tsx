/**
 * ForgotPasswordPage tests (Story 45-3 AC1). The load-bearing pin is the
 * first one: the success message must be IDENTICAL for a known and an
 * unknown address — anything that distinguishes them is an
 * account-enumeration oracle (D4).
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { ForgotPasswordPage } from './ForgotPasswordPage';

function mockJson(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/forgot-password']}>
      <ForgotPasswordPage />
    </MemoryRouter>,
  );
}

async function submit(email: string): Promise<void> {
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: email } });
  fireEvent.click(screen.getByRole('button', { name: /send reset link/i }));
}

beforeEach(() => vi.restoreAllMocks());
afterEach(() => cleanup());

describe('ForgotPasswordPage', () => {
  it('renders one success message regardless of whether the address exists', async () => {
    // The server returns the same canned 200 for known and unknown addresses
    // (AuthEndpoints.PasswordResetRequest); the client renders ONE state and
    // never branches. Render twice with both server intents and capture the
    // text — it must be byte-identical.
    const texts: string[] = [];

    for (const _serverIntent of ['known-address', 'unknown-address']) {
      const spy = vi
        .fn()
        .mockResolvedValueOnce(
          mockJson({ message: 'If the email exists, a reset link has been sent' }),
        );
      globalThis.fetch = spy as unknown as typeof fetch;

      renderPage();
      await submit('someone@example.dev');
      const status = await screen.findByRole('status');
      texts.push(status.parentElement?.textContent ?? '');
      cleanup();
    }

    expect(texts[0]).toBe(texts[1]);
    expect(texts[0]).toMatch(/if that address has an account/i);
  });

  it('POSTs the email to /api/v1/auth/password-reset/request', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ message: 'ok' }));
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    await submit('someone@example.dev');

    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    const [url, init] = spy.mock.calls[0] ?? [];
    expect(url as string).toContain('/api/v1/auth/password-reset/request');
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({
      email: 'someone@example.dev',
    });
  });

  it('surfaces rate limiting distinctly (429)', async () => {
    const spy = vi
      .fn()
      .mockResolvedValueOnce(mockJson({ error: 'Too many reset requests.' }, 429));
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    await submit('someone@example.dev');

    expect(await screen.findByRole('alert')).toHaveTextContent(/too many reset requests/i);
  });

  it('renders a generic error on a server failure without leaking existence', async () => {
    const spy = vi.fn().mockResolvedValueOnce(mockJson({ error: 'boom' }, 500));
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();
    await submit('someone@example.dev');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/something went wrong/i);
    expect(alert.textContent).not.toMatch(/exist|account|not found/i);
  });
});
