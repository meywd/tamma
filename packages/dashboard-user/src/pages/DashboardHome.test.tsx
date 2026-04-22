/**
 * DashboardHome renders three widgets pulled from the user-scoped
 * dashboard endpoints. Tenant id comes from useAuth().user.tenantId;
 * when no tenantId is present, the page shows a "no organization" empty
 * state so the user can't hit an error requesting /orgs/null/*.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider } from '../hooks/useAuth';
import { DashboardHome } from './DashboardHome';

function renderHome() {
  return render(
    <MemoryRouter>
      <AuthProvider>
        <DashboardHome />
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('DashboardHome', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('shows "no organization" when user has no tenantId', async () => {
    globalThis.fetch = vi.fn().mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          user: { id: 'u1', email: 'a@b.com', displayName: 'A', tenantId: null },
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    ) as unknown as typeof fetch;

    renderHome();

    await waitFor(() => {
      expect(screen.getByText(/no organization/i)).toBeInTheDocument();
    });
  });

  it('renders stats + recent runs when tenantId is set', async () => {
    const fetchMock = vi.fn();
    fetchMock
      // /auth/me → user with tenant
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            user: {
              id: 'u1',
              email: 'a@b.com',
              displayName: 'A',
              tenantId: 'tnt-1',
              role: 'owner',
            },
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      )
      // summary
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            tenantId: 'tnt-1',
            totalEvents: 42,
            totalWorkflows: 3,
            workflowDefinitions: 5,
            recentEvents: [],
            timestamp: '2026-04-22T00:00:00Z',
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      )
      // runs
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({ tenantId: 'tnt-1', total: 0, runs: [] }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      )
      // stats
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            tenantId: 'tnt-1',
            totalRuns: 10,
            completedRuns: 7,
            failedRuns: 2,
            runningRuns: 1,
            successRate: 0.7778,
            avgDurationSeconds: 180,
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      );
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    renderHome();

    // Wait for all three widget fetches to resolve (summary + runs + stats).
    // Using the summary's numeric tile as the gate avoids a race where the
    // /stats fetch completes first and "success rate" appears before the
    // summary's totalEvents has hydrated.
    await waitFor(() => {
      expect(screen.getByText('42')).toBeInTheDocument();
    });

    expect(screen.getByText(/success rate/i)).toBeInTheDocument();
    expect(screen.getByText(/total events/i)).toBeInTheDocument();
  });
});
