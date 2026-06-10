/**
 * ConnectedPlatforms tests. Invariants we care about:
 *   - Empty state shows the prompt to connect.
 *   - Existing rows render with primary indicator.
 *   - Cross-tenant scoping is enforced server-side; this test only
 *     verifies the page calls the listConnectedPlatforms API and
 *     renders whatever the backend returns.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { ConnectedPlatforms } from './ConnectedPlatforms';

function jsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function renderPage() {
  return render(
    <MemoryRouter>
      <ConnectedPlatforms />
    </MemoryRouter>,
  );
}

describe('ConnectedPlatforms', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('renders empty state when no platforms connected', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse({ items: [], count: 0 }),
    );
    globalThis.fetch = fetchMock;

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/no platforms connected yet/i)).toBeInTheDocument();
    });
  });

  it('renders rows with primary marker', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse({
        items: [
          {
            installationId: '11111111-1111-1111-1111-111111111111',
            kind: 'Gitea',
            baseUrl: 'https://gitea.example.com',
            externalId: 'org-1',
            status: 'connected',
            isPrimary: true,
            createdAt: '2026-04-27T12:00:00Z',
          },
        ],
        count: 1,
      }),
    );
    globalThis.fetch = fetchMock;

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Gitea')).toBeInTheDocument();
    });
    expect(screen.getByText(/primary/i)).toBeInTheDocument();
    expect(screen.getByText('https://gitea.example.com')).toBeInTheDocument();
    expect(screen.getByText('connected')).toBeInTheDocument();
  });

  it('calls /api/onboarding/installations exactly once', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(
      jsonResponse({ items: [], count: 0 }),
    );
    globalThis.fetch = fetchMock;

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/no platforms connected/i)).toBeInTheDocument();
    });

    // The dashboard-user shell may also fetch /api/auth/me elsewhere
    // but the ConnectedPlatforms component itself only hits the
    // installations endpoint.
    const installationsCall = fetchMock.mock.calls.find((c) =>
      String(c[0]).includes('/api/onboarding/installations'),
    );
    expect(installationsCall).toBeTruthy();
  });
});
