/**
 * TenantAlertFeed tests. The critical invariants are:
 *   - It fetches /api/v1/orgs/{tenantId}/alerts — the path-tenant
 *     scope is enforced by the caller and hardcoded server-side.
 *   - Ack/Resolve buttons are hidden from role: member.
 *   - Ack dialog submits POST with note.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider } from '../../hooks/useAuth';
import { TenantAlertFeed } from './TenantAlertFeed';

function renderFeed() {
  return render(
    <MemoryRouter>
      <AuthProvider>
        <TenantAlertFeed />
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

describe('TenantAlertFeed', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('fetches alerts under /api/v1/orgs/{tenantId}/alerts (no cross-tenant leak)', async () => {
    const fetchMock = vi.fn();
    // /auth/me → member of tnt-A
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        user: {
          id: 'u1',
          email: 'a@b',
          displayName: 'A',
          tenantId: 'tnt-A',
          role: 'owner',
        },
      }),
    );
    // tenant list call
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        items: [
          {
            id: 'a1',
            ruleId: null,
            severity: 'warning',
            title: 'test alert',
            description: 'd',
            correlationId: null,
            tenantId: 'tnt-A',
            metadata: '{}',
            status: 'active',
            acknowledgedBy: null,
            acknowledgedAt: null,
            resolvedBy: null,
            resolvedAt: null,
            resolution: null,
            createdAt: new Date().toISOString(),
          },
        ],
        count: 1,
        limit: 200,
      }),
    );
    globalThis.fetch = fetchMock;

    renderFeed();

    await waitFor(() => {
      expect(screen.getByText('test alert')).toBeInTheDocument();
    });

    // Second call (index 1) is the alerts list fetch. The first argument
    // (URL) must be the tenant-scoped path. Anything else = leak.
    const alertsCall = fetchMock.mock.calls[1];
    expect(alertsCall?.[0] as string).toContain(
      '/api/v1/orgs/tnt-A/alerts',
    );
  });

  it('hides ack/resolve buttons from a plain member', async () => {
    const fetchMock = vi.fn();
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        user: {
          id: 'u1',
          email: 'a@b',
          displayName: 'A',
          tenantId: 'tnt-A',
          role: 'member',
        },
      }),
    );
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        items: [
          {
            id: 'a1',
            ruleId: null,
            severity: 'critical',
            title: 'read-only',
            description: 'd',
            correlationId: null,
            tenantId: 'tnt-A',
            metadata: '{}',
            status: 'active',
            acknowledgedBy: null,
            acknowledgedAt: null,
            resolvedBy: null,
            resolvedAt: null,
            resolution: null,
            createdAt: new Date().toISOString(),
          },
        ],
        count: 1,
        limit: 200,
      }),
    );
    globalThis.fetch = fetchMock;

    renderFeed();

    await waitFor(() => {
      expect(screen.getByText('read-only')).toBeInTheDocument();
    });

    // Members see rows but not the mutation buttons.
    expect(screen.queryByRole('button', { name: /^Ack$/i })).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: /^Resolve$/i }),
    ).not.toBeInTheDocument();
  });

  it('admin can open the ack dialog and submit with note', async () => {
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
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        items: [
          {
            id: 'a1',
            ruleId: null,
            severity: 'warning',
            title: 'mutable',
            description: 'd',
            correlationId: null,
            tenantId: 'tnt-A',
            metadata: '{}',
            status: 'active',
            acknowledgedBy: null,
            acknowledgedAt: null,
            resolvedBy: null,
            resolvedAt: null,
            resolution: null,
            createdAt: new Date().toISOString(),
          },
        ],
        count: 1,
        limit: 200,
      }),
    );
    // ack response
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: 'a1', status: 'acknowledged' }));
    // refresh after ack
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ items: [], count: 0, limit: 200 }),
    );
    globalThis.fetch = fetchMock;

    renderFeed();

    await waitFor(() => {
      expect(screen.getByText('mutable')).toBeInTheDocument();
    });

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /^Ack$/i }));
    await user.type(screen.getByLabelText(/note/i), 'investigating');
    await user.click(screen.getByRole('button', { name: /^Acknowledge$/i }));

    await waitFor(() => {
      // The ack POST must have fired.
      const ackCall = fetchMock.mock.calls.find(
        (c) =>
          typeof c[0] === 'string' &&
          (c[0]).includes('/alerts/a1/acknowledge'),
      );
      expect(ackCall).toBeTruthy();
      expect((ackCall?.[1] as RequestInit).method).toBe('POST');
      const body = JSON.parse((ackCall?.[1] as RequestInit).body as string);
      expect(body.note).toBe('investigating');
    });
  });
});
