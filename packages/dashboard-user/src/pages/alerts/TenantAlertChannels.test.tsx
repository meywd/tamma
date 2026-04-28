/**
 * TenantAlertChannels tests. Invariants we care about:
 *   - Channel list fetches /api/v1/orgs/{tenantId}/alert-channels.
 *   - Plaintext credential in config BLOCKS the create POST.
 *   - Slack without credentialsSecretId shows error.
 *   - Create POST body does NOT carry `tenantId` — server forces it.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider } from '../../hooks/useAuth';
import { TenantAlertChannels } from './TenantAlertChannels';

function renderPage() {
  return render(
    <MemoryRouter>
      <AuthProvider>
        <TenantAlertChannels />
      </AuthProvider>
    </MemoryRouter>,
  );
}

function jsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

describe('TenantAlertChannels', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('fetches channels under /api/v1/orgs/{tenantId}/alert-channels', async () => {
    const fetchMock = vi.fn();
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        user: {
          id: 'u1',
          email: 'a@b',
          displayName: 'A',
          tenantId: 'tnt-A',
          role: 'admin',
        },
      }),
    );
    fetchMock.mockResolvedValueOnce(jsonResponse({ items: [], count: 0 }));
    globalThis.fetch = fetchMock;

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/no channels configured/i)).toBeInTheDocument();
    });

    const channelsCall = fetchMock.mock.calls[1];
    expect(channelsCall?.[0] as string).toContain(
      '/api/v1/orgs/tnt-A/alert-channels',
    );
  });

  it('create POST omits body.tenantId so server forces path-tenant ownership', async () => {
    const fetchMock = vi.fn();
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        user: {
          id: 'u1',
          email: 'a@b',
          displayName: 'A',
          tenantId: 'tnt-A',
          role: 'admin',
        },
      }),
    );
    fetchMock.mockResolvedValueOnce(jsonResponse({ items: [], count: 0 }));
    // create
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: 'new' }, 201));
    // refresh
    fetchMock.mockResolvedValueOnce(jsonResponse({ items: [], count: 0 }));
    globalThis.fetch = fetchMock;

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/no channels configured/i)).toBeInTheDocument();
    });

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /new channel/i }));

    // Step 1: name + type (default email)
    await user.type(screen.getByLabelText(/name/i), 'Ops email');
    await user.click(screen.getByRole('button', { name: /next/i }));

    // Step 2: email channel → no secret id needed
    await user.click(screen.getByRole('button', { name: /^Create$/i }));

    await waitFor(() => {
      const createCall = fetchMock.mock.calls.find(
        (c) =>
          typeof c[0] === 'string' &&
          (c[0]).endsWith('/api/v1/orgs/tnt-A/alert-channels') &&
          (c[1] as RequestInit).method === 'POST',
      );
      expect(createCall).toBeTruthy();
      const body = JSON.parse((createCall?.[1] as RequestInit).body as string);
      expect(body.name).toBe('Ops email');
      expect(body.channelType).toBe('email');
      // CRITICAL: body.tenantId must NOT be sent (server stamps it from the
      // path). Permitted values: undefined or null.
      expect(body.tenantId ?? null).toBeNull();
    });
  });

  it('slack channel with no credentialsSecretId shows validation error', async () => {
    const fetchMock = vi.fn();
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        user: {
          id: 'u1',
          email: 'a@b',
          displayName: 'A',
          tenantId: 'tnt-A',
          role: 'admin',
        },
      }),
    );
    fetchMock.mockResolvedValueOnce(jsonResponse({ items: [], count: 0 }));
    globalThis.fetch = fetchMock;

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/no channels configured/i)).toBeInTheDocument();
    });

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /new channel/i }));

    await user.selectOptions(screen.getByLabelText(/type/i), 'slack');
    await user.type(screen.getByLabelText(/name/i), 'Bad Slack');
    await user.click(screen.getByRole('button', { name: /next/i }));

    // Step 2: leave credentialsSecretId empty + click Create
    await user.click(screen.getByRole('button', { name: /^Create$/i }));

    await waitFor(() => {
      expect(
        screen.getByText(/credentialsSecretId from the Secret Store/i),
      ).toBeInTheDocument();
    });

    // And it must NOT have submitted.
    const createCall = fetchMock.mock.calls.find(
      (c) =>
        typeof c[0] === 'string' &&
        (c[0]).endsWith('/api/v1/orgs/tnt-A/alert-channels') &&
        (c[1] as RequestInit).method === 'POST',
    );
    expect(createCall).toBeFalsy();
  });
});
