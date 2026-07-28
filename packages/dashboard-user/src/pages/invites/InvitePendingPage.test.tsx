/**
 * InvitePendingPage tests (Story 45-3 AC7 / D5): the page states plainly that
 * it CANNOT accept the invite — the server stores only a hash of the token,
 * so this id-only URL structurally cannot complete the flow — and makes no
 * API call (there is no invitee-facing lookup or resend endpoint).
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { InvitePendingPage } from './InvitePendingPage';

function renderPage(url = '/invites/pending?inviteId=inv-42'): void {
  render(
    <MemoryRouter initialEntries={[url]}>
      <InvitePendingPage />
    </MemoryRouter>,
  );
}

beforeEach(() => vi.restoreAllMocks());
afterEach(() => cleanup());

describe('InvitePendingPage', () => {
  it('states that it cannot accept the invite and points at the original email', () => {
    renderPage();

    expect(screen.getByText(/pending invitation/i)).toBeInTheDocument();
    expect(screen.getByText(/cannot accept the invitation/i)).toBeInTheDocument();
    expect(screen.getByText(/original invitation email/i)).toBeInTheDocument();
    expect(screen.getByText(/resend/i)).toBeInTheDocument();
  });

  it('shows the invite reference from the query string', () => {
    renderPage();
    expect(screen.getByText('inv-42')).toBeInTheDocument();
  });

  it('renders without an inviteId too', () => {
    renderPage('/invites/pending');
    expect(screen.getByText(/pending invitation/i)).toBeInTheDocument();
  });

  it('makes no API call', () => {
    const spy = vi.fn();
    globalThis.fetch = spy as unknown as typeof fetch;

    renderPage();

    expect(spy).not.toHaveBeenCalled();
  });
});
